# Chat Open Crash Named And Observable Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the one chat-open defect the evidence already proves — the app binds the conversation before its history burst arrives — and make any crash that remains name itself in a log that survives the crash and reaches the adapter without a debugger.

**Architecture:** One constant and three changes on the phone. The constant first: the history wait is 2000 ms while the adapter's own cold read of the account measured about 15 s, so the list is routinely bound before its burst and the fifty frames then insert into a list that is already watching the collection — the exact failure `ConversationView.Bind` was built to avoid. Then `Diag` appends to a capped file through the existing `SerialQueue`, and every run writes a start marker and an end marker, so a run whose end marker is missing is a crash and its tail is the report — today the whole history is a 200-line in-memory list that dies with the process, which is why a crash leaves nothing to read. Then a small service sends that tail to the adapter as the existing `diag` control frame on the first connection of the next run, so the container log carries it and nobody copies anything off the phone. Then the burst, the bind and the memory are counted, so the log says which of the three died.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1; Node.js 18+ guards in `tools/` with `node:test`; adapter in `WhatsappBridge` (not touched by this plan).

**Spec:** the phone run of 2026-10-07 — "sul telefono appena apro una chat, l'app crasha" — and the adapter log of the same run, measured with a protocol stand-in for the app (the bridge key and the device token derived from the store secret): opening one conversation answers with **50 frames and `history.done`**, and the first, cold read of the account took **~15 s** (`05:03:36 reading up to 25 conversation(s)` to `05:03:51 Listed chats successfully`, with `Panic recovered in middleware: context deadline exceeded`). The wire is therefore healthy and the phone dies with it; the app's own `HistoryWaitMaxMilliseconds` is **2000**.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985); no `?.`, `$"..."`, `nameof`, pattern matching, auto-property initializers, `out var`.
- Every file LF, no BOM; normalize with `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <files>`.
- Gate before every commit (eleven guards plus the new one): `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js && node tools/check-diagnostics.js`, then `node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- There is no C# test harness: a C# task's tests are the guards, the ARM build and the phone run. The guard added in Task 2 has real tests, in `tools/test/check-diagnostics.test.js`.
- **`WhatsappBridge/` is not touched by this plan**, so no Docker mirror sync is needed.
- Build only ARM (phone) or x86 (emulator), never Any CPU. After a build, `git checkout -- WhatsappApp/Package.appxmanifest` and **never** the `.csproj`.
- A new `.cs` file needs its `<Compile Include>` in `WhatsappApp.csproj`, or `check-project-files.js` fails.
- Push is the rule: every task ends with `git push origin master`. No apostrophes in commit messages; no emoji in documents except U+26A0.
- `Diag.Failed` and `Diag.Ok` are for a condition the app survives; every new line here exists so the *next* phone run can name the site. `check-resw.js` reads every literal `Loc.Get("...")` as a key lookup, so a diagnostic label is never spelled as a `Loc.Get` call: these are English sentences in `Diag`, not localized strings.
- The counts to expect: every guard `OK`, all of `node --test` passing, and `npm test` in `WhatsappBridge` passing with the same count as before the change (290 at the time of writing).

## Review Focus

- **A run that dies without a managed exception.** The historical shape in this repo is a log that stops at the assembly list with exit code 0 and no `DIAG` line, and `App.OnUnhandled` never fires for it. The tail must already be on disk when that happens, so the log is appended as the run goes and not only at the end. Task 2 Step 5 owns it.
- **The first launch after a fresh install**, when there is no `diag.log` at all: the restore path must not throw and must not announce a crash. Task 2 Step 6 owns it.
- **A previous run the user closed properly.** It must not be reported as a crash, or every launch sends a report and the container log becomes noise. Task 2 Step 5 writes the end marker and Task 3 Step 4 clears the pending tail on a deliberate close.
- **A crash in a run that never reached the adapter** (no socket, no pairing): the tail must be kept and sent by a later run, not dropped. Task 3 Step 4 owns it.
- **A `diag.log` that reached its cap while the phone was in a crash loop**: the tail must still be the newest lines, not the oldest. Task 2 Step 5 owns it.

---

### Task 1: The history wait stops being shorter than the read

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`HistoryWaitMaxMilliseconds` and its comment)
- Modify: `.agents/skills/maintain-the-app/SKILL.md` (the invariant)
- Test: none in C#; the guards, the ARM build and the phone run are the test

**Interfaces:**
- Consumes: `DataService.HistoryCompleted`, `Guarded.RunGuardedAsync`.
- Produces: `HistoryWaitMaxMilliseconds = 20000` — still a `private const int`, with the measured reason in the comment above it. Task 2's guard pins the floor; this task is what satisfies it.

- [ ] **Step 1: Raise the constant and record the measurement**

In `ChatPage.xaml.cs`, replace the value and the comment above it:

```csharp
        /// <summary>
        /// How long the list waits for the end of a history burst before binding
        /// anyway. The adapter's first read of the account is an HTTP round trip
        /// through GOWA and was measured at about 15 s on 2026-10-07, so a shorter
        /// wait binds the list first and lets the whole burst insert into a list
        /// that is already watching the collection - which is the failure
        /// ConversationView.Bind exists to prevent. Twenty seconds covers that
        /// read with room to spare, and the wait is still bounded, so an adapter
        /// without the closing frame does not leave the chat empty.
        /// </summary>
        private const int HistoryWaitMaxMilliseconds = 20000;
