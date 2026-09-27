# Startup, Unread and Media Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The chat list is there when the app opens (cached, then refreshed), a chat with unread messages says so, a shared file no longer crashes the app and videos can be sent, and an image someone sent can actually be seen and opened full screen.

**Architecture:** Four independent fixes that share the media path. (1) The app caches the last chat list and asks for a fresh one every time it connects, instead of only when a page happens to be shown after the socket is already up. (2) The adapter - the only process that is awake while the phone is off - counts the messages each chat has not read and sends that count on the chat row. (3) The share target reports its state in the order WP8.1 requires, and the manifest and picker learn what a video is. (4) An attachment travels in several frames (the frame ceiling is 8 MiB, a video is far bigger), and a history image is downloaded on demand through GOWA's `download` route and shown at full screen when tapped.

**Tech Stack:** Node 18.13+ (adapter, `node:test`), C# 5 on WP8.1 WinRT (app), MSBuild 12.0 in the Parallels VM, GOWA v9.5.0.

## Global Constraints

- Adapter code is CommonJS, `'use strict';`, `node:test` + `node:assert`. Run with `cd WhatsappBridge && npm test`.
- The app is C# 5 only: no interpolated strings, no `?.`, no expression-bodied members, no auto-property initializers, no `nameof`. `node tools/check-csharp5.js` enforces this and the WP8.1 API surface.
- All runtime text (logs, diagnostics, adapter `text:` frames) is English. Comments may be Italian, matching the surrounding code.
- The app is the only place with a UI: every user-visible string lives in `WhatsappApp/Strings/en-US/Resources.resw` **and** `WhatsappApp/Strings/it-IT/Resources.resw`, same key, and is reached through `x:Uid` or `Loc.Get`. `node tools/check-resw.js --strict` fails on a missing, duplicated or unused key.
- No icon font. A new icon is an inline `<Path.Data><PathGeometry>` with an `<!-- IconX -->` comment. This plan adds no icon.
- The adapter is the only place that knows GOWA's HTTP shapes; the app only sees adapter frames.
- Documentation comes in pairs that stay mirror images: `README.md` + `README.it.md`, `WhatsappBridge/README.md` + `WhatsappBridge/README.it.md`. Same heading count, order and level. Root pair keeps `## Disclosure` last. No emoji in any `.md`.
- Fast gate after every task: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && node --test "tools/test/**/*.test.js"`, then `cd WhatsappBridge && npm test`.
- Build gate (Parallels VM), three separate tool calls, never chained with `&&`:
  1. `prlctl exec "Windows 11" cmd /c "if exist C:\Temp\wp81 rmdir /s /q C:\Temp\wp81"`
  2. `prlctl exec "Windows 11" cmd /c "robocopy C:\Mac\Home\Documents\WhatsappForWP C:\Temp\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP & echo COPIA=%errorlevel%"`
  3. `prlctl exec "Windows 11" cmd /c "cd /d C:\Temp\wp81 && C:\PROGRA~2\MSBuild\12.0\Bin\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=x86 /nologo /v:m"`
  Expected: `COPIA=0`, `0 Error(s)`, `0 Warning(s)`.
- Commit messages are English, `type: short imperative`, and must not contain an apostrophe. One commit per task, then push.

---

### Task 1: The chat list is there when the app opens

Today `ChatsPage.OnNavigatedTo` asks for the list only if `IsConnected && Contacts.Count == 0`, and on a cold start the socket is not up yet, so nothing is asked and the list stays empty until the user walks through the settings page and back. There is also nothing on the phone to show while the answer is in flight.

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs` (add `UnreadCount`)
- Create: `WhatsappApp/Services/ChatCache.cs`
- Modify: `WhatsappApp/Services/DataService.cs` (load the cache, remember the fresh rows, write them back)
- Modify: `WhatsappApp/Pages/ChatsPage.xaml.cs` (ask on show and when WhatsApp becomes connected)
- Modify: `WhatsappApp/WhatsappApp.csproj` (register `ChatCache.cs`)
- Modify: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: the `chat` / `chats.done` control frames the adapter already sends, `CommunicationService.Instance.WhatsAppState`, `CommunicationService.Instance.ControlMessageReceived`, `DataService.Instance.Contacts`.
- Produces: `ChatMessage.UnreadCount` (`int`, `[DataMember]`); `ChatCache.LoadAsync()` returning `Task<List<ChatMessage>>` and `ChatCache.SaveAsync(List<ChatMessage>)` returning `Task`; `DataService.Instance` applies a cached list before the network answers.

- [ ] **Step 1: Give the row a number to remember**

In `WhatsappApp/Models/ChatMessage.cs`, add the backing field next to `private bool _isHistory;`:

```csharp
        private bool _isHistory;        // messaggio vecchio, mandato aprendo la chat
        private int _unreadCount;       // riga dell'elenco chat: quanti non letti
```

and the property next to `IsHistory`:

```csharp
        /// <summary>
        /// Quanti messaggi di questa conversazione non sono ancora stati letti.
        /// Non e' un dato del messaggio: e' un dato della riga dell'elenco chat,
        /// e l'adapter lo conta perche' e' l'unico che vede i messaggi arrivati
        /// mentre il telefono era spento.
        /// </summary>
        [DataMember]
        public int UnreadCount
        {
            get { return _unreadCount; }
            set { _unreadCount = value; OnPropertyChanged(); }
        }
```

- [ ] **Step 2: Write the cache**

Create `WhatsappApp/Services/ChatCache.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>Il file su disco: un solo campo, l'elenco delle righe.</summary>
    [DataContract]
    internal class ChatCacheFile
    {
        [DataMember]
        public List<ChatMessage> Chats { get; set; }
    }

    /// <summary>
    /// L'elenco chat dell'ultima sessione, tenuto sul telefono.
    ///
    /// Perche' esiste: all'avvio la connessione non c'e' ancora, e l'adapter
    /// risponde alla richiesta dell'elenco solo quando WhatsApp e' collegato.
    /// Senza questa copia l'app si apre su un elenco vuoto per i secondi che
    /// servono, e sembra che non sia successo niente.
    ///
    /// Non e' una verita': e' una fotografia. Appena il server risponde, ogni
    /// riga viene sostituita da quella vera (vedi DataService.ApplyChat). Senza
    /// i byte dell'avatar la copia resta piccola, e l'immagine arriva con il
    /// primo aggiornamento.
    /// </summary>
    public static class ChatCache
    {
        private const string FileName = "chats.json";

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(ChatCacheFile));

        /// <summary>Le righe salvate. Mai un'eccezione: al primo avvio non c'e' file.</summary>
        public static async Task<List<ChatMessage>> LoadAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string json = await FileIO.ReadTextAsync(file);
                if (!string.IsNullOrEmpty(json))
                {
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var cache = Serializer.ReadObject(stream) as ChatCacheFile;
                        if (cache != null && cache.Chats != null) return cache.Chats;
                    }
                }
            }
            catch (Exception ex)
            {
                // Primo avvio, o cache scritta da una versione diversa.
                Diag.Failed("ChatCache.Load", ex);
            }
            return new List<ChatMessage>();
        }

        /// <summary>
        /// Scrive quello che il server ha appena mandato. Senza i byte
        /// dell'avatar: quelli si rifanno a ogni connessione, e un file da
        /// megabyte per un elenco che serve solo a riempire i primi secondi non
        /// e' un buon cambio.
        /// </summary>
        public static async Task SaveAsync(List<ChatMessage> chats)
        {
            if (chats == null || chats.Count == 0) return;

            try
            {
                var slim = new List<ChatMessage>();
                for (int i = 0; i < chats.Count; i++) slim.Add(Slim(chats[i]));

                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, new ChatCacheFile { Chats = slim });
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                // Una cache che non si scrive non e' un guasto da mostrare:
                // l'elenco arriva comunque dal server.
                Diag.Failed("ChatCache.Save", ex);
            }
        }

        /// <summary>La copia di una riga con i soli campi che l'elenco disegna.</summary>
        private static ChatMessage Slim(ChatMessage row)
        {
            return new ChatMessage
            {
                ChatId = row.ChatId,
                SenderName = row.SenderName,
                Text = row.Text,
                IsGroup = row.IsGroup,
                UnreadCount = row.UnreadCount,
                Timestamp = row.Timestamp
            };
        }
    }
}
```

- [ ] **Step 3: Register the file**

In `WhatsappApp/WhatsappApp.csproj`, next to the other service files (after `<Compile Include="Services\AttachmentInbox.cs" />`), add:

```xml
    <Compile Include="Services\ChatCache.cs" />
```

- [ ] **Step 4: Load the cache and remember the fresh rows**

In `WhatsappApp/Services/DataService.cs`, add these fields next to `_historyRequested`:

```csharp
        private readonly HashSet<string> _historyRequested = new HashSet<string>();

        // Le righe dell'elenco chat come le ha mandate il server l'ultima volta
        // (per la cache) e quelle che stanno arrivando adesso.
        private readonly List<ChatMessage> _chatRows = new List<ChatMessage>();
        private readonly List<ChatMessage> _freshChatRows = new List<ChatMessage>();
```

In `Start()`, after the two `CommunicationService` subscriptions, add:

```csharp
            // La copia dell'ultima sessione: si mostra adesso, prima che la
            // connessione esista. Il server la sostituira' con quella vera.
#pragma warning disable 4014
            LoadCachedChatsAsync();
#pragma warning restore 4014
```

and add the method next to `Start`:

```csharp
        /// <summary>
        /// Riempe l'elenco con l'ultima fotografia sul telefono. Ogni riga passa
        /// da ApplyChat, come se arrivasse dal server: cosi' non ci sono due
        /// strade che possono divergere.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCachedChatsAsync()
        {
            var cached = await ChatCache.LoadAsync();
            for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);
            NotificationService.SetUnread(TotalUnread());
        }
```

- [ ] **Step 5: Apply the count and record the row**

In `WhatsappApp/Services/DataService.cs`, in `ApplyChat`, after the block that updates the avatar, add:

```csharp
            // Il numero dei non letti e' una proprieta' della riga, non del
            // messaggio: arriva dal server, che e' l'unico sveglio mentre il
            // telefono e' spento (vedi server.js, unreadByChat).
            contact.UnreadCount = message.UnreadCount;
            RememberChatRow(message);
```

then add the two helpers next to `ApplyChat`:

