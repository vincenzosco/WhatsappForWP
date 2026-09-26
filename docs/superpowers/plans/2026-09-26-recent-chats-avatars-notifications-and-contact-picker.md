# Recent Chats, Avatars, Local Notifications and Contact Picking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the chat list show the conversations that actually exist in the linked WhatsApp account (with profile pictures), notify the user while the app is running, and let a new chat be started from a phone contact or from a conversation that already exists.

**Architecture:** Three workstreams over one repo, each shippable on its own.

1. **Chats and avatars.** The app's list is fed today only by `contact` frames built from `GET /user/my/contacts`, which is the WhatsApp address book and is empty on a freshly linked account; the real conversations live in `GET /chats`. A new `chats.js` reads the most recent chats, enriches each with its last message and (for people) its profile picture from `GET /user/avatar`, and the adapter sends one `chat` frame per chat. The app turns each frame into a `Contact` row, avatar included.
2. **Local notifications.** A WP8.1 XAML app can raise a toast and set the tile badge through `Windows.UI.Notifications` while it is running; there is no cloud in this project, so the plan says exactly that and nothing more. A `NotificationService` owns the toast and the badge, `SettingsService` owns an on/off switch, and `DataService` is the only caller: one incoming message that is not in the open chat becomes one toast.
3. **Contact picking.** The new-chat dialog gains a button that opens the system contact picker (`ContactPicker`) and a list of the conversations already known to GOWA, so a chat can be started without typing a number.

**Tech Stack:** Node.js 18.13+ (CommonJS, zero runtime dependencies, `node:test`), C# 5 on Windows Phone 8.1 (XAML/WinRT: `DataContractJsonSerializer`, `Windows.UI.Notifications`, `Windows.ApplicationModel.Contacts`), `.resw` in en-US and it-IT, MSBuild 12 (VS2013) on a Parallels Windows VM.

## Global Constraints

- **C# 5 only.** No `$"..."`, `?.`, expression-bodied members, `out var`, `is T x`, `nameof`, `_ =`, auto-property initializers. Field initializers are fine. Gate: `node tools/check-csharp5.js`.
- **The app builds only on the VM.** Every task that touches `WhatsappApp/**` ends with the build gate and expects `Errori: 0` with one warning (CS0618 `PickSingleFileAsync`). The build is the app's only test host.
- **Guards after every task:** `node tools/check-csharp5.js`, `node tools/check-icons.js`, `node tools/check-resw.js --strict`, `node tools/check-docs.js`, `node tools/check-framing.js`, `cd WhatsappBridge && npm test`, `node --test "tools/test/**/*.test.js"`, `node tools/qr-term.js --self-test`.
- **Never hardcode a user-visible string.** XAML uses `x:Uid` with the property that matches the element; C# uses `Loc.Get("Key", "fallback")`. A key read from C# must be **bare**; a key used by XAML must be `Key.Text`/`Key.Content`; the two forms must not coexist. `check-resw.js --strict` reads every `Loc.Get("...")` occurrence, comments included, and fails on an unused key.
- **Exactly one icon of a kind.** No icon font; inline `<Path.Data><PathGeometry>` only, each with its `<!-- IconX -->` comment.
- **Runtime text is English** (adapter logs, `text:` frames, `Diag`, scripts). Only the app UI is localized through the `.resw` pairs. Source comments stay Italian.
- **Docs are pairs:** `README.md`/`README.it.md` and `WhatsappBridge/README.md`/`WhatsappBridge/README.it.md` get the same heading in the same position and order, in the same commit. `## Disclosure` stays last in the root pair. No emoji (U+26A0 only).
- **Protocol values are contract:** command names (`hello`, `status`, `login.qr`, `login.code`, `contacts`, `calls`, `chats`, `logout`) and JSON keys never change; only human-readable text does. The frame stays `[4-byte LE length][payload]`, ceiling 8 MiB, and `ByteOrder = ByteOrder.LittleEndian` on both ends.
- **Nothing is invented.** A screen shows only what a server sends: no presence, no unread count the server did not report, no "last seen".
- **Commits:** English, `type: short imperative`, one per task, `git push origin master`.

---

## The evidence

The account is linked (`state: connected`) and the chat list is empty. The adapter builds the list from `GET /user/my/contacts`, which returns the **WhatsApp address book**: on a freshly linked device it is empty even though `GET /chats` is full. Verified against the running binary:

```bash
strings -a .tools/gowa/whatsapp | grep -aoE "/user[a-z_/:.-]*" | sort -u
# /user/avatar  /user/info  /user/my/contacts  /user/my/groups  /user/my/newsletters  /user/my/privacy
strings -a .tools/gowa/whatsapp | grep -aoE "is_preview|phone=" | sort | uniq -c
#   2 is_preview
#   2 phone=
```

So the profile picture is `GET /user/avatar?phone=<digits>&is_preview=true`, and the conversations are `GET /chats` (already used by the Calls scan).

## File structure

| File | Responsibility | Change |
| --- | --- | --- |
| `WhatsappBridge/chats.js` | pure chat-row extraction over a GOWA client port | new: `collectChats` |
| `WhatsappBridge/test/chats.test.js` | its tests | new |
| `WhatsappBridge/gowa-client.js` | HTTP to GOWA | `avatar(jid)` |
| `WhatsappBridge/message-format.js` | the JSON the phone reads | `IsGroup`, `AvatarData` on a message |
| `WhatsappBridge/server.js` | control protocol | `chats` command, `chat`/`chats.done` frames, cache |
| `WhatsappBridge/README.md`, `.it.md` | adapter docs | the new command and frames |
| `WhatsappApp/Models/Contact.cs` | one row of the chat list | `Avatar`, `AvatarData`, `LoadAvatarAsync` |
| `WhatsappApp/Models/ChatMessage.cs` | wire shape | `IsGroup`, `AvatarData` |
| `WhatsappApp/Services/DataService.cs` | state | `chat` frames, toast, badge |
| `WhatsappApp/Services/NotificationService.cs` | toast and badge | new |
| `WhatsappApp/Services/SettingsService.cs` | persisted settings | `NotificationsEnabled` |
| `WhatsappApp/Pages/ChatsPage.xaml(.cs)` | chat list, new chat | avatar in the row, picker, request `chats` |
| `WhatsappApp/Pages/ConnectionPage.xaml(.cs)` | settings | notifications toggle |
| `WhatsappApp/WhatsappApp.csproj` | project | the new source file |
| `WhatsappApp/Package.appxmanifest` | capabilities | `contacts`, if the picker needs it |
| `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw` | strings | the new keys |
| `README.md`, `README.it.md` | project docs | one paragraph per part |
| `.agents/skills/{maintain-the-app,test-the-app,update-the-app}/SKILL.md` | the rules | the new facts |

---

### Task 1: The adapter can read the real conversations

**Files:**
- Create: `WhatsappBridge/chats.js`
- Create: `WhatsappBridge/test/chats.test.js`

**Interfaces:**
- Consumes: nothing.
- Produces: `previewForMessage(message) : string` and `collectChats({ gowa, limit, avatars, log }) -> Promise<ChatRow[]>` where `ChatRow = { chatId, name, preview, timestamp, isGroup, avatar }` and `avatar` is a base64 string or `null`. `gowa` must have `chats(limit) -> ChatInfo[]`, `chatMessages(jid, limit) -> MessageInfo[]` and `avatar(jid) -> Promise<string|null>`.

- [ ] **Step 1: Write the failing tests**

