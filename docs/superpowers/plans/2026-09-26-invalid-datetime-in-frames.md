# Invalid DateTime in Every Frame Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the app throwing away every frame it receives, by making the `Timestamp` field carry a value the phone can actually read and by making the phone tolerant when it cannot.

**Architecture:** Two independent fixes that reinforce each other. The adapter writes `Timestamp` with two literal backslashes (`\\/Date(ms)\\/`) where Microsoft's format is `/Date(ms)/`; the phone's deserializer then fails the whole object with `SerializationException 0x8013150C` (`String was not recognized as a valid DateTime`) and the message is dropped. The adapter is fixed to emit the correct value (with tests, because Node is the only place here with a test host), and the app's model stops typing that field as a `DateTime`: it keeps the incoming string and interprets it leniently, so no single field can ever cost a whole message again.

**Tech Stack:** Node.js adapter (`WhatsappBridge`, `node:test`, zero runtime dependencies), C# 5 on Windows Phone 8.1 (`DataContractJsonSerializer`, `Windows.Storage.Streams`).

## Global Constraints

- **C# 5 only.** No `$"..."`, `?.`, expression-bodied members, `out var`, `is T x`, `nameof`, `_ =`, auto-property initializers (`X Y { get; set; } = v;`). Field initializers are fine.
- **There is no C# test host in this repo.** The adapter change is TDD against `node:test`; the app change is verified by the guards, the WP8.1 build gate on the Parallels VM, and the `DIAG` lines on the device. Each app task states the exact command and the exact line it expects to disappear.
- **The frame contract is unchanged:** `[4-byte little-endian length][payload]`, ceiling 8 MiB, payload `[tag][IV][cbc][hmac]`.
- **The JSON field names must keep matching `ChatMessage`'s `[DataMember]`s exactly** (`DataContractJsonSerializer` is case-sensitive): `Id`, `Text`, `SenderId`, `SenderName`, `ChatId`, `Timestamp`, `Status`, `Type`, `IsIncoming`, `MediaData`, `MediaMimeType`, `MediaFileName`, `Command`, `State`, `PairCode`, `QrImageData`, `QrDuration`, `AccountJid`.
- **Everything printed at run time is English** (adapter logs and errors, app `Diag`/`SelfCheck` lines). Source comments and adapter test names stay Italian.
- **Never a silent `catch`:** a value that cannot be read goes through `Diag.Failed("<call site>", ex)`.
- **No emoji in any `.md`** (U+26A0 only).
- **Docs are pairs:** `README.md`/`README.it.md`, same heading depth and order, both in the same commit.
- **Guards that must stay green:** `node tools/check-csharp5.js`, `node tools/check-icons.js`, `node tools/check-resw.js --strict` (95 keys), `node tools/check-docs.js`, `node tools/check-framing.js`, `node tools/qr-term.js --self-test`, `cd WhatsappBridge && npm test` (49 before Task 1), `node --test "tools/test/**/*.test.js"` (17).
- **Build gate (Parallels):** `COPIA=0`, `Errori: 0`, `Your package has been successfully created`.
- **Commits:** English, `type: short imperative`, one per task, `git push origin master`.

---

## The evidence

```
DIAG ChatMessage.FromJson: SerializationException 0x8013150C There was an error deserializing
the object of type WhatsappApp.Models.ChatMessage. String was not recognized as a valid DateTime.
```

`0x8013150C` is `COR_E_SERIALIZATION`, and the inner failure is the `DateTime` parser. It repeats
(a first-chance exception per frame, `Diag` printing the line once), and every message is lost:
`ChatMessage.FromJson` returns `null`, `DispatchMessage` returns, nothing else happens.

`formatDateForWp8` in `WhatsappBridge/message-format.js` is the source:

```js
return `\\\\/Date(${epoch})\\\\/`;
```

