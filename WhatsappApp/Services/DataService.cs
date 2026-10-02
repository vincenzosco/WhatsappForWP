using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Central data service that holds contacts and messages,
    /// bridges the communication service with the UI.
    /// Contacts come from the adapter (GOWA), no longer from sample data.
    /// </summary>
    public class DataService : INotifyPropertyChanged
    {
        private static DataService _instance;
        public static DataService Instance
        {
            get
            {
                if (_instance == null) _instance = new DataService();
                return _instance;
            }
        }

        private readonly ObservableCollection<Contact> _contacts;
        private readonly ObservableCollection<CallLogEntry> _calls;
        private readonly Dictionary<string, ObservableCollection<ChatMessage>> _chatMessages;

        // The chats whose history this session has already requested: once per
        // chat, not on every opening.
        private readonly HashSet<string> _historyRequested = new HashSet<string>();

        // The chats whose history burst has already been seen in this session: the
        // first row of a burst is logged once, so the log tells "the history never
        // arrived" apart from "it arrived and the list did not show it".
        private readonly HashSet<string> _historyArrived = new HashSet<string>();

        // The chat-list rows as the server last sent them (for the cache) and the
        // ones that are arriving now.
        private readonly List<ChatMessage> _chatRows = new List<ChatMessage>();
        private readonly List<ChatMessage> _freshChatRows = new List<ChatMessage>();

        // Index by id: without this every incoming message scanned the whole
        // contact list (FirstOrDefault) to find the chat.
        private readonly Dictionary<string, Contact> _contactIndex =
            new Dictionary<string, Contact>();
        private Contact _selectedContact;
        private string _connectionStatus;
        private bool _isServerRunning;
        private string _activeChatId;
        private bool _listening;

        /// <summary>
        /// Currently open chat. It serves one purpose only: not to raise an alert
        /// for a message the user is already watching. It no longer decides the
        /// unread count: that is decided by whoever shows the messages (see
        /// ClearUnread).
        /// </summary>
        public string ActiveChatId
        {
            get { return _activeChatId; }
            set { _activeChatId = value; }
        }

        public ObservableCollection<Contact> Contacts
        {
            get { return _contacts; }
        }
        public Contact SelectedContact
        {
            get { return _selectedContact; }
            set { _selectedContact = value; OnPropertyChanged(); }
        }
        public string ConnectionStatus
        {
            get { return _connectionStatus; }
            set { _connectionStatus = value; OnPropertyChanged(); }
        }
        public bool IsServerRunning
        {
            get { return _isServerRunning; }
            set { _isServerRunning = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Hooks the service up to the network. Must be called once, at startup and
        /// on the UI thread: if nobody builds the service before the app connects,
        /// the incoming messages (and the contacts) have no listeners and are lost
        /// without a trace.
        /// </summary>
        public void Start()
        {
            if (_listening) return;
            _listening = true;

            CommunicationService.Instance.MessageReceived += OnNetworkMessageReceived;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;

            // The copy of the last session: it is shown now, before the connection
            // exists. The server will replace it with the real one.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/cached chats", LoadCachedChatsAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// Fills the list with the last snapshot on the phone. Every row passes
        /// through ApplyChat, as if it arrived from the server: this way there are
        /// not two paths that can diverge.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCachedChatsAsync()
        {
            // Before the rows: ApplyChat immediately asks which are pinned, muted or
            // deleted, and a file not yet read would answer no to all three.
            await ChatPreferences.LoadAsync();

            // And also before the rows, for the same reason: the local copy does not
            // have the image bytes (ChatCache leaves them out), and ApplyChat asks
            // for them here.
            await AvatarCache.LoadAsync();

            var cached = await ChatCache.LoadAsync();
            for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);
            NotificationService.SetUnread(TotalUnread());
        }

        /// <summary>Call log, filled by the adapter on request.</summary>
        public ObservableCollection<CallLogEntry> Calls
        {
            get { return _calls; }
        }

        /// <summary>The adapter-side scan is done: the page can stop waiting.</summary>
        public event EventHandler CallsScanCompleted;

        public event PropertyChangedEventHandler PropertyChanged;

        private DataService()
        {
            _contacts = new ObservableCollection<Contact>();
            _calls = new ObservableCollection<CallLogEntry>();
            _chatMessages = new Dictionary<string, ObservableCollection<ChatMessage>>();
        }

        private async void OnNetworkMessageReceived(object sender, ChatMessage message)
        {
            // Ignore system/handshake messages
            if (message.Type == MessageType.System) return;

            // History is text, but it did not arrive now: it enters in order and
            // without counting. Everything else in this method is for what arrives
            // now - row preview, unread, alert.
            if (message.IsHistory)
            {
                if (_historyArrived.Add(message.ChatId))
                {
                    Diag.Ok("history arrived for " + message.ChatId);
                }
                AddHistoryMessage(message);
                return;
            }

            // A new message brings back a deleted chat: it is what WhatsApp does.
            // Without this, a chat deleted by mistake would never come back.
            if (ChatPreferences.IsHidden(message.ChatId)) ChatPreferences.Reveal(message.ChatId);

            // Add to the appropriate chat's message list
            if (!_chatMessages.ContainsKey(message.ChatId))
            {
                _chatMessages[message.ChatId] = new ObservableCollection<ChatMessage>();
            }
            _chatMessages[message.ChatId].Add(message);

            // Find or create contact for this chat
            var contact = FindContact(message.ChatId);
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
                    LastMessageAt = message.Timestamp,
                    Initials = InitialsFor(name),
                    IsPinned = ChatPreferences.IsPinned(message.ChatId),
                    IsMuted = ChatPreferences.IsMuted(message.ChatId),
                    // The count does not look at which chat is open: an incoming
                    // message is unread until someone reads it (see
                    // MarkDisplayedRead), and the page that shows it decides. Excluding
                    // the active chat here lost the messages that arrived while the app
                    // was suspended with that chat open: they were not counted and
                    // nobody cleared them.
                    UnreadCount = message.IsIncoming ? 1 : 0
                };
                _contacts.Insert(0, contact);
                _contactIndex[contact.Id] = contact;
            }
            else
            {
                // Update the contact preview and move to top
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                contact.LastMessageAt = message.Timestamp;
                if (message.IsIncoming)
                {
                    contact.UnreadCount++;
                    NotificationService.SetUnread(TotalUnread());
                }

                var idx = _contacts.IndexOf(contact);
                if (idx > 0)
                    _contacts.Move(idx, 0);

                // A message in a non-pinned chat must not jump the pinned ones: they
                // go back to the top, and this one stays right below.
                ResortContacts();
            }

            // An alert only for a chat we are not watching and that is not muted:
            // silence is the only thing "mute" does - the unread count stays, because
            // the message is unread all the same. The badge, on the other hand, is
            // always updated: the count is true, and the open chat is cleared when
            // the page shows it (ClearUnread), not because someone skipped it.
            if (message.IsIncoming && message.ChatId != _activeChatId && !contact.IsMuted)
                NotificationService.ShowMessage(contact.Name, message.Text);

            // And the tile shows who wrote and how many are waiting: their picture,
            // their name, and the count. Only for a person, not for a group, and only
            // for a message that arrived while the chat was not open.
            if (message.IsIncoming && message.ChatId != _activeChatId && !contact.IsMuted)
            {
                // The picture of the sender: the one in the row, or the one kept from
                // the last time it arrived. A row read from the local copy carries no
                // bytes, and without this the tile of a message that arrives just
                // after a restart would have no face at all.
                string tileAvatar = contact.AvatarData;
                if (string.IsNullOrEmpty(tileAvatar)) tileAvatar = AvatarCache.Get(message.ChatId);

                NotificationService.RememberSender(message.ChatId, contact.Name, tileAvatar);
            }

            NotificationService.SetUnread(TotalUnread());

            // Asynchronous decoding of the image: the XAML binding follows MediaImage
            if (message.Type == MessageType.Image)
                await message.LoadMediaImageAsync();
        }

        /// <summary>
        /// Control frames from the adapter: contacts, call log, revocations and
        /// edits. They all arrive on the UI thread, so here what is bound to the
        /// lists can be touched directly.
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
                case "chat":
                    ApplyChat(message);
                    break;
                case "chats.done":
                    RememberChatList();
                    RaiseChatListCompleted();
                    break;
                case "media":
                    ApplyMediaFrame(message);
                    break;
                case "error":
                    ClearMediaLoading(message);
                    break;
                case "revoked":
                    RemoveMessage(message.ChatId, message.RelatedMessageId);
                    break;
                case "edited":
                    ApplyEdit(message.ChatId, message.RelatedMessageId, message.Text);
                    break;
                case "typing":
                    RaiseTypingChanged(message);
                    break;
                case "history.done":
                    RaiseHistoryCompleted(message.ChatId);
                    break;
            }
        }

        /// <summary>
        /// Someone in a chat is writing, or has stopped. It is the only thing the
        /// server says about a person that is not a message, and the page of that
        /// chat is the only one that can show it.
        ///
        /// The frame is the whole content: ChatId is the conversation, State is
        /// "composing" or "paused". Nothing is passed on that the server did not
        /// say, and no chat is invented for it.
        /// </summary>
        public event EventHandler<ChatMessage> TypingChanged;

        private void RaiseTypingChanged(ChatMessage message)
        {
            var handler = TypingChanged;
            if (handler != null) handler(this, message);
        }

        /// <summary>
        /// The history burst of one chat has ended: the adapter sends a
        /// `history.done` frame after the last of its messages. The chat page waits
        /// for it before binding its list, because a bind while the burst is still
        /// inserting re-lays out the list on every frame. The argument is the chat
        /// id, carried in the frame's ChatId.
        /// </summary>
        public event EventHandler<string> HistoryCompleted;

        private void RaiseHistoryCompleted(string chatId)
        {
            var handler = HistoryCompleted;
            if (handler != null) handler(this, chatId);
        }

        /// <summary>A new contact (or the updated name) from the adapter.</summary>
        private void ApplyContact(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.ChatId)) return;
            if (IsNotAConversation(message.ChatId)) return;

            // It counts as for the chat list: a chat deleted from this phone does not
            // come back because the server sends its name again.
            if (ChatPreferences.IsHidden(message.ChatId)) return;

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

            var target = FindContact(message.ChatId);
            if (target != null)
            {
                target.IsPinned = ChatPreferences.IsPinned(message.ChatId);
                target.IsMuted = ChatPreferences.IsMuted(message.ChatId);
                ResortContacts();
            }
        }

        /// <summary>
        /// The status broadcast and a channel are not conversations: WhatsApp keeps
        /// status updates in the Status section, and neither can be answered, yet
        /// GOWA lists both among the chats. Drawing one as a row put a conversation
        /// called "Status" next to the people. The adapter skips the same two JIDs
        /// (isNotAConversation); this is here because the row can also come from the
        /// copy already on the phone. The predicate must stay the same two cases on
        /// both ends, or a channel already on disk survives here.
        /// </summary>
        private static bool IsNotAConversation(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return false;

            return string.Equals(chatId, "status@broadcast", StringComparison.OrdinalIgnoreCase)
                || chatId.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A chat-list row: the conversation exists in WhatsApp even if in this
        /// session we never received a message from it.
        /// </summary>
        private void ApplyChat(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.ChatId)) return;
            if (IsNotAConversation(message.ChatId)) return;

            // A chat deleted from this phone does not come back with the server list:
            // the decision lives on the phone (ChatPreferences) and only a new
            // message undoes it.
            if (ChatPreferences.IsHidden(message.ChatId)) return;

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

            // The preview comes from the server, and it replaces the one a cached
            // row brought in: that is the whole point of the snapshot on disk, and
            // the row is taken only when it is not older than what is already
            // there - a message that arrived in this session while the phone was
            // on must not go backwards.
            if (!string.IsNullOrEmpty(message.Text) && message.Timestamp >= contact.LastMessageAt)
            {
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                contact.LastMessageAt = message.Timestamp;
            }

            // The profile picture. When the row carries it, the bytes are also kept
            // on the phone: at the next opening the list has a face before the adapter
            // answers. When it does not carry it - a row from the local copy - the
            // kept one is used.
            string avatar = message.AvatarData;
            if (string.IsNullOrEmpty(avatar)) avatar = AvatarCache.Get(message.ChatId);

            if (!string.IsNullOrEmpty(avatar) && contact.AvatarData != avatar)
            {
                contact.AvatarData = avatar;
#pragma warning disable 4014
                contact.LoadAvatarAsync();
#pragma warning restore 4014
            }

            // Only the bytes that arrived from the server are kept: a row read from
            // disk is not news, it is what is already written there.
            if (!string.IsNullOrEmpty(message.AvatarData))
            {
                AvatarCache.Remember(message.ChatId, message.AvatarData);
            }

            // The unread count is a property of the row, not of the message: it comes
            // from the server, which is the only one awake while the phone is off (see
            // server.js, unreadByChat).
            contact.UnreadCount = message.UnreadCount;
            contact.IsPinned = ChatPreferences.IsPinned(message.ChatId);
            contact.IsMuted = ChatPreferences.IsMuted(message.ChatId);
            ResortContacts();

            RememberChatRow(message);
        }

        /// <summary>This update row, kept aside for the cache.</summary>
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
        /// A chat list is about to arrive. The rows of the cached snapshot went
        /// through ApplyChat at startup, and each of them registered itself in the
        /// batch: without this the batch - and so the file written back, and the
        /// order taken from it - is the previous session, not the server one.
        /// </summary>
        public void BeginChatList()
        {
            _freshChatRows.Clear();
        }

        /// <summary>
        /// The list has finished arriving: what remains becomes the copy on the
        /// phone, and the batch that just arrived restarts from scratch.
        /// </summary>
        private void RememberChatList()
        {
            if (_freshChatRows.Count == 0) return;

            // The server sends the conversations most recent first, and that is the
            // order the list has to show. A conversation that wrote while the phone
            // was off would otherwise keep the position it had in the cached
            // snapshot, and a brand new one would sit at the bottom, off screen.
            for (int i = 0; i < _freshChatRows.Count; i++)
            {
                if (i >= _contacts.Count) break;
                Contact contact = FindContact(_freshChatRows[i].ChatId);
                if (contact == null) continue;
                int at = _contacts.IndexOf(contact);
                if (at < 0 || at == i) continue;
                _contacts.Move(at, i);
            }
            ResortContacts();

            _chatRows.Clear();
            _chatRows.AddRange(_freshChatRows);
            _freshChatRows.Clear();

#pragma warning disable 4014
            ChatCache.SaveAsync(_chatRows);
#pragma warning restore 4014
        }

        /// <summary>
        /// One piece of a media byte stream. An image fits in one frame, a video does
        /// not: the adapter splits it and the pieces accumulate here until they are
        /// all there (see IncomingMediaStore). The message already exists - it was a
        /// history row with only the word - and receives the bytes at the end.
        /// </summary>
        private async void ApplyMediaFrame(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.RelatedMessageId)) return;

            try
            {
                IncomingMediaResult result = await IncomingMediaStore.AddChunkAsync(message);
                if (result == null) return;
                await ApplyMedia(message, result);
            }
            catch (Exception ex)
            {
                Diag.Failed("DataService.ApplyMediaFrame", ex);
            }
        }

        /// <summary>
        /// The server says that media is gone: the indicator stops spinning,
        /// otherwise the bubble waits forever.
        /// </summary>
        private void ClearMediaLoading(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.RelatedMessageId)) return;

            var list = GetMessages(message.ChatId);
            for (int i = 0; i < list.Count; i++)
            {
                var target = list[i];
                if (target != null && target.Id == message.RelatedMessageId)
                {
                    target.IsMediaLoading = false;
                    return;
                }
            }
        }

        private async Task ApplyMedia(ChatMessage message, IncomingMediaResult result)
        {
            var list = GetMessages(message.ChatId);
            for (int i = 0; i < list.Count; i++)
            {
                var target = list[i];
                if (target == null || target.Id != message.RelatedMessageId) continue;

                // The bytes have arrived: the spinner in the bubble stops.
                target.IsMediaLoading = false;
                target.MediaMimeType = result.MimeType ?? target.MediaMimeType;
                target.MediaType = result.MediaType;

                if (!string.IsNullOrEmpty(result.LocalFileName))
                {
                    // Video, audio or document: the bytes are on disk and the player
                    // (or the system app) opens the file from there. They would not fit
                    // in memory.
                    target.MediaFilePath = result.LocalFileName;
                    // What the card draws: the name the file had, and how big it
                    // is. The name is written here and not earlier because a
                    // history row arrives without one.
                    if (!string.IsNullOrEmpty(result.FileName)) target.MediaFileName = result.FileName;
                    target.MediaSizeBytes = result.SizeBytes;
                    if (string.Equals(result.MediaType, "video", StringComparison.OrdinalIgnoreCase))
                    {
                        target.Type = MessageType.Video;
                        // The cover frame, now that the file is on disk. A failure
                        // leaves the plain box (see VideoThumbnail).
                        await target.LoadVideoThumbnailAsync();
                    }
                    else if (string.Equals(result.MediaType, "audio", StringComparison.OrdinalIgnoreCase))
                        target.Type = MessageType.Audio;
                    // A document stays text: its bubble is the file name.
                }
                else if (string.Equals(result.MediaType, "image", StringComparison.OrdinalIgnoreCase))
                {
                    target.MediaData = result.Base64;
                    target.Type = MessageType.Image;
                    await target.LoadMediaImageAsync();
                }
                else
                {
                    // A file that is not drawn: the bytes stay, the bubble does not.
                    target.MediaData = result.Base64;
                }
                return;
            }
        }

        /// <summary>The conversation list has finished arriving.</summary>
        public event EventHandler ChatListCompleted;

        private void RaiseChatListCompleted()
        {
            var handler = ChatListCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>A call-log entry.</summary>
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

        /// <summary>Empties the log before a new scan.</summary>
        public void ClearCalls()
        {
            _calls.Clear();
        }

        private void RaiseCallsScanCompleted()
        {
            var handler = CallsScanCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// Removes a message revoked on WhatsApp. The id of an incoming message is
        /// the WhatsApp one, so the comparison is exact; if we do not find it (our
        /// own message, or one that arrived before subscribing) nothing is touched.
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

        /// <summary>Applies an edit that arrived from WhatsApp (same id as before).</summary>
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
        /// Realigns the chat preview to the last remaining message: after a
        /// revocation or an edit the preview would stay the old one.
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
                contact.LastMessageAt = DateTime.MinValue;
                return;
            }

            var last = messages[messages.Count - 1];
            contact.LastMessage = last.Text;
            contact.LastMessageTime = last.FormattedTime;
            contact.LastMessageAt = last.Timestamp;
        }

        /// <summary>Name shown for a JID when we do not know its name.</summary>
        public static string DisplayNameForJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "?";
            string user = jid.Split('@')[0];
            if (jid.EndsWith("@g.us")) return string.Format(Loc.Get("DataService_Group", "Group {0}"), user);
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

        /// <summary>
        /// Finds a contact by id. The index is rebuilt if it does not know it (the
        /// collection is public: someone may have modified it without going through
        /// AddContact) and cleaned of ids no longer present.
        /// </summary>
        public Contact FindContact(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            Contact indexed;
            if (_contactIndex.TryGetValue(id, out indexed))
            {
                if (_contacts.Contains(indexed)) return indexed;
                _contactIndex.Remove(id);
            }

            RebuildContactIndex();

            Contact found;
            return _contactIndex.TryGetValue(id, out found) ? found : null;
        }

        private void RebuildContactIndex()
        {
            _contactIndex.Clear();
            foreach (var contact in _contacts)
            {
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                _contactIndex[contact.Id] = contact;
            }
        }

        public ObservableCollection<ChatMessage> GetMessages(string chatId)
        {
            if (!_chatMessages.ContainsKey(chatId))
            {
                _chatMessages[chatId] = new ObservableCollection<ChatMessage>();
            }
            return _chatMessages[chatId];
        }

        /// <summary>
        /// Puts the saved messages into the conversation, if it is still empty. They
        /// pass through AddHistoryMessage like the real history: same in-order
        /// insertion and same discarding of ids already present, so the messages that
        /// arrive later do not enter twice. They are marked IsHistory, so they do not
        /// count and raise no alerts.
        /// </summary>
        public async Task LoadCachedMessagesAsync(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            if (GetMessages(chatId).Count > 0) return;

            var cached = await MessageCache.LoadAsync(chatId);
            for (int i = 0; i < cached.Count; i++)
            {
                var message = cached[i];
                if (message == null) continue;
                message.ChatId = chatId;
                AddHistoryMessage(message);
            }

            Diag.Ok("cache restored " + cached.Count + " message(s)");
        }

        /// <summary>
        /// Says whether this chat history must be requested now, and if so remembers
        /// it: once per chat per session. Reopening the same chat shows it right away
        /// from memory, instead of redoing the round trip on the wire.
        /// </summary>
        public bool MarkHistoryRequested(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return false;
            return _historyRequested.Add(chatId);
        }

        /// <summary>
        /// Inserts a history message in the right place.
        ///
        /// In the right place and not at the bottom: the adapter does not promise the
        /// order - chats.js already says so - and reopening a chat means the old
        /// messages must end up before the ones that arrived in this session. The
        /// same id twice does not enter: it is the key that makes requesting the
        /// history unable to duplicate what is already there.
        ///
        /// It does not touch the list row: the preview is the last real message, and
        /// an old message must not rewrite it or move the conversation to the top.
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

        public void AddMessage(string chatId, ChatMessage message)
        {
            if (!_chatMessages.ContainsKey(chatId))
            {
                _chatMessages[chatId] = new ObservableCollection<ChatMessage>();
            }
            _chatMessages[chatId].Add(message);

            // Update the contact's last message
            var contact = FindContact(chatId);
            if (contact != null)
            {
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                contact.LastMessageAt = message.Timestamp;
                if (message.IsIncoming)
                {
                    contact.UnreadCount++;
                    NotificationService.SetUnread(TotalUnread());
                }

                // Move contact to top
                _contacts.Move(_contacts.IndexOf(contact), 0);
                ResortContacts();
            }
        }

        /// <summary>
        /// The messages of this chat have been shown: from here on they are read.
        /// Only the chat page calls it, and it is the only place "read" is a decision
        /// and not a guess: it is what WhatsApp does - the number disappears from the
        /// row because you are reading it, not because the counter skips it.
        /// </summary>
        public void ClearUnread(string chatId)
        {
            var contact = FindContact(chatId);
            if (contact == null) return;
            if (contact.UnreadCount == 0) return;

            contact.UnreadCount = 0;
            NotificationService.SetUnread(TotalUnread());
        }

        /// <summary>
        /// Frees what can be rebuilt: the decoded avatar bitmaps (one per
        /// conversation, the heavy thing in this app) and the history of the chats
        /// nobody is reading.
        ///
        /// The open chat is not touched: the user is watching that one, and emptying
        /// it under their eyes would be worse than the memory it frees.
        ///
        /// The image bytes are not thrown away: the decoded copy is thrown away,
        /// which is the heavy one, and it is rebuilt when the list comes back to the
        /// front (see RestoreAvatars).
        ///
        /// A chat collection is emptied instead of thrown away: the chat page holds
        /// that instance, and replacing it would leave it attached to a list that
        /// receives nothing more. Instead, it forgets having already requested the
        /// history, so reopening the chat requests it again: without that, an emptied
        /// chat would stay empty forever.
        /// </summary>
        public void TrimForMemory()
        {
            foreach (var contact in _contacts)
            {
                if (contact != null) contact.Avatar = null;
            }

            var emptied = new List<string>();
            foreach (var pair in _chatMessages)
            {
                if (pair.Key == _activeChatId) continue;
                pair.Value.Clear();
                emptied.Add(pair.Key);
            }

            foreach (var chatId in emptied)
            {
                _historyRequested.Remove(chatId);
            }
        }

        /// <summary>
        /// Redraws the avatars whose bytes are still there. Under memory pressure
        /// TrimForMemory throws away the decoded copy and the row stays with the
        /// initials: the server sends it again only at the next list, and meanwhile
        /// the list looks empty of faces. The bytes, however, are still there - in
        /// memory, or in the cache on the phone - so it is rebuilt here, when the list
        /// comes back to the front.
        ///
        /// It must be called on the UI thread: BitmapImage is not agnostic with
        /// respect to the view (see ImageHelper).
        /// </summary>
        public void RestoreAvatars()
        {
            if (MemoryWatcher.Instance.IsUnderPressure) return;

            for (int i = 0; i < _contacts.Count; i++)
            {
                var contact = _contacts[i];
                if (contact == null || contact.Avatar != null) continue;

                string data = contact.AvatarData;
                if (string.IsNullOrEmpty(data)) data = AvatarCache.Get(contact.Id);
                if (string.IsNullOrEmpty(data)) continue;

                contact.AvatarData = data;
#pragma warning disable 4014
                contact.LoadAvatarAsync();
#pragma warning restore 4014
            }
        }

        /// <summary>Pins (or unpins) a chat at the top of the list. The phone decides, not the server.</summary>
        public void SetPinned(string chatId, bool pinned)
        {
            var contact = FindContact(chatId);
            if (contact == null) return;

            contact.IsPinned = pinned;
            ChatPreferences.SetPinned(chatId, pinned);
            ResortContacts();
        }

        /// <summary>Mutes the chat: its messages raise no alert.</summary>
        public void SetMuted(string chatId, bool muted)
        {
            var contact = FindContact(chatId);
            if (contact == null) return;

            contact.IsMuted = muted;
            ChatPreferences.SetMuted(chatId, muted);
        }

        /// <summary>
        /// Deletes a chat from this phone: the row, the messages, the copy on disk
        /// and - last, because it is the one that lasts - the decision in
        /// ChatPreferences, which keeps it out of the next server list.
        ///
        /// Nothing is touched on WhatsApp: there is no endpoint, and a "delete" that
        /// wiped the conversation for the other party too would be a different thing
        /// from what the user asks.
        ///
        /// A new message brings it back (OnNetworkMessageReceived), as WhatsApp does:
        /// a chat deleted by mistake does not stay lost.
        /// </summary>
        public void DeleteChat(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            var contact = FindContact(chatId);
            if (contact != null)
            {
                _contacts.Remove(contact);
                _contactIndex.Remove(chatId);
            }

            _chatMessages.Remove(chatId);
            _historyRequested.Remove(chatId);

            ChatPreferences.Hide(chatId);
            NotificationService.SetUnread(TotalUnread());

#pragma warning disable 4014
            MessageCache.DeleteAsync(chatId);
#pragma warning restore 4014
        }

        /// <summary>Sum of the unread: it is the number that goes on the icon.</summary>
        private int TotalUnread()
        {
            int total = 0;
            foreach (var contact in _contacts)
            {
                if (contact != null) total += contact.UnreadCount;
            }
            return total;
        }

        /// <summary>
        /// Brings the pinned chats to the top leaving the others where they are (the
        /// most recent first): each one is moved in front of the first non-pinned one,
        /// so the relative order of the others does not change. It is not a sort: a
        /// date key would only redo an order the collection already has.
        /// </summary>
        private void ResortContacts()
        {
            int target = 0;
            for (int i = 0; i < _contacts.Count; i++)
            {
                if (_contacts[i] == null || !_contacts[i].IsPinned) continue;
                if (i != target) _contacts.Move(i, target);
                target++;
            }
        }

        public void AddContact(Contact contact)
        {
            if (contact == null) return;
            _contacts.Insert(0, contact);
            if (!string.IsNullOrEmpty(contact.Id)) _contactIndex[contact.Id] = contact;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
