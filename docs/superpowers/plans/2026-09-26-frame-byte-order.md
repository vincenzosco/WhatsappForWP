# Frame Byte Order Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the app read the frame length in the same byte order the adapter writes it, so the first frame of a connection is accepted instead of being rejected as an impossible length.

**Architecture:** The device reported `DIAG ReadFrameAsync/length: ... lunghezza frame fuori intervallo: 553713664` on the very first frame. `553713664` is `0x21010000`, which is `0x00000121` = 289 read byte-swapped: 289 bytes is exactly the size of the `state` frame the adapter writes when a client connects. The wire is little-endian (`Buffer.writeUInt32LE` in the adapter); WinRT's `DataReader`/`DataWriter` are **not** little-endian by default, so the app was reading a big-endian length. Both directions have to be pinned explicitly. The read loop itself was already fixed by the previous plan (`2026-09-26-qr-connection-and-frame-limits.md`), which is why this shows up as a diagnosis instead of an `OutOfMemoryException`.

**Tech Stack:** C# 5 on Windows Phone 8.1 (`Windows.Storage.Streams.DataReader`/`DataWriter`), Node.js adapter (`Buffer.writeUInt32LE`/`readUInt32LE`), and a new static guard in `tools/`.

## Global Constraints

- **C# 5 only.** No `$"..."`, `?.`, expression-bodied members, `out var`, `is T x`, `nameof`, `_ =`. Use `var` and `delegate { }`.
- **There is no C# test host in this repo.** A C# change is verified by the static guards, the WP8.1 build gate on the Parallels VM, and the `DIAG` lines the app prints on the device. Each task states the exact command and the expected line. The guard script is real Node code and its output is the "test".
- **Never a silent `catch`.** Every survived failure goes through `Diag.Failed("<call site>", ex)`.
- **The frame contract is one contract:** `[4-byte little-endian length][payload]`, payload `[1-byte tag][16-byte IV][CBC ciphertext][32-byte HMAC]`, ceiling 8 MiB on both sides. Change one side and you must change the other.
- **No emoji in any `.md`** (U+26A0 is the only exception).
- **Docs are written in pairs** (`README.md`/`README.it.md`, `WhatsappBridge/README.md`/`README.it.md`), same heading depth and order, both languages in the same commit.
- **Guards that must stay green:** `node tools/check-csharp5.js`, `node tools/check-icons.js`, `node tools/check-resw.js --strict`, `node tools/check-docs.js`, `node tools/qr-term.js --self-test`, `cd WhatsappBridge && npm test` (49 tests), `node --test "tools/test/**/*.test.js"` (17 tests).
- **Build gate (user, Windows machine):** `COPIA=0`, then `Errori: 0` and `Your package has been successfully created` from `MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86`.
- **Commits:** English, `type: short imperative`, one per task, then `git push origin master`.

---

## The evidence

```
DIAG ReadFrameAsync/length: InvalidDataException 0x80131501 lunghezza frame fuori intervallo: 553713664
```

`553713664 = 0x21010000`. Reversed, `0x00000121 = 289`. The adapter writes `[len: 4 byte LE][payload]`, so the bytes on the wire are `21 01 00 00`; the app read them back as `0x21010000`. A byte-swapped 32-bit read is a byte-order disagreement, not a truncated stream: with the previous plan's loop the prefix is now always read in full, so the four bytes it read *were* the four bytes of the length field.

Two consequences, and both must be fixed together:

1. **adapter -> app** (what the log shows): the app reads a length about 2 billion times too large, rejects it, and the connection closes. No `state`, no `qr`, no login. This is the "connected but no QR" the user reported.
2. **app -> adapter**: `DataWriter.WriteUInt32` uses the same default byte order, so the handshake length arrives at the adapter byte-swapped too. The adapter reads it as little-endian, so it sees a length between `0x01000000` and `0xFFFFFFFF` (at least 16 MiB) and would now close the socket with `Frame non accettabile`. Before the `MAX_FRAME_LENGTH` change it simply waited for gigabytes and logged nothing.

A useful cross-check before the fix: the adapter log should contain `Frame non accettabile da ... (lunghezza <large number>)`. After the fix that line must be gone.

## File structure