In a template literal each `\\` is one backslash, so the value is `\\/Date(123)\\/` - four literal
backslashes. `JSON.stringify` doubles them again in the text, and the phone reads back a string
that is not `/Date(123)/`. **The surrounding `\/` in Microsoft's wire format is a JSON escape, not
part of the value:** the correct JavaScript value is `/Date(123)/`, and any JSON writer produces
the escaped text by itself.

The existing test enshrines the bug:

```js
assert.strictEqual(formatDateForWp8(new Date(0)), '\\\\/Date(0)\\\\/');
```

Two secondary faults live in the same function and must not survive the fix:

- `new Date(value)` on a format `Date.parse` does not know gives an *Invalid Date*, and
  `d.getTime()` gives `NaN`, so the frame would carry `/Date(NaN)/`. `mapWebhookMessage` passes
  `p.timestamp` straight into it.
- GOWA sometimes reports a timestamp in seconds (`1700000000`), not milliseconds, and
  `new Date("1700000000")` is not that either.

## File structure

| File | Responsibility | Change |
| --- | --- | --- |
| `WhatsappBridge/message-format.js` | the JSON the phone must be able to read | Task 1: `formatDateForWp8` writes `/Date(ms)/` and a helper normalises seconds, ISO strings, already-formatted values and impossible ones |
| `WhatsappBridge/test/message-format.test.js` | the adapter's contract with the model | Task 1: the old assertion replaced, five tests added |
| `WhatsappApp/Models/ChatMessage.cs` | the wire shape and the app's view of it | Task 2: `Timestamp` becomes a tolerant pair - a `string` `[DataMember]` on the wire, a `DateTime` property for the app |
| `README.md`, `README.it.md`, `.agents/skills/maintain-the-app/SKILL.md` | the rule and the reason | Task 3 |
| `docs/superpowers/plans/2026-09-26-invalid-datetime-in-frames.md` | this plan | Task 3 |

---

### Task 1: The adapter writes a timestamp the phone can read

**Files:**
- Modify: `WhatsappBridge/message-format.js`
- Modify: `WhatsappBridge/test/message-format.test.js`

**Interfaces:**
- Consumes: nothing new.
- Produces: `formatDateForWp8(value) : string` returning `/Date(<integer>)/` for every input - a `Date`, a number, a numeric string, an ISO string, an already-formatted value, `undefined`, or garbage. `buildChatMessage` and `mapWebhookMessage` keep their signatures; Task 2's parser accepts exactly this shape.

- [ ] **Step 1: Replace the assertion that pins the bug, and add the five new tests**

In `WhatsappBridge/test/message-format.test.js`, replace the whole existing test

```js
test('formatDateForWp8 usa il formato Microsoft /Date(ms)/', () => {
  assert.strictEqual(formatDateForWp8(new Date(0)), '\\\\/Date(0)\\\\/');
  assert.strictEqual(formatDateForWp8(new Date(1700000000000)), '\\\\/Date(1700000000000)\\\\/');
});
```

with

