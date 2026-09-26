# AES-256-CBC Transport and the Two Runtime Failures It Exposes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the app's encrypted channel work on a real WP8.1 phone by moving the transport from AES-256-GCM (which the device answers with `NotImplementedException 0x80004001`) to AES-256-CBC + HMAC-SHA256, and fix the two failures the last debugger log named: the `0x800710DD` storm in `DiscoveryService.OnMessageReceived` and the silent `NotImplementedException` in `ConnectToServerAsync`.

**Architecture:** The payload gains a one-byte *cipher tag* in front of what it carries today, so a frame declares its own cipher; the app writes tag `2` (CBC + HMAC, the one the phone implements) and the adapter accepts `1` (GCM) and `2`, answering each client with the cipher that client used. Two Node tests pin the byte layout and a fixed vector, and the same vector is decrypted on the device by `SelfCheck`, so a divergence between the two key derivations is caught by the debugger log instead of by a blank screen. The discovery handler stops calling `LoadAsync` on a `DataReader` that already holds a datagram, which is what produced the per-datagram `0x800710DD`; `ConnectToServerAsync` names its failing stage, cleans up the half-open socket and tells the user in words what went wrong.

**Tech Stack:** Node 26 (`node:crypto`, `node:test`, zero dependencies) on the adapter side; WinRT crypto (`Windows.Security.Cryptography.Core`) + C# 5 on the WP8.1 side; MSBuild 12 in the Parallels VM as the only compiler available.

## Global Constraints