```

- [ ] **Step 2: Record the rule**

In `.agents/skills/maintain-the-app/SKILL.md`, at the end of the `A cache is a photograph, not a truth` bullet, add: the history wait must not be shorter than the adapter's cold read of the account, because the bind that happens first is the one that inserts into a list already watching the collection; the wait is therefore 20000 ms against a measured 15 s.

- [ ] **Step 3: Gate, build and commit**

Run the gate and the ARM build from Global Constraints. Expected: every guard `OK`, all tool tests passing, `Your package has been successfully created.`

```bash
git add WhatsappApp/Pages/ChatPage.xaml.cs .agents/skills/maintain-the-app/SKILL.md
git commit -m "Wait for the history as long as the adapter takes to read it"
git push origin master
```

---

### Task 2: The log survives the crash

**Files:**
- Create: `tools/check-diagnostics.js`
- Create: `tools/test/check-diagnostics.test.js`
- Create: `tools/diagnostics-fixtures/diag-good.cs`, `tools/diagnostics-fixtures/app-good.cs`, `tools/diagnostics-fixtures/chatpage-good.cs`
- Modify: `WhatsappApp/Services/Diag.cs` (the file sink, the run markers, the restore)
- Modify: `WhatsappApp/App.xaml.cs` (`StartServicesOnce`, the closing path, `OnUnhandled`, `OnUnobservedTask`)
- Modify: `.agents/skills/test-the-app/SKILL.md` (the new guard in the chain and the table) and `.agents/skills/maintain-the-app/SKILL.md` (the invariant)
- Test: `tools/test/check-diagnostics.test.js`

**Interfaces:**
- Consumes: `SerialQueue` (`RunAsync(Func<Task> work) -> Task`), `Guarded.RunGuardedAsync(string, Func<Task>)`, `ApplicationData.Current.LocalFolder`, `FileIO`, `Diag.Ok(string)` / `Diag.Failed(string, Exception)`.
- Produces:
  - `Diag.BeginRun()` — appends the start marker; called once at startup.
  - `Diag.EndRun()` — appends the end marker and clears the pending tail; called only on a deliberate close.
  - `Diag.Flush()` and `Diag.Flush(bool force)` — append the lines not yet on disk, at most once per second unless forced.
  - `Diag.RestorePreviousRunAsync() -> Task<bool>` — true when the previous run died without its end marker.
  - `Diag.PendingCrashTail -> string` — the previous run's tail when it died, else empty. Task 3 consumes this.
  - Private consts in `Diag.cs` that the guard pins: `FileName = "diag.log"`, `MaxBytes = 65536`, `RunStarted = "=== run started "`, `RunEnded = "=== run ended "`.

- [ ] **Step 1: Write the failing guard test**

Create `tools/test/check-diagnostics.test.js`:

```js
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const { problemsFor } = require('../check-diagnostics.js');

const fixture = (name) => fs.readFileSync(
  path.join(__dirname, '..', 'diagnostics-fixtures', name), 'utf8');

const diag = fixture('diag-good.cs');
const app = fixture('app-good.cs');
const chatPage = fixture('chatpage-good.cs');

test('il buon sorgente non ha problemi', () => {
  assert.deepStrictEqual(problemsFor({ diag, app, chatPage }).problems, []);
});

