# Chat History on Open Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Opening a chat shows the conversation that already exists on the server, not only the messages that arrived while the app was running.

**Architecture:** The app asks the adapter for one chat's recent messages when the chat page opens; the adapter reads `GET /chat/:chat_jid/messages` from GOWA and answers with ordinary message frames marked `IsHistory`. The app inserts them in date order, skips duplicates by WhatsApp's message id, and keeps them out of the unread count and the toasts - they are not arriving now.

**Tech Stack:** Node 18.13+ (adapter, `node:test`), C# 5 on WP8.1 WinRT (app), MSBuild 12.0 in the Parallels VM.

## Global Constraints

- Adapter code is CommonJS, `'use strict';`, `node:test` + `node:assert`. Run with `cd WhatsappBridge && npm test`.
- The app is C# 5 only: no interpolated strings, no `?.`, no expression-bodied members, no auto-property initializers, no `nameof`. `node tools/check-csharp5.js` enforces this and the WP8.1 API surface.
- All runtime text (logs, diagnostics, resource strings) is English. Comments may be Italian, matching the surrounding code.
- The adapter is the only place that knows GOWA's HTTP shapes; the app only sees adapter frames.
- Documentation comes in pairs that stay mirror images: `README.md` + `README.it.md`, `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md`. Same heading count, order and level. Root pair keeps `## Disclosure` last. No emoji in any `.md`.
- Gates after every task: `node tools/check-csharp5.js`, `node tools/check-icons.js`, `node tools/check-resw.js --strict`, `node tools/check-docs.js`, `node tools/check-framing.js`, `node tools/qr-term.js --self-test`, `cd WhatsappBridge && npm test`, `node --test "tools/test/**/*.test.js"`.
- Build gate (Parallels VM): `prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"`, then the `robocopy` of the repo into `C:\Temp\wp81` (expect `COPIA=0`), then `MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86` (expect 0 errors, 0 warnings). One VM command must not be chained after another with `&&` in the same tool call: a failed first `prlctl` leaves the copy missing and the next build fails with "the system cannot find the path specified".
- Commit messages are English, `type: short imperative`. One commit per task, then push.

---

### Task 1: The adapter answers `messages`

`GowaClient#chatMessages(jid, limit)` already exists and is used by `chats.js` for the preview line, so the read is a command away. What is missing is the command, the field that marks a frame as history, and the config knob.

**Files:**
- Modify: `WhatsappBridge/message-format.js` (`buildChatMessage`, plus a new `mapHistoryMessage`)
- Modify: `WhatsappBridge/config.js` (`DEFAULTS`, `loadConfig`)
- Modify: `WhatsappBridge/server.js` (`sendMessages`, `handleControl`)
- Test: `WhatsappBridge/test/message-format.test.js`, `WhatsappBridge/test/config.test.js`, `WhatsappBridge/test/server.test.js`
- Modify: `WhatsappBridge/.env.example`, `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`, `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `GowaClient#chatMessages(jid, limit)` (returns GOWA's `MessageInfo` list), `buildChatMessage(fields)`, `sendControl(fields)`, `config.messages.limit`.
- Produces: `mapHistoryMessage(raw): object` returning `{ id, text, senderId, senderName, chatId, timestamp, type, isIncoming, isHistory }`; a frame field `IsHistory` (`true` when the frame is old); the control command `messages` carrying the chat JID in `Text`.

- [ ] **Step 1: Write the failing tests for the mapping**

Append to `WhatsappBridge/test/message-format.test.js` (it already imports from `../message-format`; add `mapHistoryMessage` to that import list):