- **Language/runtime:** the app is C# 5 only (no `$"..."`, `?.`, expression-bodied members, `out var`, `is T x`, `nameof`, `_ =`, async entry points). Trailing `;` after the last enum member is fine. The adapter is CommonJS on Node 26, zero dependencies, tests through `node --test`.
- **The build gate** (C# tasks cannot be compiled on the Mac; this is the only compiler). Define `BUILD` as these four commands, run in this order, and run them as one step:

```
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`, then `Errori: 0, Avvisi: 2` and the two known warnings (CS0618 `FileOpenPicker.PickSingleFileAsync`, CS4014 in `ConnectionPage.xaml.cs`). Building from the shared folder instead of `C:\Temp\wp81` fails with `WMC9999` — always copy first.

- **The guards** (run after every change; all four must stay green):

```
node tools/check-csharp5.js          # OK: 27 C# file(s)
node tools/check-icons.js            # OK: 13 icon path(s), 9 distinct
node tools/check-resw.js --strict    # OK: 91 key(s) ...  (90 before Task 5, see below)
node tools/check-docs.js             # OK: 2 doc pair(s), no emoji in 23 .md file(s)  (22 before this plan)
xmllint --noout WhatsappApp/**/*.xaml
cd WhatsappBridge && npm test        # 45 tests pass at the end of this plan (36 today)
```

- **Wire contract, version 3.** The 4-byte `UInt32LE` length prefix is unchanged. The payload after it is:

| Byte | Meaning |
|---|---|
| `0` | cipher tag: `1` = AES-256-GCM, `2` = AES-256-CBC + HMAC-SHA256 |
| `1..` | tag `1`: `[12-byte IV][GCM ciphertext ‖ 16-byte tag]` |
| `1..` | tag `2`: `[16-byte IV][CBC ciphertext ‖ 32-byte HMAC-SHA256(IV ‖ ciphertext)]` |

  With `BRIDGE_ENCRYPTION=off` the payload is plain UTF-8 with **no** tag byte, exactly as today. Both sides change together: an old app talking to a new adapter (or the reverse) fails on the tag byte, and that is accepted — the log line says so.

- **Cipher policy.** The app writes only tag `2`. The adapter accepts `1` and `2`, remembers the tag of each socket's last inbound frame and answers that socket with the same tag, and uses tag `2` for a socket that has not written yet (the immediate `state` frame), because both ciphers can read it. No negotiation frame is added.
- **Key derivation** (must be byte-identical on both sides): `master = SHA-256(passphrase)`, `encKey = HMAC-SHA256(master, "wp8-adapter enc")`, `macKey = HMAC-SHA256(master, "wp8-adapter mac")`. The passphrase is `BRIDGE_KEY` if set, otherwise `WhatsAppCommunityWP8-2026` — unchanged, and **do not** change it in this plan.
- **HMAC is encrypt-then-MAC** over `IV ‖ ciphertext`, and it is verified **before** `AES-CBC` decryption on both sides.
- **Known-answer vector** (fixed IV, produced by the reference implementation, used by both sides):

```
plaintext  {"Type":0,"Text":"ciao"}
IV         000102030405060708090a0b0c0d0e0f
payload    02000102030405060708090a0b0c0d0e0f2fc17f6d19a9bea8e286ceebf69ca87c72cf5e563e0d09ee3d755fb40f87c336e65a51262cff115a41eb866b8a82275d7d61dfb35bb0f5060ab9f1b316d45e2d
encKey     66dabe299f9c92ce26387137decb849003a0529f89032661078363aa67058cd5
macKey     22f989d8e6065c871097b2f6dc7144ac794f5b46292e9cf71fe81e7041fa5439
```

- **New user-visible string:** exactly one, the resw key `CommService_PlatformMissing` (bare key, read from C#), added to **both** `Strings/en-US/Resources.resw` and `Strings/it-IT/Resources.resw`. No XAML changes, no `x:Uid` changes. `node tools/check-resw.js --strict` therefore reports **91** keys after Task 5 instead of 90.
- **New adapter-side error strings are English** (`'Invalid CBC payload (too short)'`, `'Invalid HMAC signature'`, `'Unknown cipher tag: ...'`). The rest of `WhatsappBridge/**` is still Italian and is being translated by the unexecuted plan below; these new strings are already at the target state.
- **Do not touch** `WhatsappBridge/gowa-client.js`, `message-format.js`, `webhook-server.js`, `discovery.js`, `config.js`, `WhatsappServer/**`, or any `.resw` value other than the one key added in Task 5.
- **The unexecuted plan `docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md` must be patched by Task 6**, in five places, because this plan changes strings it translates and counts it asserts (the `crypto-helper.js` translation row, the `Cifratura:` log row, the `start-login.js` banner row, the parked-CBC note, and its final "99 resw keys / 53 tests" expectation). Leaving it stale would make its Task 1 fail.
- **One commit per task**, English subject, `type: short imperative` (`fix:`, `feat:`, `docs:`). Nothing is pushed until the user asks.
- **Nothing in this plan can be verified on a device from this machine.** The WP8.1 emulator images are x86 and need Hyper-V, which the ARM64 Windows guest does not have. Everything here is verified by the build gate, the guards and `npm test`; Task 7 is the part only the user can run.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `WhatsappBridge/crypto-helper.js` | The whole cipher contract: key derivation, both ciphers, tag byte, frame assembly. | Rewritten (v3 payload) |
| `WhatsappBridge/test/crypto-helper.test.js` | Pins the byte layout, the two round-trips, the fixed vector, tamper and unknown-tag rejection. | **New** |
| `WhatsappBridge/server.js` | Per-socket cipher state: learn the tag from each inbound frame, answer with it, default to CBC. | Modified (frame helpers, data handler, header comment, log line) |
| `WhatsappBridge/test/server.test.js` | Proves the adapter's replies carry the client's tag; records reply tags in the client helper. | Modified |
| `WhatsappApp/Services/CryptoHelper.cs` | The app half of the same contract: CBC + HMAC, tag byte, constant-time compare. | Rewritten |
| `WhatsappApp/Services/SelfCheck.cs` | Names which crypto stage is missing, and checks the fixed vector against the adapter's. | Modified (`CheckCrypto`) |
| `WhatsappApp/Services/DiscoveryService.cs` | Reads the datagram the event already holds; no `LoadAsync`, no async-void handler. | Modified (`OnMessageReceived`) |
| `WhatsappApp/Services/CommunicationService.cs` | Names the failing connect stage, cleans up the socket, says in words what is missing. | Modified (`ConnectToServerAsync`, Disconnect/cleanup) |
| `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw` | The one new message. | Modified |
| `README.md`, `README.it.md`, `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` | Protocol section now describes the tag byte and both ciphers. | Modified |
| `.agents/skills/{maintain-the-app,test-the-app,run-the-login-server}/SKILL.md` | The frame shape and the crypto rule the next session reads. | Modified |
| `tools/start-login.js` | Banner says which cipher is really in use. | Modified (one literal) |
| `docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md` | Stale rows/counts this plan invalidates. | Modified (Task 6) |

Task order: 1 → 2 (adapter, testable on this machine end to end), then 3 → 4 → 5 (app, each gated by the build), then 6 (docs and the cross-plan fixups), then 7 (the device pass only the user can do). Tasks 3, 4 and 5 are independent of each other and may land in any order after Task 2, but 3 must land before Task 7.

---

### Task 1: The v3 payload in the adapter cipher module

**Files:**
- Modify: `WhatsappBridge/crypto-helper.js` (whole file)
- Test: `WhatsappBridge/test/crypto-helper.test.js` (new)

**Interfaces:**
- Consumes: nothing.
- Produces (consumed by Task 2 and by the app in Task 3):
  - `encryptPayload(jsonStr: string, tag?: number): Buffer` — tag `1`/`2`, defaults to `DEFAULT_CIPHER_TAG`; returns plain UTF-8 when `ENCRYPTION_ENABLED` is false.
  - `decodePayload(payload: Buffer): string` — throws on a bad HMAC, a short payload or an unknown tag.
  - `buildFrame(jsonStr: string, tag?: number): Buffer` — `[4-byte UInt32LE length][payload]`.
  - `cipherTagOf(payload: Buffer): number` — `1`, `2`, or `0` when not encrypted/unrecognised.
  - `ENCRYPTION_ENABLED: boolean`, `CIPHER_GCM = 1`, `CIPHER_CBC_HMAC = 2`, `DEFAULT_CIPHER_TAG = 2`, `ModeDescription: string`.

- [ ] **Step 1: Write the failing test**

Create `WhatsappBridge/test/crypto-helper.test.js`:

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const cryptoHelper = require('../crypto-helper');

const PLAIN = JSON.stringify({ Type: 0, Text: 'ciao' });

// Vettore fisso con IV noto, calcolato con questo stesso algoritmo. Lo stesso
// vettore e' in WhatsappApp/Services/SelfCheck.cs: se le due derivazioni delle
// chiavi divergono, il telefono lo dice nel log invece di restare muto.
const VECTOR_IV_HEX = '000102030405060708090a0b0c0d0e0f';
const VECTOR_HEX =
  '02' + VECTOR_IV_HEX +
  '2fc17f6d19a9bea8e286ceebf69ca87c72cf5e563e0d09ee3d755fb40f87c336' +
  'e65a51262cff115a41eb866b8a82275d7d61dfb35bb0f5060ab9f1b316d45e2d';

test('encryptPayload scrive in CBC+HMAC e decodePayload lo rilegge', () => {
  const payload = cryptoHelper.encryptPayload(PLAIN);
  assert.strictEqual(payload[0], cryptoHelper.CIPHER_CBC_HMAC);
  // tag + IV + almeno un blocco + HMAC
  assert.ok(payload.length >= 1 + 16 + 16 + 32, 'payload troppo corto: ' + payload.length);
  assert.strictEqual(cryptoHelper.decodePayload(payload), PLAIN);
});

test('encryptPayload scrive in GCM quando glielo si chiede', () => {
  const payload = cryptoHelper.encryptPayload(PLAIN, cryptoHelper.CIPHER_GCM);
  assert.strictEqual(payload[0], cryptoHelper.CIPHER_GCM);
  assert.strictEqual(cryptoHelper.decodePayload(payload), PLAIN);
});

test('buildFrame antepone la lunghezza e porta il tag richiesto', () => {
  const frame = cryptoHelper.buildFrame(PLAIN, cryptoHelper.CIPHER_GCM);
  assert.strictEqual(frame.readUInt32LE(0), frame.length - 4);
  assert.strictEqual(frame[4], cryptoHelper.CIPHER_GCM);
});

test('il vettore di prova si decifra con le chiavi derivate', () => {
  const payload = Buffer.from(VECTOR_HEX, 'hex');
  assert.strictEqual(cryptoHelper.cipherTagOf(payload), cryptoHelper.CIPHER_CBC_HMAC);
  assert.strictEqual(cryptoHelper.decodePayload(payload), PLAIN);
});

test('un solo byte cambiato nel cifrato invalida la firma HMAC', () => {
  const payload = Buffer.from(VECTOR_HEX, 'hex');
  payload[30] ^= 0x01;
  assert.throws(() => cryptoHelper.decodePayload(payload), /Invalid HMAC signature/);
});

test('un tag cifrario sconosciuto viene rifiutato', () => {
  const payload = Buffer.from(VECTOR_HEX, 'hex');
  payload[0] = 7;
  assert.throws(() => cryptoHelper.decodePayload(payload), /Unknown cipher tag/);
});

test('cipherTagOf riconosce i due tag e ignora tutto il resto', () => {
  assert.strictEqual(cryptoHelper.cipherTagOf(Buffer.from([2, 0, 0])), cryptoHelper.CIPHER_CBC_HMAC);
  assert.strictEqual(cryptoHelper.cipherTagOf(Buffer.from([1, 0, 0])), cryptoHelper.CIPHER_GCM);
  assert.strictEqual(cryptoHelper.cipherTagOf(Buffer.from([9, 0, 0])), 0);
  assert.strictEqual(cryptoHelper.cipherTagOf(Buffer.alloc(0)), 0);
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/crypto-helper.test.js`

Expected: **FAIL** — the first test fails on `assert.strictEqual(payload[0], 2)` because today's payload starts with a random IV byte, and `cryptoHelper.CIPHER_CBC_HMAC` is `undefined` while `cipherTagOf` is not a function (`TypeError: cryptoHelper.cipherTagOf is not a function`).

- [ ] **Step 3: Rewrite `WhatsappBridge/crypto-helper.js`**

Replace the whole file with:

```js
/**
 * ============================================================================
 *  Encryption helper (AES-256-CBC + HMAC-SHA256, AES-256-GCM accepted)
 * ============================================================================
 *  Cifra e autentica il payload scambiato con l'app WP8 con una chiave
 *  condivisa derivata (HMAC-SHA256) da una passphrase.
 *
 *  Formato del payload (dopo il prefisso di 4 byte con la lunghezza):
 *    [1 byte tag cifrario][corpo]
 *      tag 1 -> corpo = [12 byte IV][AES-256-GCM ciphertext || 16 byte tag]
 *      tag 2 -> corpo = [16 byte IV][AES-256-CBC ciphertext
 *                                    || 32 byte HMAC-SHA256(IV || ciphertext)]
 *
 *  Perche' due cifrari: AES-GCM e' il piu' comodo, ma su Windows Phone 8.1 a
 *  runtime risponde NotImplementedException (0x80004001) anche se il membro
 *  esiste nella proiezione WinRT. L'app scrive quindi sempre con il tag 2;
 *  l'adapter accetta entrambi i tag e risponde a ciascun client con il
 *  cifrario che quel client ha usato (vedi server.js). Finche' un client non
 *  ha scritto niente, l'adapter usa il tag 2, che tutti sanno leggere.
 *
 *  Chiavi (devono combaciare con WhatsappApp/Services/CryptoHelper.cs):
 *    master = SHA-256(passphrase)
 *    encKey = HMAC-SHA256(master, "wp8-adapter enc")
 *    macKey = HMAC-SHA256(master, "wp8-adapter mac")
 *  La passphrase e' BRIDGE_KEY, altrimenti quella predefinita qui sotto.
 *
 *  Set BRIDGE_ENCRYPTION=off to disable encryption (plaintext payloads,
 *  no cipher tag), matching the old unencrypted protocol.
 * ============================================================================
 */

const crypto = require('crypto');

const DEFAULT_PASSPHRASE = 'WhatsAppCommunityWP8-2026';

/** Tag del cifrario, primo byte del payload. */
const CIPHER_GCM = 1;
const CIPHER_CBC_HMAC = 2;

/** Cifrario usato verso un client che non ha ancora scritto niente. */
const DEFAULT_CIPHER_TAG = CIPHER_CBC_HMAC;

const GCM_IV_LENGTH = 12;
const GCM_TAG_LENGTH = 16;
const CBC_IV_LENGTH = 16;
const MAC_LENGTH = 32;

const ENCRYPTION_ENABLED = process.env.BRIDGE_ENCRYPTION !== 'off';

const MODE_DESCRIPTION = 'AES-256-CBC + HMAC-SHA256 (AES-256-GCM accepted)';

const MASTER_KEY = crypto
  .createHash('sha256')
  .update(process.env.BRIDGE_KEY || DEFAULT_PASSPHRASE)
  .digest();

const ENC_KEY = crypto.createHmac('sha256', MASTER_KEY).update('wp8-adapter enc').digest();
const MAC_KEY = crypto.createHmac('sha256', MASTER_KEY).update('wp8-adapter mac').digest();

/** [tag][IV][CBC ciphertext][HMAC(IV || ciphertext)] */
function encryptCbc(plaintext) {
  const iv = crypto.randomBytes(CBC_IV_LENGTH);
  const cipher = crypto.createCipheriv('aes-256-cbc', ENC_KEY, iv);
  const body = Buffer.concat([cipher.update(plaintext), cipher.final()]);
  const mac = crypto.createHmac('sha256', MAC_KEY).update(iv).update(body).digest();
  return Buffer.concat([Buffer.from([CIPHER_CBC_HMAC]), iv, body, mac]);
}

/** Verifica la firma e solo dopo decifra (encrypt-then-MAC). */
function decryptCbc(payload) {
  if (payload.length < CBC_IV_LENGTH + 16 + MAC_LENGTH) {
    throw new Error('Invalid CBC payload (too short)');
  }

  const iv = payload.slice(0, CBC_IV_LENGTH);
  const body = payload.slice(CBC_IV_LENGTH, payload.length - MAC_LENGTH);
  const mac = payload.slice(payload.length - MAC_LENGTH);
  const expected = crypto.createHmac('sha256', MAC_KEY).update(iv).update(body).digest();

  if (!crypto.timingSafeEqual(mac, expected)) {
    throw new Error('Invalid HMAC signature');
  }

  const decipher = crypto.createDecipheriv('aes-256-cbc', ENC_KEY, iv);
  return Buffer.concat([decipher.update(body), decipher.final()]).toString('utf8');
}

/** [tag][12 byte IV][GCM ciphertext || tag di autenticazione] */
function encryptGcm(plaintext) {
  const iv = crypto.randomBytes(GCM_IV_LENGTH);
  const cipher = crypto.createCipheriv('aes-256-gcm', ENC_KEY, iv);
  const body = Buffer.concat([cipher.update(plaintext), cipher.final(), cipher.getAuthTag()]);
  return Buffer.concat([Buffer.from([CIPHER_GCM]), iv, body]);
}

function decryptGcm(payload) {
  if (payload.length < GCM_IV_LENGTH + GCM_TAG_LENGTH) {
    throw new Error('Invalid GCM payload (too short)');
  }

  const iv = payload.slice(0, GCM_IV_LENGTH);
  const data = payload.slice(GCM_IV_LENGTH);
  const tag = data.slice(data.length - GCM_TAG_LENGTH);
  const body = data.slice(0, data.length - GCM_TAG_LENGTH);

  const decipher = crypto.createDecipheriv('aes-256-gcm', ENC_KEY, iv);
  decipher.setAuthTag(tag);
  return Buffer.concat([decipher.update(body), decipher.final()]).toString('utf8');
}

/**
 * Cifra una stringa JSON nel payload v3. Il tag dice al destinatario con quale
 * cifrario e' stato scritto; senza indicazione si usa quello che tutti leggono.
 */
function encryptPayload(jsonStr, tag) {
  if (!ENCRYPTION_ENABLED) {
    return Buffer.from(jsonStr, 'utf8');
  }

  const chosen = tag || DEFAULT_CIPHER_TAG;
  if (chosen === CIPHER_CBC_HMAC) return encryptCbc(Buffer.from(jsonStr, 'utf8'));
  if (chosen === CIPHER_GCM) return encryptGcm(Buffer.from(jsonStr, 'utf8'));
  throw new Error('Unknown cipher tag to write: ' + chosen);
}

/**
 * Decifra un payload v3 leggendo il tag dal primo byte.
 * Lancia su payload troncato, firma non valida o tag sconosciuto.
 */
function decodePayload(payload) {
  if (!ENCRYPTION_ENABLED) {
    return payload.toString('utf8');
  }

  if (payload.length < 1) {
    throw new Error('Empty encrypted payload');
  }

  const tag = cipherTagOf(payload);
  if (tag === CIPHER_CBC_HMAC) return decryptCbc(payload.slice(1));
  if (tag === CIPHER_GCM) return decryptGcm(payload.slice(1));
  throw new Error('Unknown cipher tag: ' + payload[0]);
}

/**
 * Il tag cifrario di un payload, 0 se non e' cifrato o non si riconosce.
 * Serve al server per rispondere a ogni client con il cifrario del client.
 */
function cipherTagOf(payload) {
  if (!ENCRYPTION_ENABLED) return 0;
  if (!payload || payload.length < 1) return 0;
  const tag = payload[0];
  return tag === CIPHER_CBC_HMAC || tag === CIPHER_GCM ? tag : 0;
}

/** Un frame TCP completo: [4-byte UInt32LE lunghezza][payload]. */
function buildFrame(jsonStr, tag) {
  const payload = encryptPayload(jsonStr, tag);
  const lenBuf = Buffer.alloc(4);
  lenBuf.writeUInt32LE(payload.length, 0);
  return Buffer.concat([lenBuf, payload]);
}

module.exports = {
  encryptPayload,
  decodePayload,
  buildFrame,
  cipherTagOf,
  ENCRYPTION_ENABLED,
  CIPHER_GCM,
  CIPHER_CBC_HMAC,
  DEFAULT_CIPHER_TAG,
  ModeDescription: MODE_DESCRIPTION,
  GCM_IV_LENGTH,
  GCM_TAG_LENGTH,
  CBC_IV_LENGTH,
  MAC_LENGTH
};
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && node --test test/crypto-helper.test.js`

Expected: `# pass 7`, `# fail 0`.

- [ ] **Step 5: Run the whole adapter suite**

Run: `cd WhatsappBridge && npm test`

Expected: `# pass 43`, `# fail 0`. The existing `server.test.js` tests keep passing without a line of change, because `server.js` calls `buildFrame(json)` with no tag and the new default is already CBC — which is the point of that default. What the suite does **not** yet prove is that the adapter honours a client that chose the other cipher: that is the failing test Task 2 adds.

- [ ] **Step 6: Commit**

```bash
git add WhatsappBridge/crypto-helper.js WhatsappBridge/test/crypto-helper.test.js
git commit -m "feat: move the adapter cipher to a tagged CBC+HMAC payload"
```

---

### Task 2: The adapter answers each client with that client's cipher

**Files:**
- Modify: `WhatsappBridge/server.js` (header comment ~line 10, frame helpers ~line 56, data handler ~line 296, log line ~line 338)
- Test: `WhatsappBridge/test/server.test.js` (client helper plus two tests)

**Interfaces:**
- Consumes: `cryptoHelper.buildFrame(json, tag)`, `cryptoHelper.cipherTagOf(payload)`, `cryptoHelper.DEFAULT_CIPHER_TAG`, `cryptoHelper.CIPHER_GCM`, `cryptoHelper.CIPHER_CBC_HMAC`, `cryptoHelper.ModeDescription` from Task 1.
- Produces: every socket handed to `sendToClient`/`sendToClients` carries a `wp8Cipher` property (`1` or `2`), set at accept time and refreshed from each inbound frame. Tests read the reply tag from the first payload byte.

- [ ] **Step 1: Write the failing tests**

In `WhatsappBridge/test/server.test.js`, replace the whole `connectClient` function with this version (it records the tag byte of every payload and lets `send` choose the cipher):

```js
function connectClient(port) {
  const socket = net.connect(port, '127.0.0.1');
  let buffer = Buffer.alloc(0);
  const messages = [];
  // Il primo byte di ogni payload e' il tag cifrario: e' cosi' che si vede
  // con quale cifrario il server ha risposto.
  const tags = [];
  const waiters = [];
  socket.on('data', (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const len = buffer.readUInt32LE(0);
      if (buffer.length < 4 + len) break;
      const payload = buffer.slice(4, 4 + len);
      buffer = buffer.slice(4 + len);
      tags.push(payload[0]);
      const json = JSON.parse(cryptoHelper.decodePayload(payload));
      messages.push(json);
      while (waiters.length) waiters.shift()(json);
    }
  });
  return {
    socket,
    messages,
    tags,
    send(msg, tag) {
      socket.write(cryptoHelper.buildFrame(JSON.stringify(msg), tag));
    },
    next(timeoutMs = 2000) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('timeout')), timeoutMs);
        waiters.push((m) => { clearTimeout(timer); resolve(m); });
      });
    }
  };
}
```

Then append these two tests at the end of the file:

```js
test('il server risponde in CBC al primo stato e a un client che scrive in CBC', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    // Il primo frame parte prima che il client abbia scritto: deve essere
    // quello leggibile da tutti, cioe' CBC.
    assert.deepStrictEqual(client.tags, [cryptoHelper.CIPHER_CBC_HMAC]);

    client.send({ Type: 3, ChatId: 'system', Command: 'status' }, cryptoHelper.CIPHER_CBC_HMAC);
    await client.next();
    assert.strictEqual(client.tags[1], cryptoHelper.CIPHER_CBC_HMAC);
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il server passa a GCM se il client scrive in GCM', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    assert.strictEqual(client.tags[0], cryptoHelper.CIPHER_CBC_HMAC);

    client.send({ Type: 3, ChatId: 'system', Command: 'status' }, cryptoHelper.CIPHER_GCM);
    await client.next();
    assert.strictEqual(client.tags[1], cryptoHelper.CIPHER_GCM);
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/server.test.js`

Expected: `# pass 44`, `# fail 1`. The CBC test already passes (the default reply is CBC); the GCM test fails on `assert.strictEqual(client.tags[1], 1)` with `Expected values to be strictly equal: 2 !== 1`, because the server replies with the default CBC even after a GCM frame.

- [ ] **Step 3: Replace the frame helpers in `WhatsappBridge/server.js`**

Replace the three helpers `frame`, `sendToClient` and `sendToClients` with:

```js
  // ─── Invio verso l'app WP8 ────────────────────────────────────────────────
  //
  // Ogni socket ricorda con quale cifrario il client ha scritto (wp8Cipher) e
  // riceve le risposte con lo stesso: un telefono WP8.1 non sa fare AES-GCM e
  // deve poter leggere tutto, un client capace di GCM non deve degradare.

  function frameFor(socket, jsonObject) {
    const tag = socket.wp8Cipher || cryptoHelper.DEFAULT_CIPHER_TAG;
    return cryptoHelper.buildFrame(JSON.stringify(jsonObject), tag);
  }

  function sendToClient(socket, msg) {
    try { socket.write(frameFor(socket, msg)); } catch (e) { /* socket morto */ }
  }

  function sendToClients(msg) {
    if (wp8Clients.size === 0) return;
    const json = JSON.stringify(msg);
    // Un frame per cifrario distinto, non uno per socket: i client CBC (in
    // pratica tutti) condividono lo stesso buffer.
    const packets = {};
    const dead = [];
    for (const socket of wp8Clients) {
      const tag = socket.wp8Cipher || cryptoHelper.DEFAULT_CIPHER_TAG;
      if (!packets[tag]) packets[tag] = cryptoHelper.buildFrame(json, tag);
      try { socket.write(packets[tag]); } catch (e) { dead.push(socket); }
    }
    for (const socket of dead) wp8Clients.delete(socket);
  }
```

- [ ] **Step 4: Give each socket its cipher and learn it from inbound frames**

In the TCP server callback, right after `wp8Clients.add(socket);`, add:

```js
    // Finche' il client non scrive non sappiamo cosa sa leggere: si parte dal
    // cifrario che tutti leggono.
    socket.wp8Cipher = cryptoHelper.DEFAULT_CIPHER_TAG;
```

Then in the `socket.on('data', ...)` handler, replace the `try` block body so the tag is read before decoding:

```js
        try {
          // Il tag del frame appena arrivato dice con che cifrario e' stato
          // scritto: da qui in poi gli si risponde con lo stesso.
          const tag = cryptoHelper.cipherTagOf(payload);
          if (tag) socket.wp8Cipher = tag;

          const msg = JSON.parse(cryptoHelper.decodePayload(payload));
          if (msg.Type === 3) handleControl(msg).catch((e) => logger('ERR', e.message));
          else handleUserMessage(msg).catch((e) => logger('ERR', e.message));
        } catch (err) {
          logger('ERR', `Frame non valido da WP8: ${err.message}`);
        }
```

- [ ] **Step 5: Update the header comment and the startup line**

In the file's header comment, replace:

```js
 *   - TCP cifrato (AES-256-GCM) verso l'app WP8, protocollo invariato.
```

with:

```js
 *   - TCP cifrato (AES-256-CBC + HMAC-SHA256) verso l'app WP8, protocollo
 *     invariato salvo il tag cifrario in testa al payload: l'app WP8.1 non
 *     implementa AES-GCM. L'adapter accetta anche i frame GCM e risponde a
 *     ciascun client con il cifrario del client.
```

And the startup log line:

```js
  log('INFO', `Cifratura:   ${cryptoHelper.ModeDescription} ${cryptoHelper.ENCRYPTION_ENABLED ? 'ATTIVA' : 'DISATTIVATA'}`);
```

(The surrounding Italian is being translated by the unexecuted plan; this step only makes the algorithm true. Task 6 patches that plan.)

- [ ] **Step 6: Run the whole adapter suite**

Run: `cd WhatsappBridge && npm test`

Expected: `# pass 45`, `# fail 0` (36 before this plan, 7 from Task 1, 2 here).

- [ ] **Step 7: Commit**

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js
git commit -m "feat: reply to each WP8 client with the cipher it used"
```

---

### Task 3: The app speaks AES-256-CBC + HMAC-SHA256

**Files:**
- Modify: `WhatsappApp/Services/CryptoHelper.cs` (whole file)
- Modify: `WhatsappApp/Services/SelfCheck.cs` (`CheckCrypto` and its `using`s)

**Interfaces:**
- Consumes: the byte layout and key derivation of Task 1.
- Produces (consumed by `CommunicationService` and by later tasks):
  - `static byte[] CryptoHelper.Encrypt(byte[] plaintext)` → `[2][16-byte IV][ciphertext][32-byte HMAC]`
  - `static byte[] CryptoHelper.Decrypt(byte[] data)` → plaintext; throws `ArgumentException` on a short payload, an unknown tag or a bad HMAC
  - `const byte CryptoHelper.CipherCbcHmac = 2`, `const byte CryptoHelper.CipherGcm = 1`, `const string CryptoHelper.ModeDescription`
  - `SelfCheck.RunAsync()` unchanged in signature; its log lines become `DIAG ok: crypto AES-256-CBC + HMAC-SHA256`.

- [ ] **Step 1: Replace `WhatsappApp/Services/CryptoHelper.cs`**

Write the whole file as:

```csharp
using System;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Cifra e autentica i frame scambiati con l'adapter (WhatsappBridge).
    ///
    /// AES-256-CBC + HMAC-SHA256, non AES-GCM: su Windows Phone 8.1 il membro
    /// esiste nella proiezione WinRT ma a runtime lancia
    /// NotImplementedException 0x80004001 (visto sul dispositivo in
    /// DIAG SelfCheck.crypto), e CBC e' l'alternativa autenticata che il
    /// telefono esegue davvero.
    ///
    /// Formato del payload, dopo il prefisso di 4 byte con la lunghezza:
    ///   [1 byte tag cifrario][16 byte IV][cifrato][32 byte HMAC-SHA256]
    /// L'HMAC copre IV e cifrato (encrypt-then-MAC) e si verifica PRIMA di
    /// decifrare: un payload manomesso non arriva mai a CBC.
    ///
    /// Le chiavi devono combaciare con WhatsappBridge/crypto-helper.js:
    ///   master = SHA-256(passphrase)
    ///   encKey = HMAC-SHA256(master, "wp8-adapter enc")
    ///   macKey = HMAC-SHA256(master, "wp8-adapter mac")
    /// </summary>
    public static class CryptoHelper
    {
        // Deve restare identica a DEFAULT_PASSPHRASE in crypto-helper.js
        // (o al valore di BRIDGE_KEY sul server).
        private const string Passphrase = "WhatsAppCommunityWP8-2026";

        /// <summary>Tag del cifrario CBC+HMAC: primo byte del payload.</summary>
        public const byte CipherCbcHmac = 2;

        /// <summary>Tag di AES-GCM: riconosciuto e rifiutato, vedi il commento in testa.</summary>
        public const byte CipherGcm = 1;

        /// <summary>Nome del cifrario, per la riga di SelfCheck.</summary>
        public const string ModeDescription = "AES-256-CBC + HMAC-SHA256";

        private const int IvLength = 16;
        private const int MacLength = 32;

        // tag + IV + almeno un blocco + HMAC
        private const int MinPayloadLength = 1 + IvLength + 16 + MacLength;

        private static readonly byte[] EncKey = DeriveKey("wp8-adapter enc");
        private static readonly byte[] MacKey = DeriveKey("wp8-adapter mac");

        /// <summary>
        /// Deriva una delle due chiavi dal master come fa il server:
        /// HMAC-SHA256(SHA-256(passphrase), etichetta).
        /// </summary>
        private static byte[] DeriveKey(string label)
        {
            var hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var master = hash.HashData(
                CryptographicBuffer.ConvertStringToBinary(Passphrase, BinaryStringEncoding.Utf8));

            var provider = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256);
            var key = provider.CreateKey(master);
            var signed = CryptographicEngine.Sign(
                key,
                CryptographicBuffer.ConvertStringToBinary(label, BinaryStringEncoding.Utf8));

            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(signed, out bytes);
            return bytes;
        }

        /// <summary>
        /// Cifra in [tag][IV][cifrato][HMAC]. Il tag dice all'adapter con quale
        /// cifrario e' stato scritto il frame.
        /// </summary>
        public static byte[] Encrypt(byte[] plaintext)
        {
            var iv = CryptographicBuffer.GenerateRandom((uint)IvLength);

            var algorithm = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbcPkcs7);
            var key = algorithm.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(EncKey));

            var encrypted = CryptographicEngine.Encrypt(
                key,
                CryptographicBuffer.CreateFromByteArray(plaintext),
                iv);

            byte[] ivBytes;
            byte[] cipherBytes;
            CryptographicBuffer.CopyToByteArray(iv, out ivBytes);
            CryptographicBuffer.CopyToByteArray(encrypted, out cipherBytes);

            // L'HMAC copre IV e cifrato, in quest'ordine: e' quello che calcola
            // crypto-helper.js con update(iv).update(body).
            var signed = new byte[ivBytes.Length + cipherBytes.Length];
            Buffer.BlockCopy(ivBytes, 0, signed, 0, ivBytes.Length);
            Buffer.BlockCopy(cipherBytes, 0, signed, ivBytes.Length, cipherBytes.Length);

            byte[] mac = Hmac(signed);

            var result = new byte[1 + signed.Length + mac.Length];
            result[0] = CipherCbcHmac;
            Buffer.BlockCopy(signed, 0, result, 1, signed.Length);
            Buffer.BlockCopy(mac, 0, result, 1 + signed.Length, mac.Length);
            return result;
        }

        /// <summary>
        /// Verifica la firma e poi decifra. Lancia ArgumentException quando il
        /// payload e' troppo corto, quando il tag non e' quello che sappiamo
        /// eseguire o quando la firma non torna.
        /// </summary>
        public static byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length < MinPayloadLength)
            {
                throw new ArgumentException("Invalid encrypted payload (too short)");
            }

            if (data[0] != CipherCbcHmac)
            {
                // Il tag 1 e' AES-GCM: l'adapter non lo usa verso questa app, ma
                // se succedesse e' meglio rifiutarlo che interpretarlo male.
                throw new ArgumentException("Unsupported cipher tag: " + data[0]);
            }

            int signedLength = data.Length - 1 - MacLength;
            var signed = new byte[signedLength];
            Buffer.BlockCopy(data, 1, signed, 0, signedLength);

            var mac = new byte[MacLength];
            Buffer.BlockCopy(data, 1 + signedLength, mac, 0, MacLength);

            if (!FixedTimeEquals(Hmac(signed), mac))
            {
                throw new ArgumentException("Invalid HMAC signature");
            }

            byte[] ivBytes = new byte[IvLength];
            Buffer.BlockCopy(data, 1, ivBytes, 0, IvLength);

            int cipherLength = signedLength - IvLength;
            byte[] cipherBytes = new byte[cipherLength];
            Buffer.BlockCopy(data, 1 + IvLength, cipherBytes, 0, cipherLength);

            var algorithm = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbcPkcs7);
            var key = algorithm.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(EncKey));

            var decrypted = CryptographicEngine.Decrypt(
                key,
                CryptographicBuffer.CreateFromByteArray(cipherBytes),
                CryptographicBuffer.CreateFromByteArray(ivBytes));

            byte[] plain;
            CryptographicBuffer.CopyToByteArray(decrypted, out plain);
            return plain;
        }

        private static byte[] Hmac(byte[] data)
        {
            var provider = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256);
            var key = provider.CreateKey(CryptographicBuffer.CreateFromByteArray(MacKey));
            var signed = CryptographicEngine.Sign(key, CryptographicBuffer.CreateFromByteArray(data));

            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(signed, out bytes);
            return bytes;
        }

        /// <summary>
        /// Confronto a tempo costante: un confronto che esce al primo byte
        /// diverso lascia misurare la firma.
        /// </summary>
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
    }
}
```

- [ ] **Step 2: Make `SelfCheck` name the failing stage and check the fixed vector**

In `WhatsappApp/Services/SelfCheck.cs`, add `using Windows.Storage.Streams;` after `using System.Threading.Tasks;`, replace the line

```csharp
    ///     DIAG ok: crypto AES-256-GCM