test('una scrittura senza SerialQueue e un problema', () => {
  const broken = { diag: diag.replace('Writes = new SerialQueue()', ''), app, chatPage };
  assert.match(problemsFor(broken).problems.join('\n'), /SerialQueue/);
});

test('un handler che non scrive il log prima di morire e un problema', () => {
  const broken = { diag, app: app.replace('Diag.Flush(true);', ''), chatPage };
  assert.match(problemsFor(broken).problems.join('\n'), /App\/unhandled/);
});

test('un tetto al file mancante e un problema', () => {
  const broken = { diag: diag.replace('MaxBytes = 65536', ''), app, chatPage };
  assert.match(problemsFor(broken).problems.join('\n'), /MaxBytes/);
});

test('un attesa della storia piu corta della lettura dell adapter e un problema', () => {
  const broken = { diag, app, chatPage: chatPage.replace('20000', '2000') };
  assert.match(problemsFor(broken).problems.join('\n'), /HistoryWaitMaxMilliseconds/);
});
```

- [ ] **Step 2: Run it to watch it fail**

Run: `node --test tools/test/check-diagnostics.test.js`
Expected: FAIL, `Cannot find module '../check-diagnostics.js'`.

- [ ] **Step 3: Write the guard and its fixtures**

Create `tools/check-diagnostics.js` in the house shape (`#!/usr/bin/env node`, a header block explaining *why it exists* and the rules, `Usage:`, `main()` under `require.main === module`, `module.exports = { problemsFor }`). `problemsFor({ diag, app, chatPage })` takes source **text** and returns `{ problems }`; `main()` reads the three real files.

- `WhatsappApp/Services/Diag.cs`
- `WhatsappApp/App.xaml.cs`
- `WhatsappApp/Pages/ChatPage.xaml.cs`

Rules:

- **A.** `Diag.cs` contains `new SerialQueue()` and `MaxBytes = 65536` and `FileName = "diag.log"` — the log is written one piece at a time and cannot grow without a ceiling.
- **B.** `App.xaml.cs` contains `Diag.Flush(true);` at an index **after** `Diag.Failed("App/unhandled"` — the unhandled path writes the log before the process may die.
- **C.** `Diag.cs` contains both markers, `"=== run started "` and `"=== run ended "` — a run whose end marker is absent is the crash.
- **D.** `ChatPage.xaml.cs` contains `HistoryWaitMaxMilliseconds = ` with a value **>= 20000** — the floor Task 1 set, recorded in the header with its reason (the measured cold read of ~15 s).

Create the three fixtures whose text satisfies exactly these rules, so `problemsFor` is testable without the app.

- [ ] **Step 4: Run the guard's tests to verify they pass**

Run: `node --test tools/test/check-diagnostics.test.js`
Expected: PASS, 5 tests. These are the guard's own tests, on fixtures; the real tree satisfies the rules only after Steps 5-7, which Step 9 checks.

- [ ] **Step 5: Add the file sink and the run markers to `Diag`**

In `WhatsappApp/Services/Diag.cs`, add:

```csharp
        /// <summary>The log file, and how large it may get before it is cut to its tail.</summary>
        private const string FileName = "diag.log";
        private const int MaxBytes = 65536;

        /// <summary>What the run writes when it starts, and when it is closed on purpose.</summary>
        private const string RunStarted = "=== run started ";
        private const string RunEnded = "=== run ended ";

        /// <summary>The file writes, one at a time and in order (the ChatPreferences pattern).</summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        private static readonly object FileGate = new object();
        private static int _written;          // how many lines of History are already on disk
        private static long _lastFlushTicks;
        private static string _pendingTail = "";
```

`Flush()` appends `History[_written..]` to the file through `Guarded.RunGuardedAsync("Diag/Flush", Writes.RunAsync(...))`, returns without work when `_lastFlushTicks` is under one second ago and `force` is false, and advances `_written` only after a successful write. `WriteLinesAsync(string)` reads the file, appends, and cuts the result to its last `MaxBytes` characters when it grew past the ceiling, so the tail is always the newest lines. `BeginRun()` appends `RunStarted + DateTime.Now.ToString("s") + " ==="`; `EndRun()` appends the same with `RunEnded` and sets `_pendingTail = ""`. `AppendLine` calls `Flush()` when it is not forced, so a long run reaches the disk without a file open per line.