```js
test('mapHistoryMessage reads a text message and marks it as history', () => {
  const mapped = mapHistoryMessage({
    id: 'A1',
    chat_jid: '393401234567@s.whatsapp.net',
    sender_jid: '393401234567@s.whatsapp.net',
    sender_display_name: 'Anna',
    content: '  ciao  ',
    timestamp: '2026-09-26T09:00:00Z',
    is_from_me: false,
    media_type: ''
  });

  assert.strictEqual(mapped.id, 'A1');
  assert.strictEqual(mapped.text, 'ciao');
  assert.strictEqual(mapped.chatId, '393401234567@s.whatsapp.net');
  assert.strictEqual(mapped.senderName, 'Anna');
  assert.strictEqual(mapped.isIncoming, true);
  assert.strictEqual(mapped.type, 0);
  assert.strictEqual(mapped.isHistory, true);
  assert.ok(mapped.timestamp instanceof Date);
});

test('mapHistoryMessage marks my own messages and leaves the sender alone', () => {
  const mapped = mapHistoryMessage({ id: 'A2', chat_jid: 'x@s.whatsapp.net', content: 'io', is_from_me: true });

  assert.strictEqual(mapped.isIncoming, false);
  assert.strictEqual(mapped.senderId, 'me');
});

test('mapHistoryMessage names the media it cannot download', () => {
  // Una foto vecchia non e' fra i byte che il webhook ha consegnato: resta una
  // parola, perche' un fumetto vuoto sarebbe peggio.
  assert.strictEqual(mapHistoryMessage({ id: 'A3', media_type: 'image' }).text, '[Image]');
  assert.strictEqual(mapHistoryMessage({ id: 'A4', media_type: 'video' }).text, '[Video]');
  assert.strictEqual(mapHistoryMessage({ id: 'A5', media_type: 'audio' }).text, '[Audio]');
  assert.strictEqual(mapHistoryMessage({ id: 'A6', media_type: 'document' }).text, '[Document]');
  assert.strictEqual(mapHistoryMessage({ id: 'A7', media_type: 'sticker' }).text, '[Sticker]');

  // Con una didascalia vince la didascalia.
  assert.strictEqual(mapHistoryMessage({ id: 'A8', media_type: 'image', content: 'guarda' }).text, 'guarda');

  // Un tipo che non conosciamo e nessun testo: nessuna parola inventata.
  assert.strictEqual(mapHistoryMessage({ id: 'A9', media_type: 'poll' }).text, '');
  assert.strictEqual(mapHistoryMessage({}).text, '');
});

test('mapHistoryMessage survives a timestamp it cannot read', () => {
  const mapped = mapHistoryMessage({ id: 'A10', timestamp: 'non una data' });

  assert.ok(mapped.timestamp instanceof Date);
  assert.strictEqual(isNaN(mapped.timestamp.getTime()), false);
});

test('buildChatMessage carries IsHistory only when it is set', () => {
  const history = buildChatMessage({ command: 'history', isHistory: true, type: 0, chatId: 'x@s.whatsapp.net' });
  assert.strictEqual(history.IsHistory, true);
  assert.strictEqual(history.Type, 0);

  const live = buildChatMessage({ text: 'nuovo' });
  assert.strictEqual(Object.prototype.hasOwnProperty.call(live, 'IsHistory'), false);
});
```

Add `mapHistoryMessage` to the `require` at the top of the file if it lists functions by name, e.g. change `const { buildChatMessage, mapWebhookMessage, ... } = require('../message-format');` to include it.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd WhatsappBridge && node --test test/message-format.test.js`
Expected: FAIL - `mapHistoryMessage is not a function` (four times) and `history.IsHistory` is `undefined`.

- [ ] **Step 3: Implement the mapping**

In `WhatsappBridge/message-format.js`, add above `buildChatMessage`:

```js
// I media vecchi restano una parola nel fumetto, come nell'anteprima della riga
// dell'elenco chat: i byte di una foto che e' arrivata mesi fa non sono fra
// quelli che il webhook ha consegnato, e un fumetto vuoto e' peggio di una
// parola che dice cosa c'era.
const HISTORY_MEDIA_LABEL = {
  image: '[Image]',
  video: '[Video]',
  audio: '[Audio]',
  document: '[Document]',
  sticker: '[Sticker]'
};

/**
 * Un messaggio dello storico di una chat (`GET /chat/:chat_jid/messages`).
 *
 * Sempre testo, mai immagine: il tipo del media lo dice `media_type`, ma i byte
 * non ci sono, e mandare un messaggio di tipo immagine senza dati disegnerebbe
 * un fumetto vuoto.
 */