```csharp
        /// <summary>La riga di questo aggiornamento, tenuta da parte per la cache.</summary>
        private void RememberChatRow(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.ChatId)) return;

            for (int i = 0; i < _freshChatRows.Count; i++)
            {
                if (_freshChatRows[i].ChatId == message.ChatId)
                {
                    _freshChatRows[i] = message;
                    return;
                }
            }
            _freshChatRows.Add(message);
        }

        /// <summary>
        /// La lista e' finita di arrivare: quella che resta diventa la copia
        /// sul telefono, e il gruppo appena arrivato riparte da zero.
        /// </summary>
        private void RememberChatList()
        {
            if (_freshChatRows.Count == 0) return;

            _chatRows.Clear();
            _chatRows.AddRange(_freshChatRows);
            _freshChatRows.Clear();

#pragma warning disable 4014
            ChatCache.SaveAsync(_chatRows);
#pragma warning restore 4014
        }
```

and in `OnControlMessageReceived`, change the `chats.done` case:

```csharp
                case "chats.done":
                    RememberChatList();
                    RaiseChatListCompleted();
                    break;
```

- [ ] **Step 6: Ask for the list when the app can actually get one**

In `WhatsappApp/Pages/ChatsPage.xaml.cs`, in `OnNavigatedTo`, replace the block that begins with `if (CommunicationService.Instance.IsConnected && DataService.Instance.Contacts.Count == 0)` (comment and all) with:

```csharp
            // La richiesta si rifa' a ogni ingresso e a ogni passaggio a
            // connected, invece di aspettare che qualcuno apra le impostazioni:
            // all'avvio la connessione non c'e' ancora, e la lista arrivava solo
            // se l'utente tornava qui dopo averla aperta.
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
            RequestChats();
```

In `OnNavigatedFrom`, add the matching unsubscribe:

```csharp
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            AttachmentInbox.Ready -= OnAttachmentReady;
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
```

and add these two methods to the class (next to `SettingsButton_Click`):

```csharp
        /// <summary>
        /// WhatsApp e' passato a connected adesso. La richiesta fatta
        /// all'ingresso non poteva avere risposta (l'adapter risponde "non
        /// collegato" finche' il login non e' finito), e questa e' la sola cosa
        /// che fa comparire l'elenco senza toccare niente.
        /// </summary>
        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null || message.Command != "state") return;
            RequestChats();
        }

        /// <summary>
        /// Chiede l'elenco delle conversazioni. Solo se c'e' qualcuno che puo'
        /// rispondere: con WhatsApp non collegato l'adapter risponde con un
        /// errore e nessuna riga.
        /// </summary>
        private void RequestChats()
        {
            if (!CommunicationService.Instance.IsConnected) return;
            if (CommunicationService.Instance.WhatsAppState != "connected") return;

#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("chats");
#pragma warning restore 4014
        }
```

- [ ] **Step 7: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js`
Expected: `OK: 33 C# file(s) are C# 5 compatible.`, icons OK, `104 key(s)`, docs OK, framing OK, tile OK, memory OK, actions OK.

If `check-csharp5.js` reports `Task` or `List`, the file is missing `using System.Threading.Tasks;` / `using System.Collections.Generic;` - `DataService.cs` already has both.

- [ ] **Step 8: Build in the VM**

Run the three build-gate commands from Global Constraints.
Expected: `COPIA=0`, `0 Error(s)`, `0 Warning(s)`.

- [ ] **Step 9: Document the cache**

In `README.md`, in the GOWA Adapter feature list, after the chat-list bullet, add:

```
- Keeps the last chat list on the phone: the list is on screen while the connection is still coming up, and is refreshed as soon as WhatsApp reports itself connected
```

In `README.it.md`, in the same place, add:

```
- tiene sul telefono l'ultimo elenco delle conversazioni: l'elenco e' a schermo mentre la connessione sta ancora arrivando, e si aggiorna appena WhatsApp si dichiara collegato
```

- [ ] **Step 10: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappApp README.md README.it.md
git commit -m "fix: show the chat list at startup and refresh it on connect"
```

---

### Task 2: A chat that has unread messages says so

GOWA's `/chats` has no unread count, and a message that arrives while the phone app is closed is delivered to the adapter's webhook and to nobody else. The adapter is therefore the only place that can count it.

**Files:**
- Modify: `WhatsappBridge/message-format.js` (`buildChatMessage`)
- Modify: `WhatsappBridge/server.js` (`unreadByChat`, `sendChats`, `handleControl`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (tell the adapter when a chat has been read)
- Test: `WhatsappBridge/test/message-format.test.js`, `WhatsappBridge/test/server.test.js`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`, `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `buildChatMessage(fields)`, `sendControl(fields)`, `handleWebhookEvent(event)`, `CommunicationService.SendControlAsync(string, string)`.
- Produces: the frame field `UnreadCount` on a `chat` row; the control command `read`, whose `Text` is the chat JID and which sets that chat's unread count to zero.

- [ ] **Step 1: Write the failing mapping test**

Append to `WhatsappBridge/test/message-format.test.js`:

```js
test('buildChatMessage porta UnreadCount solo quando la riga lo dichiara', () => {
  const row = buildChatMessage({ command: 'chat', chatId: 'a@s.whatsapp.net', unreadCount: 3 });
  assert.strictEqual(row.UnreadCount, 3);

  // Zero non si scrive: e' il valore che un campo assente avrebbe comunque.
  const empty = buildChatMessage({ command: 'chat', chatId: 'a@s.whatsapp.net', unreadCount: 0 });
  assert.strictEqual(Object.prototype.hasOwnProperty.call(empty, 'UnreadCount'), true);
  assert.strictEqual(empty.UnreadCount, 0);

  // Un messaggio normale non porta il conteggio.
  const live = buildChatMessage({ text: 'ciao' });
  assert.strictEqual(Object.prototype.hasOwnProperty.call(live, 'UnreadCount'), false);
});
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `cd WhatsappBridge && node --test test/message-format.test.js`
Expected: FAIL - `row.UnreadCount` is `undefined`.

- [ ] **Step 3: Carry the field**

In `WhatsappBridge/message-format.js`, in `buildChatMessage`, next to the other conditional fields (after `if (f.avatarData) msg.AvatarData = f.avatarData;`), add:

```js
  // Riga dell'elenco chat: quanti messaggi di questa conversazione non sono
  // ancora stati letti. Lo conta l'adapter, perche' GOWA non lo dice e perche'
  // i messaggi arrivati col telefono spento non li vede nessun altro.
  if (typeof f.unreadCount === 'number') msg.UnreadCount = f.unreadCount;
```

Run: `cd WhatsappBridge && node --test test/message-format.test.js`
Expected: PASS.

- [ ] **Step 4: Write the failing server test**

Append to `WhatsappBridge/test/server.test.js`:

```js
test('un messaggio in arrivo conta come non letto, e read lo azzera', async () => {
  const sent = [];
  const gowa = {
    chats: async () => [{ jid: 'a@s.whatsapp.net', name: 'Anna' }],
    chatMessages: async () => [],
    avatar: async () => null
  };
  const bridge = createBridge({
    config: { chats: { limit: 5, avatars: false } },
    gowa,
    log: () => {},
    debug: () => {}
  });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleWebhookEvent({
    event: 'message',
    payload: {
      id: 'M1',
      chat_id: 'a@s.whatsapp.net',
      from: 'a@s.whatsapp.net',
      body: 'ciao',
      timestamp: '2026-09-27T08:00:00Z'
    }
  });

  sent.length = 0;
  await bridge.handleControl({ Type: 3, Command: 'chats', SenderName: 'test' });
  const rows = sent.filter((f) => f.Command === 'chat');
  assert.strictEqual(rows.length, 1);
  assert.strictEqual(rows[0].UnreadCount, 1);

  await bridge.handleControl({ Type: 3, Command: 'read', Text: 'a@s.whatsapp.net' });

  sent.length = 0;
  bridge.resetChatsCacheForTest();
  await bridge.handleControl({ Type: 3, Command: 'chats' });
  const after = sent.filter((f) => f.Command === 'chat');
  assert.strictEqual(after[0].UnreadCount, 0);
});
```

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL - the first `UnreadCount` assertion is `undefined`.

- [ ] **Step 5: Count and clear**

In `WhatsappBridge/server.js`, inside `createBridge`, next to the caches, add:

```js
  // Quanti messaggi di ogni chat non sono ancora stati letti. Vive qui e non in
  // GOWA: il suo elenco chat non ha questo campo, e un messaggio che arriva col
  // telefono spento non lo vede nessun altro. Si azzera con il comando `read`.
  const unreadByChat = new Map();
```

In `handleWebhookEvent`, right after `const fields = mapWebhookMessage(event.payload || {});` and its `if (!fields) return;`, add:

```js
    // Un messaggio che non e' mio e' arrivato adesso: la sua chat ha una cosa
    // in piu' da leggere, anche se l'app non e' collegata in questo momento.
    unreadByChat.set(fields.chatId, (unreadByChat.get(fields.chatId) || 0) + 1);
```

In `sendChats`, in the loop that sends a row, add the count after the `avatarData` field:

```js
          isGroup: row.isGroup,
          avatarData: row.avatar || undefined,
          unreadCount: unreadByChat.get(row.chatId) || 0
```

In `handleControl`, after the `messages` case, add:

```js
      case 'read':
        // L'app ha mostrato quella conversazione: da adesso non ha piu' niente
        // da leggere. La chat non deve esistere per forza nell'elenco.
        unreadByChat.delete((msg.Text || '').trim());
        break;
```

Run: `cd WhatsappBridge && npm test`
Expected: PASS.

- [ ] **Step 6: Tell the adapter when a chat has been read**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, add this method next to `OnMessageReceived`:

```csharp
        /// <summary>
        /// Questa conversazione e' stata mostrata: il numero si azzera qui e sul
        /// server. Il secondo pezzo non e' un dettaglio: l'adapter conta ogni
        /// messaggio in arrivo, anche quelli che l'utente sta guardando, quindi
        /// senza dirglielo il numero tornerebbe ricomparire al prossimo
        /// aggiornamento dell'elenco.
        /// </summary>
        private void MarkRead()
        {
            DataService.Instance.ClearUnread(_contact.Id);

            if (!CommunicationService.Instance.IsConnected) return;
#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("read", _contact.Id);
#pragma warning restore 4014
        }
```

Then replace the two existing calls to `ClearUnread` with `MarkRead()`:

- in `OnNavigatedTo`, change `DataService.Instance.ClearUnread(contact.Id);` to `MarkRead();`
- in `OnMessageReceived`, change `DataService.Instance.ClearUnread(_contact.Id);` to `MarkRead();`

- [ ] **Step 7: Run the guards and build**

Run the fast gate from Global Constraints, then the three build-gate commands.
Expected: guards all OK (adapter tests now include the new ones), `COPIA=0`, `0 Error(s)`, `0 Warning(s)`.

