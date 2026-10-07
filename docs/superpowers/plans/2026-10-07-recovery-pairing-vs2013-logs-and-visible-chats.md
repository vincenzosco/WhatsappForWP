# Recovery Pairing, Visual Studio Logs And Visible Chats Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the phone get its frame key and its token back by itself after any reinstall — no operator action, nothing typed — make the app's own log lines appear in the Visual Studio 2013 Output window, and make the conversation list actually show up (or say why it is empty).

**Architecture:** The phone's `BridgeKey` and `Token` live in `ApplicationData.Current.LocalSettings`, which Windows Phone 8.1 deletes on uninstall — that is a platform fact, not a bug, and no app-private store on this platform survives it. The one thing that does survive is the package-specific hardware token the app already uses as its device id (`SettingsService.DeviceId`, `diag.log`: `device id: hardware (survives a reinstall)`). So recovery is designed instead of persistence: when a frame arrives that the server's own key cannot open but the compiled default key can, and its `SenderId` is a device the user store already knows, and the command is a pairing request, the adapter answers that socket **under the default key** and pins the pairing window to that one device. The phone then completes, by itself, the pairing it already attempts today (`PairingService.TryAutoPairAsync`), and the existing `paired` frame hands back both the key and the token. Nothing else changes for a server that has no key of its own: its window stays open to anyone, exactly as now. Then the app stops calling a mute socket "connected" — it waits for the server's first frame before announcing anything, which is what makes this failure legible instead of silent; the Chats page turns the adapter's `error` frame into a sentence; and `Diag` emits through one place that writes to `Debug.WriteLine` and to `Debugger.Log`, so the lines are in the Output window even from a Release build.

**Tech Stack:** C# 5 / WinRT XAML on Windows Phone 8.1 (MSBuild 12.0, ARM); Node.js adapter and guards in `WhatsappBridge/` and `tools/` with `node:test`; Docker image `ghcr.io/vincenzosco/docker-whatsappforwp:latest` on the NAS.

**Spec:** the user's request of 2026-10-07 — *"FAI IN MODO CHE TUTTO FUNZIONI E VENGA GENERATO IN AUTOMATICO, CHIAVE, TOKEN E TUTTO SENZA RIESSERE GENERATO O PERSO DOPO UN ALTRA BUILD/INSTALLAZIONE APP, E CHE I LOG DELL'APP, SIANO VISIBILI ANCHE DA VS2013, INOLTRE FAI COMPARIRE LE CHAT"* — and the run that produced it. On 2026-10-07 the app was reinstalled at 07:24 and the phone's own `diag.log` (extracted with `ISETool.exe ts de 7ccc5b77-3cf2-4020-92a7-9542b250bb49 <dir>`) reads:

```
=== run started 2026-10-07T13:02:34 ===
ok: device id: hardware (survives a reinstall)
ChatCache.Load: FileNotFoundException 0x80070002 ...
PairingService/pair: TimeoutException 0x80131505 the server did not answer the pairing
ok: connected to 192.168.0.108:8585
out login.qr
out status
```

The adapter log for the same minute reads `[ERR] invalid frame from the app: Invalid HMAC signature` eight times, and `/data/bridge-key` still holds the key the *previous* install generated. The chain is therefore proven: the reinstall emptied the app's storage, so the phone writes with the compiled default key while the server holds its own; `openPairing()` never reopens a window once the server has a key (`server.js`: `if (!cryptoHelper.usingDefaultKey()) return false;`); `AUTH_STRICT_DEVICE=on` refuses a known device that brings no token, so pairing is the only path that restores key *and* token; and the decode failure is logged and the socket left open, so the app's `IsConnected` stays true with nothing flowing. Prior art this plan extends: `2026-10-06-automatic-pairing-on-first-open.md`, `2026-10-03-one-token-per-device.md`, `2026-10-03-why-the-chats-are-empty.md`.

## Global Constraints

- C# 5: no `await` inside a `catch` or `finally` (CS1985); no `?.`, no `$"..."`, no `nameof`, no pattern matching, no auto-property initializers, no `out var`.
- Every file LF, no BOM: `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <files>`.
- Gate before every commit (twelve guards plus the one Task 2 adds): `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js && node tools/check-diagnostics.js && node tools/check-handshake-answer.js`, then `node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- There is no C# test harness: a C# task's tests are the guards, the ARM build and the phone run. A guard added for a C# rule has real tests under `tools/test/`.
- Build ARM only, never Any CPU: `MSYS_NO_PATHCONV=1 "/c/Program Files (x86)/MSBuild/12.0/Bin/MSBuild.exe" WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=ARM /v:m`. After every build `git checkout -- WhatsappApp/Package.appxmanifest`, and **never** the `.csproj`.
- A new `.cs` file needs its `<Compile Include="Services\X.cs" />` **appended to the end of the `Services` block** in `WhatsappApp.csproj` (that block is not alphabetical), or `check-project-files.js` fails.
- A new string needs its key in **both** `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw`; `check-resw.js --strict` fails a key present in one. Italian strings write `e'`, never `è`.
- `check-resw.js` reads every literal `Loc.Get("...")` as a key lookup, so a diagnostic label is never spelled as a `Loc.Get` call: the `Diag` lines in this plan are English sentences.
- `Diag.Failed` and `Diag.Ok` deduplicate by text, so a line that repeats in a loop must carry the count or the changing value that makes repeats distinct.
- Adapter tests (`WhatsappBridge/test/`) and guard tests (`tools/test/`) are written in **Italian** with an explanatory block comment above them, like their neighbours in `WhatsappBridge/test/server.test.js` and `tools/test/check-diagnostics.test.js` (which has 8 tests, all Italian). The app's `Diag` sentences stay English, and the resw strings are localized in both languages.
- `WhatsappBridge/` is the source of truth for the adapter and the Docker repository (`vincenzosco/docker-whatsappforwp`) holds a copy in `server/`. **A commit that touches `WhatsappBridge/` is not finished until the same change is mirrored there and pushed to `origin main`** (`node tools/sync.js --from <this checkout>`, then `--check`).
- Push is the rule: every task ends with `git push origin master` and, for an adapter task, the mirror push. No apostrophes in commit messages; no emoji in documents except U+26A0.
- The expected counts: every guard `OK`; `node --test "tools/test/**/*.test.js"` with the same 4 pre-existing `download.test.js` failures and no new ones (`zip` is not on this host); `cd WhatsappBridge && npm test` green with the count grown by this plan's new tests (290 at the time of writing).
- Nothing in this plan may make a *first* pairing harder: a server that has no key of its own keeps its window open to any device, which is the trust-on-first-use behaviour `2026-10-06-automatic-pairing-on-first-open.md` shipped on purpose.