function mapHistoryMessage(raw) {
  const m = raw || {};
  const media = typeof m.media_type === 'string' ? m.media_type.trim().toLowerCase() : '';
  const content = typeof m.content === 'string' ? m.content.trim() : '';
  const isFromMe = m.is_from_me === true;

  return {
    id: m.id || null,
    text: content || HISTORY_MEDIA_LABEL[media] || '',
    senderId: isFromMe ? 'me' : (m.sender_jid || ''),
    senderName: m.sender_display_name || '',
    chatId: m.chat_jid || '',
    timestamp: formatDateForWp8(m.timestamp),
    type: 0,
    isIncoming: !isFromMe,
    isHistory: true
  };
}
```

Then in `buildChatMessage`, next to the other conditional fields, add:

```js
  // Cronologia: un messaggio vecchio, mandato aprendo la chat. E' un messaggio
  // normale - va disegnato - ma non e' arrivato adesso, e l'app non lo conta
  // come non letto ne' avvisa per ognuno (vedi DataService nell'app).
  if (f.isHistory === true) msg.IsHistory = true;
```

and add `mapHistoryMessage` to `module.exports`.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd WhatsappBridge && node --test test/message-format.test.js`
Expected: PASS.

- [ ] **Step 5: Write the failing test for the limit**

Append to `WhatsappBridge/test/config.test.js`:

```js
test('the history limit has a default and can be overridden', () => {
  const defaults = loadConfig({});
  assert.strictEqual(defaults.messages.limit, 50);

  const custom = loadConfig({ MESSAGES_LIMIT: '200' });
  assert.strictEqual(custom.messages.limit, 200);
});
```

Run: `cd WhatsappBridge && node --test test/config.test.js`
Expected: FAIL - `Cannot read properties of undefined (reading 'limit')`.

- [ ] **Step 6: Implement the limit**

In `WhatsappBridge/config.js`, add to `DEFAULTS` after `CHATS_AVATARS: 'on'`:

```js
  CHATS_AVATARS: 'on',
  MESSAGES_LIMIT: '50'
```

and add to the returned config, after the `chats` block:

```js
    chats: {
      // Quante conversazioni elencare. Gli avatar costano una richiesta HTTP
      // per persona, e si possono spegnere.
      limit: parseInt(pick(env, 'CHATS_LIMIT'), 10),
      avatars: pick(env, 'CHATS_AVATARS').toLowerCase() !== 'off'
    },
    messages: {
      // Quanti messaggi caricare aprendo una chat. Una richiesta di lettura,
      // ma la risposta e' un frame per messaggio: il limite e' quanti frame
      // passano, non quanto dura la lettura.
      limit: parseInt(pick(env, 'MESSAGES_LIMIT'), 10)
    }
```

Run: `cd WhatsappBridge && node --test test/config.test.js`
Expected: PASS.

- [ ] **Step 7: Write the failing server tests**

Append to `WhatsappBridge/test/server.test.js`, next to the other `chats` tests:

```js
test('the messages command sends one frame per stored message, marked as history', async () => {
  const sent = [];
  const gowa = {
    chatMessages: async (jid, limit) => {
      assert.strictEqual(jid, 'a@s.whatsapp.net');
      assert.strictEqual(limit, 5);
      return [
        { id: 'A1', chat_jid: jid, sender_jid: 'a@s.whatsapp.net', content: 'ciao', timestamp: '2026-09-26T09:00:00Z', is_from_me: false },
        { id: 'A2', chat_jid: jid, sender_display_name: 'Anna', media_type: 'image', timestamp: '2026-09-26T09:05:00Z', is_from_me: true }
      ];
    },
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' })
  };
  const config = { messages: { limit: 5 }, chats: {}, calls: {}, bridge: { port: 8585 } };
  const bridge = createBridge({ config, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });

  await bridge.handleControl({ Type: 3, Command: 'messages', Text: 'a@s.whatsapp.net', SenderName: 'test' });

  const frames = sent.map((packet) => decodeFrame(packet));
  assert.strictEqual(frames.length, 2);
  assert.strictEqual(frames[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(frames[0].Text, 'ciao');
  assert.strictEqual(frames[0].IsHistory, true);
  assert.strictEqual(frames[0].Type, 0);
  assert.strictEqual(frames[0].Command, undefined);
  assert.strictEqual(frames[1].Text, '[Image]');
  assert.strictEqual(frames[1].IsIncoming, false);
  assert.strictEqual(frames[1].SenderName, 'Anna');
});

test('the messages command drops a message GOWA has no id for, and reports a failure once', async () => {
  const sent = [];
  const gowa = {
    chatMessages: async () => [
      { chat_jid: 'a@s.whatsapp.net', content: 'senza id' },
      { id: 'B2', chat_jid: 'a@s.whatsapp.net', content: 'con id' }
    ],
    status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@s.whatsapp.net' })
  };
  const config = { messages: { limit: 5 }, bridge: { port: 8585 } };
  const bridge = createBridge({ config, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });

  await bridge.handleControl({ Type: 3, Command: 'messages', Text: 'a@s.whatsapp.net' });

  // Senza id la cronologia non si puo' deduplicare: si perde quel messaggio,
  // non si duplica tutta la chat.
  const frames = sent.map((packet) => decodeFrame(packet));
  assert.strictEqual(frames.length, 1);
  assert.strictEqual(frames[0].Text, 'con id');

  sent.length = 0;
  const failing = createBridge({
    config,
    gowa: { chatMessages: async () => { throw new Error('chat non trovata'); }, status: async () => ({ isConnected: true, isLoggedIn: true, jid: '39@x' }) },
    log: () => {},
    debug: () => {}
  });
  failing.setConnectedForTest();
  failing.addClientForTest({ write: (packet) => sent.push(packet) });

  await failing.handleControl({ Type: 3, Command: 'messages', Text: 'a@s.whatsapp.net' });

  const errors = sent.map((packet) => decodeFrame(packet));
  assert.strictEqual(errors.length, 1);
  assert.strictEqual(errors[0].Command, 'error');
  assert.strictEqual(errors[0].ChatId, 'a@s.whatsapp.net');
});

test('the messages command answers with an error when WhatsApp is not connected', async () => {
  const sent = [];
  const bridge = createBridge({ config: { messages: {} }, gowa: {}, log: () => {}, debug: () => {} });
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });

  await bridge.handleControl({ Type: 3, Command: 'messages', Text: 'a@s.whatsapp.net' });

  const frames = sent.map((packet) => decodeFrame(packet));
  assert.strictEqual(frames.length, 1);
  assert.strictEqual(frames[0].Command, 'error');
});
```

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL - the three new tests get no frames at all (`unknown command: messages` is the only visible effect).

- [ ] **Step 8: Implement the command**

In `WhatsappBridge/server.js`, add `mapHistoryMessage` to the `message-format` require:

```js
const { buildChatMessage, mapWebhookMessage, mapHistoryMessage } = require('./message-format');
```

add this function next to `sendChats`:

```js
  /**
   * Lo storico di una chat, come frame di messaggio.
   *
   * Non e' un frame per chat come `chats`: e' un frame per messaggio, quindi il
   * limite e' quanti frame passano. Ogni frame porta `IsHistory`, perche' l'app
   * deve disegnarlo ma non contarlo fra i non letti.
   */
  async function sendMessages(chatId) {
    if (!chatId) return;

    if (state.status !== 'connected') {
      sendControl({ command: 'error', chatId, text: 'WhatsApp is not connected: the chat history is unavailable.' });
      return;
    }

    const limits = (config && config.messages) || {};
    try {
      const list = await gowa.chatMessages(chatId, limits.limit || 50);
      let sent = 0;

      for (const raw of list) {
        const mapped = mapHistoryMessage(raw);
        // Senza l'id di WhatsApp l'app non puo' riconoscere un doppione, e la
        // stessa chat riempirebbe di copie: meglio un messaggio in meno.
        if (!mapped.id) continue;
        if (!mapped.chatId) mapped.chatId = chatId;

        sendControl(mapped);
        sent++;
      }

      logger('INFO', `history: ${sent} message(s) for ${chatId}`);
    } catch (err) {
      logger('ERR', `history failed for ${chatId}: ${err.message}`);
      sendControl({ command: 'error', chatId, text: `Chat history failed: ${err.message}` });
    }
  }
```

and add a case to `handleControl`, after the `chats` case:

```js
      case 'chats':
        await sendChats();
        break;
      case 'messages':
        // Il JID viaggia in `Text`, come per `login.code`: e' il campo che il
        // protocollo di controllo usa per il dato di accompagnamento, e cosi'
        // l'app non ha bisogno di un secondo tipo di frame in uscita.
        await sendMessages((msg.Text || '').trim());
        break;
```

- [ ] **Step 9: Run the tests to verify they pass**

Run: `cd WhatsappBridge && npm test`
Expected: PASS, 97 tests. (88 before: 5 mapping tests, 1 config test and 3 server tests are new.)

- [ ] **Step 10: Document the command**

In `WhatsappBridge/README.md`, in the protocol table after the `chats` row, add:

```
| app -> adapter | `messages` | `Text` = chat JID (up to `MESSAGES_LIMIT` messages, as ordinary frames marked `IsHistory`) |
```

and in the list above the table, after the `message.revoked` bullet, add:

```
- Opening a chat asks for its stored messages (`messages`), and the adapter answers
  with ordinary message frames marked `IsHistory`. The app inserts them in date order
  and keeps them out of the unread count and the toasts: they are not arriving now. A
  message whose media is not among the bytes the webhook delivered is sent as text -
  `[Image]`, `[Video]`, ... - because an empty bubble is worse than a word.
```

In `WhatsappBridge/README.it.md`, the same two places:

```
| app -> adapter | `messages` | `Text` = JID della chat (fino a `MESSAGES_LIMIT` messaggi, come frame normali marcati `IsHistory`) |
```

```
- Aprendo una chat l'app ne chiede i messaggi gia' in memoria al server (`messages`),
  e l'adapter risponde con frame di messaggio normali marcati `IsHistory`. L'app li
  inserisce in ordine di data e li tiene fuori dal conteggio dei non letti e dagli
  avvisi: non stanno arrivando adesso. Un messaggio il cui media non e' fra i byte che
  il webhook ha consegnato viene mandato come testo - `[Image]`, `[Video]`, ... -
  perche' un fumetto vuoto e' peggio di una parola.
```

In `WhatsappBridge/.env.example`, after the chat-list block, add:

```
# ── Storico di una chat (GOWA -> adapter -> app) ────────────────────────────
# Quanti messaggi caricare aprendo una chat. Una sola lettura, ma la risposta e'
# un frame per messaggio.
MESSAGES_LIMIT=50
```

- [ ] **Step 11: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappBridge README.md README.it.md
git commit -m "feat: answer a chat's message history on request"
```

---

### Task 2: The app asks for the history and puts it in order

The app has no way to ask, and if the frames arrived they would be treated as new: every old message would raise the unread count and a toast. So the frame needs a flag, and the flag needs a path through `DataService` that inserts without counting.

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs` (add `IsHistory`)
- Modify: `WhatsappApp/Services/DataService.cs` (the history path and the once-per-chat request)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (ask when the chat opens)
- Modify: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: the `IsHistory` frame field and the `messages` command from Task 1; `CommunicationService.SendControlAsync(string, string)`.
- Produces: `ChatMessage.IsHistory` (`bool`), `DataService.MarkHistoryRequested(string chatId)` (`bool`, true the first time for that chat in this process).

- [ ] **Step 1: Add the flag to the message**

In `WhatsappApp/Models/ChatMessage.cs`, add the backing field next to `private bool _isGroup;`:

```csharp
        private bool _isGroup;          // la chat e' un gruppo
        private bool _isHistory;        // messaggio vecchio, mandato aprendo la chat
```

and the property next to `IsGroup`:

```csharp
        /// <summary>Vero per i gruppi: GOWA non ha un avatar personale per loro.</summary>
        [DataMember]
        public bool IsGroup
        {
            get { return _isGroup; }
            set { _isGroup = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Cronologia: un messaggio vecchio, che l'adapter manda aprendo la chat.
        /// E' un messaggio normale e va disegnato come tale, ma non e' arrivato
        /// adesso: non conta come non letto e non alza nessun avviso.
        /// </summary>
        [DataMember]
        public bool IsHistory
        {
            get { return _isHistory; }
            set { _isHistory = value; OnPropertyChanged(); }
        }
```

- [ ] **Step 2: Route a history frame away from the new-message path**