- [ ] **Step 8: Document the count**

In `WhatsappBridge/README.md`, in the protocol table after the `chats` row, add:

```
| app -> adapter | `read` | `Text` = chat JID (that conversation has now been read; clears its unread count) |
```

In `WhatsappBridge/README.it.md`, at the same place:

```
| app -> adapter | `read` | `Text` = JID della chat (quella conversazione e' stata letta; azzera il suo conteggio dei non letti) |
```

In both adapter READMEs, in the chat-list bullet, add a clause about the count. `WhatsappBridge/README.md`:

```
  Each row carries the conversation name, its last message, when it arrived, its
  avatar and how many messages it has not read (`UnreadCount`). That count is kept
  by the adapter, because GOWA's chat list has no such field and because a message
  that arrives while the phone is off reaches the adapter's webhook and nobody
  else; the app clears it with `read` when the conversation is shown. The count
  lives in memory: restarting the adapter starts it again from zero.
```

`WhatsappBridge/README.it.md`:

```
  Ogni riga porta il nome della conversazione, l'ultimo messaggio, quando e'
  arrivato, l'avatar e quanti messaggi non ha ancora letto (`UnreadCount`). Quel
  conteggio lo tiene l'adapter, perche' l'elenco chat di GOWA non ha questo campo e
  perche' un messaggio che arriva col telefono spento raggiunge il webhook
  dell'adapter e nessun altro; l'app lo azzera con `read` quando la conversazione
  viene mostrata. Il conteggio vive in memoria: riavviare l'adapter lo riparte da
  zero.
```

In `README.md`, in the Limiti/Limitations section, after the notifications bullet, add:

```
- A chat's unread number is kept by the adapter, in memory, and is cleared when the conversation is opened in the app. Restarting the adapter starts the count again from zero, and messages that arrive while neither the app nor the adapter is running are not counted.
```

In `README.it.md`, in the corresponding place:

```
- Il numero dei non letti di una chat lo tiene l'adapter, in memoria, e si azzera quando la conversazione viene aperta nell'app. Riavviare l'adapter fa ripartire il conteggio da zero, e i messaggi arrivati mentre non gira ne' l'app ne' l'adapter non vengono contati.
```

- [ ] **Step 9: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappBridge WhatsappApp README.md README.it.md
git commit -m "feat: count a chat unread messages in the adapter"
```

---

### Task 3: Sharing a file stops crashing

`ShareOperation` has an order and a lifetime: `ReportStarted()` comes first, `ReportDataRetrieved()` when the bytes are in, `ReportCompleted()` (or `ReportError()`) at the end, and the asynchronous work after the activation handler returns needs a deferral, because the operation is not agile. The current code calls `ReportCompleted()` in a `finally` without ever calling `ReportStarted()`, and does its file I/O with no deferral. The manifest also declares only image types, and the picker offers only images.

**Files:**
- Modify: `WhatsappApp/App.xaml.cs` (`AcceptShareAsync`)
- Modify: `WhatsappApp/Package.appxmanifest` (video file types)
- Modify: `WhatsappApp/Services/ImagePickerService.cs` (video extensions)
- Modify: `WhatsappApp/Services/AttachmentInbox.cs` (video MIME types and the kind of a file)
- Modify: `README.md`, `README.it.md`

**Interfaces:**
- Consumes: `AttachmentInbox.PutAsync(StorageFile, string)`, `AttachmentInbox.PutBytes(byte[], string, string, string)`, `DataReader`.
- Produces: `AttachmentInbox.KindName(string mimeType, string fileName)` returning `"image"` or `"video"` (used by Task 4); a share operation that always reports its own state in order.

- [ ] **Step 1: Report the share in the order WP8.1 requires**

In `WhatsappApp/App.xaml.cs`, replace the whole body of `AcceptShareAsync` (from `try {` to the closing brace of the method) with:

```csharp
            // Lo stato della condivisione si racconta nell'ordine che WP8.1 si
            // aspetta: started, poi (quando i byte ci sono) data retrieved, poi
            // completed. Un ReportCompleted senza ReportStarted lascia l'app
            // chiamante in attesa e fa cadere il processo, ed e' quello che
            // succedeva condividendo una foto.
            //
            // Il deferral serve perche' ShareOperation non e' agile: dopo
            // l'uscita da questo gestore l'operazione non e' piu' valida, e
            // leggerne i dati senza tenerla in vita la fa esplodere.
            Windows.Foundation.Deferral deferral = null;
            try
            {
                operation.ReportStarted();
                deferral = operation.GetDeferral();

                var data = operation.Data;
                if (data != null)
                {
                    if (data.Contains(StandardDataFormats.StorageItems))
                    {
                        var items = await data.GetStorageItemsAsync();
                        for (int i = 0; i < items.Count; i++)
                        {
                            var file = items[i] as StorageFile;
                            if (file == null) continue;

                            await AttachmentInbox.PutAsync(file, null);
                            break;
                        }
                    }
                    else if (data.Contains(StandardDataFormats.Bitmap))
                    {
                        var reference = await data.GetBitmapAsync();
                        using (var stream = await reference.OpenReadAsync())
                        {
                            using (var reader = new DataReader(stream))
                            {
                                uint size = (uint)stream.Size;
                                await reader.LoadAsync(size);
                                var buffer = new byte[size];
                                reader.ReadBytes(buffer);
                                AttachmentInbox.PutBytes(buffer, "shared.png", "image/png", null);
                            }
                        }
                    }
                }

                operation.ReportDataRetrieved();
                operation.ReportCompleted();
            }
            catch (Exception ex)
            {
                Diag.Failed("App/share", ex);
                try
                {
                    // Una condivisione fallita va detta: senza questo l'app
                    // chiamante resta a girare a vuoto per sempre.
                    operation.ReportError(Loc.Get("App_ShareFailed",
                        "The shared file could not be read."));
                }
                catch (Exception reportEx)
                {
                    Diag.Failed("App/share-report", reportEx);
                }
            }
            finally
            {
                if (deferral != null)
                {
                    try { deferral.Complete(); }
                    catch (Exception ex) { Diag.Failed("App/share-deferral", ex); }
                }
            }
```

- [ ] **Step 2: Give the app the new string**

In both `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw`, add after the `ChatPage_ImageError` entry:

```xml
  <data name="App_ShareFailed" xml:space="preserve">
    <value>The shared file could not be read.</value>
  </data>
```

The Italian file gets the same key with `<value>Il file condiviso non e stato letto.</value>`.

- [ ] **Step 3: Declare videos in the share target**

In `WhatsappApp/Package.appxmanifest`, inside `<SupportedFileTypes>`, after `<FileType>.bmp</FileType>`, add:

```xml
                        <FileType>.mp4</FileType>
                        <FileType>.mov</FileType>
                        <FileType>.3gp</FileType>
                        <FileType>.avi</FileType>
                        <FileType>.mkv</FileType>
                        <FileType>.webm</FileType>
```

and update the comment above the `<Extension>` to say images and videos.

- [ ] **Step 4: Offer videos in the picker**

In `WhatsappApp/Services/ImagePickerService.cs`, after `picker.FileTypeFilter.Add(".bmp");`, add:

```csharp
            // Un video non e' un'immagine, ma arriva dallo stesso pulsante: il
            // tipo lo dice il file, e l'invio sceglie la strada giusta.
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".3gp");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".webm");
```

- [ ] **Step 5: Turn an extension into the right kind**

In `WhatsappApp/Services/AttachmentInbox.cs`, replace `MimeFor` and add the kind helper:

```csharp
        /// <summary>Il tipo MIME di un'estensione, come lo manda WhatsApp.</summary>
        private static string MimeFor(string extension)
        {
            string value = (extension ?? "").ToLower();
            if (value == ".png") return "image/png";
            if (value == ".gif") return "image/gif";
            if (value == ".bmp") return "image/bmp";
            if (value == ".mp4") return "video/mp4";
            if (value == ".mov") return "video/quicktime";
            if (value == ".3gp") return "video/3gpp";
            if (value == ".avi") return "video/x-msvideo";
            if (value == ".mkv") return "video/x-matroska";
            if (value == ".webm") return "video/webm";
            return "image/jpeg";
        }

        /// <summary>
        /// "image" o "video": la parola che l'adapter e l'app usano per decidere
        /// come spedire e come disegnare. Il tipo MIME puo' mancare (una
        /// bitmap condivisa), quindi la parola si ricava anche dall'estensione.
        /// </summary>
        public static string KindName(string mimeType, string fileName)
        {
            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return "video";
            if (mime.StartsWith("image/")) return "image";

            string name = (fileName ?? "").ToLower();
            if (name.EndsWith(".mp4") || name.EndsWith(".mov") || name.EndsWith(".3gp")
                || name.EndsWith(".avi") || name.EndsWith(".mkv") || name.EndsWith(".webm"))
            {
                return "video";
            }
            return "image";
        }
```

- [ ] **Step 6: Update the attach tooltip**

In both `.resw` files, change the `ChatPage_AttachTooltip` value from `Attach an image` to `Attach a photo or a video` (English) and from `Attach an image` to `Allega una foto o un video` (Italian file).

- [ ] **Step 7: Run the guards and build**

Run the fast gate from Global Constraints, then the three build-gate commands.
Expected: `104 key(s)` becomes `105 key(s)` (the new `App_ShareFailed`), everything else OK, `COPIA=0`, `0 Error(s)`, `0 Warning(s)`.

- [ ] **Step 8: Document the share behaviour**

In `README.md`, in the Limiti/Limitations section, after the unread bullet, add:

```
- A file shared from another app is read at the moment the share is handed over, because the app can be terminated while the picker or the sharing app is open. Files that are very large are still held in memory to be sent, so a full-length video may not fit on a phone with 512 MB.
```

In `README.it.md`, in the corresponding place:

```
- Un file condiviso da un'altra app viene letto nel momento in cui la condivisione viene consegnata, perche' l'app puo' essere terminata mentre il selettore o l'app che condivide sono aperti. I file molto grandi vengono comunque tenuti in memoria per essere spediti, quindi un video di lunghezza intera puo' non starci su un telefono da 512 MB.
```

- [ ] **Step 9: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappApp README.md README.it.md
git commit -m "fix: report a share in order and accept videos"
```

---

### Task 4: Sending a photo or a video, in pieces

