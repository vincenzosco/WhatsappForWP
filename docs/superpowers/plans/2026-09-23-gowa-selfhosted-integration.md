# GOWA Self-Hosted Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the project's own `whatsapp-web.js` bridge with the self-hosted **GOWA** (`vincenzosco/go-whatsapp-web-multidevice`) server, and let the WP8 app always log in the WhatsApp account either by **scanning a QR code** or by **phone number pairing code**, exactly like the modern WhatsApp app.

**Architecture:** The WP8 app keeps its existing encrypted, length-prefixed TCP channel. `WhatsappBridge` is rewritten from a `whatsapp-web.js` client into a thin **GOWA adapter**: it talks to the GOWA REST API over HTTP (login, status, send, contacts) and receives incoming messages through a GOWA **webhook**. The app never talks HTTP directly, so no new AppX capability is required and the existing AES-256-GCM framing is preserved. Login is driven from the app through new control frames on the same TCP channel.

**Tech Stack:** Node.js 18.13+ (built-in `fetch`, `FormData`, `Blob`, `node:test`), Go GOWA REST API v9, C# / XAML for Windows Phone 8.1.

## Global Constraints

- Target GOWA API surface (verified against the upstream `main` branch): `GET /health`, `GET /app/info`, `GET /app/status`, `GET /app/login`, `GET /app/login-with-code?phone=`, `GET /app/logout`, `GET /devices`, `POST /devices`, `PATCH /devices/:device_id/webhook`, `POST /send/message`, `POST /send/image`, `GET /user/my/contacts`, `GET /message/:message_id/download`. All device-scoped routes accept `X-Device-Id` (header) or `device_id` (query); with a single device they fall back to it.
- Response envelope is always `{ "status": int, "code": string, "message": string, "results": ... }`.
- `GET /app/login` returns `results: { device_id, qr_link, qr_duration }`; `qr_link` is an **absolute** URL to a PNG served by GOWA.
- `GET /app/login-with-code?phone=` returns `results: { device_id, pair_code }`.
- `GET /app/status` returns `results: { is_connected, is_logged_in, device_id, jid }`.
- `POST /send/message` body `{ phone, message }` and `GET /user/my/contacts` both answer under `results`; send returns `results: { message_id, status }`, contacts returns `results: { data: [ { jid, name } ] }`.
- `POST /send/image` is `multipart/form-data` with fields `phone`, `caption` (optional), `image` (binary). It accepts `phone` as a JID (`...@s.whatsapp.net` / `...@g.us`) or a bare number.
- Webhook events are POSTed as `{ event, device_id, session_id?, payload }`; message payload fields are `id`, `chat_id`, `from`, `from_name`, `sender_display_name`, `timestamp` (RFC3339), `is_from_me`, `body`, and `image`/`audio`/`video`/`document`/`sticker`. `image` is a plain string path when there is no caption, `{ path, caption }` when there is, or `{ url, caption }` when auto-download is disabled. Signature header is `X-Hub-Signature-256: sha256=<hex>` with HMAC-SHA256 over the raw body.
- GOWA is launched with `rest`, e.g. `./whatsapp rest --basic-auth=admin:secret`. Basic auth is sent by the adapter as `Authorization: Basic base64(user:pass)`.
- WhatsApp DateTime wire format for the WP8 app is Microsoft `/Date(epochMs)/`, produced in JS by `` `\\/Date(${epoch})\\/` `` (must be exactly this — `DataContractJsonSerializer` cannot parse ISO 8601).
- Message `Type`: 0 = Text, 1 = Image, 2 = Audio, 3 = System. `Status`: 0 Sending, 1 Sent, 2 Delivered, 3 Read, 4 Failed.
- The app ↔ adapter payload stays AES-256-GCM with the pre-shared passphrase; `WhatsappApp/Services/CryptoHelper.cs` and `WhatsappBridge/crypto-helper.js` must stay in sync. Do not change either key.
- Default ports: adapter TCP `8585` (`BRIDGE_PORT`), adapter webhook `8586` (`WEBHOOK_PORT`), GOWA `3000` (`GOWA_URL`).
- The WP8 app must not gain new NuGet/package references, and `Package.appxmanifest` capabilities stay `internetClientServer`.
- UI strings and code comments are Italian, matching the existing codebase.

## File Structure

**Adapter (`WhatsappBridge/`)** — one responsibility per file:

- `config.js` (create) — reads and validates environment variables, exportable for tests.
- `gowa-client.js` (create) — the only code that speaks HTTP to GOWA.
- `message-format.js` (create) — pure functions: WP8 date format, `ChatMessage` builder, webhook→message mapping.
- `webhook-server.js` (create) — HTTP receiver for GOWA webhooks, HMAC verification.
- `server.js` (rewrite) — TCP server, control protocol, status polling, contact sync, message routing.
- `crypto-helper.js` (unchanged) — AES-256-GCM framing.
- `package.json` (rewrite) — drop `whatsapp-web.js`/`puppeteer`/`qrcode-terminal`.
- `test/message-format.test.js`, `test/webhook-server.test.js`, `test/gowa-client.test.js`, `test/server.test.js` (create).
- `.env.example` (create), `README.md` (rewrite).

**App (`WhatsappApp/`)**:

- `Models/ChatMessage.cs` (modify) — add control-frame fields.
- `Services/CommunicationService.cs` (modify) — control event, control sender, message routing.
- `Services/DataService.cs` (modify) — drop demo data, consume real contacts.
- `Pages/ConnectionPage.xaml` + `Pages/ConnectionPage.xaml.cs` (rewrite) — login UI (QR + number).
- `MainPage.xaml.cs` (modify) — request contacts, "new chat by number".

**Docs**: `README.md`, `WhatsappBridge/README.md` (rewrite).

---

### Task 1: Adapter configuration module

**Files:**
- Create: `WhatsappBridge/config.js`
- Test: `WhatsappBridge/test/config.test.js`

**Interfaces:**
- Consumes: nothing.
- Produces: `loadConfig(env = process.env)` returning
  `{ gowa: { url, deviceId, user, pass }, bridge: { port }, webhook: { port, path, publicUrl, secret }, pollIntervalMs }` (all strings/numbers as below). Also exports `DEFAULTS`.

- [ ] **Step 1: Write the failing test**

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { loadConfig } = require('../config');

test('loadConfig fornisce i valori di default', () => {
  const c = loadConfig({});
  assert.strictEqual(c.gowa.url, 'http://127.0.0.1:3000');
  assert.strictEqual(c.gowa.deviceId, '');
  assert.strictEqual(c.bridge.port, 8585);
  assert.strictEqual(c.webhook.port, 8586);
  assert.strictEqual(c.webhook.path, '/webhook');
  assert.strictEqual(c.webhook.publicUrl, 'http://127.0.0.1:8586/webhook');
  assert.strictEqual(c.webhook.secret, '');
  assert.strictEqual(c.pollIntervalMs, 5000);
});

test('loadConfig legge e normalizza le variabili d\'ambiente', () => {
  const c = loadConfig({
    GOWA_URL: 'http://192.168.1.50:3000/',
    GOWA_DEVICE_ID: 'org_1',
    GOWA_USER: 'admin',
    GOWA_PASS: 'secret',
    BRIDGE_PORT: '9000',
    WEBHOOK_PORT: '9001',
    WEBHOOK_SECRET: 's3cr3t',
    POLL_INTERVAL_MS: '2500'
  });
  assert.strictEqual(c.gowa.url, 'http://192.168.1.50:3000');
  assert.strictEqual(c.gowa.deviceId, 'org_1');
  assert.strictEqual(c.gowa.user, 'admin');
  assert.strictEqual(c.gowa.pass, 'secret');
  assert.strictEqual(c.bridge.port, 9000);
  assert.strictEqual(c.webhook.port, 9001);
  assert.strictEqual(c.webhook.publicUrl, 'http://127.0.0.1:9001/webhook');
  assert.strictEqual(c.webhook.secret, 's3cr3t');
  assert.strictEqual(c.pollIntervalMs, 2500);
});

