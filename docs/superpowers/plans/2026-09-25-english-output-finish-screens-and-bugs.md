# English Output, Finished Screens and Bug Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every human-readable string the server side prints English, turn the two placeholder screens (Status, Calls) into honest screens with real data where GOWA has it, and fix the bugs found while reading the code.

**Architecture:** Three independent workstreams over one repo. (1) The adapter (`WhatsappBridge/*.js`, `tools/*.js`, `WhatsappServer`) keeps its protocol untouched and only changes text it prints or sends as `text:` — protocol values (`state`, `command` names) are a wire contract and never change. (2) The Calls screen becomes real: the adapter gains a bounded scan of GOWA's own chat storage (`GET /chats`, `GET /chat/:chat_jid/messages`, filtering `media_type == "call"`), the app gains a `call` control frame and a list. The Status screen becomes an honest explanation, because GOWA exposes no status endpoint at all. (3) Bugs are fixed in place: send-time connection checks, unread counts, an eager data service, no invented presence, and removal of verified dead code.

**Tech Stack:** Node.js 18.13+ (CommonJS, no dependencies, `node --test`), C# 5 / .NET for Windows Phone 8.1 (Silverlight-free XAML app, `DataContractJsonSerializer`), `.resw` resources in en-US and it-IT, MSBuild 12 (VS2013) in a Parallels Windows VM.

## Global Constraints

- **C# 5 only** (VS2013 toolchain). Forbidden: `$"..."`, `?.`, `?[`, expression-bodied members, `out var`, `is T x`, `nameof`, `_ =`, tuple deconstruction, `async` entry points, `static` using. A trailing `;` after the last `enum` member is fine.
- **The WP8 app builds only on the VM.** Any task that touches `WhatsappApp/**` or `WhatsappServer/**` ends with the VM build gate and expects `Errori: 0, Avvisi: 2` (the two known warnings are CS0618 `FileOpenPicker.PickSingleFileAsync` and CS4014 in `ConnectionPage.xaml.cs`; line numbers shift, the codes are what matter).
- **Guards run after every task** and must all pass:
  - `node tools/check-csharp5.js` — `OK: <n> C# file(s) are C# 5 compatible`
  - `node tools/check-icons.js` — `OK: 13 icon path(s), 9 distinct`
  - `node tools/check-resw.js --strict` — `OK: <n> key(s)`
  - `node tools/check-docs.js` — pairs aligned, no emoji
  - `xmllint --noout <every changed .xaml>`
  - `cd WhatsappBridge && npm test` — all pass
  - `node tools/qr-term.js --self-test` — all checks passed
- **`.resw` key rules:** a key read from C# via `Loc.Get("Key", ...)` must be a **bare** name; a key used by XAML `x:Uid="Key"` must exist as `Key.Text` or `Key.Content`; a bare key and `Key.Property` must not both exist; and `tools/check-resw.js` reads **every** `Loc.Get("...")` occurrence in C#, **comments included**, as a key lookup.
- **Docs live in pairs and move together:** `README.md`/`README.it.md` and `WhatsappBridge/README.md`/`WhatsappBridge/README.it.md` must keep the same number, order and level of headings, keep their mutual links at the top, keep `## Disclosure` as the last section of the root pair only, and contain no emoji anywhere (U+26A0 warning sign is the only exception). Every task that changes user-visible behaviour updates both files in the same commit.
- **Protocol values are contract, never translated:** `state` = `disconnected|waiting|connected`, command names (`hello`, `status`, `login.qr`, `login.code`, `contacts`, `logout`, `calls`, `contact`, `call`, `calls.done`, `revoked`, `edited`), and JSON keys. Only human-readable text changes.
- **Adapter stays dependency-free:** no `package.json` dependency added; Node 18.13+ globals (`fetch`, `FormData`, `Blob`) only.
- **Italian code comments stay Italian.** They are not output. Only strings that a person reads (console, `text:` frames, thrown error messages, `.resw` values) change language.
- **Console markers in `tools/*.js` stay as they are** (`U+2716` cross, `U+2714` tick, `U+00B7` middle dot, `U+2026` ellipsis, `U+2192` arrow): they are terminal glyphs, not documentation, and the emoji rule covers `.md` files. Because the rule covers every `.md`, this plan spells those code points out instead of copying them.
- Commit messages: English, `type: short imperative` (see `git log`), one commit per task.

---

## Files

**Created**

- `WhatsappBridge/calls.js` — pure call-record extraction over a GOWA client port.
- `WhatsappBridge/test/calls.test.js` — unit tests for `calls.js`.
- `WhatsappApp/Models/CallLogEntry.cs` — one row of the Calls screen.

**Modified (adapter and tools)**

- `WhatsappBridge/server.js`, `gowa-client.js`, `message-format.js`, `webhook-server.js`, `crypto-helper.js`, `discovery.js`, `config.js`, `package.json`, `.env.example`
- `WhatsappBridge/test/{message-format,gowa-client,server}.test.js`
- `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`
- `tools/start-login.js`, `tools/qr-term.js`
- `WhatsappServer/Program.cs`

**Modified (app)**

- `WhatsappApp/Models/{ChatMessage.cs,Contact.cs}`
- `WhatsappApp/Services/{DataService.cs,CommunicationService.cs}`
- `WhatsappApp/Pages/{StatusPage.xaml,StatusPage.xaml.cs,CallsPage.xaml,CallsPage.xaml.cs,ChatsPage.xaml,ChatsPage.xaml.cs,ChatPage.xaml,ChatPage.xaml.cs}`
- `WhatsappApp/Converters/Converters.cs`
- `WhatsappApp/App.xaml.cs`, `WhatsappApp/WhatsappApp.csproj`
- `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`
- `README.md`, `README.it.md`
- `.agents/skills/{maintain-the-app,test-the-app,update-the-app,release-the-app,run-the-login-server}/SKILL.md`

**Deleted**

- `WhatsappApp/Models/ServerConfig.cs` (dead: no reference outside its own file)

---

## Interfaces fixed by this plan

Later tasks rely on these exact names. Do not rename them.

**Adapter (`WhatsappBridge`)**

- `calls.js` exports `parseCallMetadata(raw) -> { callId, reason, durationSeconds, isVideo }` and `collectCalls({ gowa, chatLimit, messagesPerChat, limit, log }) -> Promise<CallEntry[]>`, where `CallEntry = { chatId, chatName, timestamp, callId, reason, durationSeconds, isVideo }` and `timestamp` is the RFC3339 string GOWA returns.
- `GowaClient#chats(limit) -> Promise<ChatInfo[]>` (`{ jid, name, ... }`).
- `GowaClient#chatMessages(jid, limit) -> Promise<MessageInfo[]>` (`{ id, chat_jid, sender_display_name, content, timestamp, is_from_me, media_type, call_metadata }`).
- `config.calls = { chatLimit, messagesPerChat, limit }` from `CALLS_CHAT_LIMIT`, `CALLS_MESSAGES_PER_CHAT`, `CALLS_LIMIT`.
- `buildChatMessage(fields)` accepts five new optional fields: `callId`, `callReason`, `callDurationSeconds`, `callIsVideo`, `relatedMessageId`.
- Adapter → app control frames: `call`, `calls.done`, `revoked`, `edited`. App → adapter command: `calls`.

**App**

- `ChatMessage` new `[DataMember]`s: `CallId` (string), `CallReason` (string), `CallDurationSeconds` (int), `CallIsVideo` (bool), `RelatedMessageId` (string).
- `DataService`: `ObservableCollection<CallLogEntry> Calls`, `event EventHandler CallsScanCompleted`, `void ClearCalls()`, `void Start()`, `static string DisplayNameForJid(string jid)` (already exists).
- `CallLogEntry`: properties `ChatId`, `Name`, `Timestamp` (DateTime), `CallId`, `Reason`, `DurationSeconds`, `IsVideo`; computed `Initials`, `TimeText`, `Detail`.
- `CallsPage`: named elements `CallsListView`, `StatusText`, `RefreshButton`.

---

### Task 1: English output for the adapter

Everything the adapter prints or sends as `text:` becomes English. The two tests that assert Italian text are updated first, so they fail, then pass.

**Files:**
- Modify: `WhatsappBridge/server.js`, `gowa-client.js`, `message-format.js`, `webhook-server.js`, `crypto-helper.js`, `discovery.js`, `package.json`
- Test: `WhatsappBridge/test/message-format.test.js`
- Modify docs: none (no command or flag changes in this task)

**Interfaces:**
- Consumes: nothing.
- Produces: no signature changes. Only string literals change; `buildChatMessage`, `mapWebhookMessage`, `createBridge`, `GowaClient` keep their shapes. `formatDateForWp8` keeps the `/Date(ms)/` format.

- [ ] **Step 1: Update the two tests that assert Italian text**

In `WhatsappBridge/test/message-format.test.js`, line 18:

```js
  assert.strictEqual(displayNameForJid('123456789012345678@g.us'), 'Group 123456789012345678');
```

In the same file, line 79:

```js
  assert.strictEqual(f.text, '[Image not downloaded]');
```

- [ ] **Step 2: Run the suite and watch those two fail**

Run: `cd WhatsappBridge && npm test`
Expected: FAIL on those two assertions (`'Gruppo ...'` and `'[Immagine non scaricata]'`), everything else passing.

- [ ] **Step 3: Translate `message-format.js`**

Replace, in `WhatsappBridge/message-format.js`:

| Before | After |
| --- | --- |
| `const DEFAULT_SENDER = 'Sconosciuto';` | `const DEFAULT_SENDER = 'Unknown';` |
| `if (String(jid).endsWith('@g.us')) return \`Gruppo ${user}\`;` | `if (String(jid).endsWith('@g.us')) return \`Group ${user}\`;` |
| `result.fallbackText = '[Immagine non scaricata]';` | `result.fallbackText = '[Image not downloaded]';` |
| `result.fallbackText = '[Audio non scaricato]';` | `result.fallbackText = '[Audio not downloaded]';` |
| `result.fallbackText = '[Video non scaricato]';` | `result.fallbackText = '[Video not downloaded]';` |
| `result.fallbackText = '[Documento non scaricato]';` | `result.fallbackText = '[Document not downloaded]';` |

- [ ] **Step 4: Run the suite — the two tests pass again**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 36 tests.

- [ ] **Step 5: Translate `gowa-client.js`**

| Before | After |
| --- | --- |
| `'fetch non disponibile: richiesto Node 18.13+'` | `'fetch unavailable: Node 18.13+ required'` |
| `'Login QR non riuscito'` | `'QR login failed'` |
| `'GOWA non ha restituito un QR code'` | `'GOWA did not return a QR code'` |
| `'Login con codice non riuscito'` | `'Code login failed'` |
| `'GOWA non ha restituito un codice di abbinamento'` | `'GOWA did not return a pair code'` |
| `'Invio messaggio non riuscito'` | `'Send message failed'` |
| `'Invio immagine non riuscito'` | `'Send image failed'` |
| `` `Download non riuscito (${res.status})` `` | `` `Download failed (${res.status})` `` |

- [ ] **Step 6: Translate `webhook-server.js`, `crypto-helper.js`, `discovery.js`**

| File | Before | After |
| --- | --- | --- |
| `webhook-server.js` | `'Webhook con firma non valida, ignorato'` | `'Webhook with an invalid signature, ignored'` |
| `webhook-server.js` | `` `Errore elaborazione webhook: ${err.message}` `` | `` `Webhook processing failed: ${err.message}` `` |
| `crypto-helper.js` | already English after the v3 payload rewrite (`'Invalid CBC payload (too short)'`, `'Invalid GCM payload (too short)'`, `'Invalid HMAC signature'`, `'Unknown cipher tag: ...'`, `'Unknown cipher tag to write: ...'`, `'Empty encrypted payload'`) | nothing to translate |
| `discovery.js` | `` `Discovery: invio a ${target} fallito (${err.message})` `` | `` `Discovery: send to ${target} failed (${err.message})` `` |
| `discovery.js` | `` `Discovery: broadcast non attivabile (${err.message})` `` | `` `Discovery: broadcast could not be enabled (${err.message})` `` |

- [ ] **Step 7: Translate `server.js`**

Every replacement below is inside a log call or a `text:` frame payload.