A frame is capped at 8 MiB on both ends, and base64 adds a third: about 6 MB of file is the ceiling for a single frame. A video is bigger than that, and so is a modern camera photo. The attachment therefore travels as a small sequence of control frames, and the adapter decides from the MIME type whether GOWA's `/send/image`, `/send/video` or `/send/file` is the right door. Today the adapter always calls `sendImage`, which is why a video goes out as a broken image.

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs` (`MessageType.Video`, the transfer fields, `MediaType`, `IsMedia`, `MediaTypeText`)
- Modify: `WhatsappApp/Services/CommunicationService.cs` (`NewControlFrame`, `SendControlAsync`, `SendMediaBeginAsync`, `SendMediaChunkAsync`, `SendMediaEndAsync`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`SendAttachmentAsync` replaces `SendImageMessage`)
- Modify: `WhatsappBridge/gowa-client.js` (`postMedia`, `sendVideo`, `sendFile`)
- Modify: `WhatsappBridge/server.js` (`mediaTransfers`, `sendMediaToGowa`, `mediaKindOf`, `sendOutgoing`, `handleControl`)
- Modify: `WhatsappBridge/message-format.js` (`mediaFromPayload` for video)
- Test: `WhatsappBridge/test/gowa-client.test.js`, `WhatsappBridge/test/server.test.js`, `WhatsappBridge/test/message-format.test.js`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: `AttachmentInbox.KindName`, `_selectedImageBase64/_selectedImageFileName/_selectedImageMimeType`, `AttachmentInbox.MimeFor`, `MessageType`.
- Produces: `MessageType.Video` (numeric value 4); `[DataMember]` fields `MediaTransferId` (`string`), `MediaChunkIndex` (`int`), `MediaChunkTotal` (`int`), `MediaType` (`string`); control commands `media.begin` / `media.chunk` / `media.end`; `GowaClient#sendVideo` and `GowaClient#sendFile`.

- [ ] **Step 1: Write the failing client tests**

Append to `WhatsappBridge/test/gowa-client.test.js`:

```js
test('sendVideo posts the video field, and sendFile the file field', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://g',
    fetchImpl: async (url, options) => {
      seen.push({ url, field: [...options.body.keys()].join(',') });
      return jsonResponse({ status: 200, results: { message_id: 'V1' } });
    }
  });

  assert.strictEqual(await client.sendVideo('39@s.whatsapp.net', 'guarda', Buffer.from([1]), 'video/mp4', 'clip.mp4'), 'V1');
  assert.strictEqual(await client.sendFile('39@s.whatsapp.net', '', Buffer.from([2]), 'application/pdf', 'doc.pdf'), 'V1');

  assert.strictEqual(seen[0].url, 'http://g/send/video');
  assert.ok(seen[0].field.includes('video'));
  assert.ok(seen[0].field.includes('phone'));
  assert.ok(seen[0].field.includes('caption'));
  assert.strictEqual(seen[1].url, 'http://g/send/file');
  assert.ok(seen[1].field.includes('file'));
});
```

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL - `client.sendVideo is not a function`.

- [ ] **Step 2: Implement the three media doors**

In `WhatsappBridge/gowa-client.js`, replace `sendImage` with:

```js
  /**
   * Un file verso GOWA. Le tre rotte differiscono solo per il nome del campo
   * multipart e per il percorso: prima ce n'era una sola (sendImage), e un
   * video finiva spedito come immagine.
   */
  async postMedia(path, field, phone, caption, buffer, mimeType, fileName) {
    const form = new FormData();
    form.append('phone', phone);
    if (caption) form.append('caption', caption);
    form.append(field, new Blob([buffer], { type: mimeType }), fileName || field);

    const res = await this.fetch(`${this.baseUrl}${path}`, {
      method: 'POST', headers: this.headers(), body: form
    });
    const text = await res.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
    if (!res.ok) throw new Error(errorMessage(data, `sending the ${field} failed`));
    return (data.results || {}).message_id || '';
  }

  async sendImage(phone, caption, buffer, mimeType, fileName) {
    return this.postMedia('/send/image', 'image', phone, caption, buffer,
      mimeType || 'image/jpeg', fileName || 'image.jpg');
  }

  async sendVideo(phone, caption, buffer, mimeType, fileName) {
    return this.postMedia('/send/video', 'video', phone, caption, buffer,
      mimeType || 'video/mp4', fileName || 'video.mp4');
  }

  async sendFile(phone, caption, buffer, mimeType, fileName) {
    return this.postMedia('/send/file', 'file', phone, caption, buffer,
      mimeType || 'application/octet-stream', fileName || 'file');
  }
```

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: PASS.

- [ ] **Step 3: Write the failing server tests**

Append to `WhatsappBridge/test/server.test.js`:

```js
function mediaBridge(gowa) {
  const sent = [];
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(packet) });
  return { bridge, sent };
}

test('i pezzi di un video si ricompongono e vanno a sendVideo', async () => {
  const delivered = [];
  const gowa = {
    sendVideo: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ phone, caption, size: buffer.length, mimeType, fileName });
      return 'V1';
    },
    sendImage: async () => { throw new Error('un video non passa da sendImage'); },
    sendFile: async () => { throw new Error('un video non passa da sendFile'); }
  };
  const { bridge } = mediaBridge(gowa);

  const bytes = Buffer.from('un video finto, lungo abbastanza da dividersi in due');
  const base64 = bytes.toString('base64');
  const middle = Math.ceil((base64.length / 2) / 4) * 4;   // multiplo di 4

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 't1', MediaFileName: 'clip.mp4', MediaMimeType: 'video/mp4', MediaChunkTotal: 2 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 't1', MediaChunkIndex: 0, MediaData: base64.slice(0, middle) });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 't1', MediaChunkIndex: 1, MediaData: base64.slice(middle) });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 't1', Text: 'guarda' });

  assert.strictEqual(delivered.length, 1);
  assert.strictEqual(delivered[0].phone, 'a@s.whatsapp.net');
  assert.strictEqual(delivered[0].caption, 'guarda');
  assert.strictEqual(delivered[0].mimeType, 'video/mp4');
  assert.strictEqual(delivered[0].fileName, 'clip.mp4');
  assert.strictEqual(delivered[0].size, bytes.length);
});

test('un allegato immagine va a sendImage e uno che non e ne image ne video a sendFile', async () => {
  const delivered = [];
  const gowa = {
    sendImage: async (phone, caption, buffer, mimeType) => { delivered.push({ door: 'image', mimeType }); return 'I1'; },
    sendVideo: async () => { throw new Error('non e un video'); },
    sendFile: async (phone, caption, buffer, mimeType) => { delivered.push({ door: 'file', mimeType }); return 'F1'; }
  };
  const { bridge } = mediaBridge(gowa);

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'i1', MediaFileName: 'foto.jpg', MediaMimeType: 'image/jpeg', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'i1', MediaChunkIndex: 0, MediaData: Buffer.from('foto').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'i1' });

  await bridge.handleControl({ Type: 3, Command: 'media.begin', ChatId: 'a@s.whatsapp.net', MediaTransferId: 'd1', MediaFileName: 'doc.pdf', MediaMimeType: 'application/pdf', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk', MediaTransferId: 'd1', MediaChunkIndex: 0, MediaData: Buffer.from('pdf').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end', MediaTransferId: 'd1' });

  assert.deepStrictEqual(delivered, [
    { door: 'image', mimeType: 'image/jpeg' },
    { door: 'file', mimeType: 'application/pdf' }
  ]);
});
```

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL - `media.begin` is an unknown command, so nothing reaches GOWA.

- [ ] **Step 4: Implement the transfer and the routing**

In `WhatsappBridge/server.js`, inside `createBridge`, next to `unreadByChat`, add:

```js
  // Un allegato in arrivo dall'app, pezzo per pezzo. Il frame ha un tetto di
  // 8 MiB e una foto o un video sono piu' grandi: i pezzi si accumulano qui e
  // si mandano a GOWA una volta sola, alla fine.
  const mediaTransfers = new Map();
  // WhatsApp rifiuta oltre 64 MB (senza compressione): oltre quel numero i byte
  // in memoria non servono a nessuno, quindi si fermano prima.
  const MAX_MEDIA_BYTES = 64 * 1024 * 1024;
```

and this block next to `sendOutgoing`:

```js
  /// La strada giusta per un allegato, dal tipo MIME (o dall'estensione quando
  /// il tipo non c'e'): image, video, altrimenti file.
  function mediaKindOf(mimeType, fileName) {
    const mime = String(mimeType || '').toLowerCase();
    const name = String(fileName || '').toLowerCase();
    if (mime.indexOf('video/') === 0 || /\.(mp4|mov|3gp|avi|mkv|webm)$/.test(name)) return 'video';
    if (mime.indexOf('image/') === 0) return 'image';
    return 'file';
  }

  async function sendMediaToGowa(chatId, caption, buffer, mimeType, fileName) {
    const kind = mediaKindOf(mimeType, fileName);
    if (kind === 'video') return gowa.sendVideo(chatId, caption || '', buffer, mimeType || 'video/mp4', fileName);
    if (kind === 'image') return gowa.sendImage(chatId, caption || '', buffer, mimeType || 'image/jpeg', fileName);
    return gowa.sendFile(chatId, caption || '', buffer, mimeType || 'application/octet-stream', fileName);
  }

  function mediaBegin(msg) {
    if (!msg.MediaTransferId) return;
    // Lo stesso id due volte: il secondo comando riparte da zero invece di
    // sommarsi al primo.
    mediaTransfers.set(msg.MediaTransferId, {
      chatId: msg.ChatId,
      fileName: msg.MediaFileName || null,
      mimeType: msg.MediaMimeType || null,
      parts: []
    });
  }

  function mediaChunk(msg) {
    const transfer = mediaTransfers.get(msg.MediaTransferId);
    if (!transfer) return;
    // Ogni pezzo e' un multiplo di 4 caratteri base64: decodificarlo da solo e
    // concatenare i byte da' esattamente il file intero.
    transfer.parts[msg.MediaChunkIndex] = Buffer.from(msg.MediaData || '', 'base64');
  }

  async function mediaEnd(msg) {
    const transfer = mediaTransfers.get(msg.MediaTransferId);
    if (!transfer) return;
    mediaTransfers.delete(msg.MediaTransferId);

    const parts = transfer.parts.filter((part) => part);
    const buffer = Buffer.concat(parts);
    if (buffer.length === 0) return;

    if (buffer.length > MAX_MEDIA_BYTES) {
      logger('WARN', `attachment too large (${buffer.length} bytes), refused`);
      sendControl({ chatId: transfer.chatId, text: 'The file is too large to send.' });
      return;
    }

    if (state.status !== 'connected') {
      // Come un messaggio di testo: si tiene da parte e parte alla connessione.
      pendingOutgoing.push({
        ChatId: transfer.chatId,
        Text: msg.Text || '',
        MediaData: buffer.toString('base64'),
        MediaMimeType: transfer.mimeType,
        MediaFileName: transfer.fileName
      });
      logger('INFO', 'WhatsApp not ready: attachment queued');
      return;
    }

    try {
      logger('MSG', `attachment to ${transfer.chatId}: ${buffer.length} bytes (${mediaKindOf(transfer.mimeType, transfer.fileName)})`);
      await sendMediaToGowa(transfer.chatId, msg.Text, buffer, transfer.mimeType, transfer.fileName);
    } catch (err) {
      logger('ERR', `attachment to ${transfer.chatId} failed: ${err.message}`);
      sendControl({ chatId: transfer.chatId, text: `Send failed: ${err.message}` });
    }
  }
```