test('loadConfig accetta WEBHOOK_PUBLIC_URL esplicita', () => {
  const c = loadConfig({ WEBHOOK_PORT: '9001', WEBHOOK_PUBLIC_URL: 'http://10.0.0.5:9001/hook' });
  assert.strictEqual(c.webhook.publicUrl, 'http://10.0.0.5:9001/hook');
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/config.test.js`
Expected: FAIL with `Cannot find module '../config'`.

- [ ] **Step 3: Write the implementation**

```js
'use strict';

// Legge la configurazione dall'ambiente con valori di default sensati.
// Non contiene segreti: quelli restano in .env / variabili d'ambiente.

const DEFAULTS = {
  GOWA_URL: 'http://127.0.0.1:3000',
  GOWA_DEVICE_ID: '',
  GOWA_USER: '',
  GOWA_PASS: '',
  BRIDGE_PORT: '8585',
  WEBHOOK_PORT: '8586',
  WEBHOOK_PATH: '/webhook',
  WEBHOOK_PUBLIC_URL: '',
  WEBHOOK_SECRET: '',
  POLL_INTERVAL_MS: '5000'
};

function pick(env, key) {
  const value = env[key];
  return (value === undefined || value === null || value === '') ? DEFAULTS[key] : String(value);
}

function loadConfig(env = process.env) {
  const gowaUrl = pick(env, 'GOWA_URL').replace(/\/+$/, '');
  const webhookPort = parseInt(pick(env, 'WEBHOOK_PORT'), 10);
  const webhookPath = pick(env, 'WEBHOOK_PATH');
  const publicUrl = pick(env, 'WEBHOOK_PUBLIC_URL')
    || `http://127.0.0.1:${webhookPort}${webhookPath}`;

  return {
    gowa: {
      url: gowaUrl,
      deviceId: pick(env, 'GOWA_DEVICE_ID'),
      user: pick(env, 'GOWA_USER'),
      pass: pick(env, 'GOWA_PASS')
    },
    bridge: {
      port: parseInt(pick(env, 'BRIDGE_PORT'), 10)
    },
    webhook: {
      port: webhookPort,
      path: webhookPath,
      publicUrl,
      secret: pick(env, 'WEBHOOK_SECRET')
    },
    pollIntervalMs: parseInt(pick(env, 'POLL_INTERVAL_MS'), 10)
  };
}

module.exports = { loadConfig, DEFAULTS };
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && node --test test/config.test.js`
Expected: PASS, `# pass 3`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/config.js WhatsappBridge/test/config.test.js
git commit -m "feat(bridge): add GOWA adapter configuration module"
```

---

### Task 2: Message formatting and webhook mapping

**Files:**
- Create: `WhatsappBridge/message-format.js`
- Test: `WhatsappBridge/test/message-format.test.js`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `formatDateForWp8(date) -> string` (e.g. `\/Date(1700000000000)\/`).
  - `displayNameForJid(jid) -> string`.
  - `buildChatMessage(fields) -> object` with `Id, Text, SenderId, SenderName, ChatId, Timestamp, Status, Type, IsIncoming` plus optional `Command, State, PairCode, QrImageData, QrDuration, AccountJid, MediaData, MediaMimeType, MediaFileName`.
  - `mapWebhookMessage(payload) -> fields|null`, where fields is
    `{ id, text, senderId, senderName, chatId, timestamp, type, mediaPath, mediaFileName, mediaMimeType }`.

- [ ] **Step 1: Write the failing test**

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const {
  formatDateForWp8,
  displayNameForJid,
  buildChatMessage,
  mapWebhookMessage
} = require('../message-format');

test('formatDateForWp8 usa il formato Microsoft /Date(ms)/', () => {
  assert.strictEqual(formatDateForWp8(new Date(0)), '\\/Date(0)\\/');
  assert.strictEqual(formatDateForWp8(new Date(1700000000000)), '\\/Date(1700000000000)\\/');
});

test('displayNameForJid gestisce numeri e gruppi', () => {
  assert.strictEqual(displayNameForJid('393401234567@s.whatsapp.net'), '+393401234567');
  assert.strictEqual(displayNameForJid('123456789012345678@g.us'), 'Gruppo 123456789012345678');
});

test('buildChatMessage applica i default e i campi di controllo', () => {
  const m = buildChatMessage({ command: 'state', state: 'connected', accountJid: '39@s.whatsapp.net' });
  assert.strictEqual(m.Type, 3);
  assert.strictEqual(m.Command, 'state');
  assert.strictEqual(m.State, 'connected');
  assert.strictEqual(m.AccountJid, '39@s.whatsapp.net');
  assert.strictEqual(m.Status, 1);
  assert.strictEqual(m.IsIncoming, true);
  assert.strictEqual(m.MediaData, undefined);
});

test('buildChatMessage include i campi media solo quando presenti', () => {
  const m = buildChatMessage({ text: 'ciao', mediaData: 'AAAA', mediaMimeType: 'image/png' });
  assert.strictEqual(m.MediaData, 'AAAA');
  assert.strictEqual(m.MediaMimeType, 'image/png');
});

test('mapWebhookMessage ignora i messaggi inviati da me', () => {
  assert.strictEqual(mapWebhookMessage({ is_from_me: true, body: 'x' }), null);
});

test('mapWebhookMessage ignora status broadcast', () => {
  assert.strictEqual(mapWebhookMessage({ chat_id: 'status@broadcast' }), null);
});

test('mapWebhookMessage mappa un messaggio di testo', () => {
  const f = mapWebhookMessage({
    id: 'ABC', chat_id: '393401234567@s.whatsapp.net', from: '393401234567@s.whatsapp.net',
    sender_display_name: 'Mario', timestamp: '2026-01-02T03:04:05Z', is_from_me: false, body: 'Ciao!'
  });
  assert.strictEqual(f.text, 'Ciao!');
  assert.strictEqual(f.chatId, '393401234567@s.whatsapp.net');
  assert.strictEqual(f.senderName, 'Mario');
  assert.strictEqual(f.type, 0);
  assert.strictEqual(f.mediaPath, null);
  assert.strictEqual(f.timestamp.toISOString(), '2026-01-02T03:04:05.000Z');
});

test('mapWebhookMessage mappa un\'immagine con path stringa', () => {
  const f = mapWebhookMessage({ id: '1', chat_id: 'a@s.whatsapp.net', image: 'statics/media/x.jpeg' });
  assert.strictEqual(f.type, 1);
  assert.strictEqual(f.mediaPath, 'statics/media/x.jpeg');
});

test('mapWebhookMessage mappa un\'immagine con didascalia (oggetto)', () => {
  const f = mapWebhookMessage({
    id: '1', chat_id: 'a@s.whatsapp.net', body: 'guarda',
    image: { path: 'statics/media/y.png', caption: 'guarda' }
  });
  assert.strictEqual(f.type, 1);
  assert.strictEqual(f.mediaPath, 'statics/media/y.png');
  assert.strictEqual(f.text, 'guarda');
});

test('mapWebhookMessage degrada a testo quando il media non è scaricato', () => {
  const f = mapWebhookMessage({ id: '1', chat_id: 'a@s.whatsapp.net', image: { url: 'https://mmg/x' } });
  assert.strictEqual(f.type, 0);
  assert.strictEqual(f.mediaPath, null);
  assert.strictEqual(f.text, '[Immagine non scaricata]');
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/message-format.test.js`
Expected: FAIL with `Cannot find module '../message-format'`.

- [ ] **Step 3: Write the implementation**

```js
'use strict';

// Funzioni pure di formattazione: nessuna I/O, nessuna dipendenza da rete.
// Il JSON prodotto deve combaciare ESATTAMENTE con i [DataMember] di
// WhatsappApp/Models/ChatMessage.cs (DataContractJsonSerializer è case-sensitive).

const DEFAULT_SENDER = 'Sconosciuto';

function formatDateForWp8(value) {
  const d = value instanceof Date ? value : new Date(value === undefined ? Date.now() : value);
  const epoch = d.getTime();
  // ATTENZIONE: il formato Microsoft è /Date(ms)/ con gli slash escapati.
  return `\\/Date(${epoch})\\/`;
}

function displayNameForJid(jid) {
  if (!jid) return '?';
  const user = String(jid).split('@')[0];
  if (String(jid).endsWith('@g.us')) return `Gruppo ${user}`;
  if (/^\d+$/.test(user)) return `+${user}`;
  return user || '?';
}

function buildChatMessage(fields) {
  const f = fields || {};
  const msg = {
    Id: f.id || `msg_${Date.now()}_${Math.random().toString(36).slice(2, 8)}`,
    Text: f.text || '',
    SenderId: f.senderId || 'unknown',
    SenderName: f.senderName || DEFAULT_SENDER,
    ChatId: f.chatId || '0',
    Timestamp: formatDateForWp8(f.timestamp),
    Status: typeof f.status === 'number' ? f.status : 1,
    // I frame di controllo sono sempre di tipo System (3).
    Type: typeof f.type === 'number' ? f.type : (f.command ? 3 : 0),
    IsIncoming: typeof f.isIncoming === 'boolean' ? f.isIncoming : true
  };

  if (f.command) msg.Command = f.command;
  if (f.state) msg.State = f.state;
  if (f.pairCode) msg.PairCode = f.pairCode;
  if (f.qrImageData) msg.QrImageData = f.qrImageData;
  if (typeof f.qrDuration === 'number') msg.QrDuration = f.qrDuration;
  if (f.accountJid) msg.AccountJid = f.accountJid;

  if (f.mediaData) {
    msg.MediaData = f.mediaData;
    msg.MediaMimeType = f.mediaMimeType || 'image/jpeg';
    if (f.mediaFileName) msg.MediaFileName = f.mediaFileName;
  }

  return msg;
}

// Estrae path/didascalia/tipo da un payload webhook GOWA.
function mediaFromPayload(p) {
  const result = { type: 0, path: null, mimeType: null, fileName: null, fallbackText: '' };

  if (p.image !== undefined) {
    if (typeof p.image === 'string') { result.type = 1; result.path = p.image; }
    else if (p.image && typeof p.image.path === 'string') { result.type = 1; result.path = p.image.path; }
    else { result.fallbackText = '[Immagine non scaricata]'; }
  } else if (p.audio !== undefined) {
    if (typeof p.audio === 'string') {
      result.type = 2; result.path = p.audio; result.mimeType = 'audio/ogg'; result.fileName = 'audio.ogg';
    } else { result.fallbackText = '[Audio non scaricato]'; }
  } else if (p.video && typeof p.video === 'object' && typeof p.video.path === 'string') {
    result.path = p.video.path; result.mimeType = 'video/mp4';
  } else if (p.video !== undefined && !p.video.path) {
    result.fallbackText = '[Video non scaricato]';
  } else if (p.document && typeof p.document === 'object' && typeof p.document.path === 'string') {
    result.path = p.document.path;
    result.fileName = p.document.filename || null;
  } else if (typeof p.sticker === 'string') {
    result.path = p.sticker; result.mimeType = 'image/webp'; result.fileName = 'sticker.webp';
  }

  return result;
}

function mapWebhookMessage(payload) {
  const p = payload || {};
  if (p.is_from_me === true) return null;
  const chatId = p.chat_id || p.from;
  if (!chatId || chatId === 'status@broadcast') return null;

  const senderId = p.from || chatId;
  const senderName = p.sender_display_name || p.from_name || displayNameForJid(senderId);
  const media = mediaFromPayload(p);

  let text = p.body || '';
  if (!text && media.fallbackText) text = media.fallbackText;

  return {
    id: p.id || null,
    text,
    senderId,
    senderName,
    chatId,
    timestamp: p.timestamp ? new Date(p.timestamp) : new Date(),
    type: media.type,
    mediaPath: media.path,
    mediaFileName: media.fileName,
    mediaMimeType: media.mimeType
  };
}

module.exports = {
  DEFAULT_SENDER,
  formatDateForWp8,
  displayNameForJid,
  buildChatMessage,
  mapWebhookMessage
};
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && node --test test/message-format.test.js`
Expected: PASS, `# pass 10`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/message-format.js WhatsappBridge/test/message-format.test.js
git commit -m "feat(bridge): add WP8 message formatting and GOWA webhook mapping"
```

---

### Task 3: GOWA REST client

**Files:**
- Create: `WhatsappBridge/gowa-client.js`
- Test: `WhatsappBridge/test/gowa-client.test.js`

**Interfaces:**
- Consumes: `config.gowa`.
- Produces: `class GowaClient`:
  - `constructor({ baseUrl, deviceId, user, pass, fetchImpl })`
  - `async ensureDevice() -> string|null`
  - `async status() -> { isConnected, isLoggedIn, jid }` (never throws; returns all-false on HTTP error)
  - `async loginQr() -> { qrLink, duration }` (throws on error)
  - `async loginWithCode(phone) -> string` (throws on error)
  - `async logout() -> void`
  - `async sendText(phone, message) -> string` (message id)
  - `async sendImage(phone, caption, buffer, mimeType, fileName) -> string`
  - `async fetchBinary(urlOrPath) -> { buffer, contentType }`
  - `async contacts() -> [{ jid, name }]`
  - `async setDeviceWebhook(url) -> boolean`
  - `static errorMessage(data, fallback) -> string`

- [ ] **Step 1: Write the failing test**

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const { GowaClient, errorMessage } = require('../gowa-client');

function jsonResponse(body, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { get: () => 'application/json' },
    text: async () => JSON.stringify(body),
    arrayBuffer: async () => Buffer.from(JSON.stringify(body))
  };
}

function makeFetch(handler) {
  const calls = [];
  const impl = async (url, options) => {
    calls.push({ url, options });
    return handler(url, options);
  };
  impl.calls = calls;
  return impl;
}

test('errorMessage preferisce il campo message della risposta GOWA', () => {
  assert.strictEqual(errorMessage({ message: 'Boom' }, 'fallback'), 'Boom');
  assert.strictEqual(errorMessage(null, 'fallback'), 'fallback');
});

test('ensureDevice crea un device quando la lista è vuota', async () => {
  const fetchImpl = makeFetch(async (url, options) => {
    if (url.endsWith('/devices')) {
      if (options.method === 'POST') return jsonResponse({ status: 200, results: { id: 'org_1' } });
      return jsonResponse({ status: 200, results: [] });
    }
    return jsonResponse({});
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const id = await client.ensureDevice();
  assert.strictEqual(id, 'org_1');
  assert.strictEqual(fetchImpl.calls[0].url, 'http://g/devices');
  assert.strictEqual(fetchImpl.calls[0].options.method, 'GET');
  assert.strictEqual(fetchImpl.calls[1].options.method, 'POST');
});

test('ensureDevice riusa il primo device esistente', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: [{ id: 'org_9' }] }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.ensureDevice(), 'org_9');
  assert.strictEqual(fetchImpl.calls.length, 1);
});

test('loginQr restituisce qr_link e qr_duration', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({
    status: 200, results: { device_id: 'd', qr_link: 'http://g/statics/qr.png', qr_duration: 30 }
  }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const qr = await client.loginQr();
  assert.strictEqual(qr.qrLink, 'http://g/statics/qr.png');
  assert.strictEqual(qr.duration, 30);
});

test('loginWithCode passa phone e legge pair_code', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { pair_code: 'ABCD-EFGH' } }));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const code = await client.loginWithCode('393401234567');
  assert.strictEqual(code, 'ABCD-EFGH');
  assert.match(fetchImpl.calls[0].url, /login-with-code\?phone=393401234567/);
});

test('sendText invia il body JSON e legge message_id', async () => {
  const fetchImpl = makeFetch(async (url, options) => {
    assert.strictEqual(url, 'http://g/send/message');
    assert.strictEqual(options.body, JSON.stringify({ phone: '39@s.whatsapp.net', message: 'ciao' }));
    return jsonResponse({ status: 200, results: { message_id: 'M1', status: 'PENDING' } });
  });
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  assert.strictEqual(await client.sendText('39@s.whatsapp.net', 'ciao'), 'M1');
});

test('status tollera gli errori HTTP', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 400, code: 'DEVICE_ID_REQUIRED' }, 400));
  const client = new GowaClient({ baseUrl: 'http://g', fetchImpl });
  const s = await client.status();
  assert.deepStrictEqual(s, { isConnected: false, isLoggedIn: false, jid: '' });
});

test('aggiunge gli header di autenticazione e X-Device-Id', async () => {
  const fetchImpl = makeFetch(async () => jsonResponse({ status: 200, results: { is_logged_in: true, jid: '39@x' } }));
  const client = new GowaClient({ baseUrl: 'http://g', user: 'admin', pass: 'secret', deviceId: 'd1', fetchImpl });
  await client.status();
  const headers = fetchImpl.calls[0].options.headers;
  assert.strictEqual(headers.Authorization, 'Basic ' + Buffer.from('admin:secret').toString('base64'));
  assert.strictEqual(headers['X-Device-Id'], 'd1');
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL with `Cannot find module '../gowa-client'`.

- [ ] **Step 3: Write the implementation**

```js
'use strict';

// Unico modulo che parla HTTP con il server GOWA.
// Usa fetch/FormData/Blob globali di Node 18.13+.

function errorMessage(data, fallback) {
  if (data && typeof data.message === 'string' && data.message.trim()) return data.message;
  if (data && typeof data.code === 'string' && data.code.trim()) return data.code;
  return fallback;
}

function buildAuthHeader(user, pass) {
  if (!user) return null;
  return 'Basic ' + Buffer.from(`${user}:${pass || ''}`, 'utf8').toString('base64');
}

class GowaClient {
  constructor({ baseUrl, deviceId, user, pass, fetchImpl } = {}) {
    this.baseUrl = String(baseUrl || '').replace(/\/+$/, '');
    this.deviceId = deviceId || '';
    this.authHeader = buildAuthHeader(user, pass);
    this.fetch = fetchImpl || (typeof fetch !== 'undefined' ? fetch : null);
    if (!this.fetch) throw new Error('fetch non disponibile: richiesto Node 18.13+');
    this.resolvedDeviceId = null;
  }

  headers(extra) {
    const h = Object.assign({}, extra || {});
    if (this.authHeader) h.Authorization = this.authHeader;
    const id = this.deviceId || this.resolvedDeviceId;
    if (id) h['X-Device-Id'] = id;
    return h;
  }

  async request(method, path, { json } = {}) {
    const headers = this.headers();
    let body;
    if (json !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(json);
    }
    const res = await this.fetch(`${this.baseUrl}${path}`, { method, headers, body });
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
    return { ok: res.ok, status: res.status, data };
  }

  async ensureDevice() {
    const list = await this.request('GET', '/devices');
    const devices = (list.data && list.data.results) || [];
    if (Array.isArray(devices) && devices.length > 0) {
      this.resolvedDeviceId = devices[0].id || null;
      return this.resolvedDeviceId;
    }
    const created = await this.request('POST', '/devices', { json: {} });
    const id = created.data && created.data.results && created.data.results.id;
    this.resolvedDeviceId = id || null;
    return this.resolvedDeviceId;
  }

  async status() {
    try {
      const r = await this.request('GET', '/app/status');
      const res = (r.data && r.data.results) || {};
      return {
        isConnected: !!res.is_connected,
        isLoggedIn: !!res.is_logged_in,
        jid: res.jid || ''
      };
    } catch (e) {
      return { isConnected: false, isLoggedIn: false, jid: '' };
    }
  }

  async loginQr() {
    const r = await this.request('GET', '/app/login');
    if (!r.ok) throw new Error(errorMessage(r.data, 'Login QR non riuscito'));
    const res = r.data.results || {};
    if (!res.qr_link) throw new Error('GOWA non ha restituito un QR code');
    return { qrLink: res.qr_link, duration: res.qr_duration || 60 };
  }

  async loginWithCode(phone) {
    const r = await this.request('GET', `/app/login-with-code?phone=${encodeURIComponent(phone)}`);
    if (!r.ok) throw new Error(errorMessage(r.data, 'Login con codice non riuscito'));
    const code = (r.data.results || {}).pair_code;
    if (!code) throw new Error('GOWA non ha restituito un codice di abbinamento');
    return code;
  }

  async logout() {
    await this.request('GET', '/app/logout');
  }

  async sendText(phone, message) {
    const r = await this.request('POST', '/send/message', { json: { phone, message } });
    if (!r.ok) throw new Error(errorMessage(r.data, 'Invio messaggio non riuscito'));
    return (r.data.results || {}).message_id || '';
  }

  async sendImage(phone, caption, buffer, mimeType, fileName) {
    const form = new FormData();
    form.append('phone', phone);
    if (caption) form.append('caption', caption);
    form.append('image', new Blob([buffer], { type: mimeType || 'image/jpeg' }), fileName || 'image.jpg');

    const res = await this.fetch(`${this.baseUrl}/send/image`, {
      method: 'POST', headers: this.headers(), body: form
    });
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
    if (!res.ok) throw new Error(errorMessage(data, 'Invio immagine non riuscito'));
    return (data.results || {}).message_id || '';
  }

  async fetchBinary(urlOrPath) {
    const value = String(urlOrPath || '');
    const absolute = /^https?:\/\//i.test(value) ? value : `${this.baseUrl}/${value.replace(/^\/+/, '')}`;
    const res = await this.fetch(absolute, { headers: this.headers() });
    if (!res.ok) throw new Error(`Download non riuscito (${res.status})`);
    return {
      buffer: Buffer.from(await res.arrayBuffer()),
      contentType: (res.headers && res.headers.get && res.headers.get('content-type')) || 'application/octet-stream'
    };
  }

  async contacts() {
    const r = await this.request('GET', '/user/my/contacts');
    const data = (r.data && r.data.results && r.data.results.data) || [];
    return data.map((c) => ({ jid: c.jid, name: c.name || '' }));
  }

  async setDeviceWebhook(url) {
    const id = this.deviceId || this.resolvedDeviceId;
    if (!id) return false;
    const r = await this.request('PATCH', `/devices/${encodeURIComponent(id)}/webhook`, {
      json: { webhook_url: url }
    });
    return r.ok;
  }
}

module.exports = { GowaClient, errorMessage, buildAuthHeader };
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: PASS, `# pass 8`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/gowa-client.js WhatsappBridge/test/gowa-client.test.js
git commit -m "feat(bridge): add GOWA REST client"
```

---

### Task 4: Webhook receiver with HMAC verification

**Files:**
- Create: `WhatsappBridge/webhook-server.js`
- Test: `WhatsappBridge/test/webhook-server.test.js`

**Interfaces:**
- Consumes: `config.webhook`.
- Produces:
  - `verifySignature(rawBody, signatureHeader, secret) -> boolean`
  - `createWebhookServer({ path, secret, onEvent, log }) -> http.Server` — the server must acknowledge with `200 OK` **before** `onEvent` runs, re-`throw`-safe, and reject bad signatures with `401`.

- [ ] **Step 1: Write the failing test**

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const crypto = require('node:crypto');
const { createWebhookServer, verifySignature } = require('../webhook-server');

const noopLog = () => {};

function listen(server) {
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server.address().port)));
}

test('verifySignature accetta quando non c\'è secret', () => {
  assert.strictEqual(verifySignature(Buffer.from('x'), undefined, ''), true);
});

test('verifySignature convalida l\'HMAC sha256', () => {
  const body = Buffer.from(JSON.stringify({ event: 'message' }));
  const sig = 'sha256=' + crypto.createHmac('sha256', 'k').update(body).digest('hex');
  assert.strictEqual(verifySignature(body, sig, 'k'), true);
  assert.strictEqual(verifySignature(body, 'sha256=deadbeef', 'k'), false);
  assert.strictEqual(verifySignature(body, undefined, 'k'), false);
});

test('il webhook consegna gli eventi firmati e risponde 200', async () => {
  const received = [];
  const server = createWebhookServer({
    path: '/webhook', secret: 'k', onEvent: async (e) => received.push(e), log: noopLog
  });
  const port = await listen(server);
  try {
    const body = JSON.stringify({ event: 'message', payload: { id: '1', body: 'hi' } });
    const sig = 'sha256=' + crypto.createHmac('sha256', 'k').update(body).digest('hex');
    const res = await fetch(`http://127.0.0.1:${port}/webhook`, {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Hub-Signature-256': sig }, body
    });
    assert.strictEqual(res.status, 200);
    await new Promise((r) => setTimeout(r, 20));
    assert.strictEqual(received.length, 1);
    assert.strictEqual(received[0].payload.body, 'hi');
  } finally {
    server.close();
  }
});