```

with

```csharp
    ///     DIAG ok: crypto AES-256-CBC + HMAC-SHA256
```

and replace the whole `CheckCrypto` method with:

```csharp
        /// <summary>
        /// Il cifrario del canale. Prima il giro di andata e ritorno, poi il
        /// vettore di prova: quello e' calcolato dal server con la sua
        /// derivazione delle chiavi, quindi se combacia le due parti si parlano
        /// davvero. Quando fallisce, il sito dice a quale passo.
        /// </summary>
        private static void CheckCrypto()
        {
            // Stesso vettore di WhatsappBridge/test/crypto-helper.test.js:
            // IV = 000102...0e0f, testo {"Type":0,"Text":"ciao"}.
            const string VectorHex =
                "02000102030405060708090a0b0c0d0e0f" +
                "2fc17f6d19a9bea8e286ceebf69ca87c72cf5e563e0d09ee3d755fb40f87c336" +
                "e65a51262cff115a41eb866b8a82275d7d61dfb35bb0f5060ab9f1b316d45e2d";
            const string VectorPlain = "{\"Type\":0,\"Text\":\"ciao\"}";

            byte[] probe = Encoding.UTF8.GetBytes("whatsapp-wp8");

            byte[] frame;
            try
            {
                frame = CryptoHelper.Encrypt(probe);
            }
            catch (Exception ex)
            {
                // Se fallisce qui il canale non puo' funzionare: era il caso del
                // vecchio CryptoHelper, che usava AES-GCM (0x80004001).
                Diag.Failed("SelfCheck.crypto/encrypt", ex);
                return;
            }

            byte[] back;
            try
            {
                back = CryptoHelper.Decrypt(frame);
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.crypto/decrypt", ex);
                return;
            }

            bool equal = back != null && back.Length == probe.Length;
            for (int i = 0; equal && i < probe.Length; i++) equal = back[i] == probe[i];
            if (!equal)
            {
                Diag.Failed("SelfCheck.crypto/roundtrip",
                    new InvalidOperationException("il giro di andata e ritorno non torna"));
                return;
            }

            try
            {
                IBuffer vector = CryptographicBuffer.DecodeFromHexString(VectorHex);
                byte[] vectorBytes;
                CryptographicBuffer.CopyToByteArray(vector, out vectorBytes);

                byte[] plain = CryptoHelper.Decrypt(vectorBytes);
                string text = Encoding.UTF8.GetString(plain, 0, plain.Length);

                if (text == VectorPlain) Diag.Ok("crypto " + CryptoHelper.ModeDescription);
                else Diag.Failed("SelfCheck.crypto/vector",
                    new InvalidOperationException("il vettore di prova non torna: " + text));
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.crypto/vector", ex);
            }
        }