Create `WhatsappBridge/test/chats.test.js`:

```js
'use strict';

const test = require('node:test');
const assert = require('node:assert');

const { previewForMessage, collectChats } = require('../chats');

test('previewForMessage uses the body when there is one', () => {
  assert.strictEqual(previewForMessage({ content: 'ciao', media_type: 'text' }), 'ciao');
});

test('previewForMessage names the media when the body is empty', () => {
  assert.strictEqual(previewForMessage({ media_type: 'image' }), '[Image]');
  assert.strictEqual(previewForMessage({ media_type: 'video' }), '[Video]');
  assert.strictEqual(previewForMessage({ media_type: 'audio' }), '[Audio]');
  assert.strictEqual(previewForMessage({ media_type: 'document' }), '[Document]');
  assert.strictEqual(previewForMessage({ media_type: 'sticker' }), '[Sticker]');
  assert.strictEqual(previewForMessage({}), '');
  assert.strictEqual(previewForMessage(null), '');
});

function fakeGowa({ chats, messagesByJid, avatarsByJid, failAvatarFor }) {
  return {
    chats: async (limit) => chats.slice(0, limit),
    chatMessages: async (jid) => messagesByJid[jid] || [],
    avatar: async (jid) => {
      if (failAvatarFor && failAvatarFor.indexOf(jid) !== -1) throw new Error('no picture');
      return avatarsByJid[jid] || null;
    }
  };
}

test('collectChats keeps the newest message as the preview and sorts by it', async () => {
  const gowa = fakeGowa({
    chats: [
      { jid: 'a@s.whatsapp.net', name: 'Anna' },
      { jid: 'b@s.whatsapp.net', name: 'Bruno' }
    ],
    messagesByJid: {
      'a@s.whatsapp.net': [
        { content: 'vecchio', timestamp: '2026-09-24T09:00:00Z' },
        { content: 'nuovo', timestamp: '2026-09-26T09:00:00Z' }
      ],
      'b@s.whatsapp.net': [{ content: 'ciao', timestamp: '2026-09-25T08:00:00Z' }]
    },
    avatarsByJid: {}
  });

  const rows = await collectChats({ gowa, limit: 10, avatars: false, log: () => {} });
  assert.strictEqual(rows.length, 2);
  assert.strictEqual(rows[0].chatId, 'a@s.whatsapp.net');
  assert.strictEqual(rows[0].preview, 'nuovo');
  assert.strictEqual(rows[1].preview, 'ciao');
  assert.strictEqual(rows[0].isGroup, false);
});

test('collectChats fills the name from the jid when GOWA does not send one', async () => {
  const gowa = fakeGowa({
    chats: [{ jid: '393401234567@s.whatsapp.net' }, { jid: '123@g.us' }],
    messagesByJid: {},
    avatarsByJid: {}
  });

  const rows = await collectChats({ gowa, limit: 10, avatars: false, log: () => {} });
  assert.strictEqual(rows.find((r) => r.chatId === '393401234567@s.whatsapp.net').name, '+393401234567');
  assert.strictEqual(rows.find((r) => r.chatId === '123@g.us').isGroup, true);
});

test('collectChats asks for the avatar of people only, and survives a failure', async () => {
  const asked = [];
  const gowa = {
    chats: async () => [
      { jid: 'a@s.whatsapp.net', name: 'Anna' },
      { jid: '123@g.us', name: 'Gruppo' }
    ],
    chatMessages: async () => [],
    avatar: async (jid) => { asked.push(jid); return jid === 'a@s.whatsapp.net' ? 'AAAA' : null; }
  };

  const rows = await collectChats({ gowa, limit: 10, avatars: true, log: () => {} });
  assert.deepStrictEqual(asked, ['a@s.whatsapp.net']);
  assert.strictEqual(rows.find((r) => r.chatId === 'a@s.whatsapp.net').avatar, 'AAAA');

  // Un avatar che non si scarica e' un avatar in meno, non una chat in meno.
  const failing = fakeGowa({
    chats: [{ jid: 'a@s.whatsapp.net', name: 'Anna' }],
    messagesByJid: { 'a@s.whatsapp.net': [{ content: 'x', timestamp: '2026-09-26T09:00:00Z' }] },
    avatarsByJid: {},
    failAvatarFor: ['a@s.whatsapp.net']
  });
  const again = await collectChats({ gowa: failing, limit: 10, avatars: true, log: () => {} });
  assert.strictEqual(again.length, 1);
  assert.strictEqual(again[0].avatar, null);
});

test('collectChats caps the result and skips a chat it cannot read', async () => {
  const gowa = {
    chats: async (limit) => [
      { jid: 'a@s.whatsapp.net', name: 'Anna' },
      { jid: 'b@s.whatsapp.net', name: 'Bruno' },
      { jid: 'c@s.whatsapp.net', name: 'Carla' }
    ].slice(0, limit),
    chatMessages: async (jid) => {
      if (jid === 'b@s.whatsapp.net') throw new Error('chatstorage unavailable');
      return [{ content: jid[0], timestamp: '2026-09-26T09:00:00Z' }];
    },
    avatar: async () => null
  };

  const rows = await collectChats({ gowa, limit: 2, avatars: false, log: () => {} });
  assert.strictEqual(rows.length, 2);
  assert.ok(rows.every((r) => r.chatId !== 'b@s.whatsapp.net'));
});
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd WhatsappBridge && node --test test/chats.test.js`
Expected: FAIL, `Cannot find module '../chats'`.

- [ ] **Step 3: Write `chats.js`**

Create `WhatsappBridge/chats.js`:

```js
'use strict';

const { displayNameForJid } = require('./message-format');

/**
 * Elenco delle conversazioni presenti nell'account collegato.
 *
 * L'app non puo' usare /user/my/contacts per questo: quella e' la *rubrica*
 * di WhatsApp, e su un dispositivo appena collegato e' vuota anche se in
 * /chats ci sono decine di conversazioni. Si legge quindi /chats, e per ogni
 * conversazione si prende l'ultimo messaggio (per l'anteprima) e, per le
 * persone, l'immagine del profilo da /user/avatar.
 *
 * Costi: una richiesta HTTP per chat per l'ultimo messaggio, piu' una per
 * l'avatar. Il numero di chat lette e' quindi un limite di configurazione, non
 * un dettaglio.
 */

// Nomi mostrati quando il messaggio non ha testo: il tipo lo dice GOWA, la
// parola la scegliamo qui perche' l'anteprima e' testo destinato a una persona.
const MEDIA_LABEL = {
  image: '[Image]',
  video: '[Video]',
  audio: '[Audio]',
  document: '[Document]',
  sticker: '[Sticker]',
};

/** L'anteprima di una riga: il testo, o il nome del media quando il testo non c'e'. */
function previewForMessage(message) {
  if (!message) return '';
  const text = typeof message.content === 'string' ? message.content.trim() : '';
  if (text) return text;
  return MEDIA_LABEL[message.media_type] || '';
}

function timeOf(value) {
  const parsed = Date.parse(value);
  return isNaN(parsed) ? 0 : parsed;
}

/** Il messaggio piu' recente della lista, qualunque ordine usi GOWA. */
function newestMessage(messages) {
  let best = null;
  let bestTime = -1;
  for (const message of messages || []) {
    if (!message) continue;
    const at = timeOf(message.timestamp);
    if (best === null || at > bestTime) {
      best = message;
      bestTime = at;
    }
  }
  return best;
}

function isGroupJid(jid) {
  return typeof jid === 'string' && jid.endsWith('@g.us');
}

/**
 * Scorre le conversazioni indicate da GOWA. Una chat illeggibile, o un avatar
 * che non si scarica, non fermano l'elenco: si perde quel dettaglio.
 */
async function collectChats(options) {
  const opts = options || {};
  const gowa = opts.gowa;
  const log = typeof opts.log === 'function' ? opts.log : () => {};
  const limit = opts.limit || 25;
  const withAvatars = opts.avatars === true;

  const chats = await gowa.chats(limit);
  const rows = [];

  for (const chat of chats) {
    if (!chat || !chat.jid) continue;

    let last = null;
    try {
      last = newestMessage(await gowa.chatMessages(chat.jid, 10));
    } catch (err) {
      log('DEBUG', `Chats: messages of ${chat.jid} not readable (${err.message})`);
    }

    const isGroup = isGroupJid(chat.jid);
    const name = chat.name || displayNameForJid(chat.jid);

    let avatar = null;
    if (withAvatars && !isGroup) {
      try {
        avatar = await gowa.avatar(chat.jid);
      } catch (err) {
        log('DEBUG', `Chats: avatar of ${chat.jid} not readable (${err.message})`);
      }
    }

    rows.push({
      chatId: chat.jid,
      name,
      preview: previewForMessage(last),
      timestamp: (last && last.timestamp) || '',
      isGroup,
      avatar: avatar || null,
    });
  }

  rows.sort((a, b) => timeOf(b.timestamp) - timeOf(a.timestamp));

  const result = rows.slice(0, limit);
  log('INFO', `Chats: ${result.length} conversation(s) from ${chats.length}`);
  return result;
}

module.exports = { previewForMessage, collectChats };
```