test('il webhook rifiuta firme errate e path sconosciuti', async () => {
  const received = [];
  const server = createWebhookServer({
    path: '/webhook', secret: 'k', onEvent: async (e) => received.push(e), log: noopLog
  });
  const port = await listen(server);
  try {
    const bad = await fetch(`http://127.0.0.1:${port}/webhook`, {
      method: 'POST', headers: { 'X-Hub-Signature-256': 'sha256=00' }, body: '{}'
    });
    assert.strictEqual(bad.status, 401);
    const missing = await fetch(`http://127.0.0.1:${port}/nope`, { method: 'POST', body: '{}' });
    assert.strictEqual(missing.status, 404);
    assert.strictEqual(received.length, 0);
  } finally {
    server.close();
  }
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/webhook-server.test.js`
Expected: FAIL with `Cannot find module '../webhook-server'`.

- [ ] **Step 3: Write the implementation**

```js
'use strict';

const http = require('http');
const crypto = require('crypto');

// Verifica la firma HMAC-SHA256 inviata da GOWA nell'header
// X-Hub-Signature-256 ("sha256=<hex>"). Se non è configurato un secret,
// la verifica è disattivata.
function verifySignature(rawBody, signatureHeader, secret) {
  if (!secret) return true;
  if (!signatureHeader) return false;
  const received = String(signatureHeader).replace(/^sha256=/, '');
  const expected = crypto.createHmac('sha256', secret).update(rawBody).digest('hex');
  if (received.length !== expected.length) return false;
  try {
    return crypto.timingSafeEqual(Buffer.from(expected, 'hex'), Buffer.from(received, 'hex'));
  } catch (e) {
    return false;
  }
}

function createWebhookServer({ path, secret, onEvent, log }) {
  const logger = typeof log === 'function' ? log : () => {};

  return http.createServer((req, res) => {
    const requestPath = String(req.url || '').split('?')[0];

    if (req.method !== 'POST' || requestPath !== path) {
      res.writeHead(404, { 'Content-Type': 'text/plain' });
      res.end('Not found');
      return;
    }

    const chunks = [];
    req.on('data', (chunk) => chunks.push(chunk));
    req.on('error', () => { /* la risposta 200/401 arriverà comunque sotto */ });
    req.on('end', () => {
      const raw = Buffer.concat(chunks);
      if (!verifySignature(raw, req.headers['x-hub-signature-256'], secret)) {
        logger('WARN', 'Webhook con firma non valida, ignorato');
        res.writeHead(401, { 'Content-Type': 'text/plain' });
        res.end('Invalid signature');
        return;
      }

      let event = null;
      try { event = JSON.parse(raw.toString('utf8')); } catch (e) { event = null; }

      // Rispondi subito: GOWA ha un timeout breve sull'inoltro webhook.
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      res.end('OK');

      if (event && typeof onEvent === 'function') {
        Promise.resolve(onEvent(event)).catch((err) =>
          logger('ERR', `Errore elaborazione webhook: ${err.message}`));
      }
    });
  });
}

module.exports = { createWebhookServer, verifySignature };
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && node --test test/webhook-server.test.js`
Expected: PASS, `# pass 4`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/webhook-server.js WhatsappBridge/test/webhook-server.test.js
git commit -m "feat(bridge): add HMAC-verified GOWA webhook receiver"
```

---

### Task 5: TCP adapter with control protocol

**Files:**
- Rewrite: `WhatsappBridge/server.js`
- Test: `WhatsappBridge/test/server.test.js`

**Interfaces:**
- Consumes: `config.js`, `gowa-client.js`, `message-format.js`, `webhook-server.js`, `crypto-helper.js`.
- Produces:
  - `createBridge({ config, gowa, log, debug }) -> { tcpServer, getState, refreshStatus, handleWebhookEvent, handleControl, stop }`
  - `main()` that wires everything and starts listening.
  - Control protocol (all frames `Type = 3`, `ChatId = "system"`):
    - app → adapter `Command`: `hello` (Text = username), `status`, `login.qr`, `login.code` (Text = phone), `contacts`, `logout`.
    - adapter → app `Command`: `state` (`State`: `disconnected|waiting|connected`, `AccountJid`), `qr` (`QrImageData` base64 PNG, `QrDuration` seconds), `paircode` (`PairCode`), `contact` (`ChatId` = JID, `SenderName` = name), `error` (Text = message).

- [ ] **Step 1: Write the failing test**

```js
'use strict';
const test = require('node:test');
const assert = require('node:assert');
const net = require('node:net');
const { createBridge } = require('../server');
const cryptoHelper = require('../crypto-helper');

const noop = () => {};

function fakeGowa(overrides = {}) {
  return Object.assign({
    status: async () => ({ isConnected: true, isLoggedIn: false, jid: '' }),
    loginQr: async () => ({ qrLink: 'http://g/statics/qr.png', duration: 30 }),
    fetchBinary: async () => ({ buffer: Buffer.from([1, 2, 3]), contentType: 'image/png' }),
    loginWithCode: async (phone) => `CODE-${phone}`,
    logout: async () => {},
    contacts: async () => [{ jid: '39@s.whatsapp.net', name: 'Mario' }],
    sendText: async () => 'M1',
    sendImage: async () => 'M2',
    setDeviceWebhook: async () => true
  }, overrides);
}

function connectClient(port) {
  const socket = net.connect(port, '127.0.0.1');
  let buffer = Buffer.alloc(0);
  const messages = [];
  const waiters = [];
  socket.on('data', (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    while (buffer.length >= 4) {
      const len = buffer.readUInt32LE(0);
      if (buffer.length < 4 + len) break;
      const payload = buffer.slice(4, 4 + len);
      buffer = buffer.slice(4 + len);
      const json = JSON.parse(cryptoHelper.decodePayload(payload));
      messages.push(json);
      while (waiters.length) waiters.shift()(json);
    }
  });
  return {
    socket,
    messages,
    send(msg) {
      socket.write(cryptoHelper.buildFrame(JSON.stringify(msg)));
    },
    next(timeoutMs = 2000) {
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('timeout')), timeoutMs);
        waiters.push((m) => { clearTimeout(timer); resolve(m); });
      });
    }
  };
}