```

(`Windows.Security.Cryptography.CryptographicBuffer` is already in scope through `Windows.Security.Cryptography;` — add that `using` if the compiler asks for it.)

- [ ] **Step 3: Build**

Run the build gate defined in Global Constraints.

Expected: `COPIA=0`, then `Errori: 0, Avvisi: 2` (the two known warnings only). A new warning about `CipherGcm` never being used is not possible — it is a `public const` — but if a CS0414 ever appears, delete the constant rather than silencing the warning.

- [ ] **Step 4: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict`

Expected: three `OK:` lines; the resw one still reports `90` keys.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/CryptoHelper.cs WhatsappApp/Services/SelfCheck.cs
git commit -m "fix: authenticate the app channel with AES-256-CBC and HMAC-SHA256"
```

---

### Task 4: The discovery handler stops throwing once per datagram

**Files:**
- Modify: `WhatsappApp/Services/DiscoveryService.cs` (`OnMessageReceived`, ~lines 143-172)

**Interfaces:**
- Consumes: nothing new.
- Produces: `DiscoveryService.ServersChanged` still fires on a new or changed beacon; the handler is now a plain `void` event handler (no `async void`), so a malformed datagram cannot escape as an unobserved exception.

**Why this is the fix:** `DatagramSocketMessageReceivedEventArgs.GetDataReader()` returns a reader that already holds the datagram. `await reader.LoadAsync(size)` on it fails with `Exception 0x800710DD` ("The operation identifier is not valid") on every single beacon — which is the storm in the log, one line per datagram. The reader is read directly instead, as in the MSDN `DatagramSocket` sample.

- [ ] **Step 1: Replace the handler**

In `WhatsappApp/Services/DiscoveryService.cs`, replace the whole `OnMessageReceived` method with:

```csharp
        /// <summary>
        /// Un datagramma di beacon. Il reader che arriva qui contiene gia' il
        /// datagramma: chiamare LoadAsync su di lui risponde
        /// "The operation identifier is not valid" (0x800710DD) a ogni pacchetto
        /// ricevuto, ed e' quello che riempiva il log. Si legge direttamente, e
        /// non e' piu' async: un handler async void che lancia non lo vede
        /// nessuno.
        /// </summary>
        private void OnMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                DataReader reader = args.GetDataReader();
                uint size = reader.UnconsumedBufferLength;
                if (size == 0) return;

                string json = reader.ReadString(size);

                BeaconPayload beacon = Parse(json);
                if (beacon == null) return;
                if (beacon.Service != "whatsapp-wp8-adapter") return;
                if (beacon.Port <= 0) return;

                string address = args.RemoteAddress == null ? "" : args.RemoteAddress.RawName;
                if (string.IsNullOrEmpty(address)) return;

                if (AddOrUpdate(address, beacon)) RaiseServersChanged();
            }
            catch (Exception ex)
            {
                // Un datagramma malformato non deve fermare l'ascolto, ma un
                // guasto che si ripete a ogni beacon va visto una volta.
                Diag.Failed("DiscoveryService.OnMessageReceived", ex);
            }
        }