| Before | After |
| --- | --- |
| `` `WhatsApp connesso come ${state.jid \|\| 'sconosciuto'}` `` | `` `WhatsApp connected as ${state.jid \|\| 'unknown'}` `` |
| `` dbg(`Stato non disponibile: ${err.message}`) `` | `` dbg(`Status unavailable: ${err.message}`) `` |
| `logger('QR', 'Nuovo QR code inviato all\'app');` | `logger('QR', 'New QR code sent to the app');` |
| `logger('ERR', \`Login QR fallito: ${err.message}\`);` | `logger('ERR', \`QR login failed: ${err.message}\`);` |
| `` text: `Login QR fallito: ${err.message}` `` | `` text: `QR login failed: ${err.message}` `` |
| `sendControl({ command: 'error', text: 'Numero di telefono mancante' });` | `sendControl({ command: 'error', text: 'Missing phone number' });` |
| `` `Codice di abbinamento inviato all'app per ${phone}` `` | `` `Pair code sent to the app for ${phone}` `` |
| `` `Login con codice fallito: ${err.message}` `` (log) | `` `Code login failed: ${err.message}` `` |
| `` text: `Login con codice fallito: ${err.message}` `` | `` text: `Code login failed: ${err.message}` `` |
| `` `Sincronizzati ${contacts.length} contatti` `` | `` `Contacts synced: ${contacts.length}` `` |
| `` `Sincronizzazione contatti fallita: ${err.message}` `` | `` `Contact sync failed: ${err.message}` `` |
| `` `Inviato a ${msg.ChatId}: ${(msg.Text \|\| '[media]').substring(0, 40)}` `` | `` `Sent to ${msg.ChatId}: ${(msg.Text \|\| '[media]').substring(0, 40)}` `` |
| `` `Invio a ${msg.ChatId} fallito: ${err.message}` `` | `` `Send to ${msg.ChatId} failed: ${err.message}` `` |
| `` text: `Invio non riuscito: ${err.message}` `` | `` text: `Send failed: ${err.message}` `` |
| `` `Invio ${queued.length} messaggi in coda...` `` | `` `Sending ${queued.length} queued messages...` `` |
| `'Messaggio WP8 senza contenuto, ignorato'` | `'Message from the app without content, ignored'` |
| `'WhatsApp non pronto: messaggio messo in coda'` | `'WhatsApp not ready yet: message queued'` |
| `'WhatsApp non ancora connesso. Il messaggio verrà inviato automaticamente.'` | `'WhatsApp is not connected yet. The message will be sent automatically.'` |
| `` `Media non scaricato (${fields.mediaPath}): ${err.message}` `` | `` `Media not downloaded (${fields.mediaPath}): ${err.message}` `` |
| `` `Da ${fields.senderName}: ${(fields.text \|\| '[media]').substring(0, 60)}` `` | `` `From ${fields.senderName}: ${(fields.text \|\| '[media]').substring(0, 60)}` `` |
| `` `Handshake da "${msg.SenderName \|\| 'Sconosciuto'}"` `` | `` `Handshake from "${msg.SenderName \|\| 'Unknown'}"` `` |
| `` dbg(`Comando sconosciuto: ${msg.Command}`) `` | `` dbg(`Unknown command: ${msg.Command}`) `` |
| `` `Client WP8 connesso: ${remote}` `` | `` `App client connected: ${remote}` `` |
| `` `Client WP8 disconnesso: ${remote}` `` | `` `App client disconnected: ${remote}` `` |
| `` `Errore socket [${remote}]: ${err.message}` `` | `` `Socket error [${remote}]: ${err.message}` `` |
| `` `Frame non valido da WP8: ${err.message}` `` | `` `Invalid frame from the app: ${err.message}` `` |
| `` `GOWA non raggiungibile su ${config.gowa.url}: ${err.message}` `` | `` `GOWA unreachable at ${config.gowa.url}: ${err.message}` `` |
| `'Avvia GOWA con: ./whatsapp rest --basic-auth=utente:password'` | `'Start GOWA with: ./whatsapp rest --basic-auth=user:password'` |
| `` `Device GOWA pronto: ${deviceId \|\| '(default)'}` `` | `` `GOWA device ready: ${deviceId \|\| '(default)'}` `` |
| `` `Webhook registrato su GOWA: ${config.webhook.publicUrl}` `` | `` `Webhook registered on GOWA: ${config.webhook.publicUrl}` `` |
| `` `Registrazione webhook automatica non riuscita: avvia GOWA con --webhook=${config.webhook.publicUrl}` `` | `` `Automatic webhook registration failed: start GOWA with --webhook=${config.webhook.publicUrl}` `` |
| `'Device GOWA:'` (banner label) | `'Device:    '` |
| `` `Cifratura:   ${cryptoHelper.ModeDescription} ${... 'ATTIVA' : 'DISATTIVATA'}` `` | `` `Encryption:  ${cryptoHelper.ModeDescription} ${... 'ON' : 'OFF'}` `` |
| `` `Server TCP in ascolto sulla porta ${config.bridge.port}` `` | `` `TCP server listening on port ${config.bridge.port}` `` |
| `` `   Connetti l'app WP8 a: ${addresses.join(', ') \|\| '(IP non trovato)'}:${config.bridge.port}` `` | `` `   Connect the app to: ${addresses.join(', ') \|\| '(no IP found)'}:${config.bridge.port}` `` |
| `` `Webhook in ascolto sulla porta ${config.webhook.port}${config.webhook.path}` `` | `` `Webhook listening on port ${config.webhook.port}${config.webhook.path}` `` |
| `` `Discovery attivo sulla porta UDP ${config.discovery.port} (nome: ${config.discovery.name})` `` | `` `Discovery active on UDP port ${config.discovery.port} (name: ${config.discovery.name})` `` |
| `` `Errore server TCP: ${err.message}` `` | `` `TCP server error: ${err.message}` `` |
| `` `Porta ${config.bridge.port} già in uso (usa BRIDGE_PORT=...).` `` | `` `Port ${config.bridge.port} already in use (use BRIDGE_PORT=...).` `` |
| `log('OK', 'Adapter arrestato.');` | `log('OK', 'Adapter stopped.');` |
| `console.error('ERRORE FATALE:', err);` | `console.error('FATAL ERROR:', err);` |

Leave the Italian header comment and the Italian inline comments in place (see Global Constraints).

- [ ] **Step 8: Translate the package description**

In `WhatsappBridge/package.json`:

```json
  "description": "Adapter between the WhatsApp Windows Phone 8.1 app and a self-hosted GOWA server (go-whatsapp-web-multidevice)",
```

Leave `keywords` as they are.

- [ ] **Step 9: Verify no human-readable string is left in Italian**

Run:

```bash
cd WhatsappBridge && node -e '
const fs=require("fs");
const files=["server.js","gowa-client.js","message-format.js","webhook-server.js","crypto-helper.js","discovery.js","config.js"];
const call=/(logger\(|console\.(log|error|warn)|throw new Error\(|text: )/;
const italian=/\b(non|il|lo|la|una|gli|delle|della|dei|richiede|riuscito|attesa|uscire|trovato|messaggio|connesso|inviato|abbinamento|sconosciuto|accanto)\b/i;
const bad=[];
for (const file of files) {
  fs.readFileSync(file,"utf8").split("\n").forEach((line,n)=>{
    if (call.test(line) && italian.test(line)) bad.push(file+":"+(n+1)+": "+line.trim());
  });
}
console.log(bad.length ? bad.join("\n") : "OK: no Italian text in output calls");
'
```

Expected: `OK: no Italian text in output calls`. Any line printed is reviewed by hand (a comment next to a log call can trigger the pattern).

- [ ] **Step 10: Run the full guard set**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
cd WhatsappBridge && npm test && cd ..
node tools/check-docs.js
```

Expected: `npm test` all pass (36), `check-docs` pairs aligned and no emoji. No C# changed in this task, so the VM build is not needed.

- [ ] **Step 11: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add WhatsappBridge
git commit -m "i18n: print the adapter logs and error texts in English"
```

---

### Task 2: English output for the launcher, the QR tool and the legacy relay

`tools/start-login.js` is the script a person runs, so its whole output surface is translated: the `--help` block, the banner, every hint, and every `fail(...)` message. `tools/qr-term.js` prints only diagnostics. `WhatsappServer/Program.cs` is the old relay that nobody runs any more, but its console text is still output, so it is translated too (not deleted: removing a project from the solution is a different decision).

**Files:**
- Modify: `tools/start-login.js`, `tools/qr-term.js`, `WhatsappServer/Program.cs`
- Modify docs: `README.md`, `README.it.md`, `.agents/skills/run-the-login-server/SKILL.md` (they quote the banner and the flag help)

**Interfaces:**
- Consumes: nothing.
- Produces: no flag, no behaviour change. `node tools/start-login.js --help` prints English; the exit codes are unchanged.

- [ ] **Step 1: List every string a person reads**