test('il bridge invia lo stato al collegamento e gestisce login.qr', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    const state = await client.next();
    assert.strictEqual(state.Command, 'state');
    assert.strictEqual(state.State, 'disconnected');

    client.send({ Type: 3, ChatId: 'system', Command: 'login.qr', Timestamp: '\\/Date(0)\\/' });
    const qr = await client.next();
    assert.strictEqual(qr.Command, 'qr');
    assert.strictEqual(qr.QrImageData, Buffer.from([1, 2, 3]).toString('base64'));
    assert.strictEqual(qr.QrDuration, 30);
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il bridge risponde a login.code con il codice di abbinamento', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next(); // stato iniziale
    client.send({ Type: 3, ChatId: 'system', Command: 'login.code', Text: '393401234567' });
    const reply = await client.next();
    assert.strictEqual(reply.Command, 'paircode');
    assert.strictEqual(reply.PairCode, 'CODE-393401234567');
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il bridge inoltra a WhatsApp un messaggio utente ricevuto via TCP', async () => {
  let sent = null;
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({
    config,
    gowa: fakeGowa({ sendText: async (phone, text) => { sent = { phone, text }; return 'M1'; } }),
    log: noop, debug: noop
  });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    bridge.setConnectedForTest();
    client.send({ Type: 0, ChatId: '39@s.whatsapp.net', Text: 'ciao', SenderName: 'Io' });
    await new Promise((r) => setTimeout(r, 50));
    assert.deepStrictEqual(sent, { phone: '39@s.whatsapp.net', text: 'ciao' });
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});

test('il bridge inoltra ai client WP8 i messaggi ricevuti dal webhook', async () => {
  const config = { bridge: { port: 0 }, webhook: {}, pollIntervalMs: 60000 };
  const bridge = createBridge({ config, gowa: fakeGowa(), log: noop, debug: noop });
  await new Promise((r) => bridge.tcpServer.listen(0, '127.0.0.1', r));
  const port = bridge.tcpServer.address().port;
  const client = connectClient(port);
  try {
    await client.next();
    await bridge.handleWebhookEvent({
      event: 'message',
      payload: {
        id: 'X1', chat_id: '39@s.whatsapp.net', from: '39@s.whatsapp.net',
        sender_display_name: 'Mario', timestamp: '2026-01-02T03:04:05Z', is_from_me: false, body: 'Ehi'
      }
    });
    const msg = await client.next();
    assert.strictEqual(msg.Type, 0);
    assert.strictEqual(msg.Text, 'Ehi');
    assert.strictEqual(msg.IsIncoming, true);
    assert.strictEqual(msg.ChatId, '39@s.whatsapp.net');
    assert.strictEqual(msg.Status, 3);
  } finally {
    client.socket.destroy();
    bridge.tcpServer.close();
    bridge.stop();
  }
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL — `createBridge` is not exported (old `server.js` exports nothing).

- [ ] **Step 3: Write the implementation**

Replace the whole `WhatsappBridge/server.js` with:

```js
/**
 * ============================================================================
 *  WhatsApp Community Adapter v2.0
 * ============================================================================
 *  Sostituisce il vecchio bridge whatsapp-web.js.
 *
 *  Fa da ponte tra l'app Windows Phone 8.1 e un server GOWA self-hosted
 *  (github.com/vincenzosco/go-whatsapp-web-multidevice):
 *
 *   - TCP cifrato (AES-256-GCM) verso l'app WP8, protocollo invariato.
 *   - HTTP verso l'API REST di GOWA (login QR / login con numero, stato,
 *     invio testo e immagini, contatti).
 *   - Server HTTP webhook che riceve da GOWA i messaggi in arrivo e li
 *     inoltra all'app WP8.
 *
 *  Protocollo di controllo (frame Type = 3, ChatId = "system"):
 *    app -> adapter : hello | status | login.qr | login.code | contacts | logout
 *    adapter -> app : state | qr | paircode | contact | error
 *
 *  Avvio:  npm install && npm start
 * ============================================================================
 */

'use strict';

const net = require('net');
const os = require('os');

const cryptoHelper = require('./crypto-helper');
const { loadConfig } = require('./config');
const { GowaClient } = require('./gowa-client');
const { buildChatMessage, mapWebhookMessage } = require('./message-format');
const { createWebhookServer } = require('./webhook-server');

const LOG_TAGS = { INFO: '[INFO]', OK: '[OK]', WARN: '[WARN]', ERR: '[ERR]', MSG: '[MSG]', QR: '[QR]', NET: '[NET]' };

function makeLogger(enabled) {
  return function log(level, ...args) {
    if (level === 'DEBUG' && !enabled) return;
    const ts = new Date().toISOString().replace('T', ' ').substring(0, 19);
    console.log(`${ts} ${LOG_TAGS[level] || ''}`, ...args);
  };
}

function createBridge({ config, gowa, log, debug }) {
  const logger = typeof log === 'function' ? log : () => {};
  const dbg = typeof debug === 'function' ? debug : () => {};

  const wp8Clients = new Set();
  const pendingOutgoing = [];
  let state = { status: 'disconnected', jid: '' };
  let qrCache = null;

  // ─── Invio verso l'app WP8 ────────────────────────────────────────────────

  function frame(jsonObject) {
    return cryptoHelper.buildFrame(JSON.stringify(jsonObject));
  }

  function sendToClient(socket, msg) {
    try { socket.write(frame(msg)); } catch (e) { /* socket morto */ }
  }

  function sendToClients(msg) {
    if (wp8Clients.size === 0) return;
    const packet = frame(msg);
    const dead = [];
    for (const socket of wp8Clients) {
      try { socket.write(packet); } catch (e) { dead.push(socket); }
    }
    for (const socket of dead) wp8Clients.delete(socket);
  }

  function broadcastState() {
    sendToClients(buildChatMessage({
      command: 'state',
      state: state.status,
      accountJid: state.jid || undefined,
      chatId: 'system',
      type: 3,
      isIncoming: true
    }));
  }

  function sendControl(fields) {
    sendToClients(buildChatMessage(Object.assign({ chatId: 'system', type: 3, isIncoming: true }, fields)));
  }

  // ─── Stato e login ────────────────────────────────────────────────────────

  async function refreshStatus() {
    try {
      const s = await gowa.status();
      const next = s.isLoggedIn ? 'connected' : (state.status === 'waiting' ? 'waiting' : 'disconnected');
      const changed = next !== state.status || (s.jid || '') !== state.jid;
      state = { status: next, jid: s.jid || '' };

      if (next === 'connected') {
        qrCache = null;
        if (changed) {
          broadcastState();
          logger('OK', `WhatsApp connesso come ${state.jid || 'sconosciuto'}`);
          await flushPending();
          await syncContacts();
        }
      } else {
        if (changed) broadcastState();
      }
    } catch (err) {
      dbg(`Stato non disponibile: ${err.message}`);
    }
  }

  async function requestQr() {
    if (state.status === 'connected') { broadcastState(); return; }
    try {
      const now = Date.now();
      if (qrCache && qrCache.expiresAt > now) {
        sendControl({ command: 'qr', qrImageData: qrCache.base64, qrDuration: qrCache.duration });
        return;
      }
      const { qrLink, duration } = await gowa.loginQr();
      const image = await gowa.fetchBinary(qrLink);
      const base64 = image.buffer.toString('base64');
      qrCache = { base64, duration, expiresAt: now + duration * 1000 };
      state = { status: 'waiting', jid: '' };
      // L'immagine va inviata prima dello stato, così l'app la mostra subito.
      sendControl({ command: 'qr', qrImageData: base64, qrDuration: duration });
      broadcastState();
      logger('QR', 'Nuovo QR code inviato all\'app');
    } catch (err) {
      logger('ERR', `Login QR fallito: ${err.message}`);
      sendControl({ command: 'error', text: `Login QR fallito: ${err.message}` });
    }
  }

  async function requestPairCode(phone) {
    if (!phone) {
      sendControl({ command: 'error', text: 'Numero di telefono mancante' });
      return;
    }
    if (state.status === 'connected') { broadcastState(); return; }
    try {
      const code = await gowa.loginWithCode(phone);
      state = { status: 'waiting', jid: '' };
      sendControl({ command: 'paircode', pairCode: code });
      broadcastState();
      logger('QR', `Codice di abbinamento inviato all'app per ${phone}`);
    } catch (err) {
      logger('ERR', `Login con codice fallito: ${err.message}`);
      sendControl({ command: 'error', text: `Login con codice fallito: ${err.message}` });
    }
  }

  async function syncContacts() {
    try {
      const contacts = await gowa.contacts();
      for (const contact of contacts) {
        if (!contact.jid) continue;
        sendControl({ command: 'contact', chatId: contact.jid, senderName: contact.name || undefined });
      }
      logger('INFO', `Sincronizzati ${contacts.length} contatti`);
    } catch (err) {
      logger('WARN', `Sincronizzazione contatti fallita: ${err.message}`);
    }
  }

  // ─── Messaggi dall'app verso WhatsApp ─────────────────────────────────────

  async function sendOutgoing(msg) {
    const hasMedia = !!msg.MediaData;
    try {
      if (hasMedia) {
        const buffer = Buffer.from(msg.MediaData, 'base64');
        await gowa.sendImage(msg.ChatId, msg.Text || '', buffer, msg.MediaMimeType || 'image/jpeg', msg.MediaFileName);
      } else if (msg.Text && msg.Text.trim()) {
        await gowa.sendText(msg.ChatId, msg.Text);
      }
      logger('MSG', `Inviato a ${msg.ChatId}: ${(msg.Text || '[media]').substring(0, 40)}`);
    } catch (err) {
      logger('ERR', `Invio a ${msg.ChatId} fallito: ${err.message}`);
      sendControl({ command: 'error', chatId: msg.ChatId, text: `Invio non riuscito: ${err.message}` });
    }
  }

  async function flushPending() {
    if (pendingOutgoing.length === 0) return;
    const queued = pendingOutgoing.splice(0, pendingOutgoing.length);
    logger('INFO', `Invio ${queued.length} messaggi in coda...`);
    for (const msg of queued) await sendOutgoing(msg);
  }

  async function handleUserMessage(msg) {
    if ((!msg.Text || !msg.Text.trim()) && !msg.MediaData) {
      logger('WARN', 'Messaggio WP8 senza contenuto, ignorato');
      return;
    }
    if (state.status !== 'connected') {
      pendingOutgoing.push(msg);
      logger('INFO', 'WhatsApp non pronto: messaggio messo in coda');
      sendControl({ chatId: msg.ChatId, text: 'WhatsApp non ancora connesso. Il messaggio verrà inviato automaticamente.' });
      return;
    }
    await sendOutgoing(msg);
  }

  // ─── Messaggi da WhatsApp verso l'app ─────────────────────────────────────

  async function handleWebhookEvent(event) {
    if (!event || event.event !== 'message') return;
    const fields = mapWebhookMessage(event.payload || {});
    if (!fields) return;

    let mediaData = null;
    let mediaMimeType = fields.mediaMimeType;
    if (fields.mediaPath) {
      try {
        const media = await gowa.fetchBinary(fields.mediaPath);
        mediaData = media.buffer.toString('base64');
        if (!mediaMimeType) mediaMimeType = media.contentType;
      } catch (err) {
        logger('WARN', `Media non scaricato (${fields.mediaPath}): ${err.message}`);
      }
    }

    logger('MSG', `Da ${fields.senderName}: ${(fields.text || '[media]').substring(0, 60)}`);
    sendToClients(buildChatMessage({
      id: fields.id,
      text: fields.text,
      senderId: fields.senderId,
      senderName: fields.senderName,
      chatId: fields.chatId,
      timestamp: fields.timestamp,
      status: 3,
      type: fields.type,
      isIncoming: true,
      mediaData,
      mediaMimeType,
      mediaFileName: fields.mediaFileName
    }));
  }

  // ─── Protocollo di controllo ──────────────────────────────────────────────

  async function handleControl(msg) {
    switch (msg.Command) {
      case 'hello':
        logger('NET', `Handshake da "${msg.SenderName || 'Sconosciuto'}"`);
        broadcastState();
        break;
      case 'status':
        broadcastState();
        break;
      case 'login.qr':
        await requestQr();
        break;
      case 'login.code':
        await requestPairCode((msg.Text || '').trim());
        break;
      case 'contacts':
        if (state.status === 'connected') await syncContacts();
        break;
      case 'logout':
        try { await gowa.logout(); } catch (e) { /* ignora */ }
        state = { status: 'disconnected', jid: '' };
        qrCache = null;
        broadcastState();
        break;
      default:
        dbg(`Comando sconosciuto: ${msg.Command}`);
    }
  }

  // ─── Server TCP ───────────────────────────────────────────────────────────

  const tcpServer = net.createServer((socket) => {
    const remote = `${socket.remoteAddress}:${socket.remotePort}`;
    logger('NET', `Client WP8 connesso: ${remote}`);
    wp8Clients.add(socket);

    // Stato immediato al collegamento.
    sendToClient(socket, buildChatMessage({
      command: 'state',
      state: state.status,
      accountJid: state.jid || undefined,
      chatId: 'system',
      type: 3,
      isIncoming: true
    }));

    let buffer = Buffer.alloc(0);
    socket.on('data', (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length >= 4) {
        const msgLen = buffer.readUInt32LE(0);
        if (buffer.length < 4 + msgLen) break;
        const payload = buffer.slice(4, 4 + msgLen);
        buffer = buffer.slice(4 + msgLen);
        try {
          const msg = JSON.parse(cryptoHelper.decodePayload(payload));
          if (msg.Type === 3) handleControl(msg).catch((e) => logger('ERR', e.message));
          else handleUserMessage(msg).catch((e) => logger('ERR', e.message));
        } catch (err) {
          logger('ERR', `Frame non valido da WP8: ${err.message}`);
        }
      }
    });

    socket.on('close', () => { logger('NET', `Client WP8 disconnesso: ${remote}`); wp8Clients.delete(socket); });
    socket.on('error', (err) => { logger('NET', `Errore socket [${remote}]: ${err.message}`); wp8Clients.delete(socket); });
  });

  return {
    tcpServer,
    getState: () => state,
    refreshStatus,
    handleWebhookEvent,
    handleControl,
    syncContacts,
    // Usato solo dai test: forza lo stato "connected" senza passare da GOWA.
    setConnectedForTest() { state = { status: 'connected', jid: '39@s.whatsapp.net' }; },
    stop() { /* il timer di polling è gestito da main() */ }
  };
}