Change `sendOutgoing` to route by kind instead of always calling `sendImage`:

```js
  async function sendOutgoing(msg) {
    try {
      if (msg.MediaData) {
        // Una versione vecchia dell'app manda l'allegato dentro il messaggio:
        // si accetta ancora, ma sulla strada giusta.
        await sendMediaToGowa(msg.ChatId, msg.Text, Buffer.from(msg.MediaData, 'base64'),
          msg.MediaMimeType, msg.MediaFileName);
      } else if (msg.Text && msg.Text.trim()) {
        await gowa.sendText(msg.ChatId, msg.Text);
      }
      logger('MSG', `sent to ${msg.ChatId}: ${(msg.Text || '[media]').substring(0, 40)}`);
    } catch (err) {
      logger('ERR', `send to ${msg.ChatId} failed: ${err.message}`);
      sendControl({ command: 'error', chatId: msg.ChatId, text: `Send failed: ${err.message}` });
    }
  }
```

In `handleControl`, after the `messages` case, add:

```js
      case 'media.begin':
        mediaBegin(msg);
        break;
      case 'media.chunk':
        mediaChunk(msg);
        break;
      case 'media.end':
        await mediaEnd(msg);
        break;
```

- [ ] **Step 5: Run the adapter tests**

Run: `cd WhatsappBridge && npm test`
Expected: PASS.

- [ ] **Step 6: Add the fields the app sends**

In `WhatsappApp/Models/ChatMessage.cs`, extend the enum:

```csharp
    public enum MessageType
    {
        Text,
        Image,
        Audio,
        System,
        Video
    }
```

add the backing fields next to `private string _mediaFileName;`:

```csharp
        private string _mediaFileName;  // optional filename
        private string _mediaTransferId;   // id di un allegato che viaggia a pezzi
        private int _mediaChunkIndex;      // quale pezzo e' questo
        private int _mediaChunkTotal;      // quanti pezzi in tutto
        private string _mediaType;         // "image", "video": il tipo dichiarato dal server
```

add the properties next to `MediaFileName`:

```csharp
        /// <summary>Identificativo di un allegato che viaggia a pezzi (media.begin/end).</summary>
        [DataMember]
        public string MediaTransferId
        {
            get { return _mediaTransferId; }
            set { _mediaTransferId = value; OnPropertyChanged(); }
        }

        /// <summary>Quale pezzo di un allegato e' questo frame.</summary>
        [DataMember]
        public int MediaChunkIndex
        {
            get { return _mediaChunkIndex; }
            set { _mediaChunkIndex = value; OnPropertyChanged(); }
        }

        /// <summary>Quanti pezzi ha in tutto l'allegato.</summary>
        [DataMember]
        public int MediaChunkTotal
        {
            get { return _mediaChunkTotal; }
            set { _mediaChunkTotal = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Il tipo di media come lo dichiara il server ("image", "video"). Non e'
        /// il tipo MIME: serve a sapere che una riga di cronologia *e'* un'immagine
        /// anche quando i byte non sono arrivati.
        /// </summary>
        [DataMember]
        public string MediaType
        {
            get { return _mediaType; }
            set { _mediaType = value; OnPropertyChanged(); }
        }
```

and update `IsMedia` and `MediaTypeText`:

```csharp
        // Convenience property: Is this message a media type (image/audio/video)?
        public bool IsMedia
        {
            get
            {
                return Type == MessageType.Image
                    || Type == MessageType.Audio
                    || Type == MessageType.Video;
            }
        }

        // Short text for media messages shown without loading the full image
        public string MediaTypeText
        {
            get
            {
                if (Type == MessageType.Image) return Loc.Get("ChatMessage_Photo", "Photo");
                if (Type == MessageType.Audio) return Loc.Get("ChatMessage_Audio", "Audio");
                if (Type == MessageType.Video) return Loc.Get("ChatMessage_Video", "Video");
                return Loc.Get("ChatMessage_File", "File");
            }
        }
```

- [ ] **Step 7: Add the string and send the frames**

In both `.resw` files, add after the `ChatMessage_Audio` entry:

```xml
  <data name="ChatMessage_Video" xml:space="preserve">
    <value>Video</value>
  </data>
```

(The Italian file uses `<value>Video</value>` too.)

In `WhatsappApp/Services/CommunicationService.cs`, add this helper next to `SendControlAsync`:

```csharp
        /// <summary>
        /// L'ossatura di un frame di controllo. L'ha costruita SendControlAsync
        /// per ogni comando: qui e' un posto solo, perche' anche i comandi di un
        /// allegato (media.begin/chunk/end) sono frame di controllo.
        /// </summary>
        private ChatMessage NewControlFrame(string command)
        {
            return new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = command,
                SenderId = _myUserId ?? "me",
                SenderName = _myUsername ?? Loc.Get("CommService_Me", "Me"),
                ChatId = "system",
                Timestamp = DateTime.Now,
                Type = MessageType.System,
                IsIncoming = false
            };
        }
```

replace the body of `SendControlAsync` so it uses it:

```csharp
        public async Task SendControlAsync(string command, string payload = null)
        {
            var message = NewControlFrame(command);
            message.Text = payload ?? "";
            await SendMessageAsync(message);
        }
```

and add the three media frames after it:

```csharp
        /// <summary>
        /// Un allegato comincia. Il contenuto non sta qui: sta nei pezzi. La
        /// chat viaggia in Text, il file e il suo tipo nei campi che portano
        /// gia' quel nome.
        /// </summary>
        public async Task SendMediaBeginAsync(string chatId, string transferId,
            string fileName, string mimeType, int totalChunks)
        {
            var frame = NewControlFrame("media.begin");
            frame.Text = chatId;
            frame.MediaTransferId = transferId;
            frame.MediaFileName = fileName;
            frame.MediaMimeType = mimeType;
            frame.MediaChunkTotal = totalChunks;
            await SendMessageAsync(frame);
        }

        /// <summary>
        /// Un pezzo dell'allegato, gia' base64. La lunghezza e' un multiplo di 4
        /// caratteri, quindi i pezzi si possono concatenare senza decodificarli.
        /// </summary>
        public async Task SendMediaChunkAsync(string transferId, int index, string base64)
        {
            var frame = NewControlFrame("media.chunk");
            frame.MediaTransferId = transferId;
            frame.MediaChunkIndex = index;
            frame.MediaData = base64;
            await SendMessageAsync(frame);
        }

        /// <summary>L'ultimo pezzo e' passato: l'adapter puo' spedire il file.</summary>
        public async Task SendMediaEndAsync(string transferId, string caption)
        {
            var frame = NewControlFrame("media.end");
            frame.MediaTransferId = transferId;
            frame.Text = caption ?? "";
            await SendMessageAsync(frame);
        }
```

- [ ] **Step 8: Send attachments as pieces**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, replace the whole `SendImageMessage` method with:

```csharp
        /// <summary>
        /// Un allegato si manda a pezzi. Un frame ha un tetto di 8 MiB e il
        /// contenuto viaggia in base64, che aggiunge un terzo: un video non ci
        /// sta in un frame solo. Si taglia la stringa base64 a multipli di 4
        /// caratteri, cosi' ogni pezzo e' base64 valido e i pezzi si
        /// ricompongono senza decodificare niente.
        /// </summary>
        private const int MediaChunkChars = 700000;

        private async System.Threading.Tasks.Task SendAttachmentAsync(string caption)
        {
            if (string.IsNullOrEmpty(_selectedImageBase64)) return;

            string base64 = _selectedImageBase64;
            string mimeType = _selectedImageMimeType ?? "image/jpeg";
            string fileName = _selectedImageFileName;
            string kind = AttachmentInbox.KindName(mimeType, fileName);

            // Il video non si disegna, e senza didascalia il fumetto resterebbe
            // vuoto: la parola si vede, la didascalia che parte e' quella vera.
            string displayText = caption ?? "";
            if (string.IsNullOrEmpty(displayText) && kind == "video")
            {
                displayText = Loc.Get("ChatMessage_Video", "Video");
            }

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = displayText,
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = kind == "video" ? MessageType.Video : MessageType.Image,
                IsIncoming = false,
                Status = MessageStatus.Sending,
                // I byte si tengono solo per un'immagine: servono a disegnarla.
                // Un video non si disegna, e tenerne la base64 per tutta la
                // sessione e' la cosa piu' pesante che questa lista potrebbe
                // fare.
                MediaData = kind == "video" ? null : base64,
                MediaMimeType = mimeType,
                MediaFileName = fileName,
                MediaType = kind
            };

            // Decodifica locale: il mittente vede la propria immagine.
            if (message.Type == MessageType.Image) await message.LoadMediaImageAsync();

            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";
            ScrollToMessage(message);
            ClearSelectedImage();

            if (!CommunicationService.Instance.IsConnected)
            {
                message.Status = MessageStatus.Failed;
                return;
            }

            string transferId = Guid.NewGuid().ToString("N");
            int total = (base64.Length + MediaChunkChars - 1) / MediaChunkChars;

            await CommunicationService.Instance.SendMediaBeginAsync(
                _contact.Id, transferId, fileName, mimeType, total);

            for (int i = 0; i < total; i++)
            {
                int start = i * MediaChunkChars;
                int length = Math.Min(MediaChunkChars, base64.Length - start);
                await CommunicationService.Instance.SendMediaChunkAsync(
                    transferId, i, base64.Substring(start, length));
            }

            await CommunicationService.Instance.SendMediaEndAsync(transferId, caption);

            message.Status = CommunicationService.Instance.IsConnected
                ? MessageStatus.Sent
                : MessageStatus.Failed;
        }
```

In `SendMessage()`, change the branch that calls `SendImageMessage`:

```csharp
            // If we have a selected image or video, send it as an attachment
            if (_selectedImageBase64 != null)
            {
                await SendAttachmentAsync(text);
                return;
            }
```