- [ ] **Step 4: Run them and watch them pass**

Run: `cd WhatsappBridge && node --test test/chats.test.js`
Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/chats.js WhatsappBridge/test/chats.test.js
git commit -m "feat: read the linked account's real conversations"
```

---

### Task 2: The GOWA client can download a profile picture

**Files:**
- Modify: `WhatsappBridge/gowa-client.js`
- Test: `WhatsappBridge/test/gowa-client.test.js`

**Interfaces:**
- Consumes: nothing.
- Produces: `GowaClient#avatar(jid) -> Promise<string|null>`, a base64 PNG/JPEG body or `null` when GOWA has no picture for that person.

- [ ] **Step 1: Write the failing test**

Append to `WhatsappBridge/test/gowa-client.test.js`:

```js
test('avatar() asks GOWA for the person, never for a group, and returns base64', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      return {
        ok: true,
        status: 200,
        headers: { get: () => 'image/jpeg' },
        arrayBuffer: async () => new Uint8Array([1, 2, 3]).buffer
      };
    }
  });

  const picture = await client.avatar('393401234567@s.whatsapp.net');
  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/user/avatar?phone=393401234567&is_preview=true');
  assert.strictEqual(picture, Buffer.from([1, 2, 3]).toString('base64'));

  assert.strictEqual(await client.avatar('123456789012345678@g.us'), null);
  assert.strictEqual(seen.length, 1);
});

test('avatar() returns null when GOWA has no picture', async () => {
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async () => ({ ok: false, status: 404, headers: { get: () => null } })
  });

  assert.strictEqual(await client.avatar('393401234567@s.whatsapp.net'), null);
});
```

- [ ] **Step 2: Run it and watch it fail**

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL, `client.avatar is not a function`.

- [ ] **Step 3: Add the method**

In `WhatsappBridge/gowa-client.js`, after `chatMessages(...)`:

```js
  // Immagine del profilo di una persona. GOWA risponde 404 quando non ce l'ha:
  // per l'elenco chat e' "nessuna immagine", non un errore da propagare.
  // I gruppi non hanno un avatar personale, quindi non si chiede.
  async avatar(jid) {
    const value = String(jid || '');
    if (!value || value.endsWith('@g.us')) return null;

    const phone = value.split('@')[0];
    const res = await this.fetch(
      `${this.baseUrl}/user/avatar?phone=${encodeURIComponent(phone)}&is_preview=true`,
      { headers: this.headers() });
    if (!res.ok) return null;

    const buffer = Buffer.from(await res.arrayBuffer());
    return buffer.length > 0 ? buffer.toString('base64') : null;
  }
```

- [ ] **Step 4: Run the suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 77 tests.

- [ ] **Step 5: Commit**

```bash
git add WhatsappBridge/gowa-client.js WhatsappBridge/test/gowa-client.test.js
git commit -m "feat: fetch a person's profile picture from GOWA"
```

---

### Task 3: The adapter serves the chat list

**Files:**
- Modify: `WhatsappBridge/message-format.js`, `WhatsappBridge/server.js`, `WhatsappBridge/config.js`, `WhatsappBridge/.env.example`
- Test: `WhatsappBridge/test/message-format.test.js`, `WhatsappBridge/test/server.test.js`, `WhatsappBridge/test/config.test.js`
- Modify docs: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: `collectChats` (Task 1), `GowaClient#avatar` (Task 2).
- Produces: `buildChatMessage` accepts `isGroup` (boolean) and `avatarData` (string); control frames `chat` (`ChatId`, `SenderName`, `Text` = preview, `Timestamp`, `IsGroup`, `AvatarData`) and `chats.done`; the app command `chats`; `config.chats = { limit, avatars }` from `CHATS_LIMIT` and `CHATS_AVATARS`.

- [ ] **Step 1: Write the failing tests**

Append to `WhatsappBridge/test/message-format.test.js`:

```js
test('buildChatMessage carries the chat-row fields', () => {
  const msg = buildChatMessage({
    command: 'chat', chatId: 'a@s.whatsapp.net', senderName: 'Anna',
    text: 'ciao', timestamp: '2026-09-26T09:00:00Z', isGroup: false, avatarData: 'AAAA'
  });
  assert.strictEqual(msg.Command, 'chat');
  assert.strictEqual(msg.SenderName, 'Anna');
  assert.strictEqual(msg.Text, 'ciao');
  assert.strictEqual(msg.IsGroup, false);
  assert.strictEqual(msg.AvatarData, 'AAAA');
  assert.strictEqual(msg.Type, 3);
});

test('buildChatMessage omits an absent avatar and marks a group', () => {
  const msg = buildChatMessage({ command: 'chat', chatId: '1@g.us', isGroup: true });
  assert.strictEqual(msg.IsGroup, true);
  assert.strictEqual(msg.AvatarData, undefined);
});
```

Append to `WhatsappBridge/test/config.test.js`:

```js
test('the chat list limits have defaults and can be overridden', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.chats.limit, 25);
  assert.strictEqual(defaults.chats.avatars, true);

  const custom = loadConfig({ CHATS_LIMIT: '5', CHATS_AVATARS: 'off' });
  assert.strictEqual(custom.chats.limit, 5);
  assert.strictEqual(custom.chats.avatars, false);
});
```

Append to `WhatsappBridge/test/server.test.js`:

```js
test('the chats command sends one frame per chat and then chats.done', async () => {
  const sent = [];
  const gowa = {
    chats: async () => [{ jid: 'a@s.whatsapp.net', name: 'Anna' }],
    chatMessages: async () => [{ content: 'ciao', timestamp: '2026-09-26T09:00:00Z' }],
    avatar: async () => 'AAAA',
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' })
  };
  const config = { chats: { limit: 10, avatars: true }, calls: {}, bridge: { port: 8585 } };
  const bridge = createBridge({ config, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });

  await bridge.handleControl({ Type: 3, Command: 'chats', SenderName: 'test' });

  const frames = sent.map((packet) => decodeFrame(packet));
  assert.deepStrictEqual(frames.map((f) => f.Command), ['chat', 'chats.done']);
  assert.strictEqual(frames[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(frames[0].Text, 'ciao');
  assert.strictEqual(frames[0].AvatarData, 'AAAA');
});

test('the chats command answers with an error and chats.done when WhatsApp is not connected', async () => {
  const sent = [];
  const bridge = createBridge({ config: { chats: {} }, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });

  await bridge.handleControl({ Type: 3, Command: 'chats', SenderName: 'test' });

  const frames = sent.map((packet) => decodeFrame(packet));
  assert.deepStrictEqual(frames.map((f) => f.Command), ['error', 'chats.done']);
});
```

- [ ] **Step 2: Run them and watch them fail**

Run: `cd WhatsappBridge && npm test`
Expected: FAIL on the three new groups (no `IsGroup`/`AvatarData`, no `chats` config, no `chat` frame).

- [ ] **Step 3: Add the message fields**

In `WhatsappBridge/message-format.js`, after the `relatedMessageId` line:

```js
  // Riga dell'elenco chat: il gruppo e la sua immagine (vedi chats.js).
  if (typeof f.isGroup === 'boolean') msg.IsGroup = f.isGroup;
  if (f.avatarData) msg.AvatarData = f.avatarData;
```

- [ ] **Step 4: Add the configuration**

In `WhatsappBridge/config.js`: add to `DEFAULTS`

```js
  CHATS_LIMIT: '25',
  CHATS_AVATARS: 'on'
```

and to the returned object, next to `calls`:

```js
    chats: {
      // Quante conversazioni elencare. Gli avatar costano una richiesta HTTP
      // per persona, e si possono spegnere.
      limit: parseInt(pick(env, 'CHATS_LIMIT'), 10),
      avatars: pick(env, 'CHATS_AVATARS').toLowerCase() !== 'off'
    },
```

- [ ] **Step 5: Add the command and the frames**

In `WhatsappBridge/server.js`:

1. Import both readers:

```js
const { collectCalls } = require('./calls');
const { collectChats } = require('./chats');
```

2. After the `sendCalls` function, add the chat sender (same shape, sharing the cache rule):

```js
  // Stessa regola delle chiamate: la scansione costa una richiesta per chat piu'
  // una per avatar, quindi il risultato si tiene per un minuto.
  let chatsCache = null;
  const CHATS_CACHE_MS = 60000;

  async function sendChats() {
    const limits = (config && config.chats) || {};

    if (state.status !== 'connected') {
      sendControl({ command: 'error', text: 'WhatsApp is not connected: the chat list is unavailable.' });
      sendControl({ command: 'chats.done' });
      return;
    }

    try {
      const fresh = !chatsCache || Date.now() - chatsCache.at > CHATS_CACHE_MS;
      if (fresh) {
        logger('INFO', `reading up to ${limits.limit || 25} conversation(s)...`);
        const rows = await collectChats({
          gowa,
          limit: limits.limit,
          avatars: limits.avatars,
          log: logger
        });
        chatsCache = { at: Date.now(), rows };
      }

      for (const row of chatsCache.rows) {
        sendControl({
          command: 'chat',
          chatId: row.chatId,
          senderName: row.name || undefined,
          text: row.preview || '',
          timestamp: row.timestamp || undefined,
          isGroup: row.isGroup,
          avatarData: row.avatar || undefined
        });
      }
    } catch (err) {
      logger('ERR', `chat list failed: ${err.message}`);
      sendControl({ command: 'error', text: `Chat list failed: ${err.message}` });
    } finally {
      sendControl({ command: 'chats.done' });
    }
  }
```

3. In `refreshStatus`, in the `next === 'connected'` branch, invalidate it with the others:

```js
      if (next === 'connected') {
        qrCache = null;
        callsCache = null;
        chatsCache = null;
        if (changed) {
          broadcastState();
          logger('OK', `WhatsApp connected as ${state.jid || 'unknown'}`);
          await flushPending();
          await sendChats();
          await syncContacts();
        }
      } else if (changed) {
```

4. In `handleControl`:

```js
      case 'chats':
        await sendChats();
        break;
```

5. In the returned object:

```js
    sendChats,
    resetChatsCacheForTest() { chatsCache = null; },
```

- [ ] **Step 6: Run the suite**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 82 tests.

- [ ] **Step 7: Document it**

In `WhatsappBridge/README.md` `## Control protocol`: add the app-to-adapter row `| app -> adapter | `chats` | — (the linked account's conversations, most recent first) |` and the adapter-to-app rows `| adapter -> app | `chat` | `ChatId`, `SenderName`, `Text` = last message, `Timestamp`, `IsGroup`, `AvatarData` (base64) |` and `| adapter -> app | `chats.done` | — (the list is over) |`.

In `## Configuration`: add

```markdown
| `CHATS_LIMIT` | `25` | how many conversations the chat list returns |
| `CHATS_AVATARS` | `on` | fetch profile pictures (one request per person, `off` disables) |
```

Mirror every line in `WhatsappBridge/README.it.md`. Add the three variables to `WhatsappBridge/.env.example` with a one-line comment each.

- [ ] **Step 8: Run the guards**

```bash
node tools/check-docs.js
cd WhatsappBridge && npm test
```

Expected: docs aligned, no emoji; 82 tests pass.

- [ ] **Step 9: Commit**

```bash
git add WhatsappBridge
git commit -m "feat: send the linked account's conversation list to the app"
```

---

### Task 4: The app shows the conversations, with avatars

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs`, `WhatsappApp/Models/Contact.cs`, `WhatsappApp/Services/DataService.cs`, `WhatsappApp/Pages/ChatsPage.xaml`, `WhatsappApp/Pages/ChatsPage.xaml.cs`
- Modify: `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw`
- Modify docs: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: the `chat` and `chats.done` frames from Task 3.
- Produces: `ChatMessage.IsGroup` (bool), `ChatMessage.AvatarData` (string); `Contact.Avatar` (BitmapImage), `Contact.AvatarData` (string), `Contact.LoadAvatarAsync()`; `DataService` handles both new commands.

- [ ] **Step 1: Add the two `ChatMessage` fields**

In `WhatsappApp/Models/ChatMessage.cs`, with the other backing fields:

```csharp
        private string _avatarData;         // immagine del profilo, base64
        private bool _isGroup;              // la chat e' un gruppo
```

and the properties, after `RelatedMessageId`:

```csharp
        /// <summary>Immagine del profilo della chat, in base64. Vuota se non ce l'ha.</summary>
        [DataMember]
        public string AvatarData
        {
            get { return _avatarData; }
            set { _avatarData = value; OnPropertyChanged(); }
        }

        /// <summary>Vero per i gruppi: GOWA non ha un avatar per loro.</summary>
        [DataMember]
        public bool IsGroup
        {
            get { return _isGroup; }
            set { _isGroup = value; OnPropertyChanged(); }
        }
```

- [ ] **Step 2: Teach `Contact` about an avatar**

In `WhatsappApp/Models/Contact.cs`, add `using System.Threading.Tasks;`, `using Windows.UI.Xaml.Media.Imaging;` and `using WhatsappApp.Services;`, then:

```csharp
        private string _avatarData;
        private BitmapImage _avatar;