// ─── Avvio ──────────────────────────────────────────────────────────────────

async function main() {
  const debug = process.argv.includes('--debug');
  const log = makeLogger(debug);
  const dbg = (...args) => { if (debug) console.log('  [DEBUG]', ...args); };

  const config = loadConfig();
  log('INFO', 'WhatsApp Community Adapter v2.0 (GOWA)');
  log('INFO', `GOWA:        ${config.gowa.url}`);
  log('INFO', `Device GOWA: ${config.gowa.deviceId || '(default)'}`);
  log('INFO', `TCP app:     ${config.bridge.port}`);
  log('INFO', `Webhook:     ${config.webhook.publicUrl}`);
  log('INFO', `Cifratura:   AES-256-GCM ${cryptoHelper.ENCRYPTION_ENABLED ? 'ATTIVA' : 'DISATTIVATA'}`);

  const gowa = new GowaClient({
    baseUrl: config.gowa.url,
    deviceId: config.gowa.deviceId,
    user: config.gowa.user,
    pass: config.gowa.pass
  });

  const webhookServer = createWebhookServer({
    path: config.webhook.path,
    secret: config.webhook.secret,
    log,
    onEvent: (event) => bridge.handleWebhookEvent(event)
  });

  const bridge = createBridge({ config, gowa, log, debug: dbg });

  try {
    const deviceId = await gowa.ensureDevice();
    log('OK', `Device GOWA pronto: ${deviceId || '(default)'}`);
    const registered = await gowa.setDeviceWebhook(config.webhook.publicUrl);
    log(registered ? 'OK' : 'WARN',
      registered
        ? `Webhook registrato su GOWA: ${config.webhook.publicUrl}`
        : `Registrazione webhook automatica non riuscita: avvia GOWA con --webhook=${config.webhook.publicUrl}`);
  } catch (err) {
    log('ERR', `GOWA non raggiungibile su ${config.gowa.url}: ${err.message}`);
    log('ERR', 'Avvia GOWA con: ./whatsapp rest --basic-auth=utente:password');
  }

  bridge.tcpServer.listen(config.bridge.port, '0.0.0.0', () => {
    log('OK', `Server TCP in ascolto sulla porta ${config.bridge.port}`);
    const addresses = [];
    const interfaces = os.networkInterfaces();
    Object.keys(interfaces).forEach((name) => {
      (interfaces[name] || []).forEach((iface) => {
        if (iface.family === 'IPv4' && !iface.internal) addresses.push(iface.address);
      });
    });
    log('INFO', `   Connetti l'app WP8 a: ${addresses.join(', ') || '(IP non trovato)'}:${config.bridge.port}`);
  });

  webhookServer.listen(config.webhook.port, '0.0.0.0', () => {
    log('OK', `Webhook in ascolto sulla porta ${config.webhook.port}${config.webhook.path}`);
  });

  bridge.tcpServer.on('error', (err) => {
    log('ERR', `Errore server TCP: ${err.message}`);
    if (err.code === 'EADDRINUSE') log('ERR', `Porta ${config.bridge.port} già in uso (usa BRIDGE_PORT=...).`);
    process.exit(1);
  });

  await bridge.refreshStatus();
  const timer = setInterval(() => bridge.refreshStatus(), config.pollIntervalMs);

  const shutdown = () => {
    clearInterval(timer);
    bridge.stop();
    try { webhookServer.close(); } catch (e) { /* ignora */ }
    try { bridge.tcpServer.close(); } catch (e) { /* ignora */ }
    log('OK', 'Adapter arrestato.');
    process.exit(0);
  };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
}

if (require.main === module) {
  main().catch((err) => {
    console.error('ERRORE FATALE:', err);
    process.exit(1);
  });
}

module.exports = { createBridge, main, makeLogger };
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: PASS, `# pass 4`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/server.js WhatsappBridge/test/server.test.js
git commit -m "feat(bridge): bridge WP8 TCP to GOWA REST API and webhook"
```

---

### Task 6: Adapter package, env example and full test run

**Files:**
- Rewrite: `WhatsappBridge/package.json`
- Create: `WhatsappBridge/.env.example`

**Interfaces:**
- Consumes: all adapter modules.
- Produces: npm scripts `start`, `start:debug`, `test`; no runtime dependencies.

- [ ] **Step 1: Replace `WhatsappBridge/package.json`**

```json
{
  "name": "whatsapp-gowa-adapter",
  "version": "2.0.0",
  "description": "Adapter tra l'app WhatsApp Windows Phone 8.1 e un server GOWA self-hosted (go-whatsapp-web-multidevice)",
  "main": "server.js",
  "scripts": {
    "start": "node server.js",
    "start:debug": "node server.js --debug",
    "test": "node --test"
  },
  "keywords": ["whatsapp", "gowa", "bridge", "wp8", "windows-phone"],
  "license": "MIT",
  "dependencies": {},
  "engines": {
    "node": ">=18.13.0"
  }
}
```

- [ ] **Step 2: Create `WhatsappBridge/.env.example`**

```dotenv
# ── Server GOWA (go-whatsapp-web-multidevice) ────────────────────────────────
# URL del server GOWA avviato con:  ./whatsapp rest
GOWA_URL=http://127.0.0.1:3000

# Se GOWA è avviato con --basic-auth=utente:password, indica le credenziali.
GOWA_USER=
GOWA_PASS=

# Facoltativo: id di un device GOWA specifico (multi-device). Vuoto = device default.
GOWA_DEVICE_ID=

# ── Adapter verso l'app Windows Phone ───────────────────────────────────────
# Porta TCP sulla quale l'app WP8 si collega.
BRIDGE_PORT=8585

# Deve combaciare con la costante "Passphrase" di WhatsappApp/Services/CryptoHelper.cs
BRIDGE_KEY=WhatsAppCommunityWP8-2026
# Metti "off" per disattivare la cifratura (protocollo in chiaro, sconsigliato).
BRIDGE_ENCRYPTION=on

# ── Webhook (GOWA -> adapter) ───────────────────────────────────────────────
WEBHOOK_PORT=8586
WEBHOOK_PATH=/webhook
# URL con cui GOWA raggiunge questo adapter. Necessario se GOWA non è sulla stessa macchina.
WEBHOOK_PUBLIC_URL=http://127.0.0.1:8586/webhook
# Deve combaciare con --webhook-secret di GOWA (default: "secret").
WEBHOOK_SECRET=

# ── Varie ───────────────────────────────────────────────────────────────────
# Intervallo di polling dello stato WhatsApp (ms).
POLL_INTERVAL_MS=5000
```

- [ ] **Step 3: Run the whole adapter test suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS — all files under `test/` run, `# fail 0`.