- [ ] **Step 9: Make an incoming video say so**

In `WhatsappBridge/message-format.js`, in `mediaFromPayload`, the video branch becomes:

```js
  } else if (p.video !== undefined) {
    // Un video non si disegna in un fumetto: la parola resta, e una didascalia
    // vince su di essa come per le immagini. Il tipo 4 e' quello che l'app
    // conosce come Video (vedi MessageType in ChatMessage.cs).
    result.type = 4;
    result.fallbackText = '[Video]';
    if (p.video && typeof p.video.path === 'string') {
      result.path = p.video.path; result.mimeType = 'video/mp4';
    } else {
      result.fallbackText = '[Video not downloaded]';
    }
  }
```

and in `mapWebhookMessage`, set the message type to video so the app does not try to draw it as an image. Change the returned `type: media.type,` line to:

```js
    type: media.type,
    mediaType: mediaKind(media),
```

and add above `mapWebhookMessage`:

```js
/// La parola del tipo di media, o vuota per un messaggio di solo testo.
function mediaKind(media) {
  const type = media && media.type;
  if (type === 1) return 'image';
  if (type === 2) return 'audio';
  if (type === 4) return 'video';
  return media && media.path ? 'document' : '';
}
```

In `buildChatMessage`, next to `if (f.isHistory === true)`, add:

```js
  // Il tipo di media dichiarato ("image", "video"): serve a sapere che una riga
  // di cronologia *e'* un'immagine anche quando i byte non sono arrivati.
  if (f.mediaType) msg.MediaType = f.mediaType;
```

Append to `WhatsappBridge/test/message-format.test.js`:

```js
test('un video in arrivo dichiara il suo tipo e resta una parola', () => {
  const fields = mapWebhookMessage({
    id: 'V1',
    chat_id: 'a@s.whatsapp.net',
    from: 'a@s.whatsapp.net',
    video: { path: 'statics/v.mp4' },
    timestamp: '2026-09-27T08:00:00Z'
  });

  assert.strictEqual(fields.type, 4);
  assert.strictEqual(fields.mediaType, 'video');
  assert.strictEqual(fields.text, '[Video]');

  const frame = buildChatMessage({
    id: 'V1', chatId: 'a@s.whatsapp.net', type: fields.type, mediaType: fields.mediaType, text: fields.text
  });
  assert.strictEqual(frame.Type, 4);
  assert.strictEqual(frame.MediaType, 'video');
});
```

Run: `cd WhatsappBridge && npm test`
Expected: PASS.

- [ ] **Step 10: Run the guards and build**

Run the fast gate from Global Constraints, then the three build-gate commands.
Expected: `33 C# file(s)`, `106 key(s)` (the new `ChatMessage_Video`), all guards OK, adapter tests pass, `COPIA=0`, `0 Error(s)`, `0 Warning(s)`.

- [ ] **Step 11: Document the two doors and the pieces**

In `WhatsappBridge/README.md`, in the protocol table, after the `read` row, add:

```
| app -> adapter | `media.begin` | `Text` = chat JID, `MediaTransferId`, `MediaFileName`, `MediaMimeType`, `MediaChunkTotal` (an attachment follows) |
| app -> adapter | `media.chunk` | `MediaTransferId`, `MediaChunkIndex`, `MediaData` = one base64 piece (a multiple of 4 characters) |
| app -> adapter | `media.end` | `MediaTransferId`, `Text` = caption (reassemble and send) |
```

and, in the list above the table, after the chat-history bullet, add:

```
- A photo, a video or a file is sent in pieces (`media.begin` / `media.chunk` /
  `media.end`): the frame ceiling is 8 MiB and base64 adds a third, so a video cannot
  travel in one frame. Each piece is a multiple of 4 base64 characters, so the adapter
  concatenates the decoded bytes without re-encoding anything. Which door GOWA gets is
  decided by the MIME type (or the extension): `/send/image`, `/send/video` and
  `/send/file` are three different routes, and before this a video went out as an image.
```

`WhatsappBridge/README.it.md`, the same two places:

```
| app -> adapter | `media.begin` | `Text` = JID della chat, `MediaTransferId`, `MediaFileName`, `MediaMimeType`, `MediaChunkTotal` (segue un allegato) |
| app -> adapter | `media.chunk` | `MediaTransferId`, `MediaChunkIndex`, `MediaData` = un pezzo in base64 (multiplo di 4 caratteri) |
| app -> adapter | `media.end` | `MediaTransferId`, `Text` = didascalia (ricompone e spedisce) |
```

```
- Una foto, un video o un file si manda a pezzi (`media.begin` / `media.chunk` /
  `media.end`): il tetto di un frame e' 8 MiB e il base64 aggiunge un terzo, quindi un
  video non ci sta in un frame solo. Ogni pezzo e' un multiplo di 4 caratteri base64,
  cosi' l'adapter concatena i byte decodificati senza ricodificare niente. La porta di
  GOWA la decide il tipo MIME (o l'estensione): `/send/image`, `/send/video` e
  `/send/file` sono tre rotte diverse, e prima di questo un video partiva come immagine.
```

- [ ] **Step 12: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappBridge WhatsappApp README.md README.it.md
git commit -m "feat: send media in pieces and route it by kind"
```

---

### Task 5: An image someone sent can be seen

A message that arrived while the app was closed is history: its bytes were delivered to the adapter's webhook, not to the phone, so the adapter mapped it to the word `[Image]`. GOWA can hand the bytes back on request (`GET /message/:message_id/download`), and that is what the app asks for when such a bubble is tapped.

**Files:**
- Modify: `WhatsappBridge/gowa-client.js` (`downloadMedia`)
- Modify: `WhatsappBridge/message-format.js` (`mapHistoryMessage` carries the kind)
- Modify: `WhatsappBridge/server.js` (`sendMedia`, `handleControl`)
- Modify: `WhatsappApp/Services/CommunicationService.cs` (`RequestMediaAsync`)
- Modify: `WhatsappApp/Services/DataService.cs` (apply a `media` frame to the message it belongs to)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (ask when a history image is tapped)
- Test: `WhatsappBridge/test/gowa-client.test.js`, `WhatsappBridge/test/server.test.js`, `WhatsappBridge/test/message-format.test.js`
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`

**Interfaces:**
- Consumes: `GowaClient#request`, `GowaClient#fetchBinary`, `buildChatMessage`, `sendControl`, `RelatedMessageId`, `ChatMessage.MediaType` (Task 4).
- Produces: `GowaClient#downloadMedia(phone, messageId)` returning `{ base64, mimeType, fileName }` or `null`; `CommunicationService.RequestMediaAsync(string chatId, string messageId)`; the control command `media.get`; the control frame `media` carrying `MediaData`.

- [ ] **Step 1: Write the failing client test**

Append to `WhatsappBridge/test/gowa-client.test.js`:

```js
test('downloadMedia va sulla rotta del messaggio e segue il file_url', async () => {
  const seen = [];
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async (url) => {
      seen.push(url);
      if (seen.length === 1) {
        return jsonResponse({
          status: 200,
          results: { message_id: 'M9', file_url: 'http://127.0.0.1:3000/statics/abc.jpg', filename: 'foto.jpg', media_type: 'image' }
        });
      }
      return {
        ok: true,
        status: 200,
        headers: { get: () => 'image/jpeg' },
        arrayBuffer: async () => new Uint8Array([1, 2, 3]).buffer
      };
    }
  });

  const media = await client.downloadMedia('393401234567@s.whatsapp.net', 'M9');

  assert.strictEqual(seen[0], 'http://127.0.0.1:3000/message/M9/download?phone=393401234567%40s.whatsapp.net');
  assert.strictEqual(seen[1], 'http://127.0.0.1:3000/statics/abc.jpg');
  assert.strictEqual(media.base64, Buffer.from([1, 2, 3]).toString('base64'));
  assert.strictEqual(media.mimeType, 'image/jpeg');
  assert.strictEqual(media.fileName, 'foto.jpg');
});

test('downloadMedia non e fatale quando il file non c e piu', async () => {
  const client = new GowaClient({
    baseUrl: 'http://127.0.0.1:3000',
    fetchImpl: async () => jsonResponse({ status: 200, results: { message_id: 'M9' } })
  });

  assert.strictEqual(await client.downloadMedia('a@s.whatsapp.net', 'M9'), null);
});
```

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: FAIL - `client.downloadMedia is not a function`.

- [ ] **Step 2: Implement the download**

In `WhatsappBridge/gowa-client.js`, add after `avatar`:

```js
  // I byte del media di un messaggio.
  //
  // Due richieste, come per l'avatar: /message/:id/download non restituisce i
  // byte, restituisce l'indirizzo statico del file scaricato (results.file_url),
  // e i byte si prendono dopo. Un file_url vuoto significa che il file non e'
  // sotto statics: per l'app e' "non piu' disponibile", non un guasto.
  async downloadMedia(phone, messageId) {
    if (!phone || !messageId) return null;

    try {
      const r = await this.request('GET',
        `/message/${encodeURIComponent(messageId)}/download?phone=${encodeURIComponent(phone)}`);
      const res = (r.data && r.data.results) || {};
      const url = res.file_url || '';
      if (!r.ok || !url) return null;

      const media = await this.fetchBinary(url);
      if (!media.buffer || media.buffer.length === 0) return null;

      return {
        base64: media.buffer.toString('base64'),
        mimeType: media.contentType || res.media_type || '',
        fileName: res.filename || null
      };
    } catch (err) {
      return null;
    }
  }
```

Run: `cd WhatsappBridge && node --test test/gowa-client.test.js`
Expected: PASS.

- [ ] **Step 3: Write the failing server test**

Append to `WhatsappBridge/test/server.test.js`:

```js
test('media.get scarica il media del messaggio e lo manda come frame di controllo', async () => {
  const sent = [];
  const gowa = {
    downloadMedia: async (phone, messageId) => {
      assert.strictEqual(phone, 'a@s.whatsapp.net');
      assert.strictEqual(messageId, 'M9');
      return { base64: Buffer.from([1, 2, 3]).toString('base64'), mimeType: 'image/jpeg', fileName: 'foto.jpg' };
    }
  };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({
    Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'M9'
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'media');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
  assert.strictEqual(sent[0].RelatedMessageId, 'M9');
  assert.strictEqual(sent[0].MediaData, Buffer.from([1, 2, 3]).toString('base64'));
  assert.strictEqual(sent[0].MediaMimeType, 'image/jpeg');
  assert.strictEqual(sent[0].Type, 3);
});

test('media.get dice che il media non c e piu invece di restare muto', async () => {
  const sent = [];
  const gowa = { downloadMedia: async () => null };
  const bridge = createBridge({ config: {}, gowa, log: () => {}, debug: () => {} });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: (packet) => sent.push(decodeFrame(packet)) });

  await bridge.handleControl({
    Type: 3, Command: 'media.get', Text: 'a@s.whatsapp.net', RelatedMessageId: 'M9'
  });

  assert.strictEqual(sent.length, 1);
  assert.strictEqual(sent[0].Command, 'error');
  assert.strictEqual(sent[0].ChatId, 'a@s.whatsapp.net');
});
```

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: FAIL - `media.get` is an unknown command, so nothing is sent.

