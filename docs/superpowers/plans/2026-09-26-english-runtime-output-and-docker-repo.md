# English Runtime Output and a Docker Home for the Server Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Everything the server and the app print at run time becomes English, and the server gets its own public repository that publishes a single-container Docker image (GOWA plus the adapter) while still running under plain Node.js.

**Architecture:** The adapter (`WhatsappBridge/`) stays the source of truth here, next to the protocol, its tests and the guards. A new public repository, `vincenzosco/docker-whatsappforwp`, holds the deployment: a copy of the adapter, a `Dockerfile` that downloads the pinned GOWA release and verifies its SHA-256, a `docker-compose.yaml` for a NAS or a PC, an entrypoint that runs GOWA and the adapter in one container, a `sync` tool that copies the adapter from this repo and records the source commit, and a GitHub Actions workflow that verifies the copy is in sync and publishes the image to GHCR. This repository links to it from both READMEs.

**Tech Stack:** Node.js 22 (the adapter has zero runtime dependencies), Go binary GOWA v9.5.0 from its official release archives, Docker with BuildKit, Docker Compose v2, GitHub Actions, GHCR.

## Global Constraints

- **C# 5 only** in `WhatsappApp/`. No `$"..."`, `?.`, expression-bodied members, `out var`, `is T x`, `nameof`, `_ =`.
- **C# has no test host here.** App changes are verified by the guards, the WP8.1 build gate on the Parallels VM and the `DIAG` lines on the device.
- **Language policy (the point of this plan):** every string that is *printed at run time* is English - adapter logs, adapter errors, launcher output, the app's `DIAG` lines and the messages inside the exceptions that reach them, and the launcher's help text. **Source comments and the adapter's test names stay as they are** (Italian). Localized UI strings in `Strings/it-IT/Resources.resw` stay Italian: that is translation, not debug output.
- **No emoji in any `.md`** (U+26A0 is the only exception), in either repository.
- **Docs are written in pairs** in this repository (`README.md`/`README.it.md`, `WhatsappBridge/README.md`/`WhatsappBridge/README.it.md`): same heading depth and order, both languages, same commit. The new repository gets the same pairing.
- **The adapter keeps zero runtime dependencies.** `package.json` must not gain a `dependencies` entry.
- **The frame contract**, unchanged by this plan: `[4-byte little-endian length][payload]`, ceiling 8 MiB on both sides.
- **Guards that must stay green here:** `node tools/check-csharp5.js`, `node tools/check-icons.js`, `node tools/check-resw.js --strict`, `node tools/check-docs.js`, `node tools/check-framing.js`, `node tools/qr-term.js --self-test`, `cd WhatsappBridge && npm test` (49), `node --test "tools/test/**/*.test.js"` (17).
- **Build gate (Parallels, user-run):** `COPIA=0`, `Errori: 0`, `Your package has been successfully created`.
- **Docker is not installed on this Mac.** The image cannot be built or run here; the GitHub Actions run is the real verification (Task B6), and `docker compose up` on the NAS is the user's.
- **Commits:** English, `type: short imperative`, one per task, `git push` after each. The new repository is pushed with `gh` and git over HTTPS.

---

## Part A - English runtime output (this repository)

The rule in one line: **if a human reads it in a terminal or a debugger log, it is English.**