- [ ] **Step 4: Smoke-test against a real GOWA (manual, documented)**

Run GOWA in one terminal:

```bash
./whatsapp rest --basic-auth=admin:admin --port=3000
```

Run the adapter in another:

```bash
cd WhatsappBridge
GOWA_URL=http://127.0.0.1:3000 GOWA_USER=admin GOWA_PASS=admin npm start
```

Expected output: `Device GOWA pronto: ...`, `Webhook registrato su GOWA: http://127.0.0.1:8586/webhook`, `Server TCP in ascolto sulla porta 8585`, `Webhook in ascolto sulla porta 8586/webhook`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/package.json WhatsappBridge/.env.example
git commit -m "chore(bridge): drop whatsapp-web.js deps and document GOWA env"
```

---

### Task 7: App model control fields

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: new `[DataMember]` string/int properties used by Task 8 and Task 9:
  `Command`, `State`, `PairCode`, `QrImageData`, `QrDuration` (int), `AccountJid`.

- [ ] **Step 1: Add the control fields after `MediaFileName`**

In `WhatsappApp/Models/ChatMessage.cs`, insert the backing fields:

```csharp
        private string _mediaFileName;  // optional filename
        private string _command;        // control frame command (see adapter protocol)
        private string _state;          // "disconnected" | "waiting" | "connected"
        private string _pairCode;       // pairing code for phone-number login
        private string _qrImageData;    // base64 PNG of the login QR code
        private int _qrDuration;        // QR validity in seconds
        private string _accountJid;     // WhatsApp JID of the logged-in account
```

and the properties right after the `MediaFileName` property (before `FormattedTime`):

```csharp
        /// <summary>Comando dei frame di controllo inviati/ricevuti dall'adapter (Type = System).</summary>
        [DataMember]
        public string Command
        {
            get => _command;
            set { _command = value; OnPropertyChanged(); }
        }

        /// <summary>Stato della connessione WhatsApp: "disconnected", "waiting" o "connected".</summary>
        [DataMember]
        public string State
        {
            get => _state;
            set { _state = value; OnPropertyChanged(); }
        }

        /// <summary>Codice di abbinamento da inserire sul telefono (login via numero).</summary>
        [DataMember]
        public string PairCode
        {
            get => _pairCode;
            set { _pairCode = value; OnPropertyChanged(); }
        }

        /// <summary>QR code di login codificato in base64 (PNG).</summary>
        [DataMember]
        public string QrImageData
        {
            get => _qrImageData;
            set { _qrImageData = value; OnPropertyChanged(); }
        }

        /// <summary>Durata di validità del QR code, in secondi.</summary>
        [DataMember]
        public int QrDuration
        {
            get => _qrDuration;
            set { _qrDuration = value; OnPropertyChanged(); }
        }

        /// <summary>JID dell'account WhatsApp collegato (es. 393401234567@s.whatsapp.net).</summary>
        [DataMember]
        public string AccountJid
        {
            get => _accountJid;
            set { _accountJid = value; OnPropertyChanged(); }
        }
```

- [ ] **Step 2: Build the app**

Run (Windows, Visual Studio 2015 developer prompt, WP8.1 SDK installed):

```
msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=AnyCPU
```

Expected: `Build succeeded.` with `0 Error(s)`. (This machine has no WP8 toolchain; the executor runs this on Windows.)

- [ ] **Step 3: Commit**

```bash
git add WhatsappApp/Models/ChatMessage.cs
git commit -m "feat(app): add control-frame fields to ChatMessage"
```

---

### Task 8: CommunicationService control channel

**Files:**
- Modify: `WhatsappApp/Services/CommunicationService.cs`

**Interfaces:**
- Consumes: `ChatMessage.Command/State/AccountJid` from Task 7.
- Produces:
  - `event EventHandler<ChatMessage> ControlMessageReceived`
  - `string WhatsAppState { get; }`, `string AccountJid { get; }`
  - `Task SendControlAsync(string command, string payload = null)`
  - `MessageReceived` now fires only for non-system messages.

- [ ] **Step 1: Add the event and state properties**

Next to the existing events and properties:

```csharp
        public event EventHandler<ChatMessage> MessageReceived;
        public event EventHandler<ChatMessage> ControlMessageReceived;
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;

        public bool IsConnected => _isConnected;
        public bool IsServerMode => _isServerMode;
        public string MyUserId => _myUserId;
        public string MyUsername => _myUsername;
        public string ServerAddress => _serverAddress;
        public string WhatsAppState { get; private set; } = "disconnected";
        public string AccountJid { get; private set; } = "";
```

- [ ] **Step 2: Route decrypted messages by type**

Add this method just above `ReadFrameAsync`:

```csharp
        /// <summary>
        /// Instrada un messaggio decifrato: i frame di controllo (Type = System)
        /// vanno all'evento ControlMessageReceived, gli altri a MessageReceived.
        /// </summary>
        private void DispatchMessage(ChatMessage message)
        {
            if (message == null) return;

            if (message.Type == MessageType.System)
            {
                if (message.Command == "state")
                {
                    WhatsAppState = string.IsNullOrEmpty(message.State) ? "disconnected" : message.State;
                    AccountJid = message.AccountJid ?? "";
                }
                DispatchOnUiThread(() => ControlMessageReceived?.Invoke(this, message));
            }
            else
            {
                DispatchOnUiThread(() => MessageReceived?.Invoke(this, message));
            }
        }
```

Then replace the two existing delivery sites. In `OnServerConnectionReceived`:

```csharp
                    // Decrypt for the local UI
                    var message = DecryptToMessage(payload);
                    if (message != null)
                    {
                        DispatchOnUiThread(() => MessageReceived?.Invoke(this, message));
                    }
```

becomes

```csharp
                    // Decrypt for the local UI
                    DispatchMessage(DecryptToMessage(payload));
```

and in `ListenForMessagesAsync`:

```csharp
                    var message = DecryptToMessage(payload);
                    if (message != null)
                    {
                        DispatchOnUiThread(() => MessageReceived?.Invoke(this, message));
                    }
```

becomes

```csharp
                    DispatchMessage(DecryptToMessage(payload));
```

- [ ] **Step 3: Tag the handshake and add the control sender**

In `ConnectToServerAsync`, the handshake object gains `Command = "hello"`:

```csharp
                var handshake = new ChatMessage
                {
                    Id = "handshake",
                    Text = username,
                    Command = "hello",
                    SenderId = _myUserId,
                    SenderName = username,
                    ChatId = "system",
                    Timestamp = DateTime.Now,
                    Type = MessageType.System,
                    IsIncoming = false
                };
```

Add this public method right after `SendMessageAsync`:

```csharp
        /// <summary>
        /// Invia un frame di controllo all'adapter (stato, login QR/numero,
        /// contatti, logout). Il payload va in Text quando serve.
        /// </summary>
        public async Task SendControlAsync(string command, string payload = null)
        {
            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = command,
                Text = payload ?? "",
                SenderId = _myUserId ?? "me",
                SenderName = _myUsername ?? "Io",
                ChatId = "system",
                Timestamp = DateTime.Now,
                Type = MessageType.System,
                IsIncoming = false
            };
            await SendMessageAsync(message);
        }
```

- [ ] **Step 4: Reset control state on disconnect**

In `Disconnect()`, right after `_isConnected = false;`, add:

```csharp
            WhatsAppState = "disconnected";
            AccountJid = "";
```

- [ ] **Step 5: Build the app**

Run: `msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=AnyCPU`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/Services/CommunicationService.cs
git commit -m "feat(app): add control channel to CommunicationService"
```

---

### Task 9: Real contacts in DataService

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs`

**Interfaces:**
- Consumes: `ControlMessageReceived` from Task 8.
- Produces: `Contacts` populated only from the adapter; helper `DisplayNameForJid` and `InitialsFor`.

- [ ] **Step 1: Remove the demo data**

In the constructor, delete the `LoadSampleData();` call so it reads:

```csharp
        private DataService()
        {
            _contacts = new ObservableCollection<Contact>();
            _chatMessages = new Dictionary<string, ObservableCollection<ChatMessage>>();

            // Wire up to receive network messages
            CommunicationService.Instance.MessageReceived += OnNetworkMessageReceived;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
        }
```

Then delete the entire `LoadSampleData()` method (the block starting at `private void LoadSampleData()` and ending before `public ObservableCollection<ChatMessage> GetMessages(string chatId)`).

- [ ] **Step 2: Add the control handler and name helpers**

Add immediately after `OnNetworkMessageReceived` (keeping the existing method as is):

```csharp
        /// <summary>
        /// Gestisce i frame di controllo in arrivo dall'adapter: per ora la
        /// sincronizzazione dei contatti ("contact").
        /// </summary>
        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null || message.Command != "contact" || string.IsNullOrEmpty(message.ChatId))
                return;

            var contact = _contacts.FirstOrDefault(c => c.Id == message.ChatId);
            string name = string.IsNullOrEmpty(message.SenderName)
                ? DisplayNameForJid(message.ChatId)
                : message.SenderName;

            if (contact == null)
            {
                _contacts.Add(new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    Status = "",
                    Initials = InitialsFor(name),
                    AvatarColor = "#FF075E54",
                    IsOnline = false,
                    UnreadCount = 0
                });
            }
            else
            {
                contact.Name = name;
                contact.Initials = InitialsFor(name);
            }
        }

        /// <summary>Nome mostrato per un JID quando non ne conosciamo il nome.</summary>
        public static string DisplayNameForJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "?";
            string user = jid.Split('@')[0];
            if (jid.EndsWith("@g.us")) return "Gruppo " + user;
            if (user.Length >= 8 && user.All(char.IsDigit)) return "+" + user;
            return string.IsNullOrEmpty(user) ? "?" : user;
        }

        private static string InitialsFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            string trimmed = name.Trim();
            if (trimmed.StartsWith("+") && trimmed.Length > 1)
                return trimmed.Substring(1, Math.Min(2, trimmed.Length - 1)).ToUpper();
            return trimmed.Substring(0, 1).ToUpper();
        }
```

- [ ] **Step 3: Use the helpers when auto-creating contacts from messages**

In `OnNetworkMessageReceived`, replace the contact-creation block:

```csharp
            var contact = _contacts.FirstOrDefault(c => c.Id == message.ChatId);
            if (contact == null)
            {
                contact = new Contact
                {
                    Id = message.ChatId,
                    Name = message.SenderName,
                    LastMessage = message.Text,
                    LastMessageTime = message.FormattedTime,
                    Initials = message.SenderName.Length > 0 ? message.SenderName.Substring(0, 1).ToUpper() : "?",
                    AvatarColor = "#FF075E54",
                    IsOnline = true,
                    UnreadCount = 0
                };
                _contacts.Insert(0, contact);
            }
```

with:

```csharp
            var contact = _contacts.FirstOrDefault(c => c.Id == message.ChatId);
            if (contact == null)
            {
                string name = string.IsNullOrEmpty(message.SenderName)
                    ? DisplayNameForJid(message.ChatId)
                    : message.SenderName;

                contact = new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    LastMessage = message.Text,
                    LastMessageTime = message.FormattedTime,
                    Initials = InitialsFor(name),
                    AvatarColor = "#FF075E54",
                    IsOnline = true,
                    UnreadCount = 0
                };
                _contacts.Insert(0, contact);
            }
```

- [ ] **Step 4: Build the app**

Run: `msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=AnyCPU`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Services/DataService.cs
git commit -m "feat(app): load real contacts from the adapter"
```

---

### Task 10: In-app QR and phone-number login page

**Files:**
- Rewrite: `WhatsappApp/Pages/ConnectionPage.xaml`
- Rewrite: `WhatsappApp/Pages/ConnectionPage.xaml.cs`