## Review Focus

- **A stranger holding the public key pairing itself.** The compiled default key is in this public repository, so anyone can write a frame the adapter will now try to open a second time. The recovery must therefore be refused unless the frame's `SenderId` is a clientId the user store already has, and must never accept a command other than `pair.code` or `pair` from such a frame. Task 1 Step 4 owns it, with the test in Step 3 that asserts an unknown `SenderId` is refused.
- **A second device inheriting a window opened for the first.** Once a recovery window is pinned to one clientId, another device must be refused, not silently allowed to pair inside it. Task 1 Step 4 owns it (`pairingIsOpen(clientId)`, `pairing.clientId`).
- **A reply the phone cannot read.** The `paired` frame has to leave under the passphrase the socket is reading with, or the phone times out exactly as it does today; and the socket must stop reading the default once the server has adopted the new key, or the next reply on it is unreadable. Task 1 Steps 5 and 6 own it, with the test that decodes the reply with the default passphrase.
- **`Debugger.Log` missing from the WP8.1 surface.** It must break the ARM build, not silently produce no log line at all; if it is absent, the plan keeps `Debug.WriteLine` and says so. Task 4 Step 3 owns it.
- **A chat list the adapter refused.** The adapter answers `error` and then `chats.done` when WhatsApp is not connected, and `chats.done` with zero rows looks exactly like an empty account; the page must say which of the two happened. Task 3 Step 2 owns it.

---

### Task 1: A device the store knows re-pairs itself, under the public key, from the adapter

**Files:**
- Modify: `WhatsappBridge/crypto-helper.js` (`encryptPayloadWith`, `decodePayloadWith`; `decryptCbc` refactored to take keys)
- Modify: `WhatsappBridge/server.js` (`frameFor`, the `socket.on('data')` catch, `openPairing`, `pairingIsOpen`, `case 'pair.code'`, `case 'pair'`)
- Modify: `WhatsappBridge/config.js` (`PAIRING_RECOVER`, `pairing.recover`)
- Test: `WhatsappBridge/test/crypto-helper.test.js`, `WhatsappBridge/test/server.test.js`, `WhatsappBridge/test/config.test.js`
- Docs: `README.md`, `README.it.md`, `.agents/skills/maintain-the-app/SKILL.md`, `.agents/skills/run-the-login-server/SKILL.md`

**Interfaces:**
- Consumes: `keyStore.newPairingCode/normalizeCode/formatCode`, `users.findByClientId`, `cryptoHelper.keysFor/setPassphrase/usingDefaultKey/DEFAULT_PASSPHRASE`, `createBridge({config, gowa, log, users})`.
- Produces:
  - `cryptoHelper.encryptPayloadWith(passphrase, jsonString, tag) -> Buffer` — the same layout as `encryptPayload` (one cipher-tag byte, then `[16-byte IV][CBC ciphertext][32-byte HMAC]`), keyed with `keysFor(passphrase)`. CBC only: the app cannot write AES-GCM on WP8.1 (see the header comment in `crypto-helper.js`).
  - `cryptoHelper.decodePayloadWith(passphrase, payload) -> string` — the inverse; throws `Invalid HMAC signature` on a wrong passphrase and `Unknown cipher tag: N` on anything but tag 2.
  - `config.pairing.recover: boolean`, from `PAIRING_RECOVER` (`'off'` disables it, absent or anything else leaves it on).
  - `pairing.clientId: string` — empty means the window belongs to any client (unchanged behaviour); a non-empty id means only that `SenderId` may use it.
  - `frameFor(socket, jsonObject)` keys the payload with `socket.wp8Passphrase` when that is set, and with the current server keys when it is not.

- [ ] **Step 1: Write the failing crypto tests**

In `WhatsappBridge/test/crypto-helper.test.js`, beside the existing `decodePayload` tests:

```js
test('encryptPayloadWith scrive un frame che la stessa passphrase rilegge', () => {
  const payload = cryptoHelper.encryptPayloadWith('una-passphrase', '{"a":1}');
  assert.strictEqual(cryptoHelper.decodePayloadWith('una-passphrase', payload), '{"a":1}');
  assert.strictEqual(payload[0], cryptoHelper.CIPHER_CBC_HMAC, 'il tag e scritto nel payload');
});

test('decodePayloadWith rifiuta unaltra passphrase', () => {
  const payload = cryptoHelper.encryptPayloadWith('una-passphrase', '{"a":1}');
  assert.throws(() => cryptoHelper.decodePayloadWith('unaltra-passphrase', payload),
    /Invalid HMAC signature/);
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/crypto-helper.test.js`
Expected: FAIL — `cryptoHelper.encryptPayloadWith is not a function`.