| File | Italian text today | English |
| --- | --- | --- |
| `CommunicationService.cs` | `nessun CoreDispatcher disponibile` | `no CoreDispatcher available` |
| `CommunicationService.cs` | `indirizzo o porta non valida: ` | `invalid address or port: ` |
| `CommunicationService.cs` | `nessuna risposta da {0}:{1} entro {2} ms` | `no answer from {0}:{1} within {2} ms` |
| `CommunicationService.cs` | `lunghezza frame fuori intervallo: ` | `frame length out of range: ` |
| `AutoConnector.cs` | `nessuna risposta da ` | `no answer from ` |
| `Diag.cs` | `(nessuna eccezione)` | `(no exception)` |
| `SelfCheck.cs` | `schermo sempre acceso (DisplayRequest)` | `screen kept awake (DisplayRequest)` |
| `SelfCheck.cs` | `beacon UDP in ascolto sulla porta 8587` | `UDP discovery beacon listening on port 8587` |
| `SelfCheck.cs` | `la porta UDP non si e' aperta` | `the UDP port did not open` |
| `SelfCheck.cs` | `il giro di andata e ritorno non torna` | `the encrypt/decrypt round trip does not return the same bytes` |
| `SelfCheck.cs` | `il vettore di prova non torna: ` | `the known-answer vector does not match: ` |
| `SelfCheck.cs` (doc comment example) | the three quoted `DIAG ok:` lines | the three English lines actually printed |
| `server.js` | `WhatsApp connesso come ${...}` | `WhatsApp connected as ${...}` |
| `server.js` | `'sconosciuto'` | `'unknown'` |
| `server.js` | `Nuovo QR code inviato all'app` | `new QR code sent to the app` |
| `server.js` | `Login QR fallito: ` | `QR login failed: ` |
| `server.js` | `Codice di abbinamento inviato all'app per ${phone}` | `pairing code sent to the app for ${phone}` |
| `server.js` | `Login con codice fallito: ` | `code login failed: ` |
| `server.js` | `Sincronizzati ${n} contatti` | `synced ${n} contacts` |
| `server.js` | `Sincronizzazione contatti fallita: ` | `contact sync failed: ` |
| `server.js` | `Inviato a ${id}: ` | `sent to ${id}: ` |
| `server.js` | `Invio a ${id} fallito: ` | `send to ${id} failed: ` |
| `server.js` | `Invio ${n} messaggi in coda...` | `flushing ${n} queued message(s)...` |
| `server.js` | `Messaggio WP8 senza contenuto, ignorato` | `empty message from the app, ignored` |
| `server.js` | `WhatsApp non pronto: messaggio messo in coda` | `WhatsApp not ready: message queued` |
| `server.js` | `Media non scaricato (${path}): ` | `media not downloaded (${path}): ` |
| `server.js` | `Da ${name}: ` | `from ${name}: ` |
| `server.js` | `Handshake da "${name}"` | `handshake from "${name}"` |
| `server.js` | `Client WP8 connesso: ${remote}` | `app client connected: ${remote}` |
| `server.js` | `Frame non accettabile da ${remote} (lunghezza ${n}): connessione chiusa` | `unacceptable frame from ${remote} (length ${n}): closing the connection` |
| `server.js` | `Frame non valido da WP8: ` | `invalid frame from the app: ` |
| `server.js` | `Client WP8 disconnesso: ${remote}` | `app client disconnected: ${remote}` |
| `server.js` | `Errore socket [${remote}]: ` | `socket error [${remote}]: ` |
| `server.js` | `'ERRORE FATALE:'` | `'FATAL ERROR:'` |
| `server.js` (comments/doc) | `Console: ...` banner lines | English |
| `webhook-server.js` | `Webhook con firma non valida, ignorato` | `webhook with an invalid signature, ignored` |
| `webhook-server.js` | `Errore elaborazione webhook: ` | `webhook handling failed: ` |
| `gowa-client.js` | `fetch non disponibile: richiesto Node 18.13+` | `fetch is not available: Node 18.13+ is required` |
| `gowa-client.js` | `Login QR non riuscito` | `QR login failed` |
| `gowa-client.js` | `GOWA non ha restituito un QR code` | `GOWA did not return a QR code` |
| `gowa-client.js` | `Login con codice non riuscito` | `code login failed` |
| `gowa-client.js` | `GOWA non ha restituito un codice di abbinamento` | `GOWA did not return a pairing code` |
| `gowa-client.js` | `Invio messaggio non riuscito` | `sending the message failed` |
| `gowa-client.js` | `Invio immagine non riuscito` | `sending the image failed` |
| `gowa-client.js` | `Download non riuscito (${status})` | `download failed (${status})` |
| `test/frame-limit.test.js` | asserts `line.includes('lunghezza')` | asserts `line.includes('length')` |
| `start-login.js` | the whole banner, help text and progress output | English |

Also `server.js` writes the adapter banner with the cipher description and the port list: those lines are already English except the labels, so only the Italian ones in the table change.

**What stays Italian on purpose:** all source comments, all adapter test *names*, the `.resw` Italian translations, and the diagnostics of the static guards (`tools/check-*.js`, `qr-term.js`, `make-brand-assets.js`) - those are developer tooling that no operator runs, and translating them is a separate job. State this in both READMEs so the rule is written down rather than implied.

---

### Task A1: English in the adapter