| File | Responsibility | Change |
| --- | --- | --- |
| `WhatsappApp/Services/CommunicationService.cs` | the encrypted TCP client, framing | Task 1: two factories that create a reader/writer with the byte order pinned, used by all three framing sites |
| `tools/check-framing.js` | new guard for the frame contract | Task 2 |
| `.agents/skills/test-the-app/SKILL.md` | the fast gate and its table | Task 2: register the guard |
| `README.md`, `README.it.md`, `.agents/skills/maintain-the-app/SKILL.md` | project docs and rules | Task 3 |
| `docs/superpowers/plans/2026-09-26-frame-byte-order.md` | this plan | Task 3: the record of what execution changed |

---

### Task 1: Pin the frame byte order in the app

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `private static DataReader CreateFrameReader(IInputStream stream)` and `private static DataWriter CreateFrameWriter(IOutputStream stream)`. Task 2's guard requires every `new DataReader(...)` / `new DataWriter(...)` in this file to set `ByteOrder = ByteOrder.LittleEndian` within the next four lines — the two factories satisfy that, and nothing else may create one.

- [ ] **Step 1: Add the two factories**

Add them next to `DisposeSocket`:

```csharp
        /// <summary>
        /// Un DataReader per un flusso di rete, con il byte order detto per
        /// esteso. Quello predefinito di WinRT non e' little-endian, e l'adapter
        /// scrive la lunghezza del frame con writeUInt32LE: sul dispositivo un
        /// frame da 289 byte (0x00000121) veniva letto 0x21010000 = 553713664,
        /// cioe' un frame che non esiste, e la connessione si chiudeva prima di
        /// ricevere lo stato e il codice QR. La lettura e la scrittura devono
        /// usare CreateFrameReader/CreateFrameWriter: sono l'unico posto in cui
        /// si sceglie il byte order, quindi non possono piu' divergere.
        /// </summary>
        private static DataReader CreateFrameReader(IInputStream stream)
        {
            var reader = new DataReader(stream);
            reader.InputStreamOptions = InputStreamOptions.Partial;
            reader.ByteOrder = ByteOrder.LittleEndian;
            return reader;
        }

        /// <summary>Lo stesso patto di CreateFrameReader, lato scrittura.</summary>
        private static DataWriter CreateFrameWriter(IOutputStream stream)
        {
            var writer = new DataWriter(stream);
            writer.ByteOrder = ByteOrder.LittleEndian;
            return writer;
        }
```

`IInputStream`, `IOutputStream`, `ByteOrder`, `InputStreamOptions`, `DataReader`, `DataWriter` are all in `Windows.Storage.Streams`, already imported.

- [ ] **Step 2: Use them in `ConnectToServerAsync`**

Replace

```csharp
                writer = new DataWriter(socket.OutputStream);
                reader = new DataReader(socket.InputStream);
                reader.InputStreamOptions = InputStreamOptions.Partial;
```

with

```csharp
                writer = CreateFrameWriter(socket.OutputStream);
                reader = CreateFrameReader(socket.InputStream);
```

- [ ] **Step 3: Use them in `OnServerConnectionReceived`**

Replace

```csharp
                var reader = new DataReader(socket.InputStream);
                reader.InputStreamOptions = InputStreamOptions.Partial;
```

with

```csharp
                var reader = CreateFrameReader(socket.InputStream);
```

- [ ] **Step 4: Use them in `BroadcastToAllClientsAsync`**

Replace

```csharp
                    var writer = new DataWriter(client.OutputStream);
```

with

```csharp
                    var writer = CreateFrameWriter(client.OutputStream);
```

- [ ] **Step 5: Run the guard**

```bash
node tools/check-csharp5.js
```

Expected: `OK: 27 C# file(s) are C# 5 compatible.`

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "fix: pin the frame byte order so the length is not read byte-swapped"
```

---

### Task 2: A guard for the frame contract

**Files:**
- Create: `tools/check-framing.js`
- Modify: `.agents/skills/test-the-app/SKILL.md`

**Interfaces:**
- Consumes: the factories from Task 1 (`CreateFrameReader`/`CreateFrameWriter` keep every `new DataReader`/`new DataWriter` paired with the byte order).
- Produces: `node tools/check-framing.js`, exit code 0 with an `OK: ...` line, added to the fast gate.

This bug reached the phone twice because nothing here could see it: the C# has no test host, the adapter's own tests run Node on both ends, and both sides are individually self-consistent. A static guard is the only thing that can hold the two halves together on this machine.

- [ ] **Step 1: Write the guard**

Create `tools/check-framing.js`:

```js
#!/usr/bin/env node
/**
 * tools/check-framing.js
 *
 * Guard for the TCP frame contract between the app and the adapter.
 *
 * Why it exists: on the device the app read a frame length of 553713664 where
 * the wire carried 289. 553713664 is 0x21010000, and 0x00000121 is 289: a
 * byte-swapped 32-bit read. The adapter writes the length with
 * `writeUInt32LE`; WinRT's DataReader/DataWriter are not little-endian by
 * default. Nothing else in this repo can catch that, because the C# has no test
 * host, the adapter's suite runs Node on both ends, and each side is
 * self-consistent.
 *
 * Rules:
 *   A. every `new DataReader(...)` / `new DataWriter(...)` in
 *      WhatsappApp/Services/CommunicationService.cs sets
 *      `ByteOrder = ByteOrder.LittleEndian` within the next four lines;
 *   B. the adapter's two halves use the little-endian helpers -
 *      `writeUInt32LE` in crypto-helper.js, `readUInt32LE` in server.js;
 *   C. the frame ceiling is the same expression on both sides:
 *      `MaxFrameLength` in the app and `MAX_FRAME_LENGTH` in the adapter.
 *
 * Usage: node tools/check-framing.js
 */