- [ ] **Step 3: Implement the passphrase-scoped pair**

In `WhatsappBridge/crypto-helper.js`: refactor `decryptCbc(payload)` into `decryptCbcWith(keys, payload)` that keeps its two current guards and its message strings, and have `decryptCbc(payload)` call it with `{ encKey: ENC_KEY, macKey: MAC_KEY }`. Then add `encryptPayloadWith` and `decodePayloadWith` on top of `keysFor(passphrase)` — `decodePayloadWith` reads the tag byte first and throws `Unknown cipher tag: ` + `payload[0]` for anything that is not `CIPHER_CBC_HMAC`. Export both.

- [ ] **Step 4: Write the failing adapter tests**

In `WhatsappBridge/test/server.test.js`, extend the `connectClient(port)` helper with an optional second argument `{ passphrase }`: when it is given, the helper decodes replies with `cryptoHelper.decodePayloadWith(options.passphrase, payload)` and writes with `cryptoHelper.encryptPayloadWith(options.passphrase, JSON.stringify(msg), tag)` instead of the current helpers. Then add, with the same block-comment style as their neighbours:

```js
/**
 * Il recupero: il telefono ha perso la sua chiave (una reinstallazione svuota
 * la memoria dellapp) e scrive con la passphrase pubblica. Il server ha una
 * chiave propria, quindi non aprirebbe nessuna finestra. Se il device id che
 * il frame dichiara e gia nello store, la finestra si apre per quel solo
 * dispositivo e la risposta esce con la passphrase che il telefono sa leggere.
 */
test('un dispositivo conosciuto rifa il pairing da solo anche se il server ha una chiave', async () => { ... });
test('un device id sconosciuto non apre nessuna finestra di recupero', async () => { ... });
test('una finestra di recupero appartiene a un solo dispositivo', async () => { ... });
```

Assertions the three tests must make:
1. after a default-keyed `pair.code` whose `SenderId` is a registered device: a `pair.info` arrives **readable with the default passphrase**, `bridge.getPairingCode()` is not null, and a following `pair` sealed with that code (`cryptoHelper.sealWith(code.normalizeCode(...), '{"BridgeKey":"<>=32 chars>"}')`) answers `paired` with a `Token` that `users.verify` accepts;
2. a default-keyed `pair.code` whose `SenderId` is in no store row: no `pair.info`, `bridge.getPairingCode()` still null, and the container log carries the refusal (pass a `log` collector and assert on its text);
3. with the window pinned to `phone-1`, a default-keyed `pair` whose `SenderId` is `phone-2` answers `pair.failed`.
   Build each bridge the way the existing pairing tests do — `process.env.BRIDGE_KEY` plus `cryptoHelper.setPassphrase(...)` in a `try/finally` that restores both — and start the socket path with `await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r))`, closing it in the `finally`.

- [ ] **Step 5: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL — no `pair.info` arrives for the first test (the frame is still dropped) and the other two fail on the missing refusal.

- [ ] **Step 6: Implement the recovery in `server.js`**