**Files:**
- Modify: `WhatsappBridge/server.js`, `WhatsappBridge/webhook-server.js`, `WhatsappBridge/gowa-client.js`
- Modify: `WhatsappBridge/test/frame-limit.test.js`

**Interfaces:**
- Consumes: nothing new.
- Produces: the log vocabulary Part B's Docker documentation quotes: `app client connected`, `handshake from`, `unacceptable frame from`, `QR login failed`.

- [ ] **Step 1: Replace every runtime string in the three adapter modules**

Apply the table above to `WhatsappBridge/server.js`, `WhatsappBridge/webhook-server.js` and `WhatsappBridge/gowa-client.js`. Only the string literals change; no control flow, no identifier, no comment. Include the `console.error('ERRORE FATALE:', err)` line at the bottom of `server.js`.

- [ ] **Step 2: Fix the one test that asserts on a log line**

In `WhatsappBridge/test/frame-limit.test.js`, the assertion

```js
    assert.ok(
      lines.some((line) => line.includes('lunghezza')),
      'il motivo della chiusura deve finire nel log: ' + JSON.stringify(lines));
```

becomes

```js
    assert.ok(
      lines.some((line) => line.includes('length')),
      'the reason for the close must reach the log: ' + JSON.stringify(lines));
```

This is the only place in the adapter's 49 tests that reads a log string; verify that with `grep -rn "lines.some\|includes(" WhatsappBridge/test/`.

- [ ] **Step 3: Run the adapter suite**

```bash
cd WhatsappBridge && npm test
```

Expected: `tests 49`, `pass 49`, `fail 0`.

- [ ] **Step 4: Run the framing guard**

```bash
node tools/check-framing.js
```

Expected: `OK: 2 socket reader/writer site(s) pinned to little-endian, ...` - the guard looks for `writeUInt32LE`/`readUInt32LE`, which the translation does not touch.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge
git commit -m "chore: print the adapter's log and error messages in English"
```

---

### Task A2: English in the launcher - DEFERRED, see the note at the end

**Files:**
- Modify: `tools/start-login.js`

**Interfaces:**
- Consumes: nothing new.
- Produces: the launcher output Part B's README shows in its "run it with Node" section.

`tools/start-login.js` is about 60 lines of operator-facing prose (the banner, the `--help` text, the on-screen login instructions), and it is the launcher of the *app* repository rather than a process inside the server container. It was deferred so that the deployment repository - the actual request - keeps its budget. The steps below are unchanged and ready to run.

- [ ] **Step 1: Translate the banner, the help and the progress lines**

Every `console.log`/`console.error`/`fail(...)` string in `tools/start-login.js` becomes English: the ASCII banner, the `--help` text (option list and descriptions), the download progress lines and the `GOWA ... installed in ... (SHA-256 verified)` line, the service start/stop lines, the QR instructions, the `Ctrl-C` line and every error message. Keep the box-drawing characters and the arrow/check glyphs exactly as they are; only the words change. Do not touch the option names, the exit codes or the control flow.

- [ ] **Step 2: Check nothing else in the file still prints Italian**

```bash
grep -n "console\.\|fail(" tools/start-login.js
```

Read every line the command prints and confirm it is English. Words to look for: `non`, `in corso`, `installato`, `avvia`, `arresto`, `richiesto`, `usa`, `Valori`, `premuto`.

- [ ] **Step 3: The launcher still parses its own options**

```bash
node tools/start-login.js --help
```

Expected: the usage text, exit code 0, no Italian. (It must not try to download anything for `--help`.)

- [ ] **Step 4: Commit**

```bash
git add tools/start-login.js
git commit -m "chore: print the launcher's output in English"
```

---

### Task A3: English in the app's diagnostics

**Files:**
- Modify: `WhatsappApp/Services/Diag.cs`, `WhatsappApp/Services/SelfCheck.cs`, `WhatsappApp/Services/CommunicationService.cs`, `WhatsappApp/Services/AutoConnector.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: the `DIAG` vocabulary the on-device checklist and Part B's documentation quote: `crypto AES-256-CBC + HMAC-SHA256`, `screen kept awake (DisplayRequest)`, `UDP discovery beacon listening on port 8587`, `frame length out of range`, `no answer from`.

- [ ] **Step 1: Change the nine strings**

Apply these, exactly:

`WhatsappApp/Services/Diag.cs`:
```csharp
            if (ex == null) return "(no exception)";
```