In `WhatsappApp/Services/DataService.cs`, in `OnNetworkMessageReceived`, right after the system-frame guard:

```csharp
            // Ignore system/handshake messages
            if (message.Type == MessageType.System) return;

            // La cronologia e' testo, ma non e' arrivata adesso: entra in ordine
            // e senza contare. Tutto il resto di questo metodo e' per quello che
            // arriva adesso - anteprima della riga, non letti, avviso.
            if (message.IsHistory)
            {
                AddHistoryMessage(message);
                return;
            }
```

- [ ] **Step 3: Insert it in order, once**

In `WhatsappApp/Services/DataService.cs`, add next to `AddMessage`:

```csharp
        /// <summary>
        /// Inserisce un messaggio di cronologia al posto giusto.
        ///
        /// Al posto giusto e non in fondo: l'adapter non promette l'ordine - lo
        /// dice gia' chats.js - e riaprendo una chat i messaggi vecchi devono
        /// finire prima di quelli arrivati in questa sessione. Lo stesso id due
        /// volte non entra: e' la chiave per cui chiedere la cronologia non
        /// puo' duplicare quello che c'e' gia'.
        ///
        /// Non tocca la riga dell'elenco: l'anteprima e' l'ultimo messaggio
        /// vero, e un messaggio vecchio non deve riscriverla ne' spostare la
        /// conversazione in cima.
        /// </summary>
        private void AddHistoryMessage(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.ChatId)) return;

            var list = GetMessages(message.ChatId);

            if (!string.IsNullOrEmpty(message.Id))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] != null && list[i].Id == message.Id) return;
                }
            }

            int index = 0;
            while (index < list.Count
                && list[index] != null
                && list[index].Timestamp <= message.Timestamp)
            {
                index++;
            }

            list.Insert(index, message);
        }
```

- [ ] **Step 4: Ask once per chat**

In `WhatsappApp/Services/DataService.cs`, add the set to the fields at the top of the class:

```csharp
        private readonly Dictionary<string, ObservableCollection<ChatMessage>> _chatMessages;
        private readonly HashSet<string> _historyRequested = new HashSet<string>();
```

and this method next to `GetMessages`:

```csharp
        /// <summary>
        /// Dice se la cronologia di questa chat va chiesta adesso, e nel caso se
        /// ne ricorda: una volta per chat per sessione. Riaprire la stessa chat
        /// la mostra subito dalla memoria, invece di rifare il giro sul filo.
        /// </summary>
        public bool MarkHistoryRequested(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return false;
            return _historyRequested.Add(chatId);
        }
```

- [ ] **Step 5: Ask when the chat opens**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, in `OnNavigatedTo`, after the line that sets `DataService.Instance.ActiveChatId = contact.Id;`, add:

```csharp
                // La cronologia della chat: l'adapter risponde con i messaggi
                // vecchi, marcati IsHistory, e si chiede una volta per chat per
                // sessione. Senza questa richiesta una conversazione appena
                // aperta resta vuota finche' non arriva qualcosa di nuovo.
                if (CommunicationService.Instance.IsConnected
                    && DataService.Instance.MarkHistoryRequested(contact.Id))
                {
#pragma warning disable 4014
                    CommunicationService.Instance.SendControlAsync("messages", contact.Id);
#pragma warning restore 4014
                }
```

- [ ] **Step 6: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js`
Expected: `OK: 31 C# file(s) are C# 5 compatible.`, icons OK, `104 key(s)`, docs OK, framing OK.

If `check-csharp5.js` reports the `HashSet<string>` line, the file is missing `using System.Collections.Generic;`: add it.

- [ ] **Step 7: Build in the VM**

```bash
prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"
```

Then, in a separate call:

```bash
prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"
```

Then:

```bash
prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"
```

Expected: `COPIA=0`, `0 Error(s)`, no warnings, `Your package has been successfully created`.

- [ ] **Step 8: Document the history and its two limits**

In `README.md`, in the GOWA Adapter feature list, after the chat-list bullet, add:

```
- Loads a chat's stored messages when it is opened (`messages`, up to `MESSAGES_LIMIT`), as frames marked `IsHistory`: they are inserted in date order and stay out of the unread count and the toasts
```

In `README.it.md`, in the same place, add:

```
- carica i messaggi gia' in memoria aprendo una chat (`messages`, fino a `MESSAGES_LIMIT`), con frame marcati `IsHistory`: vengono inseriti in ordine di data e restano fuori dal conteggio dei non letti e dagli avvisi
```

Then in both files add a bullet to the `## Limitations` section. `README.md`, after the bullet about notifications:

```
- Opening a chat shows the recent messages the server already has. Older ones are not requested from the phone, and a photo or a video in that history is shown as a word (`[Image]`, `[Video]`): its bytes are not among the ones the webhook delivered.
```

`README.it.md`, in the corresponding place:

```
- Aprendo una chat si vedono i messaggi recenti che il server ha gia'. I piu' vecchi non vengono richiesti al telefono, e una foto o un video di quella cronologia si vedono come una parola (`[Image]`, `[Video]`): i suoi byte non sono fra quelli che il webhook ha consegnato.
```

- [ ] **Step 9: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappApp README.md README.it.md
git commit -m "feat: load a chat's history when it is opened"
```

---

### Task 3: Remember the lesson, record the run, and verify

The reason this was missing is worth writing down: the app had a receive path and no read path, and nothing in the docs said a conversation was expected to be empty on open.

**Files:**
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-27-chat-history-on-open.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Add the rule to the maintain skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in the gotchas list, add:

```markdown
- **The app's memory is not the conversation.** Until this was fixed, opening a chat
  showed only what had arrived while the app was running, because nothing ever asked
  the adapter for what was already on the server: a receive path is not a read path.
  `ChatPage.OnNavigatedTo` asks (`messages`, with the chat JID in `Text`) once per chat
  per process - `DataService.MarkHistoryRequested` - and the adapter answers with
  ordinary message frames carrying `IsHistory`. The flag matters: without it every old
  message would count as unread and raise a toast, and the frames must be inserted by
  date, because the adapter does not promise an order (see `newestMessage` in
  `WhatsappBridge/chats.js`). Dedup is by WhatsApp's own message id, so a chat can be
  requested again without duplicating what is already on screen; a message the server
  has no id for is dropped rather than risked twice.
```

- [ ] **Step 2: Add the on-device checks**

In `.agents/skills/test-the-app/SKILL.md`, in the on-device checklist, add:

```markdown
27. Open a chat that already has messages, right after starting the app: the
    conversation appears, oldest first, with the times and the sender names, and the
    page ends at the bottom. An empty chat here means `messages` was never answered.
28. An old photo in that history shows a bubble with `[Image]`, not an empty bubble.
29. Open that chat again: no duplicates appear, and no toast fires for the history
    when it arrives. The unread number on the chat list must not move while a chat's
    history loads.
```

- [ ] **Step 3: Record what execution changed**

Run the full fast gate:

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/qr-term.js --self-test
cd WhatsappBridge && npm test
cd .. && node --test "tools/test/**/*.test.js"
```

Then append to this plan document a `## What execution changed about this plan` section listing, honestly, every place the plan turned out to be wrong or incomplete while it was executed - the exact error and the exact fix - including the final adapter test count. Do not write "no changes" unless the gate output in this session proves it.

- [ ] **Step 4: Commit and push**

```bash
git add .agents docs
git commit -m "docs: record that the app's memory is not the conversation"
git push origin master
```

---

## What only the phone can prove

1. A chat with existing messages shows them on open (Task 2, step 5).
2. Old media shows as a word instead of an empty bubble (Task 1, step 3).
3. No toasts and no unread movement while a history loads (Task 2, step 2).
4. No duplicates on reopening the same chat (Task 2, step 3).

## Deliberate non-goals

- **Asking the phone for older messages.** GOWA has `POST /chat/:chat_jid/history` for that, anchored on the oldest stored message, and it is the right way to page upwards later. It needs a scroll-to-top trigger in the app, which is a separate change.
- **Downloading media of old messages.** `GET /message/:message_id/download` exists, but it is one request and one payload per image, for messages that are already behind you. The label is the honest cheap answer.
- **Refetching after a reconnect.** The "once per chat per process" rule means a chat opened before a dropped connection keeps whatever it has; reopening the app refetches. Messages received while disconnected were already lost before this change.