Run:

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -nE "fail\(|console\.(log|error)|HELP" tools/start-login.js
grep -nE "fail\(|console\.(log|error)" tools/qr-term.js
```

Every line printed above is a candidate. Translate all of them; the ones already known are in the next steps.

- [ ] **Step 2: Translate the `--help` block**

Replace the whole `HELP` template (`tools/start-login.js`, lines 62-85) with:

```js
const HELP = `Starts GOWA plus the WP8 adapter and shows the login QR code in the terminal.

Usage: node tools/start-login.js [options]

  --download            downloads GOWA ${GOWA_VERSION} into .tools/gowa (verifies SHA-256)
  --code <number>       login with a pair code (international prefix)
  --url <base>          use an already running GOWA instead of starting one
  --port <n>            GOWA port (default 3000)
  --bridge-port <n>     TCP port for the WP8 app (default 8585)
  --webhook-port <n>    GOWA -> adapter webhook port (default 8586)
  --gowa <path>         alternative path to the GOWA binary
  --no-bridge           do not start the adapter (GOWA + QR only)
  --once                draw one QR code and exit
  --no-qr               start the stack without drawing the QR code: log in
                        from the phone, inside the app (recommended)
  --open-qr             open the QR PNG in Preview (it refreshes by itself)
  --plain / --ansi      drawing without colours / with colours (default: colours on a TTY)
  --quiet-zone <n>      white margin around the QR code (default 4)
  --ui                  also serve the GOWA web dashboard (default: no)
  --gowa-user <user>    GOWA Basic Auth (together with --gowa-pass)
  --gowa-pass <pass>
  --stop                stop the stack started by this script
  --help
`;
```

- [ ] **Step 3: Translate the remaining strings in `start-login.js`**

| Line area | Before | After |
| --- | --- | --- |
| `next()` | `` fail(`${arg} richiede un valore`) `` | `` fail(`${arg} requires a value`) `` |
| GOWA download | `` `  ↓  scarico ${release.file} ...` `` | `` `  ↓  downloading ${release.file} ...` `` |
| GOWA download | `` fail(`unzip non riuscito: ${unzip.stderr \|\| unzip.stdout}`) `` | `` fail(`unzip failed: ${unzip.stderr \|\| unzip.stdout}`) `` |
| GOWA download | `` `GOWA ${GOWA_VERSION} installato in ${...} (SHA-256 verificato)` `` | `` `GOWA ${GOWA_VERSION} installed in ${...} (SHA-256 verified)` `` |
| adapter pipe | `` `  [adattatore] ${line}` `` | `` `  [adapter] ${line}` `` |
| adapter pipe | `` console.error(`adattatore: ${err.message}`) `` | `` console.error(`adapter: ${err.message}`) `` |
| QR hints | `` `  ·  --open-qr: apri a mano ${path.relative(ROOT, file)}` `` | `` `  ·  --open-qr: open it by hand: ${path.relative(ROOT, file)}` `` |
| QR hints | `` `  ·  --open-qr: non riesco ad aprire il codice (${err.message})` `` | `` `  ·  --open-qr: could not open the QR code (${err.message})` `` |
| too-small window | `` `     Il codice occupa ${lines.length} righe e ${lines[0].length} colonne;` `` | `` `     The code needs ${lines.length} rows and ${lines[0].length} columns;` `` |
| too-small window | `` `     questa finestra ne ha ${rows} x ${columns}.` `` | `` `     this window has ${rows} x ${columns}.` `` |
| too-small window | `'     Ridimensionala, o premi Cmd - per rimpicciolire il testo: il prossimo'` | `'     Resize it, or press Cmd - to shrink the text: the next code will be'` |
| too-small window | `'     codice verra\' disegnato qui.'` | `'     drawn here.'` |
| too-small window | `` `     Oppure: ${hint}` `` | `` `     Alternatively: ${hint}` `` |
| too-small window | `'     Oppure: --no-qr, e fai il login dal telefono nell\'app.'` | `'     Alternatively: --no-qr, and log in from the phone, inside the app.'` |
| redraw note | `` `  · nuovo codice alle ${stamp()} (non disegnato: la finestra e' troppo piccola)` `` | `` `  · new code at ${stamp()} (not drawn: the window is too small)` `` |
| banner | `'  WhatsApp per Windows Phone 8.1 — server di login in locale'` | `'  WhatsApp for Windows Phone 8.1 — local login server'` |
| banner | `` `  Adattatore per l'app ${host}:${options.bridgePort}  (TCP, AES-256-CBC+HMAC)` `` | `` `  Adapter for the app   ${host}:${options.bridgePort}  (TCP, AES-256-CBC+HMAC)` `` |
| banner | `` `  Webhook GOWA→app     http://${host}:${options.webhookPort}/webhook` `` | `` `  GOWA→app webhook      http://${host}:${options.webhookPort}/webhook` `` |
| banner | `` `  Scoperta automatica  UDP 8587  (l'app trova questo computer da sola)` `` | `` `  Automatic discovery   UDP 8587  (the app finds this computer by itself)` `` |
| banner | `` `  Sessioni             .tools/gowa/storages/whatsapp.db` `` | `` `  Sessions              .tools/gowa/storages/whatsapp.db` `` |
| banner | `` `  Log GOWA             .tools/gowa/gowa.log` `` | `` `  GOWA log              .tools/gowa/gowa.log` `` |
| banner | `` `  Indirizzi di questa macchina: ${addresses.join(', ')}` `` | `` `  Addresses of this machine: ${addresses.join(', ')}` `` |
| banner | `` `  Device GOWA: ${deviceId}` `` | `` `  GOWA device: ${deviceId}` `` |
| `--no-qr` wait | `` `\n  Il login si fa dal telefono: apri l'app e inquadra il codice che mostra.` `` | `` `\n  Log in from the phone: open the app and scan the code it shows.` `` |
| `--stop` | `'Nessuno stack da fermare (file dei PID assente).'` | `'No stack to stop (no PID file).'` |
| `--stop` | `` `fermato processo ${pid}` `` | `` `stopped process ${pid}` `` |
| `--stop` | `` `  · processo ${pid} non attivo` `` | `` `  · process ${pid} is not running` `` |
| shutdown | `'\n  … arresto in corso'` | `'\n  … shutting down'` |
| start | `` `  →  avvio GOWA ${GOWA_VERSION} sulla porta ${options.port} (log: .tools/gowa/gowa.log)` `` | `` `  →  starting GOWA ${GOWA_VERSION} on port ${options.port} (log: .tools/gowa/gowa.log)` `` |
| start | `` `  ·  rimossi ${stale} QR scaduti dalla sessione precedente` `` | `` `  ·  removed ${stale} expired QR codes from the previous session` `` |
| start | `` `\n  GOWA non risponde: ultime righe di ${path.relative(ROOT, GOWA_LOG)}` `` | `` `\n  GOWA is not responding. Last lines of ${path.relative(ROOT, GOWA_LOG)}:` `` |
| start | `'  →  avvio l\'adattatore per l\'app WP8\n'` | `'  →  starting the adapter for the app\n'` |
| start | `` `  Per fermare tutto: Ctrl-C (oppure node tools/start-login.js --stop)` `` | `` `  To stop everything: Ctrl-C (or node tools/start-login.js --stop)` `` |
| already linked | `` `WhatsApp è già collegato come ${status.jid \|\| 'sconosciuto'}: nessun QR da inquadrare.` `` | `` `WhatsApp is already linked as ${status.jid \|\| 'unknown'}: no QR code to scan.` `` |
| instructions | `'\n  Sul telefono: WhatsApp → Impostazioni → Dispositivi collegati → Collega un dispositivo'` | `'\n  On the phone: WhatsApp → Settings → Linked devices → Link a device'` |
| instructions | `'  e inquadra il codice qui sotto. Il primo QR dura ~60 s, poi ne arriva uno nuovo'` | `'  and scan the code below. The first QR code lasts about 60 s, then a new one'` |
| instructions | `'  ogni ~20 s: il disegno si aggiorna da solo.'` | `'  arrives every ~20 s and the drawing refreshes by itself.'` |
| instructions | `'\n  Nessun QR qui: loggati dal telefono, nell\'app (Ctrl-C per fermare).'` | `'\n  No QR code here: log in from the phone, inside the app (Ctrl-C to stop).'` |
| waiting | `'già collegato'` | `'already linked'` |
| linked | `` `WhatsApp collegato come ${result.jid \|\| 'sconosciuto'}` `` | `` `WhatsApp linked as ${result.jid \|\| 'unknown'}` `` |
| linked | `` `     L'app WP8 può ora collegarsi a ${host}:${options.bridgePort} e usare questa sessione.` `` | `` `     The app can now connect to ${host}:${options.bridgePort} and use this session.` `` |
| linked | `'     Lo stack resta attivo: Ctrl-C per fermarlo.'` | `'     The stack stays up: Ctrl-C to stop it.'` |

The console marker at the start of a translated line is not part of the change: `U+2716` (cross), `U+2714` (tick), `U+00B7` (middle dot), `U+2026` (ellipsis) and `U+2192` (arrow) stay exactly where they are. The rows above quote only the words that change.

- [ ] **Step 4: Translate `qr-term.js`**

| Before | After |
| --- | --- |
| `'Uso: node tools/qr-term.js (--info \| --preview) <qr.png> [--plain] [--quiet-zone <n>] [--self-test]'` | `'Usage: node tools/qr-term.js (--info \| --preview) <qr.png> [--plain] [--quiet-zone <n>] [--self-test]'` |
| `` `${qr.count} moduli, ${qr.pitch.toFixed(2)}px per modulo, ` `` | `` `${qr.count} modules, ${qr.pitch.toFixed(2)}px per module, ` `` |
| `` `disegno: ${plain.length} righe x ${plain[0].length} colonne ` `` | `` `drawing: ${plain.length} rows x ${plain[0].length} columns ` `` |
| `'Nessuna modalità scelta: usa --info o --preview.'` | `'No mode selected: use --info or --preview.'` |
| `'SKIP: ImageMagick 7 (magick) non disponibile'` | `'SKIP: ImageMagick 7 (magick) not available'` |
| `` `${ok ? 'OK  ' : 'FAIL'} ${testCase.count} moduli: rilevati ${qr.count}, ` `` | `` `${ok ? 'OK  ' : 'FAIL'} ${testCase.count} modules: detected ${qr.count}, ` `` |
| `` `${shapeOk && marginOk ? 'OK  ' : 'FAIL'} disegno: ${lines.length} righe x ` `` | `` `${shapeOk && marginOk ? 'OK  ' : 'FAIL'} drawing: ${lines.length} rows x ` `` |
| `` `${ansiOk ? 'OK  ' : 'FAIL'} ANSI: sequenze di colore presenti e azzerate a fine riga` `` | `` `${ansiOk ? 'OK  ' : 'FAIL'} ANSI: colour sequences present and reset at the end of a line` `` |
| `` `\n${failures} controllo/i fallito/i.` `` | `` `\n${failures} check(s) failed.` `` |
| `'\nTutti i controlli sono passati.'` | `'\nAll checks passed.'` |

- [ ] **Step 5: Translate `WhatsappServer/Program.cs`**

| Before | After |
| --- | --- |
| `"Avvio server sulla porta " + port + "..."` | `"Starting the server on port " + port + "..."` |
| `"In attesa di connessioni...\n"` | `"Waiting for connections...\n"` |
| `"Server avviato! IP locale: " + GetLocalIPAddress()` | `"Server started. Local IP: " + GetLocalIPAddress()` |
| `"I client possono connettersi con: " + GetLocalIPAddress() + ":" + port + "\n"` | `"Clients can connect to: " + GetLocalIPAddress() + ":" + port + "\n"` |
| `"Nuovo client connesso: " + Describe(endpoint)` | `"New client connected: " + Describe(endpoint)` |
| `"Errore: " + ex.Message` | `"Error: " + ex.Message` |
| `"\nPremi un tasto per uscire..."` | `"\nPress a key to exit..."` |
| `"[" + timestamp + "] Messaggio ricevuto (" + json.Length + " byte)"` | `"[" + timestamp + "] Message received (" + json.Length + " bytes)"` |
| `"Client disconnesso: " + Describe(endpoint) + " (" + ex.Message + ")"` | `"Client disconnected: " + Describe(endpoint) + " (" + ex.Message + ")"` |
| `"Client rimosso. Connessioni attive: " + _clients.Count + "\n"` | `"Client removed. Active connections: " + _clients.Count + "\n"` |

Leave the `@"..."` banner block (line 39) if it is pure ASCII art; if it contains Italian words, translate the words only.

- [ ] **Step 6: Check the three surfaces by running them**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/start-login.js --help
node tools/qr-term.js --self-test
node tools/start-login.js --stop
```

Expected: the help text is English, the self-test prints `All checks passed.`, `--stop` prints `No stack to stop (no PID file).` (or stops a running stack). No Italian word in any of the three outputs.

- [ ] **Step 7: Update the documentation that quotes those texts**

In `README.md` and `README.it.md`, update the tables/paragraphs that quote the banner labels and the flag help so the quoted text matches the new English output (the Italian README keeps its Italian prose but quotes the English strings as the script prints them). Add one sentence to the `### App language` / `### Lingua dell'app` section: script and server output is English only, the app UI is the only localized surface.

Do the same for `.agents/skills/run-the-login-server/SKILL.md`, which lists the banner and the flags.

- [ ] **Step 8: Run the guards**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-docs.js
node tools/check-csharp5.js
cd WhatsappBridge && npm test && cd ..
node tools/qr-term.js --self-test
```

Expected: docs pairs aligned, no emoji; 27 C# files compatible; 36 tests pass; self-test passes.

- [ ] **Step 9: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add tools WhatsappServer README.md README.it.md .agents/skills/run-the-login-server/SKILL.md
git commit -m "i18n: print the launcher, QR tool and legacy relay in English"
```

---

### Task 3: The Status screen tells the truth instead of pretending

`StatusPage` is a placeholder whose hint says "Status updates are not supported in this version". GOWA has no status/stories endpoint: the adapter's `mapWebhookMessage` explicitly drops `status@broadcast`, and no route in the running GOWA binary mentions status. The honest screen says what is missing and why, and confirms the rest of the app is unaffected.

**Files:**
- Modify: `WhatsappApp/Pages/StatusPage.xaml` (hint text only), `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`
- Modify docs: `README.md`, `README.it.md` (new `## Limitations` / `## Limiti` section, last before `## Disclaimer`)

**Interfaces:**
- Consumes: nothing.
- Produces: the resw keys `StatusPage_Title.Text`, `StatusPage_EmptyTitle.Text`, `StatusPage_EmptyHint.Text` keep their names (only values change), so no C# changes.

- [ ] **Step 1: Replace the two Status strings in both languages**

In `WhatsappApp/Strings/en-US/Resources.resw` (lines 76 and 79):

```xml
  <data name="StatusPage_EmptyTitle.Text" xml:space="preserve">
    <value>Updates are not available</value>
  </data>
```

```xml
  <data name="StatusPage_EmptyHint.Text" xml:space="preserve">
    <value>WhatsApp does not expose status updates to this app: the server it talks to has no endpoint for them, so this section stays empty on purpose. Chats and calls are unaffected.</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw` (same lines):

```xml
  <data name="StatusPage_EmptyTitle.Text" xml:space="preserve">
    <value>Aggiornamenti non disponibili</value>
  </data>
```

```xml
  <data name="StatusPage_EmptyHint.Text" xml:space="preserve">
    <value>WhatsApp non espone gli stati a questa app: il server con cui parla non ha un endpoint per gli aggiornamenti, quindi questa sezione resta vuota per scelta. Chat e chiamate non ne sono toccate.</value>
  </data>
```

- [ ] **Step 2: Keep the XAML honest about the icon**

In `WhatsappApp/Pages/StatusPage.xaml` the hint `TextBlock` keeps `x:Uid="StatusPage_EmptyHint"`; update its fallback attribute so a missing resource does not show a different sentence:

```xml
                <TextBlock x:Uid="StatusPage_EmptyHint" Text="WhatsApp does not expose status updates to this app."
                           Foreground="#FFBDBDBD" FontSize="13" TextWrapping="Wrap" TextAlignment="Center"
                           HorizontalAlignment="Center" Margin="0,2,0,0"/>
```

- [ ] **Step 3: Run the guards**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-resw.js --strict
xmllint --noout WhatsappApp/Pages/StatusPage.xaml
```

Expected: `OK: 90 key(s)`, no xmllint output.

- [ ] **Step 4: Add the limitations section to both READMEs**

In `README.md`, immediately before `## Disclaimer` (line 341), insert:

```markdown
## Limitations

- Status updates are not available: the GOWA server this app talks to has no endpoint for them, so the Status section is empty on purpose.
- Call records list incoming calls only, taken from the most recent chats the server scanned. See the Calls section below for the exact bound.
- Message deletions and edits made on the phone reach the app only while it is connected: they are not replayed after a restart.
```

In `README.it.md`, immediately before `## Disclaimer` (line 352), insert the same section in Italian with the same number of headings and the same level:

```markdown
## Limiti

- Gli aggiornamenti non sono disponibili: il server GOWA con cui parla questa app non ha un endpoint per gli stati, quindi la sezione Stato resta vuota per scelta.
- Il registro chiamate elenca solo le chiamate in entrata, prese dalle chat più recenti che il server ha scansionato. I limiti esatti sono nella sezione Chiamate qui sotto.
- Eliminazioni e modifiche fatte dal telefono arrivano all'app solo mentre è collegata: non vengono riprodotte dopo un riavvio.
```

- [ ] **Step 5: Run the doc guard**

Run: `node tools/check-docs.js`
Expected: two pairs aligned, no emoji. If it complains about heading parity, check the count, order and level of the new sections in both files.

- [ ] **Step 6: Build on the VM**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`, then `Errori: 0`, `Avvisi: 2` (CS0618, CS4014), and `WhatsappApp_1.0.1.0_x86_Debug.appxbundle` produced.

- [ ] **Step 7: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add WhatsappApp README.md README.it.md
git commit -m "fix: say why the Status section has no data instead of pretending"
```

---

### Task 4: Call records — adapter side

The adapter gains a bounded scan of GOWA's chat storage and a `calls` command. The scan reads `GET /chats?limit=N`, then `GET /chat/:chat_jid/messages?limit=M` per chat, keeping rows whose `media_type` is `call`. GOWA records **incoming** calls only (`CreateIncomingCallRecord`), and there is no call event in the webhook, so this is the only source.

**Files:**
- Create: `WhatsappBridge/calls.js`, `WhatsappBridge/test/calls.test.js`
- Modify: `WhatsappBridge/gowa-client.js`, `WhatsappBridge/config.js`, `WhatsappBridge/server.js`, `WhatsappBridge/.env.example`
- Test: `WhatsappBridge/test/gowa-client.test.js`, `WhatsappBridge/test/server.test.js`
- Modify docs: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: `GowaClient` from `gowa-client.js`; `formatDateForWp8` and `buildChatMessage` from `message-format.js`.
- Produces: `parseCallMetadata(raw)`, `collectCalls(options)` (see "Interfaces fixed by this plan"); `GowaClient#chats(limit)`, `GowaClient#chatMessages(jid, limit)`; `config.calls = { chatLimit, messagesPerChat, limit }`; control frames `call` and `calls.done`; app command `calls`.

- [ ] **Step 1: Write the failing test for `parseCallMetadata`**

Create `WhatsappBridge/test/calls.test.js`:

```js
'use strict';

const test = require('node:test');
const assert = require('node:assert');

const { parseCallMetadata, collectCalls } = require('../calls');

test('parseCallMetadata reads the keys GOWA stores', () => {
  const meta = parseCallMetadata('{"call_id":"ABC123","reason":"timeout","duration":42}');
  assert.strictEqual(meta.callId, 'ABC123');
  assert.strictEqual(meta.reason, 'timeout');
  assert.strictEqual(meta.durationSeconds, 42);
  assert.strictEqual(meta.isVideo, false);
});

test('parseCallMetadata keeps working when keys are missing', () => {
  const meta = parseCallMetadata('{"call_id":"ABC123"}');
  assert.strictEqual(meta.callId, 'ABC123');
  assert.strictEqual(meta.reason, '');
  assert.strictEqual(meta.durationSeconds, 0);
});

test('parseCallMetadata survives malformed JSON and empty input', () => {
  for (const bad of ['{not json', '', null, undefined, 42, '[]']) {
    const meta = parseCallMetadata(bad);
    assert.strictEqual(meta.callId, '');
    assert.strictEqual(meta.reason, '');
    assert.strictEqual(meta.durationSeconds, 0);
    assert.strictEqual(meta.isVideo, false);
  }
});

test('parseCallMetadata reads the video flag when it is there', () => {
  assert.strictEqual(parseCallMetadata('{"is_video":true}').isVideo, true);
  assert.strictEqual(parseCallMetadata('{"video":true}').isVideo, true);
  assert.strictEqual(parseCallMetadata('{"is_video":false}').isVideo, false);
});
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd WhatsappBridge && node --test test/calls.test.js`
Expected: FAIL — `Cannot find module '../calls'`.

- [ ] **Step 3: Write the failing tests for `collectCalls`**

Append to `WhatsappBridge/test/calls.test.js`:

```js
function fakeGowa({ chats, messagesByJid, failFor }) {
  return {
    chats: async (limit) => chats.slice(0, limit),
    chatMessages: async (jid) => {
      if (failFor && failFor.indexOf(jid) !== -1) throw new Error('chatstorage unavailable');
      return messagesByJid[jid] || [];
    },
  };
}

test('collectCalls keeps only call rows and sorts them newest first', async () => {
  const gowa = fakeGowa({
    chats: [{ jid: 'a@s.whatsapp.net', name: 'Anna' }, { jid: 'b@s.whatsapp.net', name: 'Bruno' }],
    messagesByJid: {
      'a@s.whatsapp.net': [
        { id: 'm1', chat_jid: 'a@s.whatsapp.net', media_type: 'text', content: 'ciao', timestamp: '2026-09-24T10:00:00Z' },
        { id: 'm2', chat_jid: 'a@s.whatsapp.net', media_type: 'call', call_metadata: '{"call_id":"C1","reason":"timeout"}', timestamp: '2026-09-24T09:00:00Z' },
      ],
      'b@s.whatsapp.net': [
        { id: 'm3', chat_jid: 'b@s.whatsapp.net', media_type: 'call', call_metadata: '{"call_id":"C2"}', timestamp: '2026-09-25T08:00:00Z' },
      ],
    },
  });

  const calls = await collectCalls({ gowa, chatLimit: 10, messagesPerChat: 50, limit: 10, log: () => {} });
  assert.strictEqual(calls.length, 2);
  assert.strictEqual(calls[0].callId, 'C2');
  assert.strictEqual(calls[0].chatName, 'Bruno');
  assert.strictEqual(calls[1].callId, 'C1');
  assert.strictEqual(calls[1].reason, 'timeout');
});

test('collectCalls skips a chat it cannot read and keeps going', async () => {
  const gowa = fakeGowa({
    chats: [{ jid: 'a@s.whatsapp.net', name: 'Anna' }, { jid: 'b@s.whatsapp.net', name: 'Bruno' }],
    messagesByJid: {
      'b@s.whatsapp.net': [
        { id: 'm3', chat_jid: 'b@s.whatsapp.net', media_type: 'call', call_metadata: '{"call_id":"C2"}', timestamp: '2026-09-25T08:00:00Z' },
      ],
    },
    failFor: ['a@s.whatsapp.net'],
  });

  const calls = await collectCalls({ gowa, chatLimit: 10, messagesPerChat: 50, limit: 10, log: () => {} });
  assert.strictEqual(calls.length, 1);
  assert.strictEqual(calls[0].chatId, 'b@s.whatsapp.net');
});

test('collectCalls caps the result and tolerates a missing chat name', async () => {
  const rows = [];
  for (let i = 0; i < 5; i++) {
    rows.push({ id: 'm' + i, chat_jid: 'a@s.whatsapp.net', media_type: 'call', call_metadata: '{}', timestamp: '2026-09-2' + i + 'T08:00:00Z' });
  }
  const gowa = fakeGowa({ chats: [{ jid: 'a@s.whatsapp.net' }], messagesByJid: { 'a@s.whatsapp.net': rows } });

  const calls = await collectCalls({ gowa, chatLimit: 10, messagesPerChat: 50, limit: 3, log: () => {} });
  assert.strictEqual(calls.length, 3);
  assert.strictEqual(calls[0].chatId, 'a@s.whatsapp.net');
  assert.strictEqual(calls[0].chatName, '');
});
```

- [ ] **Step 4: Run them and watch them fail**

Run: `cd WhatsappBridge && node --test test/calls.test.js`
Expected: FAIL — `collectCalls is not a function`.

- [ ] **Step 5: Write `calls.js`**

Create `WhatsappBridge/calls.js`:

```js
'use strict';

/**
 * Registro delle chiamate ricavato dalla history di GOWA.
 *
 * GOWA non manda le chiamate nei webhook (gli eventi sono solo message,
 * message.reaction, message.revoked, message.edited) e non ha una rotta per
 * elencarle: le registra pero' nella chat storage come messaggi con
 * media_type = "call" e una colonna call_metadata in JSON. L'unico modo di
 * leggerle e' scorrere le chat una per una, quindi la scansione e' limitata:
 * le prime N chat e i primi M messaggi di ognuna.
 *
 * Attenzione: GOWA registra solo le chiamate *in entrata* (vedi
 * CreateIncomingCallRecord nel suo codice). Una chiamata fatta da qui non
 * compare.
 */

// Chiavi lette da call_metadata. "call_id" e' certo (compare come struct tag
// `json:"call_id"` nel binario di GOWA); "reason", "duration" e "is_video"
// sono opzionali: se non ci sono, la voce resta valida e la UI mostra
// semplicemente meno dettagli.
function parseCallMetadata(raw) {
  const result = { callId: '', reason: '', durationSeconds: 0, isVideo: false };
  if (typeof raw !== 'string' || !raw.trim()) return result;

  let data;
  try {
    data = JSON.parse(raw);
  } catch (err) {
    return result;
  }
  if (!data || typeof data !== 'object' || Array.isArray(data)) return result;

  if (typeof data.call_id === 'string') result.callId = data.call_id;
  if (typeof data.reason === 'string') result.reason = data.reason;

  const duration = Number(data.duration);
  if (isFinite(duration) && duration > 0) result.durationSeconds = Math.round(duration);

  result.isVideo = data.is_video === true || data.video === true;
  return result;
}

function timeOf(value) {
  const parsed = Date.parse(value);
  return isNaN(parsed) ? 0 : parsed;
}

/**
 * Scorre le chat indicate da GOWA e raccoglie le chiamate.
 * Una chat illeggibile (storage assente, jid sbagliato) non ferma la raccolta.
 */
async function collectCalls(options) {
  const opts = options || {};
  const gowa = opts.gowa;
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const chatLimit = opts.chatLimit || 25;
  const messagesPerChat = opts.messagesPerChat || 100;
  const limit = opts.limit || 50;

  const chats = await gowa.chats(chatLimit);
  const found = [];

  for (const chat of chats) {
    if (!chat || !chat.jid) continue;

    let messages;
    try {
      messages = await gowa.chatMessages(chat.jid, messagesPerChat);
    } catch (err) {
      log('DEBUG', `Calls: chat ${chat.jid} not readable (${err.message})`);
      continue;
    }

    for (const message of messages) {
      if (!message || message.media_type !== 'call') continue;
      const meta = parseCallMetadata(message.call_metadata);
      found.push({
        chatId: message.chat_jid || chat.jid,
        chatName: chat.name || '',
        timestamp: message.timestamp || '',
        callId: meta.callId,
        reason: meta.reason,
        durationSeconds: meta.durationSeconds,
        isVideo: meta.isVideo,
      });
    }
  }

  found.sort((a, b) => timeOf(b.timestamp) - timeOf(a.timestamp));

  const result = found.slice(0, limit);
  log('INFO', `Calls: ${result.length} call record(s) from ${chats.length} chat(s)`);
  return result;
}

module.exports = { parseCallMetadata, collectCalls };
```

- [ ] **Step 6: Run the tests until they pass**

Run: `cd WhatsappBridge && node --test test/calls.test.js`
Expected: PASS, 8 tests.

- [ ] **Step 7: Add the two GOWA client methods**

In `WhatsappBridge/gowa-client.js`, after `contacts()`:

```js
  // Elenco delle chat presenti nella storage di GOWA (paginato lato server).
  async chats(limit) {
    const r = await this.request('GET', `/chats?limit=${encodeURIComponent(limit)}`);
    const res = (r.data && r.data.results) || {};
    return Array.isArray(res.data) ? res.data : [];
  }

  // Messaggi di una chat. La rotta di GOWA e' /chat/:chat_jid/messages.
  async chatMessages(jid, limit) {
    const r = await this.request('GET',
      `/chat/${encodeURIComponent(jid)}/messages?limit=${encodeURIComponent(limit)}`);
    const res = (r.data && r.data.results) || {};
    return Array.isArray(res.data) ? res.data : [];
  }
```

- [ ] **Step 8: Test those two methods**

Append to `WhatsappBridge/test/gowa-client.test.js`:

```js
test('chats() asks for a bounded list and reads results.data', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return { ok: true, status: 200, text: async () => JSON.stringify({ results: { data: [{ jid: 'a@s.whatsapp.net' }] } }) };
    }
  });

  const chats = await client.chats(25);
  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/chats?limit=25');
  assert.strictEqual(chats.length, 1);
  assert.strictEqual(chats[0].jid, 'a@s.whatsapp.net');
});

test('chatMessages() encodes the jid in the path', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return { ok: true, status: 200, text: async () => JSON.stringify({ results: { data: [] } }) };
    }
  });

  await client.chatMessages('393401234567@s.whatsapp.net', 100);
  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/chat/393401234567%40s.whatsapp.net/messages?limit=100');
});
```

If the existing test file does not already import `GowaClient` and `assert`, add the same two lines the other tests in that file use.

- [ ] **Step 9: Run the suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 47 tests (36 + 8 + 2 + the tests added in step 11 below).

- [ ] **Step 10: Add the configuration keys**

In `WhatsappBridge/config.js`, extend `DEFAULTS`:

```js
  DISCOVERY_PORT: '8587',
  DISCOVERY_ENABLED: 'on',
  DISCOVERY_NAME: '',
  CALLS_CHAT_LIMIT: '25',
  CALLS_MESSAGES_PER_CHAT: '100',
  CALLS_LIMIT: '50'
```

and add to the returned object, next to `discovery`:

```js
    calls: {
      // Quante chat scansionare e quanti messaggi per chat: la scansione fa
      // una richiesta HTTP per chat, quindi il limite e' la durata.
      chatLimit: parseInt(pick(env, 'CALLS_CHAT_LIMIT'), 10),
      messagesPerChat: parseInt(pick(env, 'CALLS_MESSAGES_PER_CHAT'), 10),
      limit: parseInt(pick(env, 'CALLS_LIMIT'), 10)
    }
```

- [ ] **Step 11: Test the configuration defaults**

Append to `WhatsappBridge/test/config.test.js`:

```js
test('call scan limits have defaults and can be overridden', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.calls.chatLimit, 25);
  assert.strictEqual(defaults.calls.messagesPerChat, 100);
  assert.strictEqual(defaults.calls.limit, 50);

  const custom = loadConfig({ CALLS_CHAT_LIMIT: '5', CALLS_MESSAGES_PER_CHAT: '20', CALLS_LIMIT: '10' });
  assert.strictEqual(custom.calls.chatLimit, 5);
  assert.strictEqual(custom.calls.messagesPerChat, 20);
  assert.strictEqual(custom.calls.limit, 10);
});
```

Match the existing import style of that file for `loadConfig`.

- [ ] **Step 12: Test the `calls` command at the bridge level**

Append to `WhatsappBridge/test/server.test.js`:

```js
test('the calls command sends one frame per call and then calls.done', async () => {
  const sent = [];
  const gowa = {
    chats: async () => [{ jid: 'a@s.whatsapp.net', name: 'Anna' }],
    chatMessages: async () => ([
      { id: 'm2', chat_jid: 'a@s.whatsapp.net', media_type: 'call', call_metadata: '{"call_id":"C1","reason":"timeout"}', timestamp: '2026-09-24T09:00:00Z' },
    ]),
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' }),
  };
  const config = { calls: { chatLimit: 10, messagesPerChat: 10, limit: 10 }, bridge: { port: 8585 } };
  const bridge = createBridge({ config, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();

  bridge.addClientForTest({ write: (packet) => sent.push(packet) });
  await bridge.handleControl({ Type: 3, Command: 'calls', SenderName: 'test' });

  // Le chiavi arrivano cifrate nel frame: si controllano i comandi con un
  // decodificatore, non con un confronto testuale sul buffer.
  const commands = sent.map((packet) => decodeFrame(packet)).map((msg) => msg.Command);
  assert.deepStrictEqual(commands, ['call', 'calls.done']);
});

test('the calls command answers with an error and calls.done when WhatsApp is not connected', async () => {
  const sent = [];
  const gowa = { status: async () => ({ isConnected: false, isLoggedIn: false, jid: '' }) };
  const bridge = createBridge({ config: { calls: {} }, gowa, log: () => {}, debug: () => {} });

  bridge.addClientForTest({ write: (packet) => sent.push(packet) });
  await bridge.handleControl({ Type: 3, Command: 'calls', SenderName: 'test' });

  const commands = sent.map((packet) => decodeFrame(packet)).map((msg) => msg.Command);
  assert.deepStrictEqual(commands, ['error', 'calls.done']);
});

function decodeFrame(packet) {
  const length = packet.readUInt32LE(0);
  const payload = packet.slice(4, 4 + length);
  return JSON.parse(cryptoHelper.decodePayload(payload));
}
```

Add `const cryptoHelper = require('../crypto-helper');` at the top of the file if it is not already imported.

Note: `handleControl` takes one argument (the message). Frames go out through `sendToClients`, which writes to every socket in `wp8Clients`, so both tests register a fake client with the test-only hook `bridge.addClientForTest(...)` added in the next step, and then assert on the same `sent` array.

- [ ] **Step 13: Wire the scan into the bridge**

In `WhatsappBridge/server.js`:

1. Import the module:

```js
const { collectCalls } = require('./calls');
```

2. Add the cache and the sender inside `createBridge`, after `let qrCache = null;`:

```js
  // La scansione costa una richiesta HTTP per chat: si tiene il risultato per
  // un minuto, cosi' passare avanti e indietro tra le sezioni non la ripete.
  let callsCache = null;
  const CALLS_CACHE_MS = 60000;

  async function sendCalls() {
    const limits = (config && config.calls) || {};

    if (state.status !== 'connected') {
      sendControl({ command: 'error', text: 'WhatsApp is not connected: call records are unavailable.' });
      sendControl({ command: 'calls.done' });
      return;
    }

    try {
      const fresh = !callsCache || Date.now() - callsCache.at > CALLS_CACHE_MS;
      if (fresh) {
        logger('INFO', `Scanning up to ${limits.chatLimit || 25} chats for call records...`);
        const entries = await collectCalls({
          gowa,
          chatLimit: limits.chatLimit,
          messagesPerChat: limits.messagesPerChat,
          limit: limits.limit,
          log: logger
        });
        callsCache = { at: Date.now(), entries };
      }

      for (const call of callsCache.entries) {
        sendControl({
          command: 'call',
          chatId: call.chatId,
          senderName: call.chatName || undefined,
          timestamp: call.timestamp || undefined,
          callId: call.callId || undefined,
          callReason: call.reason || undefined,
          callDurationSeconds: call.durationSeconds,
          callIsVideo: call.isVideo
        });
      }
    } catch (err) {
      logger('ERR', `Call scan failed: ${err.message}`);
      sendControl({ command: 'error', text: `Call scan failed: ${err.message}` });
    } finally {
      sendControl({ command: 'calls.done' });
    }
  }
```

3. Handle the command in `handleControl`:

```js
      case 'calls':
        await sendCalls();
        break;
```

4. Expose the test hooks in the returned object:

```js
    // Usato solo dai test: aggiunge un client finto alla lista dei destinatari.
    addClientForTest(socket) { wp8Clients.add(socket); },
    sendCalls,
    resetCallsCacheForTest() { callsCache = null; },
```

Also invalidate the cache when the WhatsApp state changes to connected: inside `refreshStatus`, in the `next === 'connected' && changed` branch, add `callsCache = null;` next to `qrCache = null;`.

- [ ] **Step 14: Extend `buildChatMessage` for call frames**

In `WhatsappBridge/message-format.js`, after the `if (f.accountJid)` line:

```js
  // Campi delle chiamate e delle revoche (vedi calls.js e server.js).
  if (f.callId) msg.CallId = f.callId;
  if (f.callReason) msg.CallReason = f.callReason;
  if (typeof f.callDurationSeconds === 'number') msg.CallDurationSeconds = f.callDurationSeconds;
  if (typeof f.callIsVideo === 'boolean') msg.CallIsVideo = f.callIsVideo;
  if (f.relatedMessageId) msg.RelatedMessageId = f.relatedMessageId;
```

Add a matching test to `WhatsappBridge/test/message-format.test.js`:

```js
test('buildChatMessage carries the call fields', () => {
  const msg = buildChatMessage({
    command: 'call', chatId: 'a@s.whatsapp.net', senderName: 'Anna',
    timestamp: 0, callId: 'C1', callReason: 'timeout',
    callDurationSeconds: 12, callIsVideo: false
  });
  assert.strictEqual(msg.Command, 'call');
  assert.strictEqual(msg.CallId, 'C1');
  assert.strictEqual(msg.CallReason, 'timeout');
  assert.strictEqual(msg.CallDurationSeconds, 12);
  assert.strictEqual(msg.CallIsVideo, false);
  assert.strictEqual(msg.Type, 3);
});
```

- [ ] **Step 15: Run the whole suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 50 tests.

- [ ] **Step 16: Document the scan in both adapter READMEs**

In `WhatsappBridge/README.md`, inside `## Control protocol`, add to the app → adapter row list: `calls` — requests the call records (incoming only, from the most recent `CALLS_CHAT_LIMIT` chats). In the adapter → app row list add: `call`, `calls.done`.

In `## Configuration`, add:

```markdown
| `CALLS_CHAT_LIMIT` | `25` | how many of the most recent chats the call scan reads |
| `CALLS_MESSAGES_PER_CHAT` | `100` | messages read per scanned chat |
| `CALLS_LIMIT` | `50` | maximum number of call records sent to the app |
```

Mirror all of it in `WhatsappBridge/README.it.md` (same headings, same table shape).

Update `WhatsappBridge/.env.example` with the three keys and a one-line comment each.

- [ ] **Step 17: Run the guards**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-docs.js
cd WhatsappBridge && npm test
```

Expected: docs aligned, no emoji; 50 tests pass.

- [ ] **Step 18: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add WhatsappBridge
git commit -m "feat: scan GOWA's chat history for call records"
```

---

### Task 5: Call records — app side

The app gets the `CallLogEntry` model, five new `ChatMessage` fields, the `call`/`calls.done` handling in `DataService`, and a `CallsPage` that lists what the adapter sends. The page states its own limits in the empty state, because the log is a bounded scan of incoming calls only.

**Files:**
- Create: `WhatsappApp/Models/CallLogEntry.cs`
- Modify: `WhatsappApp/Models/ChatMessage.cs`, `WhatsappApp/Services/DataService.cs`, `WhatsappApp/Pages/CallsPage.xaml`, `WhatsappApp/Pages/CallsPage.xaml.cs`, `WhatsappApp/WhatsappApp.csproj`, `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`
- Modify docs: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: the `call` and `calls.done` frames from Task 4; `DataService.DisplayNameForJid`.
- Produces: `CallLogEntry` (see "Interfaces fixed by this plan"); `DataService.Calls`, `DataService.CallsScanCompleted`, `DataService.ClearCalls()`; `ChatMessage.CallId/CallReason/CallDurationSeconds/CallIsVideo`.

- [ ] **Step 1: Add the five `ChatMessage` fields**

In `WhatsappApp/Models/ChatMessage.cs`, add the backing fields next to `_accountJid`:

```csharp
        private string _callId;             // id della chiamata, da GOWA
        private string _callReason;         // esito riportato da GOWA (timeout, reject, ...)
        private int _callDurationSeconds;   // durata in secondi, 0 se sconosciuta
        private bool _callIsVideo;          // chiamata video
        private string _relatedMessageId;   // messaggio toccato da una revoca o una modifica
```

and the properties after `AccountJid`:

```csharp
        /// <summary>Identificativo della chiamata come lo conosce GOWA.</summary>
        [DataMember]
        public string CallId
        {
            get { return _callId; }
            set { _callId = value; OnPropertyChanged(); }
        }

        /// <summary>Esito della chiamata secondo GOWA: "timeout", "reject", ... Vuoto se non lo dice.</summary>
        [DataMember]
        public string CallReason
        {
            get { return _callReason; }
            set { _callReason = value; OnPropertyChanged(); }
        }

        /// <summary>Durata della chiamata in secondi. 0 significa "non lo sappiamo".</summary>
        [DataMember]
        public int CallDurationSeconds
        {
            get { return _callDurationSeconds; }
            set { _callDurationSeconds = value; OnPropertyChanged(); }
        }

        /// <summary>Vero se la chiamata era video.</summary>
        [DataMember]
        public bool CallIsVideo
        {
            get { return _callIsVideo; }
            set { _callIsVideo = value; OnPropertyChanged(); }
        }

        /// <summary>Id del messaggio a cui si riferisce un frame di revoca o modifica.</summary>
        [DataMember]
        public string RelatedMessageId
        {
            get { return _relatedMessageId; }
            set { _relatedMessageId = value; OnPropertyChanged(); }
        }
```

- [ ] **Step 2: Write `CallLogEntry`**

Create `WhatsappApp/Models/CallLogEntry.cs`:

```csharp
using System;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    /// <summary>
    /// Una voce del registro chiamate. I dati arrivano dall'adapter, che li
    /// ricava dalla history di GOWA: sono solo chiamate in entrata, prese dalle
    /// chat piu' recenti che il server ha scansionato.
    ///
    /// Non implementa INotifyPropertyChanged: la collezione viene svuotata e
    /// riempita ad ogni scansione, non modificata campo per campo.
    /// </summary>
    public class CallLogEntry
    {
        public string ChatId { get; set; }
        public string Name { get; set; }
        public DateTime Timestamp { get; set; }
        public string CallId { get; set; }
        public string Reason { get; set; }
        public int DurationSeconds { get; set; }
        public bool IsVideo { get; set; }

        /// <summary>Iniziali per l'avatar, come nell'elenco chat.</summary>
        public string Initials
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return "?";
                string trimmed = Name.Trim();
                if (trimmed.StartsWith("+") && trimmed.Length > 1)
                    return trimmed.Substring(1, Math.Min(2, trimmed.Length - 1)).ToUpper();
                return trimmed.Substring(0, 1).ToUpper();
            }
        }

        /// <summary>Orario come nell'elenco chat (oggi -> HH:mm, ieri -> "Yesterday").</summary>
        public string TimeText
        {
            get
            {
                if (Timestamp == default(DateTime)) return "";

                DateTime local = Timestamp.Kind == DateTimeKind.Utc ? Timestamp.ToLocalTime() : Timestamp;
                DateTime now = DateTime.Now;
                if (local.Date == now.Date) return local.ToString("HH:mm");
                if (local.Date == now.Date.AddDays(-1)) return Loc.Get("ChatMessage_Yesterday", "Yesterday");
                if (local.Year == now.Year) return local.ToString("dd/MM");
                return local.ToString("dd/MM/yy");
            }
        }

        /// <summary>
        /// Riga di dettaglio: esito, eventuale "video" e durata quando la
        /// conosciamo. Un esito che non riconosciamo resta una chiamata in
        /// entrata generica, invece di mostrare testo preso dal server.
        /// </summary>
        public string Detail
        {
            get
            {
                string kind;
                switch ((Reason ?? "").Trim().ToLowerInvariant())
                {
                    case "timeout": kind = Loc.Get("CallsPage_Missed", "Missed call"); break;
                    case "reject": kind = Loc.Get("CallsPage_Declined", "Declined call"); break;
                    case "busy": kind = Loc.Get("CallsPage_Busy", "Call, line busy"); break;
                    case "cancel": kind = Loc.Get("CallsPage_Cancelled", "Cancelled call"); break;
                    case "accepted": kind = Loc.Get("CallsPage_Answered", "Answered call"); break;
                    default: kind = Loc.Get("CallsPage_Incoming", "Incoming call"); break;
                }

                if (IsVideo) kind = kind + " · " + Loc.Get("CallsPage_Video", "Video");
                if (DurationSeconds > 0) kind = kind + " · " + FormatDuration(DurationSeconds);
                return kind;
            }
        }

        private static string FormatDuration(int seconds)
        {
            int minutes = seconds / 60;
            int rest = seconds % 60;
            return minutes + ":" + rest.ToString("00");
        }
    }
}
```

- [ ] **Step 3: Register the new file in the project**

In `WhatsappApp/WhatsappApp.csproj`, add next to the other `Models` entries (same `<Compile Include=...>` shape and the `<DependentUpon>`-free style used by `BeaconPayload.cs`):

```xml
    <Compile Include="Models\CallLogEntry.cs" />
```

- [ ] **Step 4: Teach `DataService` about calls**

In `WhatsappApp/Services/DataService.cs`:

1. Field and property:

```csharp
        private readonly ObservableCollection<CallLogEntry> _calls;

        /// <summary>Registro chiamate, riempito dall'adapter su richiesta.</summary>
        public ObservableCollection<CallLogEntry> Calls
        {
            get { return _calls; }
        }

        /// <summary>La scansione lato adapter e' finita: la pagina puo' smettere di aspettare.</summary>
        public event EventHandler CallsScanCompleted;
```

2. Constructor: `_calls = new ObservableCollection<CallLogEntry>();`

3. Replace the body of `OnControlMessageReceived` with a switch that keeps the existing contact handling:

```csharp
        /// <summary>
        /// Frame di controllo dall'adapter: contatti, registro chiamate,
        /// revoche e modifiche. Arrivano tutti sul thread UI, quindi qui si
        /// puo' toccare direttamente quello che e' legato alle liste.
        /// </summary>
        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.Command)) return;

            switch (message.Command)
            {
                case "contact":
                    ApplyContact(message);
                    break;
                case "call":
                    AddCall(message);
                    break;
                case "calls.done":
                    RaiseCallsScanCompleted();
                    break;
                case "revoked":
                    RemoveMessage(message.ChatId, message.RelatedMessageId);
                    break;
                case "edited":
                    ApplyEdit(message.ChatId, message.RelatedMessageId, message.Text);
                    break;
            }
        }

        /// <summary>Un contatto nuovo (o il nome aggiornato) dall'adapter.</summary>
        private void ApplyContact(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.ChatId)) return;

            var contact = FindContact(message.ChatId);
            string name = string.IsNullOrEmpty(message.SenderName)
                ? DisplayNameForJid(message.ChatId)
                : message.SenderName;

            if (contact == null)
            {
                var added = new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    Initials = InitialsFor(name),
                    UnreadCount = 0
                };
                _contacts.Add(added);
                _contactIndex[added.Id] = added;
            }
            else
            {
                contact.Name = name;
                contact.Initials = InitialsFor(name);
            }
        }

        /// <summary>Una voce del registro chiamate.</summary>
        private void AddCall(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.ChatId)) return;

            _calls.Add(new CallLogEntry
            {
                ChatId = message.ChatId,
                Name = string.IsNullOrEmpty(message.SenderName)
                    ? DisplayNameForJid(message.ChatId)
                    : message.SenderName,
                Timestamp = message.Timestamp,
                CallId = message.CallId,
                Reason = message.CallReason,
                DurationSeconds = message.CallDurationSeconds,
                IsVideo = message.CallIsVideo
            });
        }

        /// <summary>Svuota il registro prima di una nuova scansione.</summary>
        public void ClearCalls()
        {
            _calls.Clear();
        }

        private void RaiseCallsScanCompleted()
        {
            var handler = CallsScanCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }
```

4. Add the two message-editing helpers used by the switch (also used by Task 7):

```csharp
        /// <summary>
        /// Toglie un messaggio revocato su WhatsApp. L'id di un messaggio in
        /// arrivo e' quello di WhatsApp, quindi il confronto e' esatto; se non
        /// lo troviamo (messaggio nostro, o arrivato prima dell'iscrizione) non
        /// si tocca niente.
        /// </summary>
        private void RemoveMessage(string chatId, string messageId)
        {
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(messageId)) return;

            ObservableCollection<ChatMessage> messages;
            if (!_chatMessages.TryGetValue(chatId, out messages)) return;

            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i].Id != messageId) continue;
                messages.RemoveAt(i);
                RefreshPreview(chatId);
                return;
            }
        }

        /// <summary>Applica una modifica arrivata da WhatsApp (stesso id di prima).</summary>
        private void ApplyEdit(string chatId, string messageId, string text)
        {
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(messageId) || text == null) return;

            ObservableCollection<ChatMessage> messages;
            if (!_chatMessages.TryGetValue(chatId, out messages)) return;

            foreach (var message in messages)
            {
                if (message.Id != messageId) continue;
                message.Text = text;
                RefreshPreview(chatId);
                return;
            }
        }

        /// <summary>
        /// Riallinea l'anteprima della chat all'ultimo messaggio rimasto: dopo
        /// una revoca o una modifica l'anteprima resterebbe quella vecchia.
        /// </summary>
        private void RefreshPreview(string chatId)
        {
            var contact = FindContact(chatId);
            if (contact == null) return;

            ObservableCollection<ChatMessage> messages;
            if (!_chatMessages.TryGetValue(chatId, out messages) || messages.Count == 0)
            {
                contact.LastMessage = "";
                contact.LastMessageTime = "";
                return;
            }

            var last = messages[messages.Count - 1];
            contact.LastMessage = last.Text;
            contact.LastMessageTime = last.FormattedTime;
        }
```

The removed `Status = ""` line disappears with the old body (that property is removed in Task 10).

- [ ] **Step 5: Rewrite the Calls page markup**

Replace `WhatsappApp/Pages/CallsPage.xaml` with:

```xml
<Page
    x:Class="WhatsappApp.Pages.CallsPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:conv="using:WhatsappApp.Converters"
    xmlns:controls="using:WhatsappApp.Controls"
    Background="#FFECE5DD">

    <Page.Resources>
        <conv:InitialToColorConverter x:Key="InitialToColor"/>
        <SolidColorBrush x:Key="WhatsAppHeaderBrush" Color="#FF075E54"/>
        <SolidColorBrush x:Key="WhatsAppChatBgBrush" Color="#FFECE5DD"/>

        <Style x:Key="CallItemContainerStyle" TargetType="ListViewItem">
            <Setter Property="Padding" Value="0"/>
            <Setter Property="Margin" Value="0"/>
            <Setter Property="MinHeight" Value="0"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="IsTabStop" Value="False"/>
        </Style>
    </Page.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Title bar -->
        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>

            <TextBlock x:Uid="CallsPage_Title" Text="Calls"
                       Foreground="White" FontSize="24" FontWeight="SemiBold"
                       VerticalAlignment="Center" Margin="16,0,0,4"/>

            <Button x:Name="RefreshButton" x:Uid="CallsPage_Refresh" Grid.Column="1"
                    Content="Refresh"
                    Background="Transparent" BorderThickness="0"
                    Foreground="White" FontSize="15" Margin="0,0,12,0"
                    Click="RefreshButton_Click"/>
        </Grid>

        <Grid Grid.Row="1" Background="{StaticResource WhatsAppChatBgBrush}">
            <ListView x:Name="CallsListView"
                      ItemContainerStyle="{StaticResource CallItemContainerStyle}"
                      Background="Transparent"
                      SelectionMode="None">
                <ListView.ItemsPanel>
                    <ItemsPanelTemplate>
                        <VirtualizingStackPanel/>
                    </ItemsPanelTemplate>
                </ListView.ItemsPanel>
                <ListView.ItemTemplate>
                    <DataTemplate>
                        <Grid Height="64" Background="White">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="64"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>

                            <Grid Grid.Column="0" Width="44" Height="44"
                                  Margin="10,10,0,10" VerticalAlignment="Center">
                                <Ellipse Width="44" Height="44"
                                         Fill="{Binding Initials, Converter={StaticResource InitialToColor}}"/>
                                <TextBlock Text="{Binding Initials}"
                                           Foreground="White" FontSize="16" FontWeight="SemiBold"
                                           VerticalAlignment="Center" HorizontalAlignment="Center"/>
                            </Grid>

                            <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                <TextBlock Text="{Binding Name}" Foreground="Black" FontSize="17" FontWeight="SemiBold"
                                           TextTrimming="WordEllipsis" Margin="0,0,0,2"/>
                                <TextBlock Text="{Binding Detail}" Foreground="#FF808080" FontSize="14"
                                           TextTrimming="WordEllipsis" MaxHeight="18"/>
                            </StackPanel>

                            <TextBlock Grid.Column="2" Text="{Binding TimeText}" Foreground="#FF808080" FontSize="12"
                                       VerticalAlignment="Center" Margin="0,0,12,0"/>
                        </Grid>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>

            <!-- Stato: "in cerca" e "niente da mostrare" sono due cose diverse -->
            <TextBlock x:Name="StatusText"
                       Foreground="#FF9E9E9E" FontSize="15"
                       TextWrapping="Wrap" TextAlignment="Center" MaxWidth="300"
                       HorizontalAlignment="Center" VerticalAlignment="Center"
                       Visibility="Collapsed"/>
        </Grid>

        <controls:SectionNav x:Name="Nav" Grid.Row="2"/>
    </Grid>
</Page>
```

Note: no new `<!-- IconXxx -->` comment is introduced, so `tools/check-icons.js` keeps reporting the same icon set.

- [ ] **Step 6: Rewrite the Calls page code-behind**

Replace `WhatsappApp/Pages/CallsPage.xaml.cs` with:

```csharp
using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Registro chiamate. I dati non sono locali: l'adapter li ricava dalla
    /// history di GOWA (solo chiamate in entrata, dalle chat piu' recenti) e li
    /// manda quando riceve il comando "calls".
    /// </summary>
    public sealed partial class CallsPage : Page
    {
        // Vero mentre aspettiamo "calls.done": senza saperlo, una lista vuota
        // sarebbe indistinguibile da una scansione ancora in corso.
        private bool _waiting;

        public CallsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;

            CallsListView.ItemsSource = DataService.Instance.Calls;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Nav.Current = AppSection.Calls;

            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            DataService.Instance.CallsScanCompleted += OnCallsScanCompleted;

            RefreshCalls();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            _waiting = false;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshCalls();
        }

        private void RefreshCalls()
        {
            if (!CommunicationService.Instance.IsConnected)
            {
                _waiting = false;
                ShowMessage(Loc.Get("CallsPage_NotConnected",
                    "Not connected to the server: call records are unavailable."));
                return;
            }

            _waiting = true;
            DataService.Instance.ClearCalls();
            ShowMessage(Loc.Get("CallsPage_Scanning", "Looking for calls..."));

            // OnNavigatedTo non e' async: si manda la richiesta e si aspetta
            // call "calls.done" per sapere che l'adapter ha finito.
#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("calls");
#pragma warning restore 4014
        }

        private void OnCallsScanCompleted(object sender, EventArgs e)
        {
            _waiting = false;
            if (DataService.Instance.Calls.Count == 0)
            {
                ShowMessage(Loc.Get("CallsPage_EmptyHint",
                    "Incoming calls found in the most recent chats. Nothing to show yet."));
                return;
            }

            StatusText.Visibility = Visibility.Collapsed;
        }

        /// <summary>Mostra una riga di stato al posto (o sopra) della lista vuota.</summary>
        private void ShowMessage(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
```

`_waiting` is read only by `OnNavigatedFrom` and written here; it stays because losing the page while a scan is in flight must not leave a spinner state behind. If the reviewer prefers, it can be dropped, but then the empty state text would be shown during the scan.

- [ ] **Step 7: Add the new strings to both resource files**

In `WhatsappApp/Strings/en-US/Resources.resw`: delete `CallsPage_EmptyTitle.Text` (the new markup does not use it), turn `CallsPage_EmptyHint.Text` into the **bare** key `CallsPage_EmptyHint` (the code-behind reads that one with `Loc.Get`, and a bare key must not coexist with the `.Text` form), and add the new entries next to them:

```xml
  <data name="CallsPage_EmptyHint" xml:space="preserve">
    <value>WhatsApp recorded no incoming calls in the chats the server scanned. Call records come from the server's own history: incoming calls only, from the most recent 25 chats.</value>
  </data>
  <data name="CallsPage_Scanning.Text" xml:space="preserve">
    <value>Looking for calls...</value>
  </data>
  <data name="CallsPage_Refresh.Content" xml:space="preserve">
    <value>Refresh</value>
  </data>
  <data name="CallsPage_Answered" xml:space="preserve">
    <value>Answered call</value>
  </data>
  <data name="CallsPage_Missed" xml:space="preserve">
    <value>Missed call</value>
  </data>
  <data name="CallsPage_Declined" xml:space="preserve">
    <value>Declined call</value>
  </data>
  <data name="CallsPage_Busy" xml:space="preserve">
    <value>Call, line busy</value>
  </data>
  <data name="CallsPage_Cancelled" xml:space="preserve">
    <value>Cancelled call</value>
  </data>
  <data name="CallsPage_Incoming" xml:space="preserve">
    <value>Incoming call</value>
  </data>
  <data name="CallsPage_Video" xml:space="preserve">
    <value>Video</value>
  </data>
  <data name="CallsPage_NotConnected" xml:space="preserve">
    <value>Not connected to the server: call records are unavailable.</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, the same keys with Italian values:

```xml
  <data name="CallsPage_EmptyHint" xml:space="preserve">
    <value>WhatsApp non ha registrato chiamate in entrata nelle chat che il server ha scansionato. Il registro arriva dalla history del server: solo chiamate in entrata, dalle 25 chat più recenti.</value>
  </data>
  <data name="CallsPage_Scanning.Text" xml:space="preserve">
    <value>Cerco le chiamate...</value>
  </data>
  <data name="CallsPage_Refresh.Content" xml:space="preserve">
    <value>Aggiorna</value>
  </data>
  <data name="CallsPage_Answered" xml:space="preserve">
    <value>Chiamata risposta</value>
  </data>
  <data name="CallsPage_Missed" xml:space="preserve">
    <value>Chiamata persa</value>
  </data>
  <data name="CallsPage_Declined" xml:space="preserve">
    <value>Chiamata rifiutata</value>
  </data>
  <data name="CallsPage_Busy" xml:space="preserve">
    <value>Chiamata, linea occupata</value>
  </data>
  <data name="CallsPage_Cancelled" xml:space="preserve">
    <value>Chiamata annullata</value>
  </data>
  <data name="CallsPage_Incoming" xml:space="preserve">
    <value>Chiamata in entrata</value>
  </data>
  <data name="CallsPage_Video" xml:space="preserve">
    <value>Video</value>
  </data>
  <data name="CallsPage_NotConnected" xml:space="preserve">
    <value>Non connesso al server: il registro chiamate non è disponibile.</value>
  </data>
```

No `CallLogEntry_Yesterday` key is added: `CallLogEntry.TimeText` reuses the bare `ChatMessage_Yesterday` key that `ChatMessage.FormatTime` already uses for the same word.

Key count goes from 90 to **99** in both files: one key removed (`CallsPage_EmptyTitle.Text`), one renamed (no count change), ten added.

- [ ] **Step 8: Run the resource and XAML guards**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-resw.js --strict
xmllint --noout WhatsappApp/Pages/CallsPage.xaml
node tools/check-csharp5.js
node tools/check-icons.js
```

Expected: `OK: 99 key(s)`, no xmllint output, `OK: 28 C# file(s) are C# 5 compatible`, `OK: 13 icon path(s), 9 distinct`. Fix any key the resw guard flags.

- [ ] **Step 9: Build on the VM**

Run the three VM commands from Task 3 step 6.
Expected: `COPIA=0`, `Errori: 0`, `Avvisi: 2`, appxbundle produced. A compile error here is the honest gate for the new C#: fix and re-run.

- [ ] **Step 10: Document the Calls section in both READMEs**

In `README.md` `### WhatsappApp (Windows Phone 8.1 App)`, add a short `#### Calls` paragraph (mirroring the level in the Italian file) stating: the Calls tab lists incoming calls only, taken from the chats the adapter scanned (`CALLS_CHAT_LIMIT`, default 25), refreshed when the tab is opened or with the Refresh button, and that GOWA keeps no outgoing-call record. Add the same in `README.it.md` under the matching heading level.

- [ ] **Step 11: Run the full guard set**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
cd WhatsappBridge && npm test && cd ..
node tools/qr-term.js --self-test
```

Expected: all `OK`, 50 tests pass.

- [ ] **Step 12: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add WhatsappApp README.md README.it.md
git commit -m "feat: show the call records in the Calls section"
```

---

### Task 6: Webhook events — adapter side

GOWA sends `message.revoked` and `message.edited` and the adapter drops them, so a deleted message stays visible in the app for ever. The adapter now forwards both as control frames. `message.reaction` stays unhandled (documented) — reactions have no place to go in this UI.

**Files:**
- Modify: `WhatsappBridge/server.js`
- Test: `WhatsappBridge/test/server.test.js`
- Modify docs: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: `sendControl`, `buildChatMessage` (with `relatedMessageId` from Task 4).
- Produces: control frames `revoked` (`chatId`, `relatedMessageId`) and `edited` (`chatId`, `relatedMessageId`, `text`).

- [ ] **Step 1: Write the failing tests**

Append to `WhatsappBridge/test/server.test.js`:

```js
test('a revoked message becomes a revoked control frame', async () => {
  const sent = [];
  const bridge = createBridge({ config: {}, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message.revoked',
    payload: { revoked_message_id: 'ABC', revoked_from_me: false, revoked_chat: 'a@s.whatsapp.net' }
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'revoked');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(sent[0].RelatedMessageId, 'ABC');
  assert.strictEqual(sent[0].Type, 3);
});

test('an edited message becomes an edited control frame with the new text', async () => {
  const sent = [];
  const bridge = createBridge({ config: {}, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message.edited',
    payload: { original_message_id: 'ABC', chat_id: 'a@s.whatsapp.net', body: 'testo nuovo' }
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'edited');
  assert.strictEqual(sent[0].RelatedMessageId, 'ABC');
  assert.strictEqual(sent[0].Text, 'testo nuovo');
});

test('reactions and incomplete events are ignored without sending anything', async () => {
  const sent = [];
  const bridge = createBridge({ config: {}, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({ event: 'message.reaction', payload: { reaction: 'X' } });
  await bridge.handleWebhookEvent({ event: 'message.revoked', payload: {} });
  await bridge.handleWebhookEvent(null);

  assert.strictEqual(sent.length, 0);
});
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL — the revoked/edited tests see zero frames (the `message.reaction` test already passes).

- [ ] **Step 3: Route the two events**

In `WhatsappBridge/server.js`, replace the first line of `handleWebhookEvent`:

```js
  async function handleWebhookEvent(event) {
    if (!event) return;

    if (event.event === 'message.revoked') {
      const payload = event.payload || {};
      const id = payload.revoked_message_id;
      if (!id) return;
      const chatId = payload.revoked_chat || payload.chat_id || payload.from || '0';
      logger('MSG', `Message revoked on WhatsApp: ${id}`);
      sendControl({ command: 'revoked', chatId, relatedMessageId: id });
      return;
    }

    if (event.event === 'message.edited') {
      const payload = event.payload || {};
      const id = payload.original_message_id;
      if (!id || typeof payload.body !== 'string') return;
      const chatId = payload.chat_id || payload.from || '0';
      logger('MSG', `Message edited on WhatsApp: ${id}`);
      sendControl({ command: 'edited', chatId, relatedMessageId: id, text: payload.body });
      return;
    }

    // message.reaction e i tipi futuri restano ignorati: nell'app non c'e'
    // dove mostrarli.
    if (event.event !== 'message') return;
```

Keep the rest of the function body (from `const fields = mapWebhookMessage(...)`) unchanged.

- [ ] **Step 4: Run the tests until they pass**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 53 tests.

- [ ] **Step 5: Document the handled events**

In `WhatsappBridge/README.md` `## How it works`, add a sentence: the adapter forwards `message.revoked` and `message.edited` to the app as control frames and ignores `message.reaction`. Mirror it in `WhatsappBridge/README.it.md`.

- [ ] **Step 6: Run the guards**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-docs.js
cd WhatsappBridge && npm test
```

Expected: docs aligned, no emoji; 53 tests pass.

- [ ] **Step 7: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add WhatsappBridge
git commit -m "feat: forward WhatsApp message deletions and edits to the app"
```

---

### Task 7: Deletions and edits — close the loop on the app side

The frames from Task 6 have a tested consumer: Task 5 step 4 already wired the two `case` labels and the three helpers (`RemoveMessage`, `ApplyEdit`, `RefreshPreview`) into `DataService`. This task is the gate that proves it — the wiring is checked by grep before the build, the build is the real proof, and the limit is written down in both READMEs, because the match is by WhatsApp's message id and therefore never matches a message the app itself sent.

**Files:**
- Modify docs: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `DataService.RemoveMessage`, `DataService.ApplyEdit`, `ChatMessage.RelatedMessageId`, the `revoked`/`edited` frames from Task 6.
- Produces: nothing new. This task is the app-side gate for the Task 6 frames, plus the documented limit.

- [ ] **Step 1: Check the wiring is complete**

Run:

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -n "case \"revoked\"\|case \"edited\"\|RelatedMessageId" WhatsappApp/Services/DataService.cs WhatsappApp/Models/ChatMessage.cs
```

Expected: the two `case` lines in `OnControlMessageReceived`, the `RemoveMessage`/`ApplyEdit` calls, and the `RelatedMessageId` field and property. If any is missing, add it from Task 5 step 4.

- [ ] **Step 2: Build on the VM**

Run the three VM commands from Task 3 step 6.
Expected: `Errori: 0`, `Avvisi: 2`.

- [ ] **Step 3: Extend the limitations section**

In `README.md` `## Limitations`, the third bullet already mentions that deletions and edits only arrive while the app is connected. Add one more sentence to that bullet naming the second limit:

```markdown
- Message deletions and edits made on the phone reach the app only while it is connected: they are not replayed after a restart. They are matched by WhatsApp's message id, so messages the app itself sent are not matched.
```

and the same in `README.it.md`:

```markdown
- Eliminazioni e modifiche fatte dal telefono arrivano all'app solo mentre è collegata: non vengono riprodotte dopo un riavvio. Il confronto usa l'id del messaggio di WhatsApp, quindi i messaggi inviati dall'app non vengono riconosciuti.
```

- [ ] **Step 4: Run the doc guard**

Run: `node tools/check-docs.js`
Expected: two pairs aligned, no emoji.

- [ ] **Step 5: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add README.md README.it.md
git commit -m "docs: state how far message deletions and edits reach the app"
```

---

### Task 8: Sending and receiving tell the truth

Three verified bugs, one theme: the app reports states it has not checked.

1. `ChatPage` captures `_isConnectedMode` once in `OnNavigatedTo` and uses it at send time. If the socket is down when the message is written, `AddAndSendMessage` skips the network entirely and the bubble stays `Sent` for ever — a message shown as sent that was never sent.
2. `DataService.OnNetworkMessageReceived` creates a contact for a first message with `UnreadCount = 0`, ignoring whether the chat is open: the badge is lost.
3. `DataService` subscribes to the network in its constructor but is only constructed when something touches `DataService.Instance`, so messages arriving while the user is still on the connection page have no listener and vanish.

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs`, `WhatsappApp/Services/DataService.cs`, `WhatsappApp/App.xaml.cs`

**Interfaces:**
- Consumes: `CommunicationService.IsConnected`, `SendMessageAsync`.
- Produces: `DataService.Start()` (idempotent subscription), `DataService._listening` guard, `ActiveChatId` (already exists).

- [ ] **Step 1: Move the subscription behind `Start()`**

In `WhatsappApp/Services/DataService.cs`, delete these two lines from the constructor:

```csharp
            // Wire up to receive network messages and adapter control frames
            CommunicationService.Instance.MessageReceived += OnNetworkMessageReceived;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
```

and add, next to the field declarations:

```csharp
        private bool _listening;
```

and as a public method after `IsServerRunning`:

```csharp
        /// <summary>
        /// Aggancia il servizio alla rete. Va chiamato una volta, all'avvio e
        /// sul thread UI: se nessuno costruisce il servizio prima che l'app si
        /// colleghi, i messaggi in arrivo (e i contatti) non hanno ascoltatori e
        /// si perdono senza lasciare traccia.
        /// </summary>
        public void Start()
        {
            if (_listening) return;
            _listening = true;

            CommunicationService.Instance.MessageReceived += OnNetworkMessageReceived;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
        }
```

- [ ] **Step 2: Call it at startup**

In `WhatsappApp/App.xaml.cs`, right after `CommunicationService.Instance.Prewarm();`:

```csharp
            // Il servizio dati si aggancia qui: prima si creava alla prima
            // pagina che lo toccava, e i messaggi arrivati nel frattempo (o i
            // contatti sincronizzati) non avevano nessun ascoltatore.
            DataService.Instance.Start();
```

- [ ] **Step 3: Count the first message of a new chat**

In `WhatsappApp/Services/DataService.cs`, in `OnNetworkMessageReceived`, replace the new-contact assignment:

```csharp
                    IsOnline = true,
                    UnreadCount = 0
```

with:

```csharp
                    // La chat aperta non conta come non letta, e "IsOnline" non
                    // si inventa: la presenza non arriva da nessuna parte.
                    UnreadCount = message.IsIncoming && message.ChatId != _activeChatId ? 1 : 0
```

(the closing brace of the initialiser stays).

- [ ] **Step 4: Decide the send status at send time**

In `WhatsappApp/Pages/ChatPage.xaml.cs`:

1. Delete the field `private bool _isConnectedMode;` and the line `_isConnectedMode = CommunicationService.Instance.IsConnected;` in `OnNavigatedTo`.
2. In `SendMessage` and `SendImageMessage`, set `Status = MessageStatus.Sending` (this is the state shown while the send is attempted) instead of the `_isConnectedMode` ternary.
3. Replace `AddAndSendMessage` with:

```csharp
        private async void AddAndSendMessage(ChatMessage message)
        {
            // DataService è l'unico punto di inserimento: _messages è la stessa
            // ObservableCollection osservata dal ListView.
            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";

            // Auto-scroll
            ScrollToMessage(message);

            // Lo stato si decide adesso, non quando la pagina e' stata aperta:
            // un messaggio scritto a socket caduto restava "inviato" per sempre
            // senza essere mai partito. Adesso si vede fallito e si puo'
            // riscrivere.
            if (!CommunicationService.Instance.IsConnected)
            {
                message.Status = MessageStatus.Failed;
                return;
            }

            message.Status = MessageStatus.Sending;
            await CommunicationService.Instance.SendMessageAsync(message);
            message.Status = CommunicationService.Instance.IsConnected
                ? MessageStatus.Sent
                : MessageStatus.Failed;
        }
```

- [ ] **Step 5: Run the guards and the VM build**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict
```

then the three VM commands from Task 3 step 6.
Expected: all guards `OK`; `Errori: 0`, `Avvisi: 2`.

- [ ] **Step 6: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add WhatsappApp
git commit -m "fix: report the real send result and never drop incoming messages"
```

---

### Task 9: Stop inventing presence, stop re-syncing on every navigation

Two more verified defects:

1. `Contact.IsOnline` is set `true` only for contacts created from an incoming message and `false` for the ones created by the contact sync — a green dot that means nothing, on an arbitrary subset, and the chat header prints "online" or invents "last seen today at HH:mm". GOWA exposes no presence at all.
2. `ChatsPage.OnNavigatedTo` sends `contacts` on every navigation, and the adapter answers with one frame per contact, so every visit rebuilds and re-renders the whole list.

**Files:**
- Modify: `WhatsappApp/Pages/ChatsPage.xaml`, `WhatsappApp/Pages/ChatsPage.xaml.cs`, `WhatsappApp/Pages/ChatPage.xaml`, `WhatsappApp/Pages/ChatPage.xaml.cs`, `WhatsappApp/Converters/Converters.cs`, `WhatsappApp/Models/Contact.cs`, `WhatsappApp/Services/DataService.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `Contact` without `IsOnline`; `Converters.cs` without `OnlineToDotColorConverter`; `ChatPage.DisplayNumber(string jid)`.

- [ ] **Step 1: Remove the dot from the chat list**

In `WhatsappApp/Pages/ChatsPage.xaml`, delete the two converter registrations that only the dot used:

```xml
        <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
        <conv:OnlineToDotColorConverter x:Key="OnlineToDotColor"/>
```

and delete the dot itself:

```xml
                                <!-- Online dot -->
                                <Ellipse Width="12" Height="12"
                                         Fill="{Binding IsOnline, Converter={StaticResource OnlineToDotColor}}"
                                         Stroke="White" StrokeThickness="2"
                                         VerticalAlignment="Bottom" HorizontalAlignment="Right"
                                         Visibility="{Binding IsOnline, Converter={StaticResource BoolToVisibility}}"/>
```

Replace the block with a comment that says why it is gone:

```xml
                                <!-- Niente pallino di presenza: WhatsApp non ci
                                     dice chi e' online, e un pallino inventato e'
                                     peggio di nessun pallino. -->
```

- [ ] **Step 2: Delete the unused converter**

In `WhatsappApp/Converters/Converters.cs`, delete the whole `OnlineToDotColorConverter` class and its doc comment. Nothing else references it (`grep -rn OnlineToDotColor WhatsappApp` must print nothing at the end of this task).

- [ ] **Step 3: Remove `IsOnline` from the model**

In `WhatsappApp/Models/Contact.cs`, delete the `_isOnline` field and the `IsOnline` property.

In `WhatsappApp/Services/DataService.cs`, delete the `IsOnline = false,` line from `ApplyContact`. In `WhatsappApp/Pages/ChatsPage.xaml.cs`, delete `IsOnline = false,` from the new-contact initialiser in `NewChatButton_Click`.

- [ ] **Step 4: Replace the invented status line in the chat header**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace the `OnlineStatusText` assignment in `OnNavigatedTo`:

```csharp
                ContactNameText.Text = contact.Name;

                // Nessuna presenza: WhatsApp non la espone tramite il server che
                // usiamo, e dire "online" o "ultimo accesso alle HH:mm" era una
                // bugia. Resta l'unica cosa vera in piu' che abbiamo: il numero
                // della chat, quando il nome non e' gia' il numero.
                string number = DisplayNumber(contact.Id);
                bool hasNumber = !string.IsNullOrEmpty(number) && number != contact.Name;
                OnlineStatusText.Text = hasNumber ? number : "";
                OnlineStatusText.Visibility = hasNumber ? Visibility.Visible : Visibility.Collapsed;
```

Add the helper next to the other private methods:

```csharp
        /// <summary>
        /// Numero leggibile di un JID (es. +393401234567 per le persone). Vuoto
        /// per i gruppi e per tutto cio' che non e' un numero.
        /// </summary>
        private static string DisplayNumber(string jid)
        {
            if (string.IsNullOrEmpty(jid) || jid.EndsWith("@g.us")) return "";
            string user = jid.Split('@')[0];
            if (user.Length < 8) return "";
            for (int i = 0; i < user.Length; i++)
            {
                if (!char.IsDigit(user[i])) return "";
            }
            return "+" + user;
        }
```

and add `using System.Linq;` only if something else in the file needs it (the loop above deliberately avoids LINQ).

- [ ] **Step 5: Request contacts only when the list is empty**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, replace the request at the end of `OnNavigatedTo`:

```csharp
            // OnNavigatedTo is not async: fire the contacts request and ignore
            // the task. Si chiede solo se la lista e' vuota: l'adapter risponde
            // con un frame per contatto, e ripetere la sincronizzazione ad ogni
            // visita ricostruiva tutto l'elenco per niente.
            if (CommunicationService.Instance.IsConnected && DataService.Instance.Contacts.Count == 0)
            {
#pragma warning disable 4014
                CommunicationService.Instance.SendControlAsync("contacts");
#pragma warning restore 4014
            }
```

- [ ] **Step 6: Check nothing references what was removed**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -rn "IsOnline\|OnlineToDotColor" WhatsappApp --include=*.cs --include=*.xaml | grep -v "/obj/"
```

Expected: no output.

- [ ] **Step 7: Run the guards and the VM build**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
xmllint --noout WhatsappApp/Pages/ChatsPage.xaml WhatsappApp/Pages/ChatPage.xaml
```

then the three VM commands from Task 3 step 6.
Expected: all `OK`; `Errori: 0`, `Avvisi: 2`.

- [ ] **Step 8: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add WhatsappApp
git commit -m "fix: drop the invented online status and the redundant contact sync"
```

---

### Task 10: Remove the dead code the reading found

Three verified sources of dead code. Each was checked with `grep` over the whole repo:

- `CommunicationService.StartServerAsync` — a server-mode listener in an app that only ever connects out; nothing calls it.
- `WhatsappApp/Models/ServerConfig.cs` — no reference outside its own file.
- `Contact.AvatarColor`, `Contact.AvatarUri`, `Contact.Status` — written in a few places, read by nothing (the avatar colours come from `InitialToColorConverter` on `Initials`).

**Files:**
- Delete: `WhatsappApp/Models/ServerConfig.cs`
- Modify: `WhatsappApp/Services/CommunicationService.cs`, `WhatsappApp/Models/Contact.cs`, `WhatsappApp/Pages/ChatsPage.xaml.cs`, `WhatsappApp/Services/DataService.cs`, `WhatsappApp/WhatsappApp.csproj`

**Interfaces:**
- Consumes: nothing.
- Produces: `CommunicationService` without `StartServerAsync`; `Contact` reduced to the properties the UI actually binds.

- [ ] **Step 1: Confirm each is still unreferenced**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -rn "StartServerAsync\|ServerConfig\|AvatarColor\|AvatarUri\|contact.Status" WhatsappApp --include=*.cs --include=*.xaml | grep -v "/obj/"
```

Expected: only the definitions and the write-only assignments listed above. If a real usage shows up, drop that item from this task and say so in the commit message.

- [ ] **Step 2: Delete the model file and its project entry**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git rm WhatsappApp/Models/ServerConfig.cs
```

then remove the matching line from `WhatsappApp/WhatsappApp.csproj`:

```xml
    <Compile Include="Models\ServerConfig.cs" />
```

- [ ] **Step 3: Delete the unreachable server mode**

In `WhatsappApp/Services/CommunicationService.cs`, delete the whole `StartServerAsync` method (line 202 onward, up to its closing brace). Keep `ConnectToServerAsync`, `SendMessageAsync`, `SendControlAsync`, `Prewarm` and `IsConnected`.

- [ ] **Step 4: Delete the write-only `Contact` properties**

In `WhatsappApp/Models/Contact.cs`, delete `_status`/`Status`, `_avatarColor`/`AvatarColor` and `_avatarUri`/`AvatarUri`.

Then remove every assignment to them:

- `WhatsappApp/Services/DataService.cs`: the two `AvatarColor = "#FF075E54",` lines (`OnNetworkMessageReceived` and `ApplyContact`) and the `Status = "",` line if it is still there.
- `WhatsappApp/Pages/ChatsPage.xaml.cs`: the `AvatarColor = "#FF075E54",` line in `NewChatButton_Click`.

- [ ] **Step 5: Check nothing references them**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -rn "ServerConfig\|StartServerAsync\|AvatarColor\|AvatarUri" WhatsappApp --include=*.cs --include=*.xaml --include=*.csproj | grep -v "/obj/"
```

Expected: no output.

- [ ] **Step 6: Run the guards and the VM build**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
```

then the three VM commands from Task 3 step 6.
Expected: `OK: 27 C# file(s) are C# 5 compatible` (one file fewer), everything else `OK`; `Errori: 0`, `Avvisi: 2`.

- [ ] **Step 7: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add -A WhatsappApp
git commit -m "refactor: remove the unreachable server mode and the write-only model fields"
```

---

### Task 11: Record what was learned in the project skills

The rules this plan adds must outlive it: the screens have documented data limits, the adapter output is English, and the dead-code and prescription rules are facts about the platform (a member of `Windows.winmd` is not proof that the phone implements it). This task updates the skills, refreshes the stale numbers they carry, ticks this plan's checkboxes, and runs the whole gate one last time.

**Files:**
- Modify: `.agents/skills/{maintain-the-app,test-the-app,update-the-app,release-the-app,run-the-login-server}/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md`

**Interfaces:**
- Consumes: everything above.
- Produces: no code.

- [ ] **Step 1: Find the numbers that this plan invalidated**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
grep -rn "36\|47\|50\|53\|90 chiav\|90 key\|27 C#\|25 C#\|102" .agents/skills/*/SKILL.md README.md README.it.md
```

Update every hit to the value the guards print now (tests, resw keys, C# file count). A skill that quotes a stale number teaches the next reader to distrust it.

- [ ] **Step 2: Add the output-language rule to `maintain-the-app`**

Add one constraint to the numbered list: everything a person reads outside the app is English (adapter logs, `text:` frames, thrown errors, console scripts, the legacy relay); the app UI is the only localized surface, through `.resw` pairs; Italian code comments stay Italian.

- [ ] **Step 3: Add the data-limit rule**

In the same skill, add a short section: a screen may only show data that exists upstream. State the two facts this plan established — GOWA has no status endpoint (so the Status page is empty on purpose) and its call records are incoming-only and read by scanning the most recent chats — and the rule that follows: when a limit exists, the screen and the README pair say so, and no screen invents state (presence, "last seen") that no server sends.

- [ ] **Step 4: Add the two gate lessons to `test-the-app`**

- `tools/check-resw.js --strict` reads `Loc.Get("...")` occurrences in comments too, so a diagnostic label must be built as `"Loc.Get key " + key`, never with the literal call shape inside the string.
- `Windows.winmd` membership is not a runtime guarantee: `AesGcm`, `AesCbc`, `DisplayRequest` and `RequestActive` are all listed and still returned `E_NOTIMPL` on the device. `Services/SelfCheck.cs` exists for exactly this, and its output is the evidence to ask for.

- [ ] **Step 5: Add the English-output recipe to `update-the-app`**

A short recipe: change the string in the adapter or the tool, update the test that asserts it (the adapter tests assert on `message-format` output), run `npm test`, then read every file with output in it before claiming the change is complete — `grep -nE "fail\(|console\.(log|error)|text: "` finds the surface.

- [ ] **Step 6: Note the protocol additions in `release-the-app`**

Add a checklist line: a new control command or frame is additive and must be ignored gracefully by an older app, and the adapter's protocol tables in both READMEs must list it in the same commit.

- [ ] **Step 7: Tick this plan**

In `docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md`, mark each finished step `- [x]` and add a short `## What execution changed about this plan` section listing every place the code, the guards or the build forced a change (the `addClientForTest` hook and the socket argument shape in the server tests are already one, and there will be more).

- [ ] **Step 8: Run the whole gate**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
node tools/check-csharp5.js
node tools/check-icons.js
node tools/check-resw.js --strict
node tools/check-docs.js
node tools/qr-term.js --self-test
cd WhatsappBridge && npm test
```

Expected, with the counts this plan produces: C# files compatible, 13 icons / 9 distinct, 100 resw keys, 2 doc pairs aligned and 0 emoji, all QR self-checks passed, 62 tests pass.

- [ ] **Step 9: Build the app one last time**

Run the three VM commands from Task 3 step 6.
Expected: `COPIA=0`, `Errori: 0`, `Avvisi: 2`, `WhatsappApp_1.0.1.0_x86_Debug.appxbundle` produced.

- [ ] **Step 10: Commit**

```bash
cd /Users/vincenzo/Documents/WhatsappForWP
git add .agents docs
git commit -m "docs: record the output-language and data-limit rules in the project skills"
```

---

## Deliberately not in this plan

- **Removing the Status tab.** The user chose "keep the tab, make the screen honest", so it stays. Removing it (with `Nav_Status`, `StatusPage_*`, `StatusPage.xaml(.cs)`, the `IconStatus` comment and the `AppSection.Status` case) is a coherent single commit if the permanent empty tab turns out to be worse than no tab.
- **Batching the contact sync.** The adapter sends one `contact` frame per contact and the app adds each to a bound `ObservableCollection`, so the first sync of a real account re-renders the list once per contact. Task 9 removes the repeated sync, which is the cheap half. The remaining half needs a `contacts.begin`/`contacts.end` pair so the page can detach its `ItemsSource` during the burst; it is not worth the protocol surface until a real contact list shows it matters.
- **Outgoing call records.** GOWA stores incoming calls only (`CreateIncomingCallRecord`); there is nothing to read for the calls this account makes.
- **Status updates read from the `status@broadcast` chat.** `GET /chats` may list a `status@broadcast` chat and `GET /chat/:chat_jid/messages` might return the updates from it. It is unverified, ephemeral by nature, and the adapter deliberately filters that chat today; a plan of its own would be needed to find out whether it is even populated.
- **Reactions.** `message.reaction` is still ignored: the app has nowhere to render a reaction, and adding one is a UI project, not a bug fix.
- **AES-256-CBC + HMAC transport.** Done in `docs/superpowers/plans/2026-09-26-cbc-transport-and-runtime-failures.md`: the device reported `NotImplementedException 0x80004001` for AES-GCM, so the app now writes cipher tag 2 (CBC + HMAC) and the adapter accepts both tags.

## Self-review

- **Spec coverage.** "Server output in English" → Tasks 1 and 2 (adapter, tools, legacy relay) with a grep gate. "Finish the unfinished screens" → Task 3 (Status, honest) and Tasks 4-5 (Calls, real data). "Find and fix bugs" → Tasks 6-7 (dropped webhook events), 8 (send status, unread badge, dropped messages), 9 (invented presence, redundant sync), 10 (dead code).
- **Placeholder scan.** No step says "add validation", "handle edge cases" or "similar to Task N". Every code-changing step carries the code, and the two mechanically-long translations carry the exact before/after pair for every line, with a runnable check at the end (Task 1 step 9, Task 2 step 1).
- **Type consistency.** `collectCalls`/`parseCallMetadata` signatures, the five `ChatMessage` members, the `call`/`calls.done`/`revoked`/`edited` command names, `RelatedMessageId`, `DataService.Calls`/`CallsScanCompleted`/`ClearCalls`/`Start`, and the `CallsPage` element names (`CallsListView`, `StatusText`, `RefreshButton`) are used with the same spelling in every task that touches them. `CallLogEntry` is defined before its only consumer, `CallsPage.xaml`, references its `Initials`, `Detail` and `TimeText`.