```js
test('formatDateForWp8 produce il valore /Date(ms)/, senza backslash', () => {
  assert.strictEqual(formatDateForWp8(new Date(0)), '/Date(0)/');
  assert.strictEqual(formatDateForWp8(new Date(1700000000000)), '/Date(1700000000000)/');
});

test('il Timestamp sopravvive a JSON.stringify e torna come lo legge il telefono', () => {
  const wire = JSON.parse(JSON.stringify({ Timestamp: formatDateForWp8(new Date(1700000000000)) }));
  assert.strictEqual(wire.Timestamp, '/Date(1700000000000)/');
  assert.ok(!wire.Timestamp.includes('\\'), 'nessun backslash nel valore: ' + wire.Timestamp);
});

test('formatDateForWp8 non produce mai una data impossibile', () => {
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8('non una data')));
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8(new Date('x'))));
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8(NaN)));
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8(undefined)));
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8('')));
});

test('i timestamp di GOWA in secondi diventano millisecondi', () => {
  assert.strictEqual(formatDateForWp8(1700000000), '/Date(1700000000000)/');
  assert.strictEqual(formatDateForWp8('1700000000'), '/Date(1700000000000)/');
  assert.strictEqual(formatDateForWp8(1700000000000), '/Date(1700000000000)/');
  assert.strictEqual(formatDateForWp8('2023-11-14T22:13:20.000Z'), '/Date(1700000000000)/');
  assert.strictEqual(formatDateForWp8('/Date(1700000000000)/'), '/Date(1700000000000)/');
});

test('buildChatMessage manda un Timestamp leggibile dal telefono', () => {
  const m = buildChatMessage({ command: 'state', state: 'disconnected' });
  assert.ok(/^\/Date\(\d+\)\/$/.test(m.Timestamp), 'Timestamp: ' + m.Timestamp);
});

test('mapWebhookMessage non propaga un timestamp impossibile', () => {
  const fields = mapWebhookMessage({
    id: 'x', from: '39@s.whatsapp.net', chat_id: '39@s.whatsapp.net',
    body: 'ciao', timestamp: 'non una data'
  });
  assert.ok(/^\/Date\(\d+\)\/$/.test(formatDateForWp8(fields.timestamp)),
    'Timestamp: ' + fields.timestamp);
});
```

- [ ] **Step 2: Run them and watch them fail**

```bash
cd WhatsappBridge && node --test test/message-format.test.js
```

Expected: FAIL on the first test with `'\\/Date(0)\\/' !== '/Date(0)/'` (the actual value has the two stray backslashes), and FAIL on the JSON round-trip test with `nessun backslash nel valore`. The two "impossibile" tests fail on `NaN`. The seconds test fails on the `1700000000000` cases. The other tests pass.

- [ ] **Step 3: Implement it**

In `WhatsappBridge/message-format.js`, replace the whole `formatDateForWp8` function with:

```js
// Il valore che il campo Timestamp deve avere *dopo* JSON.parse: Microsoft scrive
// /Date(ms)/ e DataContractJsonSerializer se lo aspetta cosi'. Il \/ che si vede
// nel testo JSON e' un escape del lettore, non parte del valore: metterlo nel
// valore lo raddoppia e il telefono risponde "String was not recognized as a
// valid DateTime" (0x8013150C), buttando via l'intero frame.
const WP8_DATE = /^\/Date\((-?\d+)\)\/$/;

/**
 * Millisecondi dall'epoch, da qualunque cosa arrivi nel campo timestamp. Non
 * lancia e non restituisce mai NaN: un timestamp storto e' un timestamp
 * in meno, non un messaggio in meno.
 */
function epochMillis(value) {
  if (value === undefined || value === null || value === '') return Date.now();

  if (value instanceof Date) {
    const time = value.getTime();
    return Number.isFinite(time) ? time : Date.now();
  }

  if (typeof value === 'number') {
    if (!Number.isFinite(value)) return Date.now();
    // GOWA a volte manda i secondi: 1.7e9 invece di 1.7e12.
    return Math.round(Math.abs(value) < 1e12 ? value * 1000 : value);
  }

  // I backslash sono escape del lettore JSON: qui non servono.
  const text = String(value).replace(/\\/g, '').trim();

  // Gia' nel formato Microsoft (un valore rispedito indietro, per esempio).
  const microsoft = WP8_DATE.exec(text);
  if (microsoft) return Number(microsoft[1]);

  if (/^-?\d+$/.test(text)) {
    const n = Number(text);
    return Math.round(Math.abs(n) < 1e12 ? n * 1000 : n);
  }

  const parsed = Date.parse(text);
  return Number.isNaN(parsed) ? Date.now() : parsed;
}

function formatDateForWp8(value) {
  return `/Date(${epochMillis(value)})/`;
}
```

`WP8_DATE` and `epochMillis` are module-private; only `formatDateForWp8` is exported. Update the export list only if you introduced a new exported name (you should not).