- [ ] **Step 4: Implement the command**

In `WhatsappBridge/server.js`, next to `sendMessages`, add:

```js
  /**
   * I byte del media di un messaggio che l'app ha gia' (una riga di cronologia
   * arrivata come parola). Viaggia come frame di controllo, non di messaggio:
   * e' un pezzo che completa un messaggio esistente, non uno nuovo, e come
   * messaggio alzerebbe il conteggio dei non letti e un avviso.
   */
  async function sendMedia(chatId, messageId) {
    if (!chatId || !messageId) return;

    if (state.status !== 'connected') {
      sendControl({ command: 'error', chatId, text: 'WhatsApp is not connected: the media is unavailable.' });
      return;
    }

    try {
      const media = await gowa.downloadMedia(chatId, messageId);
      if (!media) {
        // Il file non c'e' piu': si dice, invece di lasciare la bolla in attesa
        // per sempre (vedi il test del comando).
        sendControl({ command: 'error', chatId, text: 'This media is no longer available on the server.' });
        return;
      }

      sendControl({
        command: 'media',
        chatId,
        relatedMessageId: messageId,
        mediaData: media.base64,
        mediaMimeType: media.mimeType,
        mediaFileName: media.fileName
      });
      logger('INFO', `media downloaded for ${messageId} (${media.base64.length} chars)`);
    } catch (err) {
      logger('ERR', `media download failed for ${messageId}: ${err.message}`);
      sendControl({ command: 'error', chatId, text: `Media download failed: ${err.message}` });
    }
  }
```

and in `handleControl`, after the `media.end` case:

```js
      case 'media.get':
        // Il JID della chat in Text (come `messages`), l'id del messaggio in
        // RelatedMessageId: e' il campo che dice a cosa si riferisce un frame.
        await sendMedia((msg.Text || '').trim(), msg.RelatedMessageId);
        break;
```

- [ ] **Step 5: Carry the kind on a history row**

In `WhatsappBridge/message-format.js`, in `mapHistoryMessage`, add a `mediaType` field to the returned object, next to `type: 0,`:

```js
    type: 0,
    mediaType: media,
```

and append to `WhatsappBridge/test/message-format.test.js`:

```js
test('una riga di cronologia dice che tipo di media e', () => {
  assert.strictEqual(mapHistoryMessage({ id: 'A1', media_type: 'image' }).mediaType, 'image');
  assert.strictEqual(mapHistoryMessage({ id: 'A2', media_type: 'video' }).mediaType, 'video');
  assert.strictEqual(mapHistoryMessage({ id: 'A3', content: 'ciao' }).mediaType, '');
});
```

Run: `cd WhatsappBridge && npm test`
Expected: PASS.

- [ ] **Step 6: Add the request frame to the app**

In `WhatsappApp/Services/CommunicationService.cs`, after `SendMediaEndAsync`, add:

```csharp
        /// <summary>
        /// Chiede i byte del media di un messaggio che l'app ha gia'. La chat
        /// viaggia in Text, il messaggio in RelatedMessageId.
        /// </summary>
        public async Task RequestMediaAsync(string chatId, string messageId)
        {
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(messageId)) return;

            var frame = NewControlFrame("media.get");
            frame.Text = chatId;
            frame.RelatedMessageId = messageId;
            await SendMessageAsync(frame);
        }
```

- [ ] **Step 7: Give the bytes to the message that asked for them**

In `WhatsappApp/Services/DataService.cs`, in `OnControlMessageReceived`, add a case:

```csharp
                case "media":
                    ApplyMedia(message);
                    break;
```

and add the method next to `ApplyChat`:

```csharp
        /// <summary>
        /// I byte di un media appena scaricato. Il messaggio e' gia' nell'elenco
        /// - era una riga di cronologia con la sola parola - e qui riceve i byte
        /// e, se e' un'immagine, il tipo con cui disegnarla.
        /// </summary>
        private async void ApplyMedia(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.RelatedMessageId)) return;

            var list = GetMessages(message.ChatId);
            for (int i = 0; i < list.Count; i++)
            {
                var target = list[i];
                if (target == null || target.Id != message.RelatedMessageId) continue;

                target.MediaData = message.MediaData;
                target.MediaMimeType = message.MediaMimeType;
                if (string.Equals(target.MediaType, "image", StringComparison.OrdinalIgnoreCase))
                {
                    target.Type = MessageType.Image;
                }
                await target.LoadMediaImageAsync();
                return;
            }
        }
```

- [ ] **Step 8: Ask when the bubble is tapped**

In `WhatsappApp/Pages/ChatPage.xaml`, add `Tapped="Image_Tapped"` to both image `<Border>` elements that wrap `<Image Source="{Binding MediaImage}" .../>` (the incoming and the outgoing bubble). The Border becomes:

```xml
                                <Border CornerRadius="4"
                                        Margin="0,0,0,4"
                                        Visibility="{Binding Type, Converter={StaticResource MsgTypeToImageVis}}"
                                        MaxWidth="300" MaxHeight="200"
                                        Tapped="Image_Tapped">
```

In `WhatsappApp/Pages/ChatPage.xaml.cs`, add:

```csharp
        /// <summary>
        /// Come con il pulsante allegato: il selettore di file di WP8.1 non ha un
        /// risultato di ritorno, e il file scelto arriva ad App.OnActivated.
        /// Qui si aspetta solo la scelta, non i byte.
        /// </summary>
        private static bool DownloadableImage(ChatMessage message)
        {
            if (message == null) return false;
            return string.Equals(message.MediaType, "image", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(message.MediaData);
        }

        private void RequestMedia(ChatMessage message)
        {
            if (!CommunicationService.Instance.IsConnected) return;
#pragma warning disable 4014
            CommunicationService.Instance.RequestMediaAsync(message.ChatId, message.Id);
#pragma warning restore 4014
        }
```

(The tap handler itself is `Image_Tapped`, written in Task 6.)

- [ ] **Step 9: Run the guards and build**

Run the fast gate from Global Constraints, then the three build-gate commands.
Expected: all guards OK, adapter tests pass, `COPIA=0`, `0 Error(s)`, `0 Warning(s)`.
(`DownloadableImage` and `RequestMedia` are unused until Task 6, which is the next task in this same plan; the compiler does not warn on unused private methods, but if `0 Warning(s)` fails, that is the reason and Task 6 is the fix.)

- [ ] **Step 10: Document the download**

In `WhatsappBridge/README.md`, in the protocol table, after the `media.end` row, add:

```
| app -> adapter | `media.get` | `Text` = chat JID, `RelatedMessageId` = message id (downloads that message media and answers with a `media` frame) |
| adapter -> app | `media` | `RelatedMessageId`, `MediaData`: the bytes of a message the app already has |
```

and, in the list above the table, after the media-in-pieces bullet, add:

```
- A photo or a video in a chat's history arrived while the phone was off: its bytes were
  delivered to the adapter and nowhere else, so the row is a word (`[Image]`). Tapping it
  asks the adapter (`media.get`), which reads `GET /message/:id/download` from GOWA and
  answers with the bytes in a `media` frame; the app puts them on the message it already
  has. Nothing is downloaded until it is asked for.
```

`WhatsappBridge/README.it.md`, the same two places:

```
| app -> adapter | `media.get` | `Text` = JID della chat, `RelatedMessageId` = id del messaggio (scarica il media di quel messaggio e risponde con un frame `media`) |
| adapter -> app | `media` | `RelatedMessageId`, `MediaData`: i byte di un messaggio che l'app ha gia' |
```

```
- Una foto o un video nella cronologia di una chat e' arrivato col telefono spento: i suoi
  byte sono stati consegnati all'adapter e a nessun altro, quindi la riga e' una parola
  (`[Image]`). Toccarla lo chiede all'adapter (`media.get`), che legge
  `GET /message/:id/download` da GOWA e risponde con i byte in un frame `media`; l'app li
  mette sul messaggio che ha gia'. Non si scarica niente finche' non viene chiesto.
```

In `README.md`, in the Limiti/Limitations section, replace the existing bullet about old media (`Aprendo una chat si vedono i messaggi recenti ...`) with:

```
- Opening a chat shows the recent messages the server already has. Older ones are not requested from the phone. A photo or a video in that history shows a word (`[Image]`, `[Video]`) until it is tapped, when the adapter downloads it from the server; a video is shown as a word because this app has no player for it.
```

and the Italian counterpart in `README.it.md`:

```
- Aprendo una chat si vedono i messaggi recenti che il server ha gia'. I piu' vecchi non vengono richiesti al telefono. Una foto o un video di quella cronologia mostrano una parola (`[Image]`, `[Video]`) finche' non vengono toccati, e allora l'adapter li scarica dal server; un video resta una parola perche' questa app non ha un lettore per riprodurlo.
```

- [ ] **Step 11: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappBridge WhatsappApp README.md README.it.md
git commit -m "feat: download a history image when it is asked for"
```

---

### Task 6: An image opens full screen

The bubble is a 300x200 box, and the tap on it does nothing today. The tap already has one job from Task 5 — asking for a history image — and this task gives it the second: an image that has bytes opens over the whole page.

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (the viewer overlay)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`Image_Tapped`, the viewer)

**Interfaces:**
- Consumes: `ChatMessage.MediaData`, `ImageHelper.FromBase64Async(string, int)`, `DownloadableImage` and `RequestMedia` from Task 5.
- Produces: nothing another task reads.

- [ ] **Step 1: Add the overlay**

In `WhatsappApp/Pages/ChatPage.xaml`, add this as the last child of the root `<Grid>` (after the `<Grid Grid.Row="2" ...>` input area, before `</Grid>`):

```xml
        <!-- Immagine a tutto schermo. Sta sopra tutto e si chiude toccandola:
             niente pulsante, niente icona nuova, niente stringa nuova. -->
        <Grid x:Name="ImageViewer" Grid.RowSpan="3"
              Background="#F0000000"
              Visibility="Collapsed"
              Tapped="ImageViewer_Tapped">
            <Image x:Name="ImageViewerImage" Stretch="Uniform"/>
        </Grid>