```

- [ ] **Step 2: Build**

Run the build gate defined in Global Constraints.

Expected: `COPIA=0`, then `Errori: 0, Avvisi: 2`. `StartAsync` still passes `OnMessageReceived` to `socket.MessageReceived += ...`: `void (DatagramSocket, DatagramSocketMessageReceivedEventArgs)` is the delegate's exact shape, so no other line changes.

- [ ] **Step 3: Confirm nothing else relied on the async handler**

Run: `node tools/check-csharp5.js && node tools/check-resw.js --strict`

Expected: two `OK:` lines. Also run `rg -n "OnMessageReceived" WhatsappApp` and confirm the only two hits remain `s.Socket.MessageReceived += OnMessageReceived` (in `StartAsync`), `socket.MessageReceived -= OnMessageReceived` (in `Stop`), and the method itself.

- [ ] **Step 4: Commit**

```bash
git add WhatsappApp/Services/DiscoveryService.cs
git commit -m "fix: read the discovery datagram the event already loaded"
```

---

### Task 5: A failed connection says what failed, and leaves nothing behind

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs` (`ConnectToServerAsync`, ~lines 294-356; a new private cleanup helper next to `Disconnect`)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw` (one key)
- Modify: `WhatsappApp/Strings/it-IT/Resources.resw` (the same key)

**Interfaces:**
- Consumes: `CryptoHelper` (Task 3) and `Diag` (already present).
- Produces: `ConnectToServerAsync` returns `false` and leaves `_writer`/`_reader`/`_clientSocket` null and `_isConnected` false on **every** failure path; the `ErrorOccurred` text names the missing platform feature instead of quoting "The method or operation is not implemented"; new resw key `CommService_PlatformMissing`.

- [ ] **Step 1: Add the resw key to both languages**

In `WhatsappApp/Strings/en-US/Resources.resw`, after the `CommService_Connecting` entry, insert:

```xml
  <data name="CommService_PlatformMissing" xml:space="preserve">
    <value>This phone does not implement a required Windows feature ({0}: {1})</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, in the same position, insert:

```xml
  <data name="CommService_PlatformMissing" xml:space="preserve">
    <value>Questo telefono non implementa una funzione di Windows necessaria ({0}: {1})</value>
  </data>
```

Run: `node tools/check-resw.js --strict`

Expected: `OK: 91 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.`

- [ ] **Step 2: Name the stage in `ConnectToServerAsync`**

In `WhatsappApp/Services/CommunicationService.cs`, replace this block inside `ConnectToServerAsync`:

```csharp
                await SendFrameAsync(_writer, Encoding.UTF8.GetBytes(handshake.ToJson()));
```

with:

```csharp
                // Il primo frame e' anche il primo uso del cifrario: se il
                // cifrario non c'e' l'errore va detto qui, invece di uscire
                // come "operazione non implementata" senza dire quale passo.
                try
                {
                    await SendFrameAsync(_writer, Encoding.UTF8.GetBytes(handshake.ToJson()));
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConnectToServerAsync/handshake", ex);
                    _isConnected = false;
                    CleanUpClientSocket();
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_ConnectError", "Connection error: {0}"),
                            ExplainConnectionFailure(ex, "handshake"))));
                    return false;
                }
```

and replace the outer `catch` of the same method with:

```csharp
            catch (Exception ex)
            {
                Diag.Failed("ConnectToServerAsync", ex);
                _isConnected = false;
                // Senza questo il socket di un tentativo fallito resta aperto e
                // il tentativo successivo parte con due connessioni.
                CleanUpClientSocket();
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_ConnectError", "Connection error: {0}"),
                        ExplainConnectionFailure(ex, "socket"))));
                return false;
            }
```