'use strict';

const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const APP_SOCKET = path.join(ROOT, 'WhatsappApp', 'Services', 'CommunicationService.cs');
const CRYPTO = path.join(ROOT, 'WhatsappBridge', 'crypto-helper.js');
const SERVER = path.join(ROOT, 'WhatsappBridge', 'server.js');

const problems = [];
const read = (file) => fs.readFileSync(file, 'utf8');

// ---------------------------------------------------------------------------
// A. the byte order of every socket reader and writer
// ---------------------------------------------------------------------------
const appLines = read(APP_SOCKET).split(/\r?\n/);
let sites = 0;
appLines.forEach((line, i) => {
  if (!/new\s+(DataReader|DataWriter)\s*\(/.test(line)) return;
  sites++;
  const nearby = appLines.slice(i, i + 5).join('\n');
  if (!/ByteOrder\s*=\s*ByteOrder\.LittleEndian/.test(nearby)) {
    problems.push('WhatsappApp/Services/CommunicationService.cs:' + (i + 1) + ': ' +
      line.trim() + ' without "ByteOrder = ByteOrder.LittleEndian" nearby - the ' +
      'WinRT default byte order turns the frame length into a number that does ' +
      'not exist');
  }
});
if (sites === 0) {
  problems.push('WhatsappApp/Services/CommunicationService.cs: no DataReader or ' +
    'DataWriter found (did the framing move? then this guard must move too)');
}

// ---------------------------------------------------------------------------
// B. the adapter side of the same contract
// ---------------------------------------------------------------------------
if (!/writeUInt32LE/.test(read(CRYPTO))) {
  problems.push('WhatsappBridge/crypto-helper.js: buildFrame no longer writes the ' +
    'frame length with writeUInt32LE');
}
const serverText = read(SERVER);
if (!/readUInt32LE/.test(serverText)) {
  problems.push('WhatsappBridge/server.js: the frame reader no longer reads the ' +
    'frame length with readUInt32LE');
}

// ---------------------------------------------------------------------------
// C. one ceiling, two sides
// ---------------------------------------------------------------------------
function ceiling(text, pattern, where) {
  const m = pattern.exec(text);
  if (!m) {
    problems.push(where + ': not found (the ceiling moved or was renamed)');
    return null;
  }
  return m[1].replace(/\s+/g, '');
}
const appCeiling = ceiling(read(APP_SOCKET), /MaxFrameLength\s*=\s*([^;]+);/, 'MaxFrameLength');
const adapterCeiling = ceiling(serverText, /MAX_FRAME_LENGTH\s*=\s*([^;]+);/, 'MAX_FRAME_LENGTH');
if (appCeiling && adapterCeiling && appCeiling !== adapterCeiling) {
  problems.push('the frame ceiling differs: MaxFrameLength = ' + appCeiling +
    ' in the app, MAX_FRAME_LENGTH = ' + adapterCeiling + ' in the adapter - one ' +
    'side will drop what the other sends');
}

if (problems.length) {
  console.log(problems.join('\n'));
  console.log('\n' + problems.length + ' problem(s).');
  process.exit(1);
}

console.log('OK: ' + sites + ' socket reader/writer site(s) pinned to little-endian, ' +
  'the adapter reads and writes the length little-endian, one frame ceiling (' +
  appCeiling + ').');
```

- [ ] **Step 2: Run it against the code as it is**

```bash
node tools/check-framing.js
```

Expected: `OK: 2 socket reader/writer site(s) pinned to little-endian, the adapter reads and writes the length little-endian, one frame ceiling (8*1024*1024).`

Wait - the guard counts the `new DataReader`/`new DataWriter` *calls to the constructors*, which live inside the two factories, so the count is 2. If the app still had the three inline construction sites, the count would have been 3 and two of them would have been flagged. Verify the number is 2; a different number means the factories are not the only construction site.

- [ ] **Step 3: Prove the guard fails on a regression**

Temporarily remove the `reader.ByteOrder = ByteOrder.LittleEndian;` line from `CreateFrameReader`, run `node tools/check-framing.js`, confirm it prints a problem for `CommunicationService.cs` and exits non-zero, then put the line back and confirm `OK` again. This is the only way to know the guard is not vacuous.

- [ ] **Step 4: Register the guard in the fast gate**

In `.agents/skills/test-the-app/SKILL.md`, in `## The fast gate`, add the line after `check-docs.js`:

```bash
node tools/check-framing.js      # the frame byte order and the shared frame ceiling
```

and this row to the table:

```markdown
| `check-framing.js` | A socket `DataReader`/`DataWriter` without `ByteOrder = ByteOrder.LittleEndian` (the WinRT default byte-swaps the frame length: `0x00000121` came back as `0x21010000`), an adapter that stopped using `writeUInt32LE`/`readUInt32LE`, a frame ceiling that differs between the app and the adapter. |
```

- [ ] **Step 5: Run the whole fast gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/qr-term.js --self-test
```

Expected: six `OK: ...` lines.

- [ ] **Step 6: Commit**

```bash
git add tools/check-framing.js .agents/skills/test-the-app/SKILL.md
git commit -m "test: guard the frame byte order and the shared frame ceiling"
```

---

### Task 3: Say it in the docs, then prove it on the phone

**Files:**
- Modify: `README.md`, `README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-26-frame-byte-order.md` (the record)

- [ ] **Step 1: Add the bullet to the project docs**

In `README.md`, in the `## Protocol` section, right after the paragraph that begins `A frame length is never trusted:`, add:

```markdown
Both sides pin the byte order explicitly: the adapter writes the length with
`writeUInt32LE` and the app creates its readers and writers through
`CreateFrameReader`/`CreateFrameWriter`, which set
`ByteOrder = ByteOrder.LittleEndian`. WinRT's default is not little-endian, and a
reader that disagrees does not fail loudly: it reads a byte-swapped length
(`0x00000121` came back as `0x21010000`, 553713664) and drops a frame that was
perfectly fine. `tools/check-framing.js` fails the fast gate if a
`DataReader`/`DataWriter` in the socket layer is created any other way.
```

In `README.it.md`, in the `## Protocollo` section, right after the paragraph that begins `Una lunghezza di frame non viene mai creduta`, add the matching text:

```markdown
Il byte order e' detto per esteso da entrambe le parti: l'adapter scrive la
lunghezza con `writeUInt32LE` e l'app costruisce lettori e scrittori con
`CreateFrameReader`/`CreateFrameWriter`, che impostano
`ByteOrder = ByteOrder.LittleEndian`. Il valore predefinito di WinRT non e'
little-endian, e un lettore che non concorda non fallisce in modo evidente:
legge una lunghezza invertita (`0x00000121` tornava come `0x21010000`, 553713664)
e scarta un frame che era perfettamente valido. `tools/check-framing.js` fa
fallire il gate veloce se nella parte socket un `DataReader`/`DataWriter` viene
creato in un altro modo.
```

- [ ] **Step 2: Add the gotcha to the maintain skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in `## Known gotchas`, replace the bullet that begins `- **A frame length is not trusted.**` with:

```markdown
- **A frame length is not trusted, and its byte order is never left to a
  default.** `ReadFrameAsync` fills the 4-byte prefix fully
  (`InputStreamOptions.Partial` can split it) and rejects anything outside
  `1..MaxFrameLength` (8 MiB). The length itself is little-endian on the wire
  (`writeUInt32LE`/`readUInt32LE`), but WinRT's `DataReader`/`DataWriter` do not
  default to that: build them only through `CreateFrameReader`/`CreateFrameWriter`
  in `CommunicationService.cs`. A byte-swapped length does not throw, it reads a
  number that looks like a corrupt frame (`0x00000121` came back as `0x21010000`,
  553713664) and closes the connection. The adapter's `MAX_FRAME_LENGTH` is the
  same 8 MiB: change one and you must change the other.
```

- [ ] **Step 3: Record what execution changed**

Append to this plan:

```markdown
## What execution changed about the plan

1. **The guard counts 2 construction sites, not 3.** The three framing sites go
   through the two factories, so the only `new DataReader`/`new DataWriter` calls
   left are the two inside them. Step 2 of Task 2 checks that number: if it ever
   reads 3, one site bypasses the factories and the guard is the only thing that
   will say so.
2. **Nothing else used the old inline construction.** `ChatPage.xaml.cs` and
   `ConnectionPage.xaml.cs` also create a `DataReader`/`DataWriter`, but over
   files and in-memory buffers where the byte order is irrelevant, so the guard is
   scoped to `CommunicationService.cs` on purpose.
```

- [ ] **Step 4: Run the docs gates**

```bash
node tools/check-docs.js && node tools/check-framing.js && node tools/check-resw.js --strict
```

Expected: three `OK: ...` lines.

- [ ] **Step 5: Commit and push**

```bash
git add README.md README.it.md .agents/skills/maintain-the-app/SKILL.md docs/superpowers/plans/2026-09-26-frame-byte-order.md
git commit -m "docs: the frame length is little-endian by contract, not by default"
git push origin master
```

- [ ] **Step 6: The build gate and the phone (user)**

```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`, `Errori: 0`, `Your package has been successfully created`.

On the phone, deploy with the debugger and check, in this order:

1. The three `DIAG ok:` start-up lines, **no** `DIAG ReadFrameAsync/length`, **no** `DIAG ConnectToServerAsync`, **no** `DIAG ListenForMessagesAsync`.
2. The adapter log shows `Handshake da "..."` - the handshake now arrives with a length it can read. If instead it shows `Frame non accettabile da ... (lunghezza <milioni>)`, one of the two sides is still byte-swapping.
3. The QR overlay opens by itself and the phone can scan it.

---

## Self-Review

**1. Spec coverage**

| Evidence | Task |
| --- | --- |
| `ReadFrameAsync/length ... 553713664` is a byte-swapped 289 | Task 1 pins the byte order on both the reader and the writer |
| The same defect could come back unnoticed | Task 2 adds a guard that the fast gate runs, plus a step that proves the guard fails on a regression |
| The next person to touch framing must know the rule | Task 3 puts it in both READMEs and in the maintain skill, and names the two factories |

**2. Placeholder scan**

No `TBD`, no "add validation", no "similar to Task N". Every step carries the complete code or the complete replacement text, and every verification step carries the command and the exact expected line.

**3. Type consistency**

- `CreateFrameReader(IInputStream) : DataReader` and `CreateFrameWriter(IOutputStream) : DataWriter` are declared once, in Task 1, and used at the three framing sites in the same task. Task 2's guard requires `Reader`/`Writer` construction to happen only there.
- `reader.InputStreamOptions = InputStreamOptions.Partial` moved into the factory, so the three call sites no longer set it (and `OnServerConnectionReceived` no longer can forget it).
- The guard's rule C compares `MaxFrameLength` (app) with `MAX_FRAME_LENGTH` (adapter) as whitespace-stripped expressions, so both must read `8 * 1024 * 1024`; Task 3's docs state the same 8 MiB.
- `ByteOrder.LittleEndian` is spelled the same in the code, the guard, the READMEs and the skill.

---

## What execution changed about the plan

1. **The guard counts 2 construction sites, not 3.** The three framing sites go
   through the two factories, so the only `new DataReader`/`new DataWriter` calls
   left are the two inside them. Task 2 Step 2 checks that number: if it ever reads
   3, one site bypasses the factories and this guard is the only thing that will
   say so.
2. **Task 2 Step 3 was actually run, not skipped.** Removing the
   `reader.ByteOrder` line made the guard print
   `WhatsappApp/Services/CommunicationService.cs:514: var reader = new
   DataReader(stream); without "ByteOrder = ByteOrder.LittleEndian" nearby` and
   exit 1; restoring it returned the `OK:` line. A guard that has never been seen
   to fail is not a guard.
3. **Nothing else creates a socket reader or writer.** `ChatPage.xaml.cs`,
   `ConnectionPage.xaml.cs` and `ImageHelper.cs` also create a
   `DataReader`/`DataWriter`, but over files and in-memory buffers where the byte
   order is irrelevant, so the guard is scoped to `CommunicationService.cs` on
   purpose; the count in the guard output is what makes that visible.