**Interfaces:**
- Consumes: `CommunicationService.SendControlAsync`, `ControlMessageReceived`, `WhatsAppState`, `AccountJid`; `SettingsService`.
- Produces: UI controls `ServerAddressBox`, `ServerPortBox`, `UsernameBox`, `ActionButton`, `DisconnectButton`, `StatusPanel`, `StatusText`, `WhatsAppPanel`, `WhatsAppStateText`, `LoginQrButton`, `QrImage`, `QrInfoText`, `PhoneBox`, `LoginCodeButton`, `PairCodeText`, `ContinueButton`; sends control `login.qr`, `login.code`, `status`, `contacts`.

- [ ] **Step 1: Replace `WhatsappApp/Pages/ConnectionPage.xaml`**

```xml
<Page
    x:Class="WhatsappApp.Pages.ConnectionPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:local="using:WhatsappApp"
    Background="#FFECE5DD">

    <Page.Resources>
        <SolidColorBrush x:Key="WhatsAppHeaderBrush" Color="#FF075E54"/>
        <SolidColorBrush x:Key="WhatsAppAccentBrush" Color="#FF25D366"/>
        <SolidColorBrush x:Key="WhatsAppGreenBrush" Color="#FF128C7E"/>
    </Page.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Header -->
        <Grid Grid.Row="0" Background="{StaticResource WhatsAppHeaderBrush}" Height="56">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <Button x:Name="BackButton" Grid.Column="0"
                    Content="&#xE0C4;" FontFamily="Segoe MDL2 Assets"
                    Foreground="White" Background="Transparent"
                    Width="48" Height="48" Margin="0,4,0,0"
                    Click="BackButton_Click"
                    BorderThickness="0"/>

            <TextBlock x:Name="PageTitleText" Grid.Column="1" Text="Configurazione"
                       Foreground="White" FontSize="20" FontWeight="SemiBold"
                       VerticalAlignment="Center" Margin="8,0,0,4"/>
        </Grid>

        <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto">
            <StackPanel Margin="16,16,16,16">

                <!-- Server (adapter) -->
                <TextBlock Text="Server WhatsApp (adapter)" Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold"/>
                <TextBox x:Name="ServerAddressBox" Text="192.168.1.100"
                         PlaceholderText="es. 192.168.1.100"
                         Background="White" FontSize="16" Margin="0,4,0,0"/>

                <TextBlock Text="Porta TCP" Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold" Margin="0,12,0,0"/>
                <TextBox x:Name="ServerPortBox" Text="8585"
                         PlaceholderText="8585"
                         Background="White" FontSize="16" Margin="0,4,0,0"/>

                <TextBlock Text="Il tuo nome" Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold" Margin="0,12,0,0"/>
                <TextBox x:Name="UsernameBox" Text="Utente"
                         Background="White" FontSize="16" Margin="0,4,0,0"/>

                <Button x:Name="ActionButton" Content="Connetti al server"
                        Background="{StaticResource WhatsAppAccentBrush}"
                        Foreground="White" FontSize="18" FontWeight="SemiBold"
                        Height="52" BorderThickness="0" Margin="0,12,0,0"
                        Click="ActionButton_Click"/>

                <Button x:Name="DisconnectButton" Content="Disconnetti"
                        Background="#FFE53935"
                        Foreground="White" FontSize="16"
                        Height="44" BorderThickness="0" Margin="0,8,0,0"
                        Visibility="Collapsed"
                        Click="DisconnectButton_Click"/>

                <!-- Status -->
                <Border x:Name="StatusPanel" Background="#FFF0F0F0" CornerRadius="8" Margin="0,8,0,0"
                        Visibility="Collapsed">
                    <StackPanel Margin="12">
                        <TextBlock x:Name="StatusText" Text="Connessione in corso..."
                                   Foreground="#FF128C7E" FontSize="14" TextAlignment="Center"
                                   TextWrapping="Wrap"/>
                    </StackPanel>
                </Border>

                <!-- WhatsApp login -->
                <StackPanel x:Name="WhatsAppPanel" Visibility="Collapsed" Margin="0,16,0,0">
                    <TextBlock Text="Connessione a WhatsApp" Foreground="#FF075E54" FontSize="14" FontWeight="SemiBold"/>

                    <Border Background="White" CornerRadius="8" Margin="0,4,0,0">
                        <StackPanel Margin="12">
                            <TextBlock x:Name="WhatsAppStateText" Text="Non connesso a WhatsApp"
                                       Foreground="#FF606060" FontSize="14" TextWrapping="Wrap"
                                       TextAlignment="Center"/>

                            <!-- QR code login -->
                            <Button x:Name="LoginQrButton" Content="Accedi con QR code"
                                    Background="{StaticResource WhatsAppGreenBrush}"
                                    Foreground="White" FontSize="16"
                                    Height="44" BorderThickness="0" Margin="0,10,0,0"
                                    Click="LoginQrButton_Click"/>

                            <Image x:Name="QrImage" Width="240" Height="240" Margin="0,10,0,0"
                                   Visibility="Collapsed" Stretch="Uniform"/>

                            <TextBlock x:Name="QrInfoText" Foreground="#FF808080" FontSize="12"
                                       TextWrapping="Wrap" TextAlignment="Center" Margin="0,6,0,0"/>

                            <!-- Phone number login -->
                            <TextBlock Text="oppure accedi con il tuo numero"
                                       Foreground="#FF606060" FontSize="13"
                                       HorizontalAlignment="Center" Margin="0,14,0,0"/>

                            <TextBox x:Name="PhoneBox" PlaceholderText="Numero con prefisso, es. 393401234567"
                                     Background="#FFF7F7F7" FontSize="16" Margin="0,6,0,0"/>

                            <Button x:Name="LoginCodeButton" Content="Ottieni codice"
                                    Background="{StaticResource WhatsAppGreenBrush}"
                                    Foreground="White" FontSize="16"
                                    Height="44" BorderThickness="0" Margin="0,8,0,0"
                                    Click="LoginCodeButton_Click"/>

                            <TextBlock x:Name="PairCodeText" Foreground="#FF075E54" FontSize="22"
                                       FontWeight="Bold" TextAlignment="Center"
                                       TextWrapping="Wrap" Margin="0,10,0,0"/>

                            <Button x:Name="ContinueButton" Content="Continua"
                                    Background="{StaticResource WhatsAppAccentBrush}"
                                    Foreground="White" FontSize="18" FontWeight="SemiBold"
                                    Height="48" BorderThickness="0" Margin="0,14,0,0"
                                    IsEnabled="False"
                                    Click="ContinueButton_Click"/>
                        </StackPanel>
                    </Border>

                    <!-- How to -->
                    <Border Background="#FFF7F7F7" CornerRadius="8" Margin="0,8,0,0">
                        <StackPanel Margin="12">
                            <TextBlock TextWrapping="Wrap" FontSize="12" Foreground="#FF606060">
                                <Run Text="QR code:" FontWeight="SemiBold"/>
                                <Run Text=" apri WhatsApp sul telefono > Dispositivi collegati > Collega un dispositivo e inquadra il codice."/>
                                <LineBreak/><LineBreak/>
                                <Run Text="Numero:" FontWeight="SemiBold"/>
                                <Run Text=" inserisci il numero internazionale, tocca Ottieni codice e digita il codice nelle impostazioni di WhatsApp."/>
                            </TextBlock>
                        </StackPanel>
                    </Border>
                </StackPanel>

                <!-- Version -->
                <TextBlock Text="WhatsApp Community Edition v2.0 (GOWA)"
                           Foreground="#FF808080" FontSize="11"
                           HorizontalAlignment="Center" Margin="0,16,0,16"/>
            </StackPanel>
        </ScrollViewer>
    </Grid>
</Page>
```

- [ ] **Step 2: Replace `WhatsappApp/Pages/ConnectionPage.xaml.cs`**

```csharp
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    public sealed partial class ConnectionPage : Page
    {
        private bool _isFirstRun;

        public ConnectionPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _isFirstRun = !SettingsService.HasSavedSettings;

            string savedAddress = SettingsService.ServerAddress;
            if (!string.IsNullOrEmpty(savedAddress))
                ServerAddressBox.Text = savedAddress;

            int savedPort = SettingsService.ServerPort;
            if (savedPort > 0)
                ServerPortBox.Text = savedPort.ToString();

            string savedUsername = SettingsService.Username;
            if (!string.IsNullOrEmpty(savedUsername))
                UsernameBox.Text = savedUsername;

            PageTitleText.Text = _isFirstRun ? "Prima configurazione" : "Impostazioni Server";

            CommunicationService.Instance.ConnectionStatusChanged += OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred += OnErrorOccurred;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;

            if (CommunicationService.Instance.IsConnected)
            {
                ShowConnectedState();
                _ = CommunicationService.Instance.SendControlAsync("status");
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.ConnectionStatusChanged -= OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred -= OnErrorOccurred;
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameBox.Text?.Trim();
            if (string.IsNullOrEmpty(username))
            {
                username = "Utente";
                UsernameBox.Text = username;
            }

            string address = ServerAddressBox.Text?.Trim();
            if (string.IsNullOrEmpty(address)) address = "192.168.1.100";

            int port = 8585;
            int boxPort;
            if (!string.IsNullOrEmpty(ServerPortBox.Text) &&
                int.TryParse(ServerPortBox.Text.Trim(), out boxPort))
            {
                port = boxPort;
            }

            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = false;
            StatusText.Text = $"Connessione a {address}:{port}...";

            bool connected = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
            if (connected)
            {
                SettingsService.Save(address, port, username);
                StatusText.Text = "Connesso!";
                ShowConnectedState();
                await CommunicationService.Instance.SendControlAsync("status");
            }
            else
            {
                StatusText.Text = "Connessione fallita";
                ActionButton.IsEnabled = true;
            }
        }

        private void ShowConnectedState()
        {
            ActionButton.Visibility = Visibility.Collapsed;
            DisconnectButton.Visibility = Visibility.Visible;
            WhatsAppPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = true;
            UpdateLoginUi(CommunicationService.Instance.WhatsAppState, CommunicationService.Instance.AccountJid);
        }

        private void UpdateLoginUi(string state, string accountJid)
        {
            switch (state)
            {
                case "connected":
                    WhatsAppStateText.Text = string.IsNullOrEmpty(accountJid)
                        ? "WhatsApp connesso!"
                        : $"Connesso come {accountJid.Split('@')[0]}";
                    LoginQrButton.Visibility = Visibility.Collapsed;
                    QrImage.Visibility = Visibility.Collapsed;
                    PhoneBox.Visibility = Visibility.Collapsed;
                    LoginCodeButton.Visibility = Visibility.Collapsed;
                    QrInfoText.Text = "";
                    ContinueButton.IsEnabled = true;
                    break;

                case "waiting":
                    WhatsAppStateText.Text = "In attesa di abbinamento... segui le istruzioni qui sotto.";
                    LoginQrButton.Visibility = Visibility.Visible;
                    PhoneBox.Visibility = Visibility.Visible;
                    LoginCodeButton.Visibility = Visibility.Visible;
                    ContinueButton.IsEnabled = false;
                    break;

                default:
                    WhatsAppStateText.Text = "Non connesso a WhatsApp. Accedi con QR code o con il numero.";
                    LoginQrButton.Visibility = Visibility.Visible;
                    PhoneBox.Visibility = Visibility.Visible;
                    LoginCodeButton.Visibility = Visibility.Visible;
                    QrImage.Visibility = Visibility.Collapsed;
                    PairCodeText.Text = "";
                    QrInfoText.Text = "";
                    ContinueButton.IsEnabled = false;
                    break;
            }
        }

        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null) return;

            switch (message.Command)
            {
                case "state":
                    UpdateLoginUi(message.State, message.AccountJid);
                    break;

                case "qr":
                    ShowQrCode(message.QrImageData, message.QrDuration);
                    break;

                case "paircode":
                    PairCodeText.Text = $"Codice: {message.PairCode}";
                    WhatsAppStateText.Text = "Inserisci questo codice su WhatsApp > Dispositivi collegati > Collega un dispositivo > Collega con numero di telefono.";
                    QrImage.Visibility = Visibility.Collapsed;
                    break;

                case "error":
                    WhatsAppStateText.Text = message.Text;
                    break;
            }
        }

        private async void ShowQrCode(string base64, int duration)
        {
            if (string.IsNullOrEmpty(base64))
            {
                QrInfoText.Text = "QR code non disponibile.";
                return;
            }

            try
            {
                QrImage.Source = await BitmapFromBase64Async(base64);
                QrImage.Visibility = Visibility.Visible;
                PairCodeText.Text = "";
                QrInfoText.Text = duration > 0
                    ? $"Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice (valido ~{duration}s)."
                    : "Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice.";
            }
            catch (Exception ex)
            {
                QrInfoText.Text = $"Impossibile mostrare il QR code: {ex.Message}";
            }
        }

        private static async Task<BitmapImage> BitmapFromBase64Async(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }
                var bitmap = new BitmapImage();
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }

        private async void LoginQrButton_Click(object sender, RoutedEventArgs e)
        {
            PairCodeText.Text = "";
            QrInfoText.Text = "Richiesta del QR code in corso...";
            await CommunicationService.Instance.SendControlAsync("login.qr");
        }

        private async void LoginCodeButton_Click(object sender, RoutedEventArgs e)
        {
            string phone = (PhoneBox.Text ?? "").Trim();
            phone = phone.Replace("+", "").Replace(" ", "").Replace("-", "");
            if (phone.Length < 6 || !phone.All(char.IsDigit))
            {
                WhatsAppStateText.Text = "Inserisci un numero valido con prefisso internazionale (es. 393401234567).";
                return;
            }

            QrImage.Visibility = Visibility.Collapsed;
            QrInfoText.Text = "";
            WhatsAppStateText.Text = "Richiesta del codice in corso...";
            await CommunicationService.Instance.SendControlAsync("login.code", phone);
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            ContinueToMainPage();
        }

        private void ContinueToMainPage()
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(MainPage));
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            CommunicationService.Instance.Disconnect();
            StatusPanel.Visibility = Visibility.Collapsed;
            WhatsAppPanel.Visibility = Visibility.Collapsed;
            ActionButton.Visibility = Visibility.Visible;
            DisconnectButton.Visibility = Visibility.Collapsed;
            ActionButton.IsEnabled = true;
        }

        private void OnConnectionStatusChanged(object sender, string status)
        {
            StatusText.Text = status;
            StatusPanel.Visibility = Visibility.Visible;
            if (status != null && (status.Contains("Connesso") || status.Contains("avviato")))
                ShowConnectedState();
        }

        private void OnErrorOccurred(object sender, string error)
        {
            StatusText.Text = error;
            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = true;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                ContinueToMainPage();
        }
    }
}
```