- [ ] **Step 3: Add the two private helpers**

Immediately after `ConnectToServerAsync` (before `ListenForMessagesAsync`), add:

```csharp
        /// <summary>
        /// Chiude il socket client e i suoi due wrapper. Idempotente: la
        /// chiamano sia il ramo di fallimento del handshake sia il catch
        /// esterno, e in nessun caso deve lanciare.
        /// </summary>
        private void CleanUpClientSocket()
        {
            try
            {
                if (_writer != null) { _writer.Dispose(); _writer = null; }
                if (_reader != null) { _reader.Dispose(); _reader = null; }
                if (_clientSocket != null) { _clientSocket.Dispose(); _clientSocket = null; }
            }
            catch (Exception ex)
            {
                Diag.Failed("CleanUpClientSocket", ex);
            }
        }

        /// <summary>
        /// Traduce il guasto in una riga comprensibile. "The method or operation
        /// is not implemented" non dice all'utente che manca un pezzo di
        /// piattaforma, ne' quale passo della connessione e' caduto.
        /// </summary>
        private static string ExplainConnectionFailure(Exception ex, string stage)
        {
            bool platformMissing = ex is NotImplementedException
                || ex is PlatformNotSupportedException
                || ex.HResult == unchecked((int)0x80004001);

            if (platformMissing)
            {
                return string.Format(
                    Loc.Get("CommService_PlatformMissing",
                        "This phone does not implement a required Windows feature ({0}: {1})"),
                    stage, ex.Message);
            }

            return ex.Message;
        }
```

- [ ] **Step 4: Build**

Run the build gate defined in Global Constraints.

Expected: `COPIA=0`, then `Errori: 0, Avvisi: 2`.

- [ ] **Step 5: Run all the guards**

Run:

```
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
```

Expected: `OK: 27 C# file(s) compatible with C# 5`, `OK: 13 icon path(s), 9 distinct`, `OK: 91 key(s) ...`, `OK: 2 doc pair(s) ...`.

Also run: `rg -n "CommService_PlatformMissing" WhatsappApp` and confirm exactly two hits (the two `.resw` files) plus the one `Loc.Get("CommService_PlatformMissing",` in `CommunicationService.cs`.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw
git commit -m "fix: name the failing connect stage and close the socket it left open"
```

---

### Task 6: Documentation, skills and the stale cross-plan rows

**Files:**
- Modify: `README.md`, `README.it.md` (protocol section)
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` (diagram + protocol)
- Modify: `.agents/skills/maintain-the-app/SKILL.md`, `.agents/skills/test-the-app/SKILL.md`, `.agents/skills/run-the-login-server/SKILL.md`
- Modify: `tools/start-login.js` (banner literal, ~line 475)
- Modify: `docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md` (five stale rows/counts)

**Interfaces:**
- Consumes: everything the previous tasks produced.
- Produces: no code semantics; the next session reads an accurate frame shape, and the unexecuted English-output plan becomes self-consistent again.

- [ ] **Step 1: Update the two root README protocol sections**

In `README.md`, find the protocol section (it currently says the payload is encrypted with **AES-256-GCM** using a pre-shared key and lists `12`-byte IV / `16`-byte authentication tag) and replace that paragraph with:

```markdown
The payload is encrypted with **AES-256-CBC and authenticated with HMAC-SHA256** using a pre-shared key (the SHA-256 of a passphrase that both sides derive twice with HMAC-SHA256). The payload starts with a one-byte cipher tag: `2` is CBC + HMAC (`16`-byte IV, ciphertext, `32`-byte HMAC over IV and ciphertext), `1` is AES-256-GCM (`12`-byte IV, ciphertext, `16`-byte tag). The app writes tag `2` because Windows Phone 8.1 answers AES-GCM with `NotImplementedException`; the adapter accepts both and replies to each client with the cipher that client used.
```

Write the same content in `README.it.md`, in Italian, in the corresponding paragraph:

```markdown
Il payload e' cifrato con **AES-256-CBC e autenticato con HMAC-SHA256** con una chiave
condivisa (lo SHA-256 di una passphrase, da cui entrambe le parti derivano due chiavi
con HMAC-SHA256). Il payload inizia con un byte di tag cifrario: `2` e' CBC + HMAC
(IV di `16` byte, cifrato, HMAC di `32` byte su IV e cifrato), `1` e' AES-256-GCM
(IV di `12` byte, cifrato, tag di `16` byte). L'app scrive il tag `2` perche'
Windows Phone 8.1 risponde a AES-GCM con `NotImplementedException`; l'adapter accetta
entrambi e risponde a ciascun client con il cifrario che quel client ha usato.
```

Keep the heading count and order of the pair identical: only the paragraph under the existing heading changes.

- [ ] **Step 2: Update the adapter READMEs**

In `WhatsappBridge/README.md` line 11, change

```
 WP8 app  ⇄  (AES-256-GCM encrypted TCP)  ⇄  Adapter  ⇄  (HTTP REST + webhook)  ⇄  GOWA  ⇄  WhatsApp
```

to

```
 WP8 app  ⇄  (AES-256-CBC + HMAC TCP)  ⇄  Adapter  ⇄  (HTTP REST + webhook)  ⇄  GOWA  ⇄  WhatsApp
```

and in `WhatsappBridge/README.it.md` line 11 the same line with `(TCP cifrato AES-256-CBC + HMAC)`. If either file has a protocol paragraph listing `IV_LENGTH`/`TAG_LENGTH`, replace those names with `CBC_IV_LENGTH`/`MAC_LENGTH` and mention `CIPHER_CBC_HMAC`/`DEFAULT_CIPHER_TAG`.

- [ ] **Step 3: Update the three skills**

- `.agents/skills/test-the-app/SKILL.md` line 65: change `2. **Frame shape.** \`[4-byte UInt32LE length][AES-256-GCM payload]\`, JSON inside,` to `2. **Frame shape.** \`[4-byte UInt32LE length][1-byte cipher tag (1 = GCM, 2 = CBC+HMAC)][payload]\`, JSON inside,` and add, as the next bullet, `The app always writes tag 2 (AES-256-CBC + HMAC-SHA256): WP8.1 answers AES-GCM with NotImplementedException 0x80004001. The adapter replies with the tag of the client's last inbound frame.`
- `.agents/skills/maintain-the-app/SKILL.md` line 22: change `CryptoHelper (AES-GCM), Loc (strings), ImageHelper,` to `CryptoHelper (AES-256-CBC + HMAC), Loc (strings), ImageHelper,`.
- `.agents/skills/run-the-login-server/SKILL.md` line 134: change `the adapter (port 8585, AES-256-GCM with \`BRIDGE_KEY\`) may face the LAN.` to `the adapter (port 8585, AES-256-CBC + HMAC-SHA256 with \`BRIDGE_KEY\`) may face the LAN.`

- [ ] **Step 4: Update the launcher banner**

In `tools/start-login.js` line 475, replace `(TCP, AES-256-GCM)` with `(TCP, AES-256-CBC+HMAC)` inside the existing template literal. Do not translate or restructure the rest of that line (the English-output plan owns it).

- [ ] **Step 5: Patch the unexecuted English-output plan**