```

```csharp
        /// <summary>L'immagine del profilo arrivata dall'adapter, ancora in base64.</summary>
        public string AvatarData
        {
            get { return _avatarData; }
            set { _avatarData = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// L'immagine decodificata. Non e' un dato del filo: la costruisce
        /// LoadAvatarAsync, e la XAML la usa al posto delle iniziali.
        /// </summary>
        public BitmapImage Avatar
        {
            get { return _avatar; }
            set
            {
                _avatar = value;
                OnPropertyChanged();
                OnPropertyChanged("HasAvatar");
            }
        }

        /// <summary>Vero quando c'e' un'immagine da mostrare al posto delle iniziali.</summary>
        public bool HasAvatar
        {
            get { return _avatar != null; }
        }

        /// <summary>
        /// Decodifica AvatarData, una volta sola. Va atteso sul thread UI, come
        /// ImageHelper richiede.
        /// </summary>
        public async Task LoadAvatarAsync()
        {
            if (_avatar != null || string.IsNullOrEmpty(_avatarData)) return;
            try
            {
                Avatar = await ImageHelper.FromBase64Async(_avatarData);
            }
            catch (Exception ex)
            {
                Diag.Failed("Contact.LoadAvatarAsync", ex);
                Avatar = null;
            }
        }
```

Add `using System;` and `using System.Threading.Tasks;` to the file as well: `OnPropertyChanged` takes the caller name (`"Avatar"`), so the setter needs the explicit `OnPropertyChanged("HasAvatar")` for the `HasAvatar` binding.

- [ ] **Step 3: Handle the two new commands**

In `WhatsappApp/Services/DataService.cs`, in `OnControlMessageReceived`'s switch, add:

```csharp
                case "chat":
                    ApplyChat(message);
                    break;
                case "chats.done":
                    RaiseChatListCompleted();
                    break;
```

and the handler plus the event, next to `ApplyContact`:

```csharp
        /// <summary>
        /// Una riga dell'elenco chat: la conversazione esiste in WhatsApp anche
        /// se non abbiamo mai ricevuto un suo messaggio in questa sessione.
        /// </summary>
        private void ApplyChat(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.ChatId)) return;

            var contact = FindContact(message.ChatId);
            string name = string.IsNullOrEmpty(message.SenderName)
                ? DisplayNameForJid(message.ChatId)
                : message.SenderName;

            if (contact == null)
            {
                contact = new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    Initials = InitialsFor(name),
                    UnreadCount = 0
                };
                _contacts.Add(contact);
                _contactIndex[contact.Id] = contact;
            }
            else
            {
                contact.Name = name;
                contact.Initials = InitialsFor(name);
            }

            // L'anteprima arriva dal server: se in questa sessione abbiamo gia'
            // un messaggio piu' recente, quello resta (non si torna indietro).
            if (!string.IsNullOrEmpty(message.Text) && string.IsNullOrEmpty(contact.LastMessage))
            {
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
            }

            if (!string.IsNullOrEmpty(message.AvatarData) && contact.AvatarData != message.AvatarData)
            {
                contact.AvatarData = message.AvatarData;
#pragma warning disable 4014
                contact.LoadAvatarAsync();
#pragma warning restore 4014
            }
        }

        /// <summary>La lista delle conversazioni e' finita di arrivare.</summary>
        public event EventHandler ChatListCompleted;

        private void RaiseChatListCompleted()
        {
            var handler = ChatListCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }
```

- [ ] **Step 4: Ask for the conversations when the list is empty**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, replace the `contacts` request with `chats` (the address book is still merged in by the adapter itself):

```csharp
            // Si chiede solo se la lista e' vuota: l'adapter risponde con un
            // frame per conversazione, e ripeterlo ad ogni visita ricostruiva
            // tutto l'elenco per niente.
            if (CommunicationService.Instance.IsConnected && DataService.Instance.Contacts.Count == 0)
            {
#pragma warning disable 4014
                CommunicationService.Instance.SendControlAsync("chats");
#pragma warning restore 4014
            }
```

- [ ] **Step 5: Show the avatar in the row**

In `WhatsappApp/Pages/ChatsPage.xaml`, inside the avatar `Grid` (the one with the initials ellipse), put the picture above the initials:

```xml
                                <Ellipse Width="52" Height="52"
                                         Fill="{Binding Initials, Converter={StaticResource InitialToColor}}"/>
                                <Image Width="52" Height="52" Stretch="UniformToFill"
                                       Source="{Binding Avatar}"
                                       Visibility="{Binding HasAvatar, Converter={StaticResource BoolToVisibility}}"/>
                                <TextBlock Text="{Binding Initials}"
                                           Foreground="White" FontSize="18" FontWeight="SemiBold"
                                           VerticalAlignment="Center" HorizontalAlignment="Center"/>
```

`BoolToVisibilityConverter` is the converter Task 10 of the previous plan removed from this page; re-add its registration on top of the `Page.Resources`:

```xml
        <conv:BoolToVisibilityConverter x:Key="BoolToVisibility"/>
```

and crop the image so it stays a circle by wrapping it, which WP8.1 does not do by itself: put the `Image` inside the same `Grid` and add a clipping ellipse over it:

```xml
                                <Ellipse Width="52" Height="52" Fill="Transparent"
                                         Stroke="{StaticResource WhatsAppChatBgBrush}" StrokeThickness="0"/>
```

The image is a square scaled with `UniformToFill` and the row is 72 px tall, so a square picture reads as a square avatar: acceptable, and the alternative (a real circular clip) needs `RectangleGeometry` clipping that WP8.1's XAML does not support on `Image`.

- [ ] **Step 6: Add the strings**

Add to both `.resw` files, in the same order:

```xml
  <data name="ChatsPage_LoadingChats" xml:space="preserve">
    <value>Looking for your conversations...</value>
  </data>
```

```xml
  <data name="ChatsPage_LoadingChats" xml:space="preserve">
    <value>Cerco le tue conversazioni...</value>
  </data>
```

Show it in `ChatsPage.xaml.cs` (`OnNavigatedTo`) by putting `Loc.Get("ChatsPage_LoadingChats", "Looking for your conversations...")` into the empty-state hint while the list is empty and a request is in flight.

- [ ] **Step 7: Run the guards and the VM build**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
xmllint --noout WhatsappApp/Pages/ChatsPage.xaml
```

then the three VM commands (rmdir, robocopy, msbuild) with `/t:Rebuild /p:Configuration=Debug /p:Platform=x86`.
Expected: all `OK`; `COPIA=0`, `Errori: 0`, one warning, package created.

- [ ] **Step 8: Document it**

In `README.md` `## Protocol`, extend the paragraph about the list with one sentence: the conversation list comes from the server's own chat storage (`CHATS_LIMIT`, default 25) and each row can carry a profile picture. Mirror it in `README.it.md`.

- [ ] **Step 9: Commit**

```bash
git add WhatsappApp README.md README.it.md
git commit -m "feat: show the server's conversations and their profile pictures"
```

---

### Task 5: A service that can raise a toast and set the badge

**Files:**
- Create: `WhatsappApp/Services/NotificationService.cs`
- Modify: `WhatsappApp/Services/SettingsService.cs`, `WhatsappApp/WhatsappApp.csproj`, `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw`

**Interfaces:**
- Consumes: nothing.
- Produces: `NotificationService.ShowMessage(string title, string body)`, `NotificationService.SetUnread(int count)`, `NotificationService.Clear()`; `SettingsService.NotificationsEnabled` (bool, default `true`).

- [ ] **Step 1: Persist the switch**