- [ ] **Step 4: Run them and watch them pass**

```bash
cd WhatsappBridge && node --test test/message-format.test.js
```

Expected: PASS, 9 tests (3 previous + 6 in this task's two groups, one of which replaced the old assertion).

- [ ] **Step 5: Run the whole adapter suite**

```bash
cd WhatsappBridge && npm test
```

Expected: `tests 54`, `pass 54`, `fail 0` (49 before, 5 added).

- [ ] **Step 6: Commit**

```bash
git add WhatsappBridge/message-format.js WhatsappBridge/test/message-format.test.js
git commit -m "fix: write the frame timestamp as the value the phone expects"
```

---

### Task 2: The app cannot lose a message over one bad field

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs`

**Interfaces:**
- Consumes: the `/Date(ms)/` value Task 1 produces.
- Produces: `[DataMember(Name = "Timestamp")] public string TimestampWire { get; set; }` and `public DateTime Timestamp { get; set; }` (no longer a `[DataMember]`). Nothing in the app reads `Timestamp` today, and the four writers (`ChatPage.xaml.cs:132`, `ChatPage.xaml.cs:156`, `CommunicationService.cs` handshake and `SendControlAsync`) keep compiling because the `DateTime` property keeps its name and its setter.

Why: after Task 1 the adapter is correct, but the app must not depend on that. A `[DataMember]` typed `DateTime` turns *any* unexpected string from the server into a dead frame - that is the whole bug - and GOWA's own timestamp format is not something this app controls. A string field that is parsed leniently makes the failure local.

- [ ] **Step 1: Add the `using` for `DateTimeStyles`**

At the top of `WhatsappApp/Models/ChatMessage.cs`, after `using System.IO;`:

```csharp
using System.Globalization;
```

(The file already imports `System`, `System.Runtime.Serialization`, `System.Runtime.Serialization.Json`, `System.IO`, `System.Text`, `System.Threading.Tasks`.)

- [ ] **Step 2: Initialise the backing field**

Replace

```csharp
        private DateTime _timestamp;
```

with

```csharp
        private DateTime _timestamp = DateTime.Now;

        // Il valore del campo Timestamp cosi' come e' arrivato. E' una stringa e
        // non un DateTime perche' la deserializzazione non deve poter fallire:
        // una data che il telefono non riconosce faceva cadere l'intero frame
        // ("String was not recognized as a valid DateTime", 0x8013150C) e il
        // messaggio spariva senza che l'utente vedesse niente.
        private string _timestampWire;
```

- [ ] **Step 3: Replace the `Timestamp` property with the pair**

Replace

```csharp
        [DataMember]
        public DateTime Timestamp
        {
            get { return _timestamp; }
            set
            {
                _timestamp = value;
                FormattedTime = FormatTime(value);
                OnPropertyChanged();
            }
        }
```

with

```csharp
        /// <summary>
        /// Il campo che viaggia sul filo, cosi' com'e'. Quando il messaggio e'
        /// stato costruito qui (e non letto da un frame) e' vuoto, e il getter
        /// lo scrive dal DateTime: e' l'unica sorgente della forma /Date(ms)/.
        /// </summary>
        [DataMember(Name = "Timestamp")]
        public string TimestampWire
        {
            get { return _timestampWire == null ? FormatWire(_timestamp) : _timestampWire; }
            set
            {
                _timestampWire = value;
                _timestamp = ParseWire(value);
                FormattedTime = FormatTime(_timestamp);
                OnPropertyChanged("TimestampWire");
                OnPropertyChanged("FormattedTime");
            }
        }

        /// <summary>La stessa data come la usa l'app. Non e' un [DataMember]: sul filo va la stringa.</summary>
        public DateTime Timestamp
        {
            get { return _timestamp; }
            set
            {
                _timestamp = value;
                _timestampWire = null;   // si riscrive dal DateTime alla prossima serializzazione
                FormattedTime = FormatTime(value);
                OnPropertyChanged("Timestamp");
                OnPropertyChanged("FormattedTime");
            }
        }
```

- [ ] **Step 4: Add the parser and the writer**

Add these three members next to `FormatTime` (they are `private`, `static`, and used by the properties above):

```csharp
        /// <summary>Il primo istante dell'epoch, in UTC: la base del formato Microsoft.</summary>
        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Millisecondi dall'epoch, nella forma /Date(ms)/ che l'altro capo legge.</summary>
        private static string FormatWire(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            long milliseconds = (long)(utc - Epoch).TotalMilliseconds;
            return "/Date(" + milliseconds.ToString(CultureInfo.InvariantCulture) + ")/";
        }

        /// <summary>
        /// Interpreta il campo Timestamp di un frame.
        ///
        /// Accetta /Date(ms)/ con un numero qualsiasi di backslash davanti agli
        /// slash - un adattatore piu' vecchio li raddoppiava, e quei backslash
        /// sono escape del lettore JSON, non parte del valore -, una data ISO
        /// 8601 con o senza fuso, e i millisecondi nudi. Qualunque altra cosa
        /// diventa l'ora attuale, con la riga di Diag che dice cosa non andava:
        /// un campo illeggibile non deve far sparire il messaggio.
        /// </summary>
        private static DateTime ParseWire(string value)
        {
            if (string.IsNullOrEmpty(value)) return DateTime.Now;

            string text = value.Replace("\\", "").Trim();

            const string Prefix = "/Date(";
            if (text.StartsWith(Prefix, StringComparison.Ordinal)
                && text.EndsWith(")/", StringComparison.Ordinal))
            {
                string inner = text.Substring(Prefix.Length, text.Length - Prefix.Length - 2);
                long fromWire;
                if (long.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out fromWire))
                {
                    // FormatTime si aspetta una data locale: il filo porta UTC.
                    return Epoch.AddMilliseconds(fromWire).ToLocalTime();
                }
            }

            long raw;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
            {
                return Epoch.AddMilliseconds(raw).ToLocalTime();
            }

            DateTime parsed;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed.ToLocalTime();
            }

            Diag.Failed("ChatMessage/Timestamp",
                new FormatException("data non riconosciuta: " + text));
            return DateTime.Now;
        }