- `openPairing(clientId)` writes `clientId: ''` into the window it opens (the startup call and the `pair.code` reopen keep passing no id, so their behaviour is unchanged).
- `pairingIsOpen(clientId)` keeps its expiry check and adds: refuse when `pairing.clientId` is non-empty and differs from `String(clientId || '')`.
- New `openRecovery(clientId)`: refuse unless `pairingEnabled` and `config.pairing.recover`; refuse unless `clientId` is non-empty **and** `users.findByClientId(clientId)` returns a row; refuse when `cryptoHelper.usingDefaultKey()` (that server's window is already open to anyone); otherwise open the window pinned to `clientId` and log `RECOVERY PAIRING: the device <id> lost its key: the window is open for it alone`.
- `case 'pair.code'` reads `const clientId = String(msg.SenderId || '').trim()`, tries `pairingInfo(clientId)`, then `openPairing()` and `openRecovery(clientId)`, and answers `sendToClient(socket, ...)` as today.
- `case 'pair'` calls `pairingIsOpen(String(msg.SenderId || '').trim())`.
- In the `socket.on('data')` handler's `catch`, before the existing `logger('ERR', ...)`: when `err.message` is exactly `Invalid HMAC signature`, try `cryptoHelper.decodePayloadWith(cryptoHelper.DEFAULT_PASSPHRASE, payload)`; on success, accept the message **only** when `Command` is `pair.code` or `pair` (log `a frame written with the public default key is not a pairing request: refused` otherwise, and drop it), set `socket.wp8Passphrase = cryptoHelper.DEFAULT_PASSPHRASE`, and hand it to the same `socket.chain` the readable path uses. The failure of the second attempt falls through to the existing log line.
- After a successful pairing, `delete socket.wp8Passphrase` once the new key is adopted, so the next reply on that socket is written with the new key.

- [ ] **Step 7: Add `PAIRING_RECOVER` to the config and its test**

`WhatsappBridge/config.js`: `PAIRING_RECOVER: 'on'` among the pairing defaults, and `recover: pick(env, 'PAIRING_RECOVER').toLowerCase() !== 'off'` beside `enabled` and `ttlMs`. In `WhatsappBridge/test/config.test.js` assert that the default is `true` and that `PAIRING_RECOVER=off` makes it `false`.

- [ ] **Step 8: Run the whole adapter suite and the guard gate**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, with the previous count plus five new tests and the two existing pairing tests (`il pairing adotta la chiave del telefono...`, `un server che ha gia una chiave non apre la finestra di pairing`) still green.
Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node tools/check-fire-and-forget.js && node tools/check-project-files.js && node tools/check-chat-list-source.js && node tools/check-diagnostics.js`
Expected: every guard prints `OK`.

- [ ] **Step 9: Document the flag and the invariant**

`README.md` and `README.it.md` (the same section, English and Italian): `PAIRING_RECOVER` — what it allows (a device the store already knows may re-open the pairing window for itself with the public key after losing its storage), what it costs (that device id becomes the credential for that window; the id sits in `users.json` in plain text, so the file is a credential), and how to turn it off. `.agents/skills/run-the-login-server/SKILL.md` lists it beside `PAIRING` and `PAIRING_TTL_MIN`; `.agents/skills/maintain-the-app/SKILL.md` gains the invariant: *a reinstall must need no operator action: a device the store knows re-pairs itself*.

- [ ] **Step 10: Commit, push, mirror**

```bash
git add WhatsappBridge README.md README.it.md .agents/skills
git commit -m "fix: let a known device re-pair itself after a reinstall"
git push origin master
```

Then mirror (`node tools/sync.js --from <this checkout>` twice, `--check` printing `OK: server/ matches the adapter`), run `(cd server && npm test)`, commit and `git push origin main` in the Docker repository.

---

### Task 2: The phone stops calling a mute socket connected

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs` (`ConnectToServerAsync`, after `SendFrameAsync(handshake)`)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw` (`CommService_NoAnswer`)
- Create: `tools/check-handshake-answer.js`
- Create: `tools/test/check-handshake-answer.test.js`
- Create: `tools/handshake-fixtures/communication-service-good.cs`, `tools/handshake-fixtures/communication-service-no-wait.cs`, `tools/handshake-fixtures/communication-service-announces-first.cs`
- Modify: `.agents/skills/test-the-app/SKILL.md` (the gate chain and the guard table)

**Interfaces:**
- Consumes: `CommunicationService.LastInboundUtc` (set on connect and on every frame read), `ListenForMessagesAsync(attempt, reader)`, `DisposePublishedSocket()`, `Diag.Failed`, `Guarded.RunGuardedAsync`.
- Produces: `private const int HandshakeAnswerMs = 20000;` and `private async Task<bool> WaitForServerAnswerAsync(int attempt, DateTime sentAtUtc)` — true when a frame arrived after `sentAtUtc`, false when the deadline passed, the attempt was superseded (`attempt != _connectionId`) or the socket went away (`!_isConnected`). On false the caller logs `Diag.Failed("ConnectToServerAsync/no answer", new TimeoutException("the server did not answer the handshake within " + HandshakeAnswerMs + " ms: the cipher keys do not match")  )`, disposes the published socket, raises `CommService_NoAnswer` through `RaiseErrorOccurred` and returns false.

- [ ] **Step 1: Write the failing guard test**

`tools/test/check-handshake-answer.test.js`, modelled on `tools/test/check-diagnostics.test.js` (a `node:test` file that runs the guard against the fixtures and asserts on its `problems` list), with Italian test names like its neighbours: one case expecting `[]` for `communication-service-good.cs`, and one case each for `-no-wait.cs` and `-announces-first.cs` expecting that particular problem's text.

- [ ] **Step 2: Run it to verify it fails**

Run: `node --test tools/test/check-handshake-answer.test.js`
Expected: FAIL — `Cannot find module '../check-handshake-answer'`.

- [ ] **Step 3: Write the guard and its fixtures**

`tools/check-handshake-answer.js`, shaped like `tools/check-fire-and-forget.js` (a `problems` array, a per-rule function reading the file, `console.log('OK: ...')` when empty, `process.exit(1)` with the numbered problems otherwise). Rules, against `WhatsappApp/Services/CommunicationService.cs`:
- **A** a constant named `HandshakeAnswerMs` exists and its literal value is `>= 20000`;
- **B** `RaiseConnectionEstablished()` appears **after** the call to `WaitForServerAnswerAsync` in the file;
- **C** `WaitForServerAnswerAsync` reads both `LastInboundUtc` and `_connectionId`;
- **D** the no-answer path contains a `Diag.Failed(` whose first argument is the literal `"ConnectToServerAsync/no answer"`;
- **E** `Task.Run(() => ListenForMessagesAsync(` appears **before** the `WaitForServerAnswerAsync` call (the reader must be running while the wait polls, or the answer is never seen).
The three fixtures are copies of the `ConnectToServerAsync` region: the good one, one with no wait, one that announces the connection before the wait, one with the reader started after the wait.

- [ ] **Step 4: Run the guard and its test**

Run: `node tools/check-handshake-answer.js; echo "exit=$?"` and `node --test tools/test/check-handshake-answer.test.js`
Expected: the guard reports the problems against the untouched `CommunicationService.cs` (it does not pass yet), and the test passes for all three fixtures.

- [ ] **Step 5: Add the string**

`CommService_NoAnswer` in both `.resw` files: en-US `The server did not answer: this phone and the server are not using the same key.`, it-IT `Il server non ha risposto: questo telefono e il server non usano la stessa chiave.`

- [ ] **Step 6: Implement the wait**

In `ConnectToServerAsync`, after the handshake frame is written and before the `DispatchOnUiThread` block that raises `CommService_Connected`:
1. start the reader exactly as the current code does (`#pragma warning disable 4014` / `Task.Run(() => ListenForMessagesAsync(attempt, reader))` / `#pragma warning restore 4014`), so it is inside the `#pragma` region `check-fire-and-forget.js` scans;
2. the anchor the wait compares against is the value `LastInboundUtc` holds when the socket is published (the `LastInboundUtc = DateTime.UtcNow;` in the successful branch, just above `_isConnected = true;`): capture it in a local time and pass that local to the wait, because the wait's test is `LastInboundUtc > that local`, and it must be false until a frame really arrives;
3. `bool answered = await WaitForServerAnswerAsync(attempt, answerSince);` and only then raise `CommService_Connected` and `ConnectionEstablished`;
4. on `false`, in the same branch, `Diag.Failed`, `_isConnected = false`, `DisposePublishedSocket()`, and — only when `attempt == _connectionId` and `!silent` — `RaiseErrorOccurred(Loc.Get("CommService_NoAnswer", ...))`, then `return false`.
   The wait itself polls every 200 ms with `await Task.Delay(200)`, and returns as soon as `LastInboundUtc > sentAtUtc`, or when `attempt != _connectionId`, or when `!_isConnected`, or at the deadline.

- [ ] **Step 7: Run the guard, the gate and the build**

Run: `node tools/check-handshake-answer.js` (expect `OK`), the twelve existing guards (expect `OK` each), `node --test "tools/test/**/*.test.js"` (expect the same 4 pre-existing `download.test.js` failures and no others), and the ARM rebuild (expect `0` lines matching `warning CS|error CS` in the log and the package created).

- [ ] **Step 8: Update the skill gate block**

`.agents/skills/test-the-app/SKILL.md`: `check-handshake-answer.js` joins the chain in the gate block and the guard table, with the rule it enforces (the phone announces a connection only after the server has answered).

- [ ] **Step 9: Commit and push**

```bash
git add WhatsappApp tools .agents/skills
git commit -m "fix: do not announce a connection the server never answered"
git push origin master
```

---

### Task 3: The Chats page says why the list is empty

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs` (`case "error"`, the `AdapterError` event)
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs` (`RequestChats`'s two early returns, an `OnAdapterError` handler, subscribe/unsubscribe beside the existing `ServerUnavailable` wiring)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw` (`ChatsPage_AdapterError`)
- Test: none in C#; the guards, the ARM build and the phone run of Task 5 are the test

**Interfaces:**
- Consumes: `DataService.OnControlMessageReceived` (`case "error"` at present calls `ClearMediaLoading(message)`), `ChatsPage.ServerUnavailableBar`, `ChatsPage.ServerUnavailableText`.
- Produces: `public event EventHandler<string> AdapterError;` on `DataService`, raised from `case "error"` when `message.Text` is not empty, after `ClearMediaLoading(message)`; `ChatsPage` sets `ServerUnavailableText.Text` to the localized prefix plus the adapter's own sentence and shows the bar.

- [ ] **Step 1: Make the early returns say why**

In `RequestChats`, before each of the two `return`s, add a `Diag.Ok` that names the values that stopped the request: `chats not requested: connected=<IsConnected> whatsapp=<WhatsAppState>` and the same line is not written twice in the same run — `Diag.Ok` deduplicates, so include the values (they change, so the line changes).

- [ ] **Step 2: Surface the adapter's own words**

Add the `AdapterError` event to `DataService` and raise it in `case "error"` for a non-empty `Text`. In `ChatsPage`: subscribe in `OnNavigatedTo` beside `ServerUnavailable` and unsubscribe in `OnNavigatedFrom`; the handler writes

```csharp
ServerUnavailableText.Text = Loc.Get("ChatsPage_AdapterError", "The server answered:") + " " + text;
```

with the new key in both `.resw` files, and calls `Diag.Ok("adapter error on the chats page: " + text)`.

- [ ] **Step 3: Run the gate and the build**

Run: the twelve guards (expect `OK` each), `node --test "tools/test/**/*.test.js"` (the same 4 pre-existing failures and no others), and the ARM rebuild (0 warnings, 0 errors, package created).

- [ ] **Step 4: Commit and push**

```bash
git add WhatsappApp
git commit -m "fix: show the adapter's answer when the chat list is empty"
git push origin master
```

---

### Task 4: The app's log lines in the Visual Studio 2013 Output window

**Files:**
- Modify: `WhatsappApp/Services/Diag.cs` (the two `Debug.WriteLine("DIAG " + ...)` sites at the frame sink and the line sink)
- Modify: `tools/check-diagnostics.js` (a new rule F)
- Modify: `tools/test/check-diagnostics.test.js` and the fixtures under `tools/diagnostics-fixtures/`
- Modify: `README.md`, `README.it.md` (the Visual Studio section)

**Interfaces:**
- Consumes: `System.Diagnostics.Debug.WriteLine`, `System.Diagnostics.Debugger` (already used at `App.xaml.cs`: `Debugger.IsAttached`).
- Produces: `private static void EmitToDebugger(string text)` — one place, called by both sinks, that writes `Debug.WriteLine("DIAG " + text)` and `Debugger.Log(0, "DIAG", text + "\r\n")`.

- [ ] **Step 1: Add the failing fixture and test**

`tools/diagnostics-fixtures/diag-no-debugger-sink.cs` — a copy of the good fixture with both `Debug.WriteLine("DIAG " + ...)` calls present but no `EmitToDebugger`. In `tools/test/check-diagnostics.test.js`, one more case asserting rule F reports it.

- [ ] **Step 2: Run it to verify it fails**

Run: `node --test tools/test/check-diagnostics.test.js`
Expected: FAIL on the new case (the fixture and the rule do not exist yet).

- [ ] **Step 3: Implement the sink and the rule**

In `Diag.cs`, replace both `Debug.WriteLine("DIAG " + x)` calls with `EmitToDebugger(x)` and add the method. **If `Debugger.Log` is not in the Windows Phone 8.1 API surface the ARM build fails** — `error CS0117: 'Debugger' does not contain a definition for 'Log'` — and in that case keep `Debug.WriteLine` alone inside `EmitToDebugger` and record the failure in the commit message; do not delete the guard rule (it still pins the single sink).
Rule F in `tools/check-diagnostics.js`: `EmitToDebugger` is declared once, contains `Debug.WriteLine(`, is called from both the frame sink and the line sink, and neither sink still contains a literal `Debug.WriteLine("DIAG `.

- [ ] **Step 4: Run the guard, its tests and the gate**

Run: `node tools/check-diagnostics.js`, `node --test tools/test/check-diagnostics.test.js` (expect all cases green, including the eight that already passed), the twelve guards, `node --test "tools/test/**/*.test.js"`, and the ARM rebuild (0 warnings / 0 errors).

- [ ] **Step 5: Document the recipe**

In `README.md` and `README.it.md`, the same section: the app writes every diag line to the debugger as well as to `diag.log`, so `WhatsappApp.sln` opened in Visual Studio 2013 with **Debug**, **ARM** and the phone as the target shows them in **Debug → Windows → Output** when started with **F5** (deploy and debug); for an app already installed use **Debug → Attach to Process** and pick the app; filter the window on `DIAG`. Say plainly what the line above means: without a debugger attached nothing appears, and the file `diag.log` plus the Diagnostics page remain the channels on the phone.

- [ ] **Step 6: Commit and push**

```bash
git add WhatsappApp tools README.md README.it.md
git commit -m "feat: emit every diag line to the debugger as well as the file"
git push origin master
```

---

### Task 5: Deploy, and prove on the phone that nothing is lost any more

**Files:** none (the acceptance run); `.agents/skills/test-the-app/SKILL.md` and `.agents/skills/run-the-login-server/SKILL.md` get the two commands that were missing (the isolated-storage export and the chats check).

**Interfaces:**
- Consumes: `.tools/nas-deploy.sh` (the NAS pulls `ghcr.io/vincenzosco/docker-whatsappforwp:latest` and runs `docker compose ... up -d`), the AppDeploy tool (`/c/Program Files (x86)/Microsoft SDKs/Windows Phone/v8.1/Tools/AppDeploy/AppDeployCmd.exe /install <abs .appx> /targetdevice:de`), `ISETool.exe ts de 7ccc5b77-3cf2-4020-92a7-9542b250bb49 <dir>`.
- Produces: a phone run whose log shows the recovery pairing happening with no operator action, and a chat list with rows.

- [ ] **Step 1: Wait for the image and deploy the adapter**

Confirm the Docker repository's image workflow went green for the mirror commit, then run `.tools/nas-deploy.sh` and confirm inside the container that the new code is there: `docker exec whatsapp-for-wp8 grep -c PAIRING_RECOVER /app/config.js` returns at least 1, and `docker exec whatsapp-for-wp8 env | grep PAIRING_RECOVER` is unset or `on`.

- [ ] **Step 2: Leave the server with a key of its own**

Restore a server-side key so the recovery path is the one under test, not the virgin-server window: `docker exec whatsapp-for-wp8 sh -c 'test -f /data/bridge-key || cp /data/bridge-key.pre-pair-20261007 /data/bridge-key'` if the phone has not paired since, followed by `docker restart whatsapp-for-wp8`, and confirm the log says `no key of its own` is **absent** (that is: the pairing code is *not* printed at startup). If the phone has already paired, the file is there already and the restart is all that is needed.

- [ ] **Step 3: Install the new app, which is itself the test**

Build ARM (Debug) and install with `AppDeployCmd.exe /install`. The install empties the app's storage — that is the condition under test — so do **nothing** on the phone afterwards except open the app. Watch the adapter log for, in this order:

```
RECOVERY PAIRING: the device <the phone's id> lost its key: the window is open for it alone
paired device registered: e3841c1a742a7665 (device)
pairing window closed: the phone sent its key
BRIDGE_KEY replaced with the key generated by this phone
handshake from "<name>"
```

and the absence of `[ERR] invalid frame from the app: Invalid HMAC signature`.

- [ ] **Step 4: Read the phone's own log back**

`ISETool.exe ts de 7ccc5b77-3cf2-4020-92a7-9542b250bb49 <dir>`, then read `IsolatedStore/diag.log`. Expected, with no `TimeoutException` line in it: the run marker, `device id: hardware (survives a reinstall)`, the auto-pair success line, no `CommService_NoAnswer`, and `chat list: <N> row(s), showing <N>` with `N > 0`.

- [ ] **Step 5: Confirm the list is on the screen**

On the phone: the Chats section shows conversation rows and a row opens into its conversation with no crash. If rows are zero, the log now says which of the two happened (the adapter's `error` line or `chats not requested: connected=... whatsapp=...`) — record it in the plan's Status section and fix it there, in this plan, before declaring the task done.

- [ ] **Step 6: Record and commit**

Append a `## Status` section to this plan with the two log excerpts (the adapter's and the phone's) and the row count, then:

```bash
git add docs .agents/skills
git commit -m "docs: record the run where the phone recovered by itself"
git push origin master
```

---

## Self-Review

**Spec coverage.** *"Chiave, token e tutto in automatico senza essere perso dopo un'altra build"* — Task 1 (the adapter answers a known device under the public key, and `paired` carries key and token) plus Task 2 (the app now waits for the answer, so the exchange cannot silently fail) plus Task 5 Steps 3–4 (the proof, by installing a build, which is the very action that empties the storage). Persisting the values on the phone is not attempted anywhere, and the Architecture paragraph says why: no app-private store on WP8.1 outlives an uninstall. *"I log dell'app visibili anche da VS2013"* — Task 4. *"Fai comparire le chat"* — Task 3 (the request is made and its refusal is legible) and Task 5 Steps 4–5 (rows on the screen). The three themes are ordered by dependency: Task 3 is only observable once Task 2 stops the app from believing a mute socket is a connection, and Task 5 needs all of them.

**Step scan.** Every step names the file, the symbol, the assertion or the command, and the outcome. No step says "handle edge cases" or "add validation". The one place a choice is left open — `Debugger.Log` missing from the surface — is a step that names the exact compiler error and the fallback, so it cannot be silently skipped.

**Type consistency.** `encryptPayloadWith`/`decodePayloadWith` are named the same in Task 1's Interfaces, tests and implementation steps; `pairing.clientId` and `pairingIsOpen(clientId)` are used the same way in Steps 4 and 6; `AdapterError` is `EventHandler<string>` in both the interface block and the handler; `HandshakeAnswerMs`, `WaitForServerAnswerAsync(attempt, sentAtUtc)` and `EmitToDebugger(text)` each appear once in an Interfaces block and once in the step that writes them.

**Review Focus coverage.** Each of the five lines names the task and step that owns it and the test that pins it: Task 1 Step 4 (the unknown `SenderId` test, the pinned-window test, the reply decodable with the default), Task 4 Step 3 (the build must break, not lose the line), Task 3 Step 2 (the `error` frame becomes a sentence). The virgin-server case the first line implies is pinned by keeping `un server che ha gia una chiave non apre la finestra di pairing` and `il telefono puo chiedere il codice di accoppiamento invece di leggerlo dal log` green in Task 1 Step 8.

**Proportion.** Five tasks, no code bodies except the two test-shaped blocks whose assertions are the decision and the promise in Step 4 of Task 1, which is the one place a wrong implementation is invisible in the tests until a phone is in the field. The adapter work is one task on purpose: the codec pair and the policy that uses it are one deliverable, and a reviewer could not accept one without the other.

---

## Status

Executed 2026-10-07 on `master`. All six tasks are pushed. One place where this plan's own
steps were superseded is recorded below, together with the run that proves the outcome and the
bug that run found.

### Task 1 was implemented, then replaced

Task 1 was written to keep the pairing window and add `PAIRING_RECOVER=on`, so that a device
the store knew could be re-paired under the public key. That is commit b6ef81e, *fix: let a
known device re-pair itself after a reinstall*, pushed as written.

Mid-session the user asked to drop the pairing key entirely - it is a value to keep in step for
no benefit - and to derive the frame key from the phone's own device id instead, so that the
same token comes back after any reinstall. Task 6 (commit 1fee395, *fix: derive the frame key
from the device id and drop the pairing key*) therefore removed the whole mechanism Task 1 had
added: `WhatsappBridge/key-store.js` and its test, the `pair` and `pair.code` commands, the
pairing window and its tickets, `PAIRING*`, `BRIDGE_REQUIRE_KEY` and `BRIDGE_KEY_FILE`, the
app's `PairingService`, the *Server key* field and button on the connection page, and the eight
pairing strings. `PAIRING_RECOVER` no longer exists in the tree.

What Task 1 promised - a reinstalled phone returns with no operator action - is delivered by the
derivation Task 6 put in its place, and is proved by the run below. The plan's Task 1 steps are
history, not instructions.

### The run that proves it

Commit ae09dc1 (*fix: let a known device return on a service that hands out no tokens*), image
`latest` == `app-ae09dc1d07cf4c3106c2d6e1f208b556a016eb0e`, digest
`sha256:11539cebf97bf016a2e14626961aa9619bd3bba3407da1810e534cf786610945`, pulled and
recreated on the NAS (`Up ... (healthy)`, WhatsApp connected). The app on the phone had been
installed from a build that never saw this server: its isolated storage held no token and no
caches, which is exactly the state a reinstall leaves.

The adapter (`docker logs whatsapp-for-wp8`; 15:20 UTC is 17:20 on the phone):

```
2026-10-07 15:20:32 [OK] device returned: e3841c1a742a7665 (device)
2026-10-07 15:20:32 [NET] handshake from "unknown"
```

The phone (`diag.log`, pulled back with `ISETool ts de`):

```
=== run started 2026-10-07T17:20:26 ===
ok: endpoint 2 server(s), first bore.pub:41417
ok: ping: bore.pub:41417=399 ms, 192.168.0.108:8585=44 ms
ok: connecting: public=True candidates=2 saved=192.168.0.108:8585
out login.qr (x2)
in  registered
in  state
out chats
ok: connected to 192.168.0.108:8585
out diag  previous run did not end
in  chat 
in  chat  Oke
in  chat  Vengo giovedì meglio
...
in  chats.done
ok: chats.done: 22 row(s)
ok: chat list: 22 row(s), showing 22
```

**Row count: 22 conversations, 22 shown.** `/data/users.json` still holds 25 users and exactly
one row for the phone's device id (`e3841c1a742a7665`, name "device"): the reinstall reused its
own account instead of appending a second one.

The `handshake from "unknown"` is the one cosmetic artefact: a freshly installed app has no
stored user name yet, so the recovery `hello` carries no `SenderName` and the line falls back to
`"unknown"`. The store keeps the name it already had (`device`), so nothing is lost by it.

### The bug the run found first

The first run of the same build, against the running container, was refused:

```
2026-10-07 14:47:15 [WARN] refused a handshake: missing token
2026-10-07 14:47:43 [WARN] refused a handshake: missing token
```

and the phone's log showed `in unauthorized` with `ok: chats not requested: connected=False
whatsapp=disconnected` - Task 3's diagnostic doing its job.

Cause: the recovery branch in the `hello` case was gated behind `authRegister`, and the NAS runs
`AUTH_REGISTER=off` with `AUTH_STRICT_DEVICE=on`. On that combination a device the store already
knew could never be handed its derived token, so the reinstall path was dead on precisely the
instance this plan is for. The frame itself decoded fine (a phone with no token keys its `hello`
with the compiled passphrase), so the refusal was the flag alone - visible in the log as the
absence of the `unknown device without a token` line that sits beside it.

Fix (commit ae09dc1): `authRegister` no longer guards the branch, only the admission of a device
the store has never seen:

```js
const known = clientId ? users.findByClientId(clientId) : null;
const mayRegister = known ? true : (authRegister && !authStrictDevice);
```

A known device is always let back in and handed its own derived token; a stranger needs both an
open register and an open door. The refusal log now names the rule that stopped it
(`AUTH_REGISTER is off` / `AUTH_STRICT_DEVICE`) instead of mislabelling both as strict device.
`test/server.test.js` gained *un device noto rientra anche con AUTH_REGISTER spento, uno
sconosciuto no*, which fails on the old code; the `AUTH_REGISTER` row in both adapter READMEs and
the paragraph in both root READMEs now say what `off` does and does not close.

### Verification of the fixed build

- `cd WhatsappBridge && npm test` -> 276/276 pass.
- the thirteen guards -> OK each.
- mirror `docker-whatsappforwp` synced (35 files, `--check` OK, server suite 276/276), pushed
  b20f86d; workflow `image` on b20f86d -> success; NAS container recreated on the new digest.

### The review of this branch, and what it found

Reviewed `eff1591..91fdd9c` along the two axes of `.agents/skills/code-review/SKILL.md`. No
documented standard was broken. Two findings on the Spec axis, both fixed in commit 828f4b2:

1. **Critical.** `sendToClients` grouped outgoing frames by cipher alone and wrote them with
   `cryptoHelper.buildFrame`, the server's own key. A socket that had adopted a passphrase
   (`wp8Passphrase`, set by `decodeWithKnownKeys` for the phone's device-derived key) therefore
   received a `state` - and every incoming message, which is also sent through `sendToClients` -
   under a key it could not read. That is the *normal* state of a phone that holds a token: it
   would have answered the handshake and then never asked for the chats, because the `state` that
   tells it the account is connected never arrived. The live run above only exercised the
   no-token path (compiled key equals the server's key), which is why it looked green: the phone
   in that run had no token, so `decodePayload` opened its frames first time and the socket never
   adopted a passphrase. The grouping key is now `passphrase + '|' + tag` and the frame is written
   by `frameFor`. Pinned by *un telefono con la chiave del suo device legge anche i broadcast*
   (66/67 without the fix, 277/277 with it).
2. **Important, and pre-existing.** `Diag.WriteLinesAsync` created the log with
   `CreationCollisionOption.ReplaceExisting` - an immediate truncation - and wrote in a second
   call, and it marked the lines as written *before* the write was queued. An app suspended
   between the two therefore left a 0-byte `diag.log` with the run marker still in place, and the
   early-return on `_written >= History.Count` kept it empty for the rest of the run. This was
   observed live in this session: the export taken after the successful run came back 0 bytes
   while `diag-run.marker` still said `2026-10-07T17:20:26`, so the run and its crash tail were
   lost - exactly the artefact this plan exists to make readable. The file is now fetched and
   created only when the fetch fails, and `FileIO.WriteTextAsync` truncates and writes in one
   operation on a file that already exists. Guard rule G pins the order, two fixtures and two
   tests were added (9 to 11), and the C# 5 guard answered the first attempt with CS1985
   (`await` in a `catch`), so the fetch leaves a null instead.
3. Judgement call: five Italian names in `WhatsappBridge/crypto-helper.js` (four comments and
   `const scelto`) in a module whose identifiers and comments are English; rewritten. The two
   pre-existing Italian slips in `server.js` are outside this diff and are left alone.
4. Dead code left by the removal, found in the final pass over the tree for the pair names:
   `ChatMessage` still carried `PairingPayload`, `PairingCode` and `PairingSeconds` - the model
   half of the protocol Task 6 deleted, with a doc comment naming the `pair.info` and `pair.code`
   frames - and nothing read or wrote any of them. Task 6 removed the service, the page, the
   strings and the adapter side, but not these three. Removed; `PairCode` is left alone: it is
   the code the phone enters for the WhatsApp phone-number login, a different thing that is still
   used.

### The final run: the device's own key, with the log to prove it

Installing the app with `AppDeployCmd /install` **wipes its isolated storage** (the run at
18:56:50 shows `ChatCache.Load: FileNotFoundException` and `in registered`, i.e. the phone
arrived with no token). That run is therefore the compiled-key path again, and it is the one
that produced the excerpts below. Relaunching without reinstalling is what exercises the
device-key path, and that is the run at 18:58:14, with the log readable this time:

```
=== run started 2026-10-07T18:58:14 ===
...
in  state
out chats
in  chats.done
ok: chats.done: 22 row(s)
ok: chat list: 22 row(s), showing 22
```

and the adapter, for the same run (`docker logs whatsapp-for-wp8`):

```
2026-10-07 16:58:16 [NET] handshake from "unknown"
```

There is **no `device returned`** for that handshake: the phone presented a token that verified,
so it keyed its frames with `CryptoHelper.DevicePassphrase(deviceId)`, the server adopted that
passphrase through `decodeWithKnownKeys`, and the `state` broadcast that followed reached it -
which is precisely what finding 1 above had broken. The 22 rows are the proof that the list was
then asked for and answered. `/data/users.json` still holds 25 users with exactly one row for
the phone's device id. `diag.log` came back at 3267 bytes with no truncation, so finding 2 is
fixed on the device too, and the crash report for the earlier run is intact.