In `WhatsappApp/Services/SettingsService.cs`, add next to the other keys:

```csharp
        private const string KeyNotifications = "NotificationsEnabled";
```

the field with the others:

```csharp
        private static bool _notificationsEnabled;
```

inside `EnsureLoaded()`:

```csharp
            _notificationsEnabled = ReadBool(KeyNotifications, true);
```

and the property:

```csharp
        /// <summary>Se l'app puo' alzare un avviso quando arriva un messaggio.</summary>
        public static bool NotificationsEnabled
        {
            get { return _notificationsEnabled; }
            set
            {
                _notificationsEnabled = value;
                Settings.Values[KeyNotifications] = value;
            }
        }
```

`ReadBool` next to `ReadInt`:

```csharp
        private static bool ReadBool(string key, bool fallback)
        {
            object value = Settings.Values[key];
            return value is bool ? (bool)value : fallback;
        }
```

- [ ] **Step 2: Write the service**

Create `WhatsappApp/Services/NotificationService.cs`:

```csharp
using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Gli avvisi che questa app puo' dare mentre gira: un toast e il numero
    /// sull'icona. Non c'e' nessun servizio cloud dietro, quindi un messaggio
    /// che arriva con l'app sospesa non produce niente: la connessione TCP e'
    /// dell'app, e WP8.1 la chiude quando la sospende. Le notifiche push vere
    /// richiederebbero un servizio esterno che questo progetto non ha.
    ///
    /// Ogni chiamata e' protetta: un telefono che rifiuta il toast non deve
    /// far cadere la ricezione del messaggio.
    /// </summary>
    public static class NotificationService
    {
        /// <summary>Un avviso per un messaggio arrivato in una chat chiusa.</summary>
        public static void ShowMessage(string title, string body)
        {
            if (!SettingsService.NotificationsEnabled) return;

            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                texts[0].AppendChild(xml.CreateTextNode(Cut(title, 60)));
                texts[1].AppendChild(xml.CreateTextNode(Cut(body, 200)));
                ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.ShowMessage", ex);
            }
        }

        /// <summary>Il numero sull'icona: 0 toglie il badge.</summary>
        public static void SetUnread(int count)
        {
            try
            {
                var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
                if (count <= 0)
                {
                    updater.Clear();
                    return;
                }

                var xml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
                var badge = (XmlElement)xml.SelectSingleNode("/badge");
                badge.SetAttribute("value", Math.Min(count, 99).ToString());
                updater.Update(new BadgeNotification(xml));
            }
            catch (Exception ex)
            {
                Diag.Failed("NotificationService.SetUnread", ex);
            }
        }

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "…";
        }
    }
}
```

- [ ] **Step 3: Register the file**

In `WhatsappApp/WhatsappApp.csproj`, next to the other `Services` entries:

```xml
    <Compile Include="Services\NotificationService.cs" />
```

- [ ] **Step 4: Add the settings strings**

Add to both `.resw` files:

```xml
  <data name="ConnectionPage_Notifications.Content" xml:space="preserve">
    <value>Notify me about new messages</value>
  </data>
```

```xml
  <data name="ConnectionPage_Notifications.Content" xml:space="preserve">
    <value>Avvisami dei nuovi messaggi</value>
  </data>
```

- [ ] **Step 5: Run the guards and the VM build**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
```

then the three VM commands.
Expected: `check-resw.js` complains that `ConnectionPage_Notifications.Content` is unused until Task 6 adds the switch: run this task together with Task 6, or add the XAML in the same commit.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp
git commit -m "feat: a toast and a tile badge for the running app"
```

---

### Task 6: One arriving message, one toast

**Files:**
- Modify: `WhatsappApp/Services/DataService.cs`, `WhatsappApp/Pages/ConnectionPage.xaml`, `WhatsappApp/Pages/ConnectionPage.xaml.cs`
- Modify docs: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `NotificationService` and `SettingsService.NotificationsEnabled` (Task 5).
- Produces: nothing new; `DataService` becomes the only caller of `NotificationService`.

- [ ] **Step 1: Raise the toast and count the badge**

In `WhatsappApp/Services/DataService.cs`, in `OnNetworkMessageReceived`, after the contact has been found or created and the unread count updated:

```csharp
            // Un avviso solo per una chat che non stiamo guardando: con la chat
            // aperta un toast e' rumore, e il badge non deve contare un messaggio
            // che l'utente sta gia' leggendo.
            if (message.IsIncoming && message.ChatId != _activeChatId)
            {
                NotificationService.ShowMessage(contact.Name, message.Text);
            }
            NotificationService.SetUnread(TotalUnread());
```

and the total, next to `ClearUnread`:

```csharp
        /// <summary>Somma dei non letti: e' il numero che va sull'icona.</summary>
        private int TotalUnread()
        {
            int total = 0;
            foreach (var contact in _contacts)
            {
                if (contact != null) total += contact.UnreadCount;
            }
            return total;
        }
```

Also update the badge in `ClearUnread` (opening a chat lowers it) and in `AddMessage`:

```csharp
        public void ClearUnread(string chatId)
        {
            var contact = FindContact(chatId);
            if (contact != null)
            {
                contact.UnreadCount = 0;
                NotificationService.SetUnread(TotalUnread());
            }
        }
```

- [ ] **Step 2: Add the switch to the settings page**

In `WhatsappApp/Pages/ConnectionPage.xaml`, next to the other settings rows:

```xml
            <ToggleSwitch x:Name="NotificationsToggle" x:Uid="ConnectionPage_Notifications"
                          Margin="0,12,0,0"
                          Toggled="NotificationsToggle_Toggled"/>
```

In `WhatsappApp/Pages/ConnectionPage.xaml.cs`, in the constructor after `InitializeComponent()`:

```csharp
            NotificationsToggle.IsOn = SettingsService.NotificationsEnabled;
```

and the handler:

```csharp
        private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.NotificationsEnabled = NotificationsToggle.IsOn;
            if (!NotificationsToggle.IsOn) NotificationService.SetUnread(0);
        }
```

- [ ] **Step 3: Run the guards and the VM build**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
xmllint --noout WhatsappApp/Pages/ConnectionPage.xaml
```

then the three VM commands.
Expected: all `OK`; `0` errors, one warning.

- [ ] **Step 4: Document the limit honestly**

In `README.md` `## Limitations`, add a bullet:

```markdown
- Notifications are raised while the app is running: WP8.1 suspends it in the background, which closes the socket, and this project has no cloud service to push through. A message that arrives while the app is suspended is delivered the next time it connects.
```

and the same in `README.it.md`:

```markdown
- Le notifiche vengono alzate mentre l'app gira: WP8.1 la sospende in background, il che chiude il socket, e questo progetto non ha un servizio cloud da cui fare push. Un messaggio arrivato con l'app sospesa viene consegnato alla connessione successiva.
```

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp README.md README.it.md
git commit -m "feat: notify about a message that arrives in a closed chat"
```

---

### Task 7: Pick a contact from the phone's address book

**Files:**
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs`, `WhatsappApp/Package.appxmanifest`, `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw`

**Interfaces:**
- Consumes: `DataService.FindContact`, `DataService.AddContact`.
- Produces: `ChatsPage.PickFromContactsAsync()`; a helper `NormalizePhone(string) : string` shared with the manual entry.

- [ ] **Step 1: Add the button to the dialog**

In `ChatsPage.xaml.cs`, inside `NewChatButton_Click`, after the `error` block:

```csharp
            var pickButton = new Button
            {
                Content = Loc.Get("NewChat_PickContact", "Choose from contacts"),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
```