In `docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md`:

1. Line ~157, the `crypto-helper.js` row of the translation table — replace the row

```
| `crypto-helper.js` | `'Payload cifrato non valido (troppo corto)'` | `'Invalid encrypted payload (too short)'` |
```

with

```
| `crypto-helper.js` | already English after the v3 payload rewrite: `'Invalid CBC payload (too short)'`, `'Invalid GCM payload (too short)'`, `'Invalid HMAC signature'`, `'Unknown cipher tag: ...'`, `'Unknown cipher tag to write: ...'`, `'Empty encrypted payload'` | nothing to translate |
```

2. Line ~199 — replace the source cell `` `Cifratura:   AES-256-GCM ${... 'ATTIVA' : 'DISATTIVATA'}` `` with `` `Cifratura:   ${cryptoHelper.ModeDescription} ${... 'ATTIVA' : 'DISATTIVATA'}` `` (the English target cell becomes `` `Encryption:  ${cryptoHelper.ModeDescription} ${... 'ON' : 'OFF'}` ``).
3. Line ~338 — replace the banner source cell `(TCP, AES-256-GCM)` with `(TCP, AES-256-CBC+HMAC)`.
4. Line ~2416 — replace the paragraph `- **AES-256-CBC + HMAC fallback.** Still parked: ...` with `- **AES-256-CBC + HMAC transport.** Done in `docs/superpowers/plans/2026-09-26-cbc-transport-and-runtime-failures.md`; the app now writes tag 2 and the adapter accepts both tags.`
5. Line ~2392 — in the "Expected, with the counts this plan produces" sentence, change `99 resw keys` to `100 resw keys` and `53 tests pass` to `62 tests pass`.

Run: `node tools/check-docs.js`

Expected: `OK: 2 doc pair(s) in step (1 closed by "Disclosure"), no emoji in 23 .md file(s).` (the plan files are not a doc pair; this plan added the 23rd `.md`).

- [ ] **Step 6: Run every guard and the adapter suite once more**

Run:

```
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
cd WhatsappBridge && npm test
```

Expected: four `OK:` lines (`27 C# file(s)`, `13 icon path(s), 9 distinct`, `91 key(s)`, `2 doc pair(s)`), then `# pass 45`, `# fail 0`.

- [ ] **Step 7: Commit**

```bash
git add README.md README.it.md WhatsappBridge/README.md WhatsappBridge/README.it.md tools/start-login.js .agents/skills docs/superpowers/plans/2026-09-25-english-output-finish-screens-and-bugs.md
git commit -m "docs: describe the tagged CBC transport and refresh the stale plan rows"
```

---

### Task 7: The device pass (only the user can run this)

**Files:** none. If this task finds a defect, it produces a new plan task, not an edit here.

**Why it is a task:** the last log proved that compiling and guards passing say nothing about what the phone implements. This is the step that closes the loop, and it has an exact expected output.

- [ ] **Step 1: Start the adapter on the Mac**

```bash
cd WhatsappBridge && npm start
```

Expected: the banner shows `Cifratura:   AES-256-CBC + HMAC-SHA256 (AES-256-GCM accepted) ATTIVA`, `Server TCP in ascolto sulla porta 8585`, `Webhook in ascolto sulla porta 8586/api/webhook`, `Discovery attivo sulla porta UDP 8587`.

- [ ] **Step 2: Deploy and watch the Output window**

Deploy `WhatsappApp_1.0.1.0_x86_Debug.appxbundle` from Visual Studio 2013 (Debug > x86) with the debugger attached to the phone, then read the Output window.

Expected, and nothing else:

```
DIAG ok: crypto AES-256-CBC + HMAC-SHA256
DIAG ok: schermo sempre acceso (DisplayRequest)
DIAG ok: beacon UDP in ascolto sulla porta 8587
```

There must be **no** `WinRT information: The operation identifier is not valid.` at all — not once, not per beacon — and no `NotImplementedException`.

- [ ] **Step 3: Connect and confirm the two sides agree**

On the phone, open the connection page, let it find the adapter (or type the address), and connect.

Expected: `DIAG` shows no `ConnectToServerAsync` line, the page moves to the QR/status state, and the adapter's terminal logs `Client WP8 connesso: <ip>:<port>` followed by `Handshake da "<name>"`.

- [ ] **Step 4: If something still fails, report the exact DIAG line**

Copy the whole Output window. The possible readings, and what each one means:

| Log line | Meaning | Next action |
|---|---|---|
| `DIAG SelfCheck.crypto/encrypt: NotImplementedException 0x80004001` | `AesCbcPkcs7` or HMAC-SHA256 is missing too | New plan task: derive the MAC differently (e.g. SHA-256 over key‖IV‖ciphertext) or fall back to an unauthenticated cipher plus a length check |
| `DIAG SelfCheck.crypto/vector: ...` | The two sides derive different keys | Compare `encKey`/`macKey` against the values in Global Constraints; the labels are the usual suspect |
| `DIAG ok: crypto ...` followed by `DIAG ConnectToServerAsync/handshake: ...` | The cipher works, the socket does not | The address/port or the firewall; `ExplainConnectionFailure`'s text says which stage |
| `DIAG DiscoveryService.OnMessageReceived: ...` still present | The datagram handler still throws | Re-read Task 4: the reader must not be awaited at all |

- [ ] **Step 5: No commit**

This task changes no file. Record the result in the next session's opening message; do not create an empty commit.

---

## Self-Review

**1. Spec coverage.** The three new log facts, one task each: `SelfCheck.crypto` → `0x80004001` is covered by Tasks 1-3 (the app no longer uses GCM at all, the adapter accepts both, and the same fixed vector is checked on the device in Task 3 Step 2 so a key-derivation divergence cannot pass silently). `DIAG DiscoveryService.OnMessageReceived: Exception 0x800710DD` is Task 4, with the root cause named (a `LoadAsync` on an already-loaded datagram reader) and the per-datagram storm removed. `DIAG ConnectToServerAsync: NotImplementedException 0x80004001` is Task 5, which names the stage, cleans up the socket and says in words what is missing. The still-unexecuted `2026-09-25-english-output-finish-screens-and-bugs.md` is reconciled in Task 6 rather than merged, because it is a self-contained plan whose only conflict with this one is five strings and two counts.

**2. Placeholder scan.** No "TBD", no "add error handling", no "similar to Task N": every step that changes code shows the full code, including the two resw entries, the two README paragraphs in both languages, and the five cross-plan replacements. Task 7 has no code by design and says so.

**3. Type consistency.** `encryptPayload(jsonStr, tag)`, `decodePayload(payload)`, `buildFrame(jsonStr, tag)`, `cipherTagOf(payload)` are defined in Task 1 and used with exactly those names in Task 2. `CIPHER_CBC_HMAC`/`CIPHER_GCM`/`DEFAULT_CIPHER_TAG`/`ModeDescription` match between Task 1 Step 3 (definition) and Task 2 Steps 3-5 (consumption). The C# side defines `CipherCbcHmac`/`CipherGcm`/`ModeDescription`/`Encrypt`/`Decrypt` in Task 3 and consumes only `Encrypt`/`Decrypt`/`ModeDescription` in `SelfCheck`; nothing in Tasks 4-5 calls the renamed constants. `ExplainConnectionFailure(Exception, string)` and `CleanUpClientSocket()` are defined in Task 5 Step 3 and called in Task 5 Step 2 — both inside the same task, both `private` and `static`/instance respectively as written. The resw key `CommService_PlatformMissing` is added to both languages in Task 5 Step 1 with the two `{0}`/`{1}` placeholders that `ExplainConnectionFailure` fills.

**4. Ordering.** Task 1 leaves the adapter suite red on purpose (Step 5) because the untagged frames are Task 2's business; every later task starts from a green suite. Tasks 3-5 touch three different C# files and can be reordered, but all three need Task 2's contract to be meaningful, and Task 7 needs all of them.