- [ ] **Step 6: Restore the previous run**

Add `RestorePreviousRunAsync() -> Task<bool>`: read the file, find the last `RunStarted` marker, and answer false when a `RunEnded` marker follows it. When it answers true, keep the text from that marker to the end of the file in `_pendingTail`, and append to the in-memory history one line — `"previous run did not end: " + _pendingTail.Length + " characters"` — followed by the tail itself, so `Diag.HistoryText()` carries it. Never throw: a missing file, an unreadable one and an empty one are all "no previous run".

- [ ] **Step 7: Wire it into `App`**

In `WhatsappApp/App.xaml.cs`:

- In `StartServicesOnce`, after `Loc.Prewarm();`, call `Diag.BeginRun();` and `Guarded.RunGuardedAsync("Diag/Restore", Diag.RestorePreviousRunAsync());`.
- In `OnSuspending` and in the app-closing path, call `Diag.EndRun();`.
- In `OnUnhandled` and `OnUnobservedTask`, put `Diag.Flush(true);` **immediately after** the existing `Diag.Failed(...)` call, before `#if DEBUG` and before `e.SetObserved()`, so the line and the tail reach the disk while the process still can.

- [ ] **Step 8: Add the guard to the chain and record the invariant**

Add `node tools/check-diagnostics.js` to the gate chain in `.agents/skills/test-the-app/SKILL.md` (both the command block and the guard table) and to any workflow file that runs the chain. In `.agents/skills/maintain-the-app/SKILL.md`, add one bullet: the diagnostics go to `diag.log` through a `SerialQueue` and the file is cut to its tail at 64 KB, because the in-memory history dies with the process and a crash with no line is the failure that cannot be fixed.

- [ ] **Step 9: Gate, build and commit**

Run the gate and the ARM build. Expected: every guard `OK`, including the new one reporting its four rule families, all tool tests passing, a package created.

```bash
git add tools/check-diagnostics.js tools/test/check-diagnostics.test.js tools/diagnostics-fixtures WhatsappApp/Services/Diag.cs WhatsappApp/App.xaml.cs .agents/skills/test-the-app/SKILL.md .agents/skills/maintain-the-app/SKILL.md
git commit -m "Keep the diagnostics on disk so a crash leaves its tail"
git push origin master
```

---

### Task 3: The next run sends the tail to the adapter

**Files:**
- Create: `WhatsappApp/Services/CrashReport.cs`
- Create: `tools/diagnostics-fixtures/crashreport-good.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj` (a `Compile Include`, appended at the end of the `Services` block)
- Modify: `WhatsappApp/App.xaml.cs` (`StartServicesOnce`)
- Modify: `tools/check-diagnostics.js` and `tools/test/check-diagnostics.test.js` (one new rule, one new input)
- Test: `tools/test/check-diagnostics.test.js`

**Interfaces:**
- Consumes: `Diag.PendingCrashTail` and `Diag.EndRun()` (Task 2), `CommunicationService.Instance.ConnectionEstablished` (`event EventHandler`), `CommunicationService.Instance.SendControlAsync(string command, string payload) -> Task`, `CommunicationService.Instance.IsConnected`.
- Produces: `CrashReport.Start()` — idempotent, subscribes once, sends at most one report per run, and never for a run that ended properly. `problemsFor` additionally accepts `crashReport` and applies rule E.

- [ ] **Step 1: Write the failing test**

In `tools/test/check-diagnostics.test.js`, add:

```js
test('il report del crash parte dal servizio e non dalla pagina', () => {
  const crashReport = fixture('crashreport-good.cs');
  const broken = { diag, app, chatPage, crashReport: crashReport.replace('SendControlAsync', '') };
  assert.match(problemsFor(broken).problems.join('\n'), /CrashReport/);
});
```

- [ ] **Step 2: Run it to watch it fail**

Run: `node --test tools/test/check-diagnostics.test.js`
Expected: FAIL — `problemsFor` does not read the fourth input yet, so no problem mentions `CrashReport`.

- [ ] **Step 3: Extend the guard with rule E and add the fixture**

Rule **E.** `crashReport` contains `SendControlAsync("diag"` and `Diag.PendingCrashTail` — the tail is sent by the service on the next connection, not by a page the user has to find. Accept the fourth input in `problemsFor` and create `tools/diagnostics-fixtures/crashreport-good.cs`. Re-run `node --test tools/test/check-diagnostics.test.js`; expected PASS, 6 tests.