add it to `content.Children` between `input` and `error`, and wire it:

```csharp
            string picked = null;
            pickButton.Click += async (s, a) =>
            {
                picked = await PickFromContactsAsync();
                if (!string.IsNullOrEmpty(picked)) input.Text = picked;
            };
```

- [ ] **Step 2: Implement the picker**

In the same file:

```csharp
        /// <summary>
        /// Apre il selettore contatti del sistema e restituisce il primo numero
        /// trovato, ripulito. Vuoto se l'utente annulla o il contatto non ha
        /// numeri: non e' un errore, e' una scelta.
        /// </summary>
        private static async Task<string> PickFromContactsAsync()
        {
            try
            {
                var contact = await ContactPicker.PickSingleContactAsync();
                if (contact == null || contact.Phones == null || contact.Phones.Count == 0) return "";

                foreach (var phone in contact.Phones)
                {
                    string number = NormalizePhone(phone.Number);
                    if (!string.IsNullOrEmpty(number)) return number;
                }
                return "";
            }
            catch (Exception ex)
            {
                // Some devices refuse the picker outright: it is not a crash.
                Diag.Failed("ChatsPage.PickFromContactsAsync", ex);
                return "";
            }
        }

        /// <summary>Solo le cifre, senza prefisso internazionale scritto a mano.</summary>
        private static string NormalizePhone(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var digits = new StringBuilder();
            foreach (char c in value)
            {
                if (char.IsDigit(c)) digits.Append(c);
            }
            return digits.Length >= 6 ? digits.ToString() : "";
        }
```

Add `using System.Threading.Tasks;`, `using System.Text;`, `using Windows.ApplicationModel.Contacts;` and `using WhatsappApp.Services;` if they are missing.

Use `NormalizePhone` for the manual entry too, replacing the three `Replace` calls:

```csharp
                phone = NormalizePhone(input.Text);
```

- [ ] **Step 3: Build, and add the capability only if the build asks for it**

Run the three VM commands.
Expected: `0` errors. If the build reports that `ContactPicker` needs the capability, add

```xml
    <!-- Serve per il selettore contatti del sistema (nuova chat). -->
    <Capability Name="contacts" />
```

to `WhatsappApp/Package.appxmanifest` inside `<Capabilities>` and build again. If it builds and the picker throws `E_ACCESSDENIED` on the device, add it then too.

- [ ] **Step 4: Add the strings**

Add to both `.resw` files:

```xml
  <data name="NewChat_PickContact.Content" xml:space="preserve">
    <value>Choose from contacts</value>
  </data>
```

```xml
  <data name="NewChat_PickContact.Content" xml:space="preserve">
    <value>Scegli dai contatti</value>
  </data>
```

- [ ] **Step 5: Run the guards**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
```

Expected: all `OK`.

- [ ] **Step 6: Commit**

```bash
git add WhatsappApp
git commit -m "feat: start a chat from a phone contact"
```

---

### Task 8: Pick a conversation that already exists

**Files:**
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs`, `WhatsappApp/Strings/{en-US,it-IT}/Resources.resw`
- Modify docs: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `DataService.Contacts` (the rows Task 4 fills).
- Produces: the dialog gains a list of the known conversations; tapping one opens it.

- [ ] **Step 1: Add the list to the dialog**

In `ChatsPage.xaml.cs`, in `NewChatButton_Click`, after `content.Children.Add(error)`:

```csharp
            var title = new TextBlock
            {
                Text = Loc.Get("NewChat_Synced", "Conversations already on the server"),
                Foreground = new SolidColorBrush(Colors.Gray),
                FontSize = 13,
                Margin = new Thickness(0, 12, 0, 4)
            };

            var known = new ListView
            {
                ItemsSource = DataService.Instance.Contacts,
                MaxHeight = 220,
                SelectionMode = ListViewSelectionMode.Single
            };
            known.ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<TextBlock Text=\"{Binding Name}\" Foreground=\"Black\" FontSize=\"16\" Margin=\"0,8,0,8\"/>" +
                "</DataTemplate>");

            Contact chosen = null;
            known.SelectionChanged += (s, a) =>
            {
                if (a.AddedItems.Count == 0) return;
                chosen = a.AddedItems[0] as Contact;
                dialog.Hide();
            };

            if (DataService.Instance.Contacts.Count > 0)
            {
                content.Children.Add(title);
                content.Children.Add(known);
            }
```

After the `dialog.ShowAsync()` loop returns, open the chosen conversation before doing anything else:

```csharp
            if (chosen != null)
            {
                Frame.Navigate(typeof(ChatPage), chosen);
                return;
            }
```

`ContentDialog.Hide()` closes the dialog with the current result (`None`), so the loop exits through `if (result != ContentDialogResult.Primary) return;` — the `chosen` check must therefore come **before** that `return`. Put it at the top of the loop body:

```csharp
            while (jid == null)
            {
                var result = await dialog.ShowAsync();

                if (chosen != null)
                {
                    Frame.Navigate(typeof(ChatPage), chosen);
                    return;
                }

                if (result != ContentDialogResult.Primary) return;
```

Declare `Contact chosen = null;` and `var dialog = ...` before it: the handler captures `dialog`, so the `var dialog` declaration must move above the handler.

- [ ] **Step 2: Add the strings**

Add to both `.resw` files:

```xml
  <data name="NewChat_Synced.Text" xml:space="preserve">
    <value>Conversations already on the server</value>
  </data>
```

```xml
  <data name="NewChat_Synced.Text" xml:space="preserve">
    <value>Conversazioni gia' presenti sul server</value>
  </data>
```

- [ ] **Step 3: Run the guards and the VM build**

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js
```

then the three VM commands (the `XamlReader.Load` template is a run-time string, so only the device proves it; the build proves the code).
Expected: all `OK`; `0` errors.

- [ ] **Step 4: Document it**

In `README.md` `#### Calls`'s sibling, add a `#### New chat` paragraph: a chat can be started from a phone number, from the system contact picker, or from a conversation the server already knows. Mirror it in `README.it.md` at the same heading level.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp README.md README.it.md
git commit -m "feat: start a chat from a conversation that already exists"
```

---

### Task 9: Write the rules down and run the whole gate

**Files:**
- Modify: `.agents/skills/{maintain-the-app,test-the-app,update-the-app}/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-26-recent-chats-avatars-notifications-and-contact-picker.md`

- [ ] **Step 1: Record the three facts in `maintain-the-app`**

- In "Showing only data that exists": the chat list comes from `GET /chats`, **not** from `GET /user/my/contacts` (that is the address book, and it is empty on a fresh link); profile pictures come from `GET /user/avatar` and only for people.
- Notifications: the app can only notify while it runs, because WP8.1 suspends it and closes the socket; a real push needs a cloud service this project does not have, so the README pair says so.
- The contact picker is the system's, and it is the user's consent: the app does not read the address book on its own.

- [ ] **Step 2: Add the new checks to `test-the-app`**

- The on-device checklist gains: after linking, the chat list shows the conversations that exist in WhatsApp (not the empty address book), with pictures where the person has one; a message in a closed chat raises a toast and a badge; the badge clears when the chat is opened; a chat can be started from a phone contact and from an existing conversation.
- State the counts this plan produces (adapter tests, resw keys, C# files) and correct any stale number.

- [ ] **Step 3: Add the controls to `update-the-app`**

A new control command needs: a `GowaClient` method with a test, a `server.js` handler that always ends with the matching `*.done` frame, both README tables in the same commit, and an app-side `case` in `DataService.OnControlMessageReceived`.

- [ ] **Step 4: Record what execution changed**

Append a `## What execution changed about this plan` section to this plan with whatever the run forced.