- [ ] **Step 3: Build the app**

Run: `msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=AnyCPU`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 4: Manual verification with a real GOWA + adapter**

1. Deploy the app to a device/emulator.
2. Enter adapter address + port `8585`, tap **Connetti al server**.
3. Confirm the WhatsApp panel appears with the text "Non connesso a WhatsApp...".
4. Tap **Accedi con QR code**; expected: a QR image appears within a few seconds and the adapter terminal logs `[QR] Nuovo QR code inviato all'app`.
5. Scan with WhatsApp > Dispositivi collegati; expected: the app switches to "Connesso come <numero>" and **Continua** becomes enabled.
6. Repeat from a clean session (logout on GOWA: `GET /app/logout`, then `GET /app/reconnect` is not needed) using **Numero + Ottieni codice**; expected: `Codice: XXXX-XXXX` appears and the phone completes pairing.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Pages/ConnectionPage.xaml WhatsappApp/Pages/ConnectionPage.xaml.cs
git commit -m "feat(app): in-app login via QR code or phone pairing code"
```

---

### Task 11: New chat by phone number and contact refresh in MainPage

**Files:**
- Modify: `WhatsappApp/MainPage.xaml.cs`

**Interfaces:**
- Consumes: `DataService.Contacts`, `DataService.AddContact`, `DataService.DisplayNameForJid`, `CommunicationService.SendControlAsync`.
- Produces: working `NewChatButton_Click`; contact refresh on navigation.

- [ ] **Step 1: Add the usings**

At the top of `WhatsappApp/MainPage.xaml.cs`, add `System.Linq`:

```csharp
using System;
using System.Linq;
using Windows.Phone.UI.Input;
```

- [ ] **Step 2: Refresh contacts on navigation**

In `OnNavigatedTo`, after `ChatListView.ItemsSource = DataService.Instance.Contacts;`, add:

```csharp
            if (CommunicationService.Instance.IsConnected)
                _ = CommunicationService.Instance.SendControlAsync("contacts");
```

- [ ] **Step 3: Implement the new chat dialog**

Replace the empty handler:

```csharp
        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Open new chat screen
        }
```

with:

```csharp
        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = "Numero con prefisso internazionale, es. 393401234567"
            };

            var dialog = new ContentDialog
            {
                Title = "Nuova chat",
                Content = input,
                PrimaryButtonText = "Apri",
                CloseButtonText = "Annulla"
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            string phone = (input.Text ?? "").Trim()
                .Replace("+", "").Replace(" ", "").Replace("-", "");
            if (phone.Length < 6 || !phone.All(char.IsDigit))
            {
                return;
            }

            string jid = phone.Contains("@") ? phone : phone + "@s.whatsapp.net";

            var existing = DataService.Instance.Contacts.FirstOrDefault(c => c.Id == jid);
            if (existing != null)
            {
                Frame.Navigate(typeof(ChatPage), existing);
                return;
            }

            var contact = new Contact
            {
                Id = jid,
                Name = "+" + phone,
                Initials = phone.Substring(0, 2).ToUpper(),
                AvatarColor = "#FF075E54",
                IsOnline = false,
                UnreadCount = 0
            };
            DataService.Instance.AddContact(contact);
            Frame.Navigate(typeof(ChatPage), contact);
        }
```

- [ ] **Step 4: Build the app**

Run: `msbuild WhatsappApp.sln /p:Configuration=Debug /p:Platform=AnyCPU`
Expected: `Build succeeded.` with `0 Error(s)`.

- [ ] **Step 5: Manual verification**

1. Connect and log in as in Task 10.
2. Tap the green **+** button, enter a real WhatsApp number with country code, tap **Apri**.
3. Send a text; expected: it appears in the bubble and is received on the other phone.
4. Have the contact reply; expected: the message arrives in the open chat without a manual refresh.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp/MainPage.xaml.cs
git commit -m "feat(app): start a new chat by phone number and refresh contacts"
```

---

### Task 12: Documentation

**Files:**
- Modify: `README.md`
- Modify: `WhatsappBridge/README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: accurate setup and usage docs.

- [ ] **Step 1: Update the root `README.md`**

Replace the `WhatsappBridge (Node.js Unified Server)` section with:

```markdown
### GOWA Adapter (Node.js)

A thin adapter that connects the Windows Phone 8.1 app to a self-hosted
[GOWA](https://github.com/vincenzosco/go-whatsapp-web-multidevice) server
(`go-whatsapp-web-multidevice`). It does **not** implement its own WhatsApp
client any more: it uses GOWA's REST API and webhooks.

**Features**

- Login via **QR code** or via **phone number pairing code**, both shown in the app
- Keeps the encrypted (AES-256-GCM) TCP channel between app and adapter
- Sends text and images through `POST /send/message` and `POST /send/image`
- Receives incoming messages through a GOWA webhook (HMAC-verified)
- Syncs contacts from `GET /user/my/contacts`

**Setup**

1. Start GOWA:

```bash
git clone https://github.com/vincenzosco/go-whatsapp-web-multidevice
cd go-whatsapp-web-multidevice/src
go run . rest --basic-auth=admin:admin --port=3000
```

2. Start the adapter:

```bash
cd WhatsappBridge
cp .env.example .env   # optional, or export the variables
npm install
npm start
```

The adapter registers its webhook on GOWA automatically. If that fails, start
GOWA with `--webhook=http://<adapter-host>:8586/webhook`.

3. In the app, set the adapter address/port and tap **Connetti al server**, then
   log in with the QR code or with your phone number.

**Requirements:** Node.js 18.13+ and a reachable GOWA instance.

Environment variables are documented in `WhatsappBridge/.env.example`.
```

Also in the same file, replace the `## Protocol` paragraph about `wa_...` chat IDs with:

```markdown
Chat IDs are WhatsApp JIDs (`393401234567@s.whatsapp.net` for 1-to-1 chats,
`120363...@g.us` for groups), used unchanged in both directions.
```

- [ ] **Step 2: Rewrite `WhatsappBridge/README.md`**

```markdown
# GOWA Adapter

Ponte tra l'app WhatsApp per Windows Phone 8.1 e un server GOWA self-hosted
([go-whatsapp-web-multidevice](https://github.com/vincenzosco/go-whatsapp-web-multidevice)).

## Come funziona

```
 App WP8  ⇄  (TCP cifrato AES-256-GCM)  ⇄  Adapter  ⇄  (HTTP REST + webhook)  ⇄  GOWA  ⇄  WhatsApp
```

- Il login (QR code o codice di abbinamento) è richiesto **dall'app** tramite frame di controllo.
- I messaggi in arrivo da WhatsApp arrivano via webhook e vengono inoltrati all'app sul canale TCP.
- I messaggi in uscita sono inviati alle API REST di GOWA.

## Protocollo di controllo

Frame `Type = System`, `ChatId = "system"`.

| Direzione | `Command` | Campi usati |
|---|---|---|
| app → adapter | `hello` | `Text` = nome utente |
| app → adapter | `status` | — |
| app → adapter | `login.qr` | — |
| app → adapter | `login.code` | `Text` = numero con prefisso |
| app → adapter | `contacts` | — |
| app → adapter | `logout` | — |
| adapter → app | `state` | `State`, `AccountJid` |
| adapter → app | `qr` | `QrImageData` (base64 PNG), `QrDuration` |
| adapter → app | `paircode` | `PairCode` |
| adapter → app | `contact` | `ChatId` = JID, `SenderName` = nome |
| adapter → app | `error` | `Text` |

## Configurazione

Vedi `.env.example`. Le variabili principali:

| Variabile | Default | Descrizione |
|---|---|---|
| `GOWA_URL` | `http://127.0.0.1:3000` | URL del server GOWA |
| `GOWA_USER` / `GOWA_PASS` | — | Credenziali Basic Auth di GOWA |
| `GOWA_DEVICE_ID` | — | Device GOWA (multi-device); vuoto = default |
| `BRIDGE_PORT` | `8585` | Porta TCP per l'app WP8 |
| `WEBHOOK_PORT` | `8586` | Porta HTTP del webhook |
| `WEBHOOK_PUBLIC_URL` | `http://127.0.0.1:8586/webhook` | URL con cui GOWA raggiunge l'adapter |
| `WEBHOOK_SECRET` | — | Deve combaciare con `--webhook-secret` di GOWA |
| `BRIDGE_KEY` | `WhatsAppCommunityWP8-2026` | Deve combaciare con `CryptoHelper.cs` |
| `POLL_INTERVAL_MS` | `5000` | Polling stato WhatsApp |

## Test

```bash
npm test
```
```

- [ ] **Step 3: Verify the whole adapter suite still passes**

Run: `cd WhatsappBridge && npm test`
Expected: `# fail 0`.

- [ ] **Step 4: Commit**

```bash
git add README.md WhatsappBridge/README.md
git commit -m "docs: document GOWA adapter setup, login flows and protocol"
```

---

## Known limitations (out of scope, documented for the reviewer)

- Group chats work for sending/receiving, but the contact label for a group is derived from its JID (`Gruppo <id>`) until a chat-name sync is added; 1-to-1 names come from `/user/my/contacts`.
- Read/delivered receipts (`message.ack`) are not forwarded to the app; incoming messages are shown as read and outgoing as sent.
- The GOWA QR is valid for a limited window. If it expires the user must tap **Accedi con QR code** again; the adapter re-requests a fresh QR automatically.
- Reactions, edits, revocations, polls and location/document/video messages are received as plain text or ignored; only text, image and audio types are mapped to the app's `MessageType`.