- [ ] **Step 4: Write the service**

Create `WhatsappApp/Services/CrashReport.cs` along the lines of the other small services (`MemoryWatcher`, `ConnectionWatchdog`): a static `Start()`, a `_started` flag, every call guarded, `Diag.Failed("CrashReport/...", ex)` on failure. On `ConnectionEstablished`, and only when `Diag.PendingCrashTail` is not empty and nothing has been sent this run, call

```csharp
                CommunicationService.Instance.SendControlAsync("diag",
                    "previous run did not end\r\n" + Diag.PendingCrashTail);
```

through `Guarded.RunGuardedAsync("CrashReport/send", ...)` and log `Diag.Ok("crash report sent")`. A tail that cannot be sent (no socket yet, no pairing yet) is **not** dropped: it stays in `Diag.PendingCrashTail` for a later run, and only `Diag.EndRun()` clears it, which runs on a deliberate close and never on a crash.

- [ ] **Step 5: Register and start it**

Add `<Compile Include="Services\CrashReport.cs" />` to `WhatsappApp/WhatsappApp.csproj` at the **end** of the `Services` block, after the last entry, which is `<Compile Include="Services\ConversationView.cs" />`. The block is not kept sorted - the top of it is in its original order and every file added since has been appended - so appending is what matches the file, and it is the placement `check-project-files.js` is indifferent to either way.

In `StartServicesOnce`, after the `Diag.RestorePreviousRunAsync()` call from Task 2, add `CrashReport.Start();`.

- [ ] **Step 6: Gate, build and commit**

Run the gate and the ARM build. Expected: every guard `OK`, `check-project-files.js` accepting the new file, a package created.

```bash
git add WhatsappApp/Services/CrashReport.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/App.xaml.cs tools/check-diagnostics.js tools/test/check-diagnostics.test.js tools/diagnostics-fixtures
git commit -m "Send the previous crash to the adapter on the next connection"
git push origin master
```

---

### Task 4: The run says where it got to and how big it was

**Files:**
- Modify: `WhatsappApp/Services/FrameCodec.cs` (a new const)
- Modify: `WhatsappApp/Services/CommunicationService.cs` (the frame read loop, and the call into `DataService`)
- Modify: `WhatsappApp/Services/DataService.cs` (the `IsHistory` branch and the `history.done` case)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (after `_view.Bind`)
- Test: none in C#; the guards, the ARM build and the phone run are the test

**Interfaces:**
- Consumes: `FrameCodec.MaxFrameLength`, `Windows.System.MemoryManager.AppMemoryUsage` (`ulong`), `Diag.Ok(string)`, `DataService`'s existing `_historyArrived` set.
- Produces: the log lines `ok: large frame <bytes> bytes`, `ok: history done for <chatId>: <n> message(s), <b> bytes` and `ok: bound <n> rows, memory <m> MB`, which separate "it died in the burst", "it died in the bind" and "it died of memory".

- [ ] **Step 1: Name a frame big enough to matter**

In `FrameCodec.cs`, add `public const int LargeFrameBytes = 64 * 1024;`. In the `CommunicationService` read loop, immediately after `ReadFrameAsync` returns a payload and **before** it is decrypted, add:

```csharp
                if (payload.Length >= FrameCodec.LargeFrameBytes)
                {
                    Diag.Ok("large frame " + payload.Length + " bytes");
                }
```

The size is known before the cipher, which is the point: a frame that cannot be decrypted at all still has a size, and a size at the ceiling is the one case the reader refuses.

- [ ] **Step 2: Count the burst's bytes**

In `DataService`, add `private readonly Dictionary<string, int> _historyBytes` next to `_historyArrived`, add the frame's size to it in the `IsHistory` branch, and extend the existing line in the `history.done` case to

```csharp
                    Diag.Ok("history done for " + message.ChatId + ": "
                        + GetMessages(message.ChatId).Count + " message(s), " + bytes + " bytes");
```

The byte count is not visible in `DataService`, so add the parameter `int payloadBytes = 0` to the method that carries the frame to it (`OnNetworkMessageReceived`) and pass `payload.Length` from `CommunicationService`; every other caller compiles unchanged because the parameter is optional.

- [ ] **Step 3: Say what the bind cost**