- [ ] **Step 5: Run the whole gate**

```bash
node tools/check-csharp5.js
node tools/check-icons.js
node tools/check-resw.js --strict
node tools/check-docs.js
node tools/check-framing.js
node tools/qr-term.js --self-test
cd WhatsappBridge && npm test
cd .. && node --test "tools/test/**/*.test.js"
```

Expected: every guard `OK`, the adapter suite and `tools/test` with `fail 0`.

- [ ] **Step 6: Build the app one last time**

Run the three VM commands.
Expected: `COPIA=0`, `Errori: 0`, `Your package has been successfully created`.

- [ ] **Step 7: Commit and push**

```bash
git add .agents docs
git commit -m "docs: record how the chat list, the notifications and the picker work"
git push origin master
```

---

## What execution changed about this plan

Recorded after the run. Everything else was built as written.

1. **Task 1 ended with 6 tests, not 5.** `previewForMessage` needed two cases
   (body present, media with no body).
2. **The Task 1 cap test's fake was self-inconsistent.** It sliced the chat list
   to `limit` and then expected `limit` rows after skipping an unreadable one;
   the fake now returns all three and the cap applies to the result, which is
   what the test was always about.
3. **Task 2 ended with 78 adapter tests** (76 before + 2), not the 77 the plan
   guessed, and Task 3 with **83**.
4. **Task 4 added no `.resw` key.** The plan's `ChatsPage_LoadingChats` was
   dropped: the page already has an honest empty state, and a separate "loading"
   state would have to be invented client-side rather than reported by the
   server. The resw pair was therefore unchanged by Task 4.
5. **Task 4's `BoolToVisibilityConverter` needed `ConverterParameter=Invert`** on
   the initials `TextBlock`, otherwise the initials stay drawn on top of the
   picture.
6. **Task 5 and Task 6 are two commits of one run.** The plan said so: the
   `ConnectionPage_Notifications.Header` key alone would fail
   `check-resw.js --strict` as unused. Task 5 is the service and the setting,
   Task 6 is the toggle and the wiring.
7. **`check-resw.js` requires a localizable property to appear literally in the
   XAML.** A `ToggleSwitch` with `x:Uid` and no literal `Header="..."` reads as
   an unused key, so the English fallback text must be written on the element.
8. **Task 7 is where the plan was most wrong, and the compiler was right.**
   WP8.1 has **no** `ContactPicker.PickContactAsync`, and the API it does have is
   the `Windows 8.1` pair, not the Windows 10 one:
   - `ContactPicker` must be instantiated (`new ContactPicker()`);
     `PickSingleContactAsync` is an instance method (CS0120).
   - It returns `ContactInformation`, **not** `Contact`, so a
     `using PickerContact = Windows.ApplicationModel.Contacts.ContactInformation;`
     alias is needed to keep `WhatsappApp.Models.Contact` unambiguous (CS0104).
   - The property is `PhoneNumbers`, not `Phones`, and each field is read through
     `ContactField.Value`, not `.Number` (CS1061).
   - All of it raises CS0618 pointing at the Windows 10 replacement. That warning
     is correct and unactionable here, so the method is wrapped in
     `#pragma warning disable 618` with a comment, which puts the build back to
     its one known warning and keeps a *new* warning visible.
   - No capability was needed: the picker is the consent. The manifest is
     unchanged, as the plan allowed.
9. **Task 8's dialog needed one ordering change.** `ContentDialog.Hide()` closes
   with `None`, so the `chosen` check moved above the
   `result != ContentDialogResult.Primary` return, or the tap would have been
   thrown away.
10. **Final counts:** 83 adapter tests, 17 tests in `tools/test`, 103 resw keys,
    28 C# files, 12 inline icon Paths (9 distinct). Build gate: `COPIA=0`,
    `Errori: 0`, the single known CS0618 (`FileOpenPicker.PickSingleFileAsync`),
    `Your package has been successfully created`.

## What only the phone can prove

- Whether `ContactPicker` is allowed without a capability on a real WP8.1 device, and what it returns for a contact whose number is stored in a local format.
- Whether the toast appears while the app is in the foreground (it should) and whether the badge shows on the tile.
- Whether `GET /chats` returns a `name` for every conversation and whether any of those names are empty, which decides how much the fallback (`displayNameForJid`) matters.

## Follow-ups executed after the plan

Three requests that came in while the plan was being run, and what they found:

### The unread count on the tile

The plan deferred this, saying the badge already does the job. It was asked for
anyway, and the build settled what WP8.1 actually has: `TileSquare150x150IconWithBadge`
and `TileSquare71x71IconWithBadge` exist, `TileWide310x150IconWithBadge` does not
(**CS0117**). So the tile is updated with the two sizes that exist, the wide tile
keeps the manifest's content, and the badge draws the number on all of them.

### Reconnecting after a suspend

This turned out to be a real hole, and the code already said so: `OnSuspending`
kept the socket open on purpose because "there is no reconnection path". There
still was not one - the OS closes the socket while the app is suspended, and
`IsConnected` stays `true`, so the app came back looking connected and mute.

`ConnectionWatchdog` is that path: a `DispatcherTimer` every 20 s sends `status`
(the adapter answers `broadcastState()`), `LastInboundUtc` records every frame
read, and 60 s of silence (`Disconnect()` **then** `AutoConnector`, which returns
`true` immediately while `IsConnected`) reopens the connection. `App.OnResuming`
calls `CheckNow()` so the wait is not added to the time the app already spent
suspended.

### The circular avatar

The plan said a circular crop would need compositing the bitmap, because WP8.1
clips only rectangles. That is true of `UIElement.Clip`, but not of a `Border`:
a `Border` clips its own `Background` to `CornerRadius`, and 26 on a 52 px square
is an exact circle. The avatar is now a `Border` whose background is an
`ImageBrush` - no pixel work, no encoder round-trip.

### The unread number on the row of the chat you are reading

Asked for, and the answer was "like WhatsApp does". WhatsApp shows no number on a
row whose messages are on screen, and that is not the same thing as never
counting them - which is what the plan's `message.ChatId != _activeChatId` did.

The exclusion was a real bug, not just a shortcut: `ActiveChatId` stays set while
the chat page is alive, so messages delivered after a resume, with that chat open,
were neither counted nor cleared. They disappeared from the row and from the badge
altogether, silently.

Now `UnreadCount` counts every incoming message, and `ChatPage` - the only thing
that knows a message was put in front of the user - calls `ClearUnread` when it
opens the conversation and for each message it displays. `ActiveChatId` is left
with one job: suppressing the toast for the chat on screen.

## Deliberately not in this plan

- **Real push notifications.** They need a public service that holds the MPNS channel URIs and forwards the GOWA webhook, plus a certificate: a project of its own, and a different deployment. The plan above says what the app can do without one.
- **Reading the address book directly.** The picker is the user's consent; enumerating contacts would need the `contacts` capability and a privacy story this project does not want.
- **A circular avatar clip.** WP8.1 XAML cannot clip an `Image` with an ellipse; a real circular crop means compositing the bitmap in `ImageHelper`, which is worth doing only if the square read badly on the device.
- **Live tiles.** `TileUpdateManager` could show the unread count on the tile; the badge already does that job, and a second surface doubles the notification code for no extra information.