`WhatsappApp/Services/CommunicationService.cs`:
```csharp
                Diag.Failed("GetUiDispatcher", new InvalidOperationException("no CoreDispatcher available"));
```
```csharp
                        new ArgumentException("invalid address or port: " + Endpoint(address, port)));
```
```csharp
                throw new TimeoutException(string.Format(
                    "no answer from {0}:{1} within {2} ms",
                    hostName.RawName, port, ConnectDeadlineMs));
```
```csharp
                    new InvalidDataException("frame length out of range: " + payloadLength));
```

`WhatsappApp/Services/AutoConnector.cs`:
```csharp
                        new InvalidOperationException("no answer from " + address + ":" + port));
```

`WhatsappApp/Services/SelfCheck.cs`:
```csharp
                    new InvalidOperationException("the encrypt/decrypt round trip does not return the same bytes"));
```
```csharp
                    new InvalidOperationException("the known-answer vector does not match: " + text));
```
```csharp
                Diag.Ok("screen kept awake (DisplayRequest)");
```
```csharp
                if (DiscoveryService.Instance.IsListening) Diag.Ok("UDP discovery beacon listening on port 8587");
```
```csharp
                else Diag.Failed("SelfCheck.discovery",
                    new InvalidOperationException("the UDP port did not open"));
```

- [ ] **Step 2: Fix the example in the `SelfCheck` doc comment**

The class comment quotes the three `DIAG ok:` lines the phone prints. Replace the quoted block:

```csharp
    ///     DIAG ok: crypto AES-256-CBC + HMAC-SHA256
    ///     DIAG ok: screen kept awake (DisplayRequest)
    ///     DIAG ok: UDP discovery beacon listening on port 8587
```

- [ ] **Step 3: Check for any other runtime Italian left in the app**

```bash
grep -rn "\"nessun\|\"non \|\"il \|\"la \|\"nessuna\|in ascolto\|fuori intervallo\|non torna\|non valida" WhatsappApp --include=*.cs
```

Expected: no match, other than doc comments and `Loc.Get` fallbacks, which are already English.

- [ ] **Step 4: Run the guards**

```bash
node tools/check-csharp5.js && node tools/check-resw.js --strict && node tools/check-framing.js
```

Expected: three `OK:` lines, `95 key(s)` in the middle one.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services
git commit -m "chore: print the app's diagnostics in English"
```

---

### Task A4: Write the language rule down

**Files:**
- Modify: `README.md`, `README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`

**Interfaces:**
- Consumes: the three tasks above.
- Produces: the rule Part B's README can point at.

- [ ] **Step 1: Add the rule to both project READMEs**

In `README.md`, in the `## Protocol` section, add at the end:

```markdown
Everything printed at run time is English: the adapter's log and error messages,
the launcher's output, and the app's `DIAG` lines and the exception messages that
reach them. Source comments and the adapter's test names are not part of that
rule, and neither are the localized UI strings in `Strings/it-IT`, which are
translations rather than diagnostics.
```

In `README.it.md`, in the `## Protocollo` section, add the matching paragraph:

```markdown
Tutto cio' che viene stampato a runtime e' in inglese: il log e i messaggi di
errore dell'adapter, l'output del launcher, le righe `DIAG` dell'app e i messaggi
delle eccezioni che ci finiscono. I commenti nel sorgente e i nomi dei test
dell'adapter non rientrano nella regola, e nemmeno le stringhe localizzate in
`Strings/it-IT`, che sono traduzioni e non diagnostica.
```

- [ ] **Step 2: Add the rule to the maintain skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in `## Hard constraints`, add a ninth point:

```markdown
9. **Runtime text is English.** Adapter logs and errors, launcher output, the app's
   `Diag`/`SelfCheck` lines and every message inside an exception that can reach
   them are written in English, so that an operator reading a container log or a
   debugger window needs no other language. Source comments and the adapter's test
   names are Italian and stay that way; `Strings/it-IT` is a translation and is not
   affected.
```

- [ ] **Step 3: Run the gates**

```bash
node tools/check-docs.js && node tools/check-resw.js --strict && node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-framing.js
```

Expected: five `OK:` lines.

- [ ] **Step 4: Commit and push Part A**

```bash
git add README.md README.it.md .agents/skills/maintain-the-app/SKILL.md
git commit -m "docs: state that runtime text is English"
git push origin master
```