```

- [ ] **Step 5: Check the guards**

```bash
node tools/check-csharp5.js
node tools/check-framing.js
node tools/check-resw.js --strict
```

Expected: `OK: 27 C# file(s) are C# 5 compatible.`, the framing `OK:` line, and `OK: 95 key(s) in en-US and it-IT, ...`.

- [ ] **Step 6: Prove the parser on the four shapes the wire can carry**

There is no C# test host, so this is the closest thing to one: the adapter's own test can be read as the specification of the values, and the device run in Task 3 Step 4 is where the parser is actually exercised. Before that, check by eye that all four shapes are handled by the code above:

| Wire value | Expected result |
| --- | --- |
| `/Date(1700000000000)/` | `Epoch.AddMilliseconds(...).ToLocalTime()` |
| `\\/Date(1700000000000)\\/` (the old adapter) | the same: the backslashes are stripped first |
| `2023-11-14T22:13:20.000Z` | `DateTime.TryParse` with `AssumeUniversal`, then local |
| `1700000000000` | the bare-milliseconds branch |
| `non una data`, `""`, `null` | `DateTime.Now`, with one `DIAG ChatMessage/Timestamp` line |

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Models/ChatMessage.cs
git commit -m "fix: read the frame timestamp as a string, so a bad field cannot kill a message"
```

---

### Task 3: Write it down, then prove it on the phone

**Files:**
- Modify: `README.md`, `README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-26-invalid-datetime-in-frames.md` (the record)

- [ ] **Step 1: Say it in both project READMEs**

In `README.md`, in the `## Protocol` section, right after the paragraph that starts `A frame length is never trusted:`, add:

```markdown
`Timestamp` is the one field whose *type* on the wire is worth spelling out: it carries
`/Date(<milliseconds since 1970, UTC>)/`, and no backslashes - the `\/` seen in JSON text is the
reader's escape, not part of the value. Writing the value with backslashes (an adapter bug fixed
in these commits) made the phone throw the whole frame away with
`SerializationException 0x8013150C`, "String was not recognized as a valid DateTime". The app
reads that field as a string and interprets it leniently, so a timestamp it cannot parse costs
the timestamp, not the message.
```

In `README.it.md`, in the `## Protocollo` section, right after the paragraph that starts `Una lunghezza di frame non viene mai creduta`, add the matching text:

```markdown
`Timestamp` e' l'unico campo di cui vale la pena dire il *tipo* sul filo: porta
`/Date(<millisecondi dal 1970, UTC>)/`, e nessun backslash - il `\/` che si vede nel testo JSON e'
l'escape del lettore, non parte del valore. Scrivere il valore con i backslash (un difetto
dell'adapter, corretto in questi commit) faceva buttare via l'intero frame al telefono con
`SerializationException 0x8013150C`, "String was not recognized as a valid DateTime". L'app legge
quel campo come stringa e lo interpreta con tolleranza, quindi un timestamp che non riesce a
leggere costa il timestamp, non il messaggio.
```

- [ ] **Step 2: Add the gotcha to the maintain skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in `## Known gotchas`, add:

```markdown
- **A `[DataMember]` with a strict type is a whole-frame failure waiting to happen.** One
  unexpected string in one field makes `DataContractJsonSerializer` throw
  `SerializationException 0x8013150C` for the entire object, and `ChatMessage.FromJson` returns
  `null`: the message disappears with no visible cause. `Timestamp` is the field that bit us
  (the adapter wrote `\\/Date(ms)\\/` instead of `/Date(ms)/`), so it is a `string` on the wire
  and a leniently parsed `DateTime` in the app. When adding a field, ask what the deserializer
  does with a value it did not expect.
```

- [ ] **Step 3: Add the check to the test skill**

In `.agents/skills/test-the-app/SKILL.md`, in `## On-device checklist`, extend the item about the log so the timestamp is covered:

```markdown
11. A frame that the phone cannot read shows up as `DIAG ChatMessage.FromJson: SerializationException`.
    After a change to `ChatMessage` or to `message-format.js`, that line must not appear: if it
    does, the message it named was dropped, and the field it names is the one to look at.
```

- [ ] **Step 4: Record what execution changed**

Append to this plan a `## What execution changed about the plan` section with whatever the run
actually forced: the real adapter test count, any C# the guards rejected, and the outcome of the
build gate.

- [ ] **Step 5: Run the whole fast gate**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict \
  && node tools/check-docs.js && node tools/check-framing.js && node tools/qr-term.js --self-test