```

- [ ] **Step 2: Wire the tap and the viewer**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, add:

```csharp
        /// <summary>
        /// La bolla decodifica a 320 px: ingrandirla a tutto schermo la lascia
        /// sfocata. Qui si decodifica alla misura dello schermo, e si
        /// restituisce chiudendo: sono i pixel piu' pesanti che questa pagina
        /// tiene, e non devono sopravvivere alla vista.
        /// </summary>
        private const int ViewerDecodePixels = 720;

        private async void Image_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ChatMessage;
            if (message == null) return;
            e.Handled = true;

            // Con i byte: si apre. Senza, ed e' un'immagine di cronologia: si
            // chiede al server, e si aprira' al tocco successivo.
            if (!string.IsNullOrEmpty(message.MediaData))
            {
                await ShowFullScreenAsync(message);
                return;
            }

            if (DownloadableImage(message)) RequestMedia(message);
        }

        private async System.Threading.Tasks.Task ShowFullScreenAsync(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaData)) return;

            try
            {
                ImageViewerImage.Source = await ImageHelper.FromBase64Async(
                    message.MediaData, ViewerDecodePixels);
                ImageViewer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ShowFullScreenAsync", ex);
                HideFullScreen();
            }
        }

        private void ImageViewer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideFullScreen();
            e.Handled = true;
        }

        private void HideFullScreen()
        {
            ImageViewer.Visibility = Visibility.Collapsed;
            ImageViewerImage.Source = null;
        }
```

In `OnNavigatedFrom`, add the cleanup next to `_pendingScroll = null;`:

```csharp
            HideFullScreen();
```

- [ ] **Step 3: Run the guards and build**

Run the fast gate from Global Constraints, then the three build-gate commands.
Expected: `check-icons.js` unchanged (no new Path), `check-memory.js` OK (the 720 is a named constant), all guards OK, `COPIA=0`, `0 Error(s)`, `0 Warning(s)`.

- [ ] **Step 4: Document the viewer**

In `README.md`, in the feature list, after the history bullet, add:

```
- Tapping an image in a chat opens it over the whole page; tapping it again closes it. A `[Image]` from an old conversation is downloaded first and opens at the next tap.
```

In `README.it.md`, in the same place:

```
- Toccando un'immagine in una chat si apre a tutto schermo; toccandola di nuovo si chiude. Un `[Image]` di una conversazione vecchia viene prima scaricato e si apre al tocco successivo.
```

- [ ] **Step 5: Run the docs guard and commit**

```bash
node tools/check-docs.js
git add WhatsappApp README.md README.it.md
git commit -m "feat: open a chat image full screen on tap"
```

---

### Task 7: Record the lessons and verify

**Files:**
- Modify: `.agents/skills/maintain-the-app/SKILL.md`
- Modify: `.agents/skills/test-the-app/SKILL.md`
- Modify: `docs/superpowers/plans/2026-09-27-startup-cache-unread-and-media.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing.

- [ ] **Step 1: Add the gotchas**

In `.agents/skills/maintain-the-app/SKILL.md`, in the gotchas list, add:

```markdown
- **A screen that is shown before the socket is up cannot ask for its data.**
  `ChatsPage.OnNavigatedTo` used to request the list only when `IsConnected && Contacts.Count == 0`;
  on a cold start neither is true, so the list stayed empty until the user walked through
  the settings page, which is what "I have to press Continue every time" was. The request
  now happens in `RequestChats()` - called on navigation and on every `state` frame - and it
  waits for `WhatsAppState == "connected"`, because the adapter answers "not connected" until
  the WhatsApp login is done. `ChatCache` keeps the last list on the phone so the screen is
  not empty while that happens: it is a photograph, replaced row by row by `ApplyChat`, and
  it holds no avatar bytes.
- **`ShareOperation` has an order and a lifetime.** `ReportStarted()` first, then
  `ReportDataRetrieved()` once the bytes are in, then `ReportCompleted()`; and because the
  operation is not agile, the asynchronous reads after the activation handler returns need
  `GetDeferral()`. Calling `ReportCompleted()` in a `finally` without ever calling
  `ReportStarted()` is the crash that sharing a photo produced. A share target's file types
  are declared in the manifest's default namespace; a video type missing there means the
  app is not offered at all for it.
- **A frame is capped at 8 MiB and base64 adds a third.** An attachment therefore travels as
  `media.begin` / `media.chunk` / `media.end`, with each chunk a multiple of 4 base64
  characters so the bytes can be concatenated without re-encoding. The adapter picks GOWA's
  door from the MIME type (or the extension): `/send/image`, `/send/video`, `/send/file`.
  Before this, every attachment went through `sendImage`, so a video arrived as a broken
  image.
- **GOWA's `/message/:id/download` answers with an address, not bytes.** Like
  `/user/avatar`, it is two requests: `results.file_url` is the static file, fetched after.
  An empty `file_url` means the file is not under `statics`, which for the app is "no longer
  available", not a fault. The bytes go back in a `media` control frame tied to the existing
  message by `RelatedMessageId` - a message frame would count as new and raise a toast.
```

- [ ] **Step 2: Add the on-device checks**

In `.agents/skills/test-the-app/SKILL.md`, in the on-device checklist, after the last item, add:

```markdown
33. Start the app from the tile: the chat list is already on screen, with names and last
    messages, before the connection is up, and it refreshes by itself within a few seconds.
    Settings must not have to be opened.
34. Have someone send a message while the app is closed, then open the app: that chat is at
    the top and shows an unread number. Open it and go back: the number is gone.
35. Share a photo from Photos: the app opens on the chats with the image ready, and it does
    not crash. Share a short video (under about 6 MB): it appears in the chat and the other
    side receives a video, not a broken image.
36. Open a chat whose messages arrived while the app was closed: a photo shows `[Image]`.
    Tap it once, wait, tap it again: the image opens full screen, and a tap closes it.
37. Send a photo yourself and tap it: it opens full screen. Tap it again: it closes.
```

- [ ] **Step 3: Record what execution changed**

Run the full fast gate:

```bash
node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js
cd WhatsappBridge && npm test
cd .. && node --test "tools/test/**/*.test.js"
```

Then append to this plan document a `## What execution changed about this plan` section listing, honestly, every place the plan turned out to be wrong or incomplete while it was executed - the exact error and the exact fix - plus the final counts (C# files, resw keys, adapter tests). Do not write "no changes" unless the gate output in this session proves it.

- [ ] **Step 4: Commit and push**

```bash
git add .agents docs
git commit -m "docs: record what startup, shares and media taught us"
git push origin master
```

---

## What execution changed about this plan

Recorded while it was executed.

1. **`ShareOperation` has no `GetDeferral()` on WP8.1.** Task 3 step 1 used
   `Windows.Foundation.Deferral` and `operation.GetDeferral()`; the build answered
   `error CS0234: 'Deferral' does not exist in Windows.Foundation` and
   `error CS1061: 'ShareOperation' does not contain 'GetDeferral'`. The type does not
   exist in this projection, and the operation does not need it: the share target's app
   is in the foreground, so the operation stays valid. The fix was to drop the deferral
   and the `finally`, keeping only `ReportStarted()` -> `ReportDataRetrieved()` ->
   `ReportCompleted()`, with `ReportError` in the catch. `tools/check-csharp5.js` did not
   catch it, because `ShareOperation.GetDeferral` is not in its list of WP8.1-missing
   members; that is worth adding there.
2. **There was no "chat-list bullet" in `WhatsappBridge/README.md`.** Task 2 step 8
   assumed one; the adapter README describes the chat list through its protocol table,
   so the count went into the `chat` row (`UnreadCount`), a new `read` row, and a new
   bullet after the history one, in both languages.
3. **Task 5 planned helpers it did not ship.** Task 5 step 8 specified
   `ChatPage.DownloadableImage(ChatMessage)` and `ChatPage.RequestMedia(ChatMessage)` for
   the tap handler Task 6 would write, but the Task 5 commit carried only
   `CommunicationService.RequestMediaAsync`, `DataService.ApplyMedia` and the docs. Task 6
   found them missing and added them with `Image_Tapped`; the plan's own note that they
   would be "unused until Task 6" was true, which is also why nothing failed in between.
4. **The build output was not ignored.** `WhatsappApp/AppPackages/` and
   `WhatsappApp/BundleArtifacts/` were not in `.gitignore`, and the VM build leaves
   `BundleArtifacts/arm.txt` behind. `git add WhatsappApp` swept it into the Task 6 commit
   (the same file had already been removed from the index in Task 1, with no rule to stop it
   coming back). The fix was to remove it from the index, amend, and add both directories to
   `.gitignore`.
5. **The gotcha this plan wrote about `ShareOperation` was itself wrong.** Task 7 step 1
   said the asynchronous reads "need `GetDeferral()`"; item 1 above records that the WP8.1
   projection has no such member. The skill now states the opposite, with the compiler
   errors, so the next reader does not reintroduce it.
6. **`tools/check-csharp5.js` has a gap here.** It enforces the WP8.1 API surface, but
   `ShareOperation.GetDeferral` is not in its list, so the mistake that cost a build round
   trip would not have been caught by the fast gate. Worth adding to the guard.

## Final counts

- 33 C# files (`check-csharp5.js`), 12 inline icon Paths (9 distinct), 106 `.resw` keys in
  both en-US and it-IT.
- 36 tool tests passing; 109 adapter tests passing.
- Build on the Windows 11 VM: `COPIA=0`, package created, no errors.

## What only the phone can prove

1. The chat list is on screen before the connection is up, and refreshes by itself (Task 1).
2. A chat that received a message while the app was closed shows an unread number, and the
   number clears when the chat is opened (Task 2).
3. Sharing a photo no longer crashes the app, and a shared video arrives as a video (Task 3,
   Task 4).
4. An image in a conversation's history can be downloaded with one tap and opened full
   screen with the next (Task 5, Task 6).

## Deliberate non-goals

- **Playing a video.** The app has no video player and WP8.1's `MediaPlayerLauncher` would
  leave the app; a video is sent and received, and shown as `[Video]`.
- **Persistence in the adapter.** The unread count lives in memory and starts from zero when
  the adapter restarts. Persisting it means a file and a schema in a program whose only
  state is WhatsApp's own; the count is a hint, not a record.
- **Paging older messages.** GOWA's `POST /chat/:chat_jid/history` is the right way to load
  older messages on scroll-to-top, which is a separate change.
- **A cache of message history.** The chat *list* is cached, not the conversations: the
  message bodies are what the memory budget cannot hold on a 512 MB phone.