In `ChatPage.BindAfterCacheAsync`, immediately after `_view.Bind(_messages);`, add:

```csharp
            Diag.Ok("bound " + _messages.Count + " rows, memory "
                + (Windows.System.MemoryManager.AppMemoryUsage / (1024UL * 1024UL)) + " MB");
```

This is the line that answers the question the phone run exists to answer: a bind of 50 rows with the memory near the budget of a 512 MB device names memory as the cause, and a bind of 0 or 1 names the burst.

- [ ] **Step 4: Gate, build and commit**

Run the gate and the ARM build. Expected: every guard `OK`, a package created.

```bash
git add WhatsappApp/Services/FrameCodec.cs WhatsappApp/Services/CommunicationService.cs WhatsappApp/Services/DataService.cs WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "Say in the log which step of opening a chat died and how big it was"
git push origin master
```

---

### Task 5: The phone run, and the fix it names

**Files:**
- Modify: `docs/superpowers/plans/2026-10-07-chat-open-crash-named-and-observable.md` (the closing section, the way the two previous crash plans record what execution changed)
- Test: the phone, which is not verifiable from the build machine

**Interfaces:**
- Consumes: everything above — `Diag.PendingCrashTail`, the `CrashReport` `diag` frame, the three size lines, the 20 s wait.
- Produces: the cause, named, and the shape of its fix.

- [ ] **Step 1: Deploy and reproduce**

Deploy the Debug ARM package to the unlocked device: `AppDeployCmd.exe /install <appx> /targetdevice:de`, then `/launch`. Note two things about that tool: `/updatelaunch` fails with `0x81030137` (not applicable for update), and the tool exits 0 even when it prints `Errore`, so read its output rather than its status. Open a chat and let it die.

- [ ] **Step 2: Read the tail from the container**

On the NAS: `docker logs whatsapp-for-wp8 --since 10m | grep -iE 'crash report|previous run|bound |history done|large frame|App/unhandled'`. The `diag` frame is written to the container log by the adapter's `diag` handler, so the tail arrives there on the run after the crash, with no debugger and nothing copied by hand.

- [ ] **Step 3: Name the cause and plan its fix**

Write the cause into this plan's closing section, from the evidence: `bound 50 rows, memory N MB` with N near the device budget is memory, and its fix is the layout and the decode budget; `history requested` with no `history done` is the wire; `large frame N bytes` at the ceiling is the frame contract; `DIAG App/unhandled: <type> 0x<code>` names the site directly. Whichever it is, its fix gets its own plan against the same files: this plan ends where the cause becomes known.

---

## Self-Review

**1. Spec coverage.** The report has two halves. "L'app crasha appena apro una chat" is Task 1 (the one defect the evidence already proves: a 2 s wait against a measured 15 s read), then Tasks 2-4 make any remaining crash observable, and Task 5 turns the captured tail into the cause. "Aggiungi linee di debug per vedere l'errore" is Tasks 2 and 4 literally: the log survives the crash, and the three lines with sizes and memory say which step died.

**2. Step scan.** Each step is one action with one checkable result: raise one constant, record it, gate; write the test, watch it fail, write the guard and its fixtures, watch it pass, add the sink, restore the file, wire the app, record it, gate; add a rule, watch it fail, add it, write the service, register it, gate; add a constant, add a counter, add a line, gate; deploy, read, record. No step carries a second decision.

**3. Type consistency.** `Diag.Flush(bool force)` has a no-argument `Flush()` overload; `Diag.RestorePreviousRunAsync()` returns `Task<bool>` and is consumed by `Guarded.RunGuardedAsync`; `Diag.PendingCrashTail` is a `string` property read by `CrashReport`; `FrameCodec.LargeFrameBytes` is an `int` const; the frame size passed into `DataService` is `int payloadBytes = 0`; `HistoryWaitMaxMilliseconds` stays `private const int`, so Task 1 changes a value and not a type.