cd WhatsappBridge && npm test && cd ..
node --test "tools/test/**/*.test.js"
```

Expected: six `OK:` lines; `pass 54`; `pass 17`.

- [ ] **Step 6: The build gate (user, Windows machine)**

```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`, then `Errori: 0` and `Your package has been successfully created`.

- [ ] **Step 7: The phone**

Deploy with the debugger attached and check:

1. **No** `DIAG ChatMessage.FromJson`. This is the whole point of the plan: that line meant every
   incoming frame was being thrown away.
2. No `DIAG ChatMessage/Timestamp` either: it would mean the adapter is still sending something
   the app has to guess at.
3. The `state` control frame arrives, so the settings page stops saying "not connected to
   WhatsApp" when the session is actually linked, and the QR flow works as in the previous plan.
4. Send a message from the phone and receive one: both bubbles show the right time, which is what
   `FormattedTime` is for.

---

## Self-Review

**1. Spec coverage**

| Evidence | Task |
| --- | --- |
| `SerializationException 0x8013150C`, "not a valid DateTime", repeated per frame | Task 1 removes the cause (the double backslashes), Task 2 makes the app survive any future one |
| The bug is pinned by a test that asserts the wrong value | Task 1 Step 1 replaces that assertion and Step 2 requires it to fail first |
| `NaN` from an unparseable GOWA timestamp, and seconds-versus-milliseconds | Task 1 Step 3 (`epochMillis`) with tests in Step 1 |
| The next person must not reintroduce it | Task 3 Step 2 (maintain skill) and Step 3 (test skill) |

**2. Placeholder scan**

No `TBD`, no "handle edge cases", no "similar to Task 1". Every code step carries the complete
text to write, including the full replacement of the two properties and all three new members.
Every verification step names its command and its expected output, and Task 1 Step 2 requires the
new tests to fail before the implementation lands.

**3. Type consistency**

- `formatDateForWp8(value) : string` keeps its name and its single argument in Task 1 and is used
  by `buildChatMessage` and `mapWebhookMessage`, which are unchanged.
- `epochMillis` is module-private, declared and used inside Task 1; it is not exported, and the
  module's `module.exports` list is unchanged.
- `WP8_DATE` is used once, in `epochMillis`.
- Task 2 introduces `TimestampWire` (string, `[DataMember(Name = "Timestamp")]`), `Timestamp`
  (`DateTime`, no `[DataMember]`), `_timestampWire`, `Epoch`, `FormatWire(DateTime) : string` and
  `ParseWire(string) : DateTime`. They are declared once and used only inside that file; no other
  file reads `Timestamp` (verified: only four assignments exist, in `ChatPage.xaml.cs` and
  `CommunicationService.cs`).
- `FormatTime(DateTime)` already handles a local `DateTime` (its `Utc` branch converts); `ParseWire`
  returns local, so `FormattedTime` is correct without touching `FormatTime`.
- Test arithmetic: 49 adapter tests, one assertion replaced and five tests added, so 54 - asserted
  in Task 1 Step 5 and Task 3 Step 5.

---

## What execution changed about the plan

- **The plan's "9 tests" for Task 1 was a guess, and wrong.** `message-format.test.js` already
  held **10** tests, not 3: the replaced assertion plus `displayNameForJid`, the two
  `buildChatMessage` tests, and six `mapWebhookMessage` tests. Replacing one and adding five gives
  **15** tests in that file, which is what ran. The adapter total is still **54** (49 + 5) as
  predicted, because only the net test count matters for `npm test`.
- **The old value had *one* backslash per slash, not two.** Task 1's evidence text said the
  adapter emitted `\\/Date(ms)\\/`; the file actually carried `\\/Date(ms)\\/` in source, which is
  the single-backslash value `\/Date(ms)\/`. `DataContractJsonSerializer` rejects it just the same,
  and the fix is unchanged, but the diagnosis was off by one level of escaping.
- **Task 3 Step 3's "item 11" already existed.** `test-the-app/SKILL.md` already had an item 11
  (the fresh-install / QR-on-open check added by the previous plan), so the new check was added as
  **item 12** rather than renaming or overwriting the existing one.
- **The old test file's `str_replace` kept failing on over-escaped backslashes.** The edit was
  applied by rewriting the whole file with the file-writer rather than by a targeted replace; the
  result is identical.
- **Guards after Task 2:** `OK: 27 C# file(s) are C# 5 compatible.`, the framing `OK:` line, and
  `OK: 95 key(s) in en-US and it-IT, ...`. The full fast gate (Task 3 Step 5) is six `OK:` lines,
  `pass 54` for the adapter and `pass 17` for `tools/test`.
- **The build gate passed:** `COPIA=0`, `Errori: 0`, `Your package has been successfully created`
  (`WhatsappApp_1.0.1.0_x86_Debug.appxbundle`).
- **The on-device pass (Task 3 Step 7) is still the user's**, as there is no WP8.1 runtime on this
  machine.