**4. Review Focus.** Each of the five lines names its owner: the no-managed-exception death (Task 2 Step 5, appended as the run goes), the fresh install (Task 2 Step 6, never throw), the proper close (Task 2 Step 5's end marker, cleared in Task 3 Step 4), the run that never connected (Task 3 Step 4, the tail is kept), and the crash loop at the cap (Task 2 Step 5, the cut keeps the tail).

**5. Proportion.** The plan is shorter than the code it changes and prints only the fragments whose exact text is the decision. Tasks 2-4 exist because the evidence is missing, and they are the cheapest way to get it: the app already has a diagnostics page, a `diag` frame and an `App/unhandled` handler, and this plan gives that machinery the one thing it lacks, which is survival.

**6. Ordering.** Task 1 must be first, and the first draft of this plan had it last: the guard's rule D fails while `HistoryWaitMaxMilliseconds` is 2000, so every task before it would have ended on a red gate. The proven fix ships before the guard that pins it.

---

## What execution changed about this plan

- **The crash signal is a marker file, not a missing end marker.** The plan said a
  run whose `=== run ended ===` line is absent is the crash. That is wrong on this
  platform: `OnSuspending` fires on every normal end of a run - suspend and close
  alike - so "the log has no end marker" is the normal case too, and a crash after a
  resume would be missed entirely, because the end marker of the previous segment is
  already in the file. `Diag` now keeps a `diag-run.marker` file while the run is
  alive, removes it in `EndRun`, and puts it back in `MarkAlive` on resume: a marker
  still there at the next startup is the crash. The end marker stayed, for the human
  reading the timeline.
- **`BeginRun()` and `RestorePreviousRunAsync()` became one method,
  `StartRunAsync()`.** Two entry points meant the new run could write its own marker
  before the old one was read, and then report itself as a crash. Read and write are
  one queued piece now, so that order cannot invert.
- **The frame size rides on the message, not through the event.** The plan passed an
  optional `int payloadBytes` into `OnNetworkMessageReceived`. That method is an
  event *subscriber* (`MessageReceived`), so the size has to come from the event, and
  the handler runs later on the dispatcher - by which time the reader has read the
  next frame, and a field on the service would be describing that one. `ChatMessage`
  gained a non-`DataMember` `WireBytes`, set by the reader as the frame arrives.
- **Rule B was too weak when first written.** It searched from
  `Diag.Failed("App/unhandled"` to the end of the file, so deleting the flush from
  the unhandled handler still passed - the flush of the *next* handler satisfied it.
  The guard's own test caught it; the rule now slices the handler's body, up to the
  next `private `.
- **The guard's first message produced a failing test and the message was fixed, not
  the assertion.** Rule C's text said the marker was never deleted without naming
  `DeleteAsync`, so a reader was told the symptom and not the call. Naming the call is
  also what every other guard does.
- **The fire-and-forget call escaped its own guard once.** The first version of
  `Guarded.RunGuardedAsync("Diag/StartRun", ...)` was written outside the
  `#pragma warning disable 4014` region, which produced `CS4014` and kept the call
  out of `check-fire-and-forget.js` - the guard scans those regions. Wrapped, and the
  build went back to zero warnings.
- **The csproj entry is appended, not sorted.** The plan's first draft said to insert
  `Services\CrashReport.cs` alphabetically after `Services\ConnectionWatchdog.cs`. The
  `Services` block is not alphabetical: its first entries are in original order and
  everything added since has been appended. The entry goes at the end.
- **`.agents/skills` matches a `.gitignore` pattern, and the files are tracked.**
  `git add` warns about the directory and stages the file anyway, so the skill edits
  do reach the repository. Verified with `git show --stat`, not assumed from the
  warning.
- **The four tool-test failures are pre-existing.** `tools/test/download.test.js`
  fails on `extractLargestEntry` and `ensureArchive` on this machine because `zip` is
  not installed; 7 pass and 4 fail with this work stashed, exactly as with it. The
  suite is 98 tests now: 93 pass, those 4 fail, 1 skipped.

## Status: Tasks 1 to 4 are done and pushed; Task 5 is blocked on the device

The four commits are `da5c66d`, `fa504c7`, `85d7030` and `889e861` on `master`. Each
was green on the guards that existed at the time (eleven before Task 2 added the
twelfth), and each built for ARM with zero warnings and zero errors. The package with
all four is installed on the Lumia (`AppDeployCmd /install` answered `Completato`).

`/launch` is refused with `Verificare che lo schermo del dispositivo sia sbloccato`:
the **phone screen is locked**, and the tool cannot start an app on a locked device.
So the one thing this plan exists to produce - the log of a chat being opened by the
new build - has not been read yet. Unlock the phone, keep the screen awake, open a
chat, and the next launch reports either the crash tail as a `diag` frame in the
container log or the three size lines that say the chat opened cleanly.
