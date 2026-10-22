using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using WhatsappApp.Models;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Media;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Central data service that holds contacts, messages and calls: the one path
    /// the pages read and the one place a message is applied, whichever source
    /// brought it (a live node off the native client, or the history sync).
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
        private readonly ObservableCollection<ChatMessage> _statuses;
        private readonly Dictionary<string, ObservableCollection<ChatMessage>> _chatMessages;

        // How many messages of one chat are kept in memory. The server still holds
        // the older ones: this is a ceiling on growth, not a deletion, and reopening
        // the chat asks for the history again. Without it a busy chat grows for as
        // long as the session lasts, which is the last unbounded allocation on a
        // 512 MB phone.
        private const int MaxMessagesPerChat = 200;

        // The chats whose history burst this session has already waited for: once
        // per chat, not on every opening.
        private readonly HashSet<string> _historyRequested = new HashSet<string>();

        // The chats whose history burst has already been seen in this session: the
        // first row of a burst is logged once, so the log tells "the history never
        // arrived" apart from "it arrived and the list did not show it".
        private readonly HashSet<string> _historyArrived = new HashSet<string>();

        // The chats the reader has cleared while the socket was not usable. The
        // `read` frame is re-sent for each of them as soon as a connection is
        // established: a frame written into a socket the OS already closed is
        // lost, and without this a chat that had been read is counted again at the
        // next list, because nothing on the other side was ever told.
        private readonly HashSet<string> _pendingReads = new HashSet<string>();

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

            // The native client is the only source of the chats and the messages.
            // Nothing else feeds this service: two sources for one chat list is how
            // every message arrives twice.
            NativeConnection.Instance.MessageArrived += OnNativeMessage;
            NativeConnection.Instance.ChatArrived += OnNativeChat;
            NativeConnection.Instance.ChatMerged += OnChatMerged;
            NativeConnection.Instance.HistoryFinished += OnNativeHistoryFinished;
            NativeConnection.Instance.MessageStatusChanged += OnNativeStatus;
            NativeConnection.Instance.MediaArrived += OnNativeMedia;
            NativeConnection.Instance.CallsScanned += OnNativeCalls;
            NativeConnection.Instance.StatusArrived += OnNativeStatusPost;
            NativeConnection.Instance.StatusMediaArrived += OnNativeStatusMedia;

            // The reads that could not go out while the account was closed wait here,
            // and nothing else drains them: without this line they waited forever, which
            // is a chat the other side keeps showing as unread.
            NativeConnection.Instance.ConnectionStatusChanged += OnNativeConnectionStatus;

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

            // And before them too: a row read from the local copy may carry no name at all
            // (a person whose chat was never named while it was written), and the pair that
            // turns a LID into a phone number is in this file.
            await NameCache.LoadAsync();

            // The phone's own address book, in the background: the sync names groups and
            // push names but not the contacts saved on the primary, so without this every
            // person is a number. The rows below are renamed when it answers.
            ContactDirectory.Start();

            // And also before the rows, for the same reason: the local copy does not
            // have the image bytes (ChatCache leaves them out), and ApplyChat asks
            // for them here.
            await AvatarCache.LoadAsync();

            var cached = await ChatCache.LoadAsync();
            for (int i = 0; i < cached.Count; i++) ApplyChat(cached[i]);
            SortContacts();

            // The address book may have answered before there were rows to rename: the
            // other order (rows first) is covered by ContactDirectory calling this too.
            ApplyAddressBook();
            NotificationService.SetUnread(TotalUnread());
            Diag.OkNow("order after the cached list: " + OrderSample());
        }

        /// <summary>Call log, filled by a scan of WhatsApp's own log.</summary>
        public ObservableCollection<CallLogEntry> Calls
        {
            get { return _calls; }
        }

        /// <summary>The scan is done: the page can stop waiting.</summary>
        public event EventHandler CallsScanCompleted;

        /// <summary>
        /// The status feed: one row per update, newest first.
        ///
        /// It is a `ChatMessage` like every other row this service hands a page, so the page
        /// draws it with the controls it already has - the initials square, the picture and
        /// the time are the same three as a chat's.
        /// </summary>
        public ObservableCollection<ChatMessage> Statuses
        {
            get { return _statuses; }
        }

        /// <summary>The feed changed: a status arrived, or its photo did.</summary>
        public event EventHandler StatusesChanged;

        public event PropertyChangedEventHandler PropertyChanged;

        private DataService()
        {
            _contacts = new ObservableCollection<Contact>();
            _calls = new ObservableCollection<CallLogEntry>();
            _statuses = new ObservableCollection<ChatMessage>();
            _chatMessages = new Dictionary<string, ObservableCollection<ChatMessage>>();
        }

        /// <summary>
        /// One message off the native client, into the one path every message takes.
        /// The list, the unread counts, the previews, the preferences and the caches are one
        /// implementation and stay one: a second path for the new source would be a second
        /// set of bugs, and the two would disagree within a day.
        /// </summary>
        private void OnNativeMessage(object sender, WhatsappApp.Protocol.WaProto.HistoryMessage incoming)
        {
            if (incoming == null || string.IsNullOrEmpty(incoming.Jid)) return;

            var message = new ChatMessage();
            message.Id = incoming.Id;
            message.ChatId = incoming.Jid;
            message.Text = incoming.Text;
            message.Type = MessageType.Text;
            message.IsIncoming = !incoming.IsFromMe;
            message.IsGroup = incoming.Jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
            // Without the device: this is what a bubble and a contact lookup are keyed on.
            message.SenderId = JidUtils.Normalize(incoming.Sender);
            message.SenderName = string.IsNullOrEmpty(incoming.SenderName) ? null : incoming.SenderName;
            message.Timestamp = Moment(incoming.Timestamp);
            // What the sync carried and what arrived now take the same path from here, so the
            // one bit that tells them apart has to travel: a history row is inserted by date, is
            // not counted and raises no toast (`OnNetworkMessageReceived`), while a live one is
            // the newest message of its chat. Dropping the flag turned every message of the
            // sync into a new one: a toast each and a chat row counted once more.
            message.IsHistory = incoming.IsHistory;

            // A photo, a voice note, a video or a document: the encoded message is where
            // its keys are, and the same decoding serves a live message and a history row.
            ApplyNativeMedia(message, incoming.Encoded);

            OnNetworkMessageReceived(this, message);
        }

        /// <summary>
        /// The media a message carries, applied to the row the pages bind: the type that
        /// decides which bubble is drawn, the MIME type, the file name, the caption, and the
        /// keys and address a later tap fetches the bytes with.
        ///
        /// The encoded `Message` and not a decoded one, in both directions: the history row
        /// carries it on the wire (`HistoryMessage.Encoded`) and the live reader puts it there
        /// (`NativeConnection.ReadMediaAsync`), so there is exactly one decoder and one place
        /// the media type is worked out (`MediaMessages.Of`).
        /// </summary>
        private static void ApplyNativeMedia(ChatMessage message, byte[] encoded)
        {
            if (message == null || encoded == null) return;

            MediaInfo info = MediaMessages.Of(Message.Decode(encoded));
            if (info == null) return;

            message.Media = info;
            message.MediaType = info.Type;
            if (!string.IsNullOrEmpty(info.MimeType)) message.MediaMimeType = info.MimeType;
            if (!string.IsNullOrEmpty(info.FileName)) message.MediaFileName = info.FileName;
            if (!string.IsNullOrEmpty(info.Caption) && string.IsNullOrEmpty(message.Text)) message.Text = info.Caption;

            if (string.Equals(info.Type, MediaKeys.Image, StringComparison.OrdinalIgnoreCase))
                message.Type = MessageType.Image;
            else if (string.Equals(info.Type, MediaKeys.Video, StringComparison.OrdinalIgnoreCase))
                message.Type = MessageType.Video;
            else if (string.Equals(info.Type, MediaKeys.Audio, StringComparison.OrdinalIgnoreCase))
                message.Type = MessageType.Audio;
            // A document has no type of its own on purpose: its bubble is the file name, and
            // `MediaType` ("document") is what tells the card to draw it as one.
        }

        /// <summary>
        /// The bytes of a native media message have arrived: `IncomingMediaStore` decides
        /// where they go (memory for an image, the phone's folder for a video, a note or a
        /// document) and this puts them into the bubble that is already on screen.
        /// </summary>
        private void OnNativeMedia(object sender, MediaArrival arrival)
        {
            if (arrival == null || string.IsNullOrEmpty(arrival.Id)) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/native media", ApplyNativeMediaAsync(arrival));
#pragma warning restore 4014
        }

        private async Task ApplyNativeMediaAsync(MediaArrival arrival)
        {
            try
            {
                MediaInfo info = arrival.Info;

                IncomingMediaResult result = await IncomingMediaStore.StoreAsync(arrival.Id,
                    info == null ? null : info.Type,
                    info == null ? null : info.MimeType,
                    info == null ? null : info.FileName,
                    arrival.Bytes);

                if (result == null) return;

                var message = new ChatMessage();
                message.ChatId = arrival.Jid;
                message.RelatedMessageId = arrival.Id;

                await ApplyMedia(message, result);
            }
            catch (Exception ex)
            {
                Diag.Failed("DataService/ApplyNativeMediaAsync", ex);
            }
        }

        /// <summary>
        /// A tap on a bubble whose media has no bytes: the file is fetched from WhatsApp,
        /// decrypted here, stored and put in the bubble, which is where the image control and
        /// the player read it from. A media message that arrived without keys (or an old one
        /// the client never kept them for) says so and stops the spinner rather than spinning
        /// forever.
        /// </summary>
        public void RequestMedia(ChatMessage message)
        {
            if (message == null) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/request media", RequestNativeMediaAsync(message));
#pragma warning restore 4014
        }

        private async Task RequestNativeMediaAsync(ChatMessage message)
        {
            MediaInfo info = message.Media;
            if (info == null)
            {
                message.IsMediaLoading = false;
                Diag.Ok("media: message " + message.Id + " arrived without keys, so it is not fetchable");
                return;
            }

            try
            {
                byte[] bytes = await NativeConnection.Instance.DownloadMediaAsync(info);

                IncomingMediaResult result = await IncomingMediaStore.StoreAsync(message.Id,
                    info.Type, info.MimeType, info.FileName, bytes);

                if (result == null)
                {
                    message.IsMediaLoading = false;
                    return;
                }

                await ApplyMedia(message, result);
            }
            catch (Exception ex)
            {
                // The spinner stops and the failure is named: a bubble that spins forever is
                // what a person reads as "this phone is broken".
                message.IsMediaLoading = false;
                Diag.Failed("DataService/request media", ex);
            }
        }

        /// <summary>
        /// One chat row as the history knows it: who it is with, what it is called, how much
        /// is unread, and the last of the messages that came with it.
        ///
        /// A row with no messages is still a row - WhatsApp knows the conversation even when
        /// nothing was synced from it - which is what `ApplyChat` already does.
        /// </summary>
        private void OnNativeChat(object sender, WhatsappApp.Protocol.WaProto.HistoryConversation row)
        {
            if (row == null || string.IsNullOrEmpty(row.Jid)) return;

            // The first row of a batch opens it: the rows of the cached snapshot went
            // through ApplyChat at startup and registered themselves, and without this
            // the batch - and so the file written back at the end - would be the
            // previous session rather than the one WhatsApp just sent.
            if (_freshChatRows.Count == 0) BeginChatList();

            var message = new ChatMessage();
            message.ChatId = row.Jid;
            message.SenderName = row.Name;
            message.UnreadCount = row.UnreadCount;
            message.IsGroup = row.IsGroup;
            message.Type = MessageType.Text;

            // A conversation the sync named with no time at all is not "now":
            // stamping it now lifted every such row to the top of the list and tied
            // them together, which is why the chats came out in the order the sync
            // listed them (all the groups first) and not by activity. `Moment(0)`
            // answers `DateTime.Now` on purpose for a *live* message; a row has no
            // such excuse, so it keeps the time it already had.
            // The last activity is the newest of the two the sync names; when it
            // names neither, the row's own messages are the clock (`NativeChats`
            // raises ConversationTimestamp with every message it adds).
            long at = Math.Max(row.LastMessageTimestamp, row.ConversationTimestamp);
            message.Timestamp = at > 0 ? Moment(at) : DateTime.MinValue;

            if (row.Messages.Count > 0) message.Text = row.Messages[row.Messages.Count - 1].Text;

            ApplyChat(message);
        }

        /// <summary>
        /// WhatsApp says the history is all here. Two things follow from it: the list
        /// that just arrived becomes the copy on the phone, and every chat whose rows
        /// came in this burst is told the burst has closed - which is what a
        /// conversation bound before it was waiting for (see ChatPage.OnHistoryCompleted).
        /// </summary>
        private void OnNativeHistoryFinished(object sender, WhatsappApp.Protocol.WaProto.HistorySync sync)
        {
            HistoryComplete = true;

            // The sync may have taught a pair that makes a cached row obsolete: the row was
            // keyed on a LID and its number is now known. The rows of this sync are already
            // canonical (`NativeChats`), so this is the copy read from disk that is folded.
            FoldMappedContacts();

            int chatRows = _freshChatRows.Count;
            RememberChatList();
            RaiseChatListCompleted(chatRows);

            // One event per chat that received rows: the page of a chat that got none
            // has nothing to move to.
            foreach (string chatId in _historyArrived)
            {
                RaiseHistoryCompleted(chatId);
            }

            Diag.Ok("native: the history is complete, " + chatRows + " row(s) in the batch, "
                + _contacts.Count + " in the list");
        }

        /// <summary>
        /// A chat that moved from a `@lid` to its phone number.
        ///
        /// Everything in this service keys a conversation on its JID: the row, the unread
        /// count, the messages, the pin and the mute. A merge done only in the protocol layer
        /// would leave the old row on screen beside the new one - the same person twice, which
        /// is the defect the pair exists to end - so the pages' copy is moved here.
        /// </summary>
        private void OnChatMerged(object sender, ChatMerge merge)
        {
            if (merge == null || string.IsNullOrEmpty(merge.From) || string.IsNullOrEmpty(merge.To)) return;
            if (merge.From == merge.To) return;

            MergeMessages(merge.From, merge.To);
            ChatPreferences.Rename(merge.From, merge.To);

            Contact gone = FindContact(merge.From);
            if (gone != null)
            {
                Contact keep = FindContact(merge.To);

                if (keep == null)
                {
                    // Only one row exists: re-key it rather than drop it. The messages are
                    // already under the new address (MergeMessages).
                    _contactIndex.Remove(gone.Id);
                    gone.Id = merge.To;

                    string name = NameCache.Book.NameFor(merge.To);
                    if (string.IsNullOrEmpty(name)) name = DisplayNameForJid(merge.To);
                    gone.Name = name;
                    gone.Initials = InitialsFor(name);
                    gone.IsPinned = ChatPreferences.IsPinned(merge.To);
                    gone.IsMuted = ChatPreferences.IsMuted(merge.To);

                    _contactIndex[gone.Id] = gone;
                }
                else
                {
                    // Two rows, one person: the newer preview and the unread count are the
                    // two things the person would notice going missing.
                    if (gone.LastMessageAt > keep.LastMessageAt)
                    {
                        keep.LastMessage = gone.LastMessage;
                        keep.LastMessageTime = gone.LastMessageTime;
                        keep.LastMessageAt = gone.LastMessageAt;
                    }

                    keep.UnreadCount += gone.UnreadCount;

                    if (string.IsNullOrEmpty(keep.AvatarData) && !string.IsNullOrEmpty(gone.AvatarData))
                    {
                        keep.AvatarData = gone.AvatarData;
#pragma warning disable 4014
                        keep.LoadAvatarAsync();
#pragma warning restore 4014
                    }

                    _contacts.Remove(gone);
                    _contactIndex.Remove(merge.From);
                }
            }

            if (_activeChatId == merge.From) _activeChatId = merge.To;

            NotificationService.SetUnread(TotalUnread());
            ResortContacts();
        }

        /// <summary>
        /// The messages of a chat under its new address. The list is re-sorted: the two halves
        /// were written by different keys and appending one to the other would put a newer
        /// message above an older one.
        /// </summary>
        private void MergeMessages(string from, string to)
        {
            ObservableCollection<ChatMessage> gone;
            if (!_chatMessages.TryGetValue(from, out gone)) return;

            _chatMessages.Remove(from);

            ObservableCollection<ChatMessage> keep;
            if (!_chatMessages.TryGetValue(to, out keep))
            {
                for (int i = 0; i < gone.Count; i++) gone[i].ChatId = to;
                _chatMessages[to] = gone;
                return;
            }

            var merged = new List<ChatMessage>(keep);
            for (int i = 0; i < gone.Count; i++)
            {
                gone[i].ChatId = to;
                merged.Add(gone[i]);
            }

            merged.Sort(delegate(ChatMessage left, ChatMessage right)
            {
                return left.Timestamp.CompareTo(right.Timestamp);
            });

            keep.Clear();
            for (int i = 0; i < merged.Count; i++) keep.Add(merged[i]);

            TrimToCeiling(keep);
        }

        /// <summary>
        /// Folds the rows read from the local copy whose LID is now known to be a phone
        /// number. The rows of a sync are already canonical (`NativeChats`); this is the
        /// snapshot on disk, written before the pair was known.
        /// </summary>
        private void FoldMappedContacts()
        {
            var snapshot = new List<Contact>(_contacts);

            for (int i = 0; i < snapshot.Count; i++)
            {
                Contact contact = snapshot[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;

                string canonical = NameCache.Book.Canonical(contact.Id);
                if (canonical == null || canonical == contact.Id) continue;

                OnChatMerged(this, new ChatMerge(contact.Id, canonical));
            }
        }

        /// <summary>
        /// Whether WhatsApp has sent the history it means to send. Until it has, a
        /// conversation opened now binds from the phone's copy and waits for the burst;
        /// after it, a conversation is opened with everything the account has and
        /// binds at once. It is one login's worth: a fresh connection brings a new sync.
        /// </summary>
        public bool HistoryComplete { get; private set; }

        /// <summary>
        /// The call log, as the history brought it.
        ///
        /// The whole list is rewritten each time rather than appended to: the sync re-sends
        /// the same records in chunks, and a log that grew a duplicate per chunk would show a
        /// call three times. The page's list is the one a person reads, and it is built from
        /// what WhatsApp last said - which is also what makes a call refused from this phone
        /// and one made on another disappear the way they should (WhatsApp does not send them).
        /// </summary>
        private void OnNativeCalls(object sender, IList<CallLogEntryModel> entries)
        {
            ClearCalls();

            if (entries == null) return;

            for (int i = 0; i < entries.Count; i++)
            {
                var row = new ChatMessage();
                row.ChatId = entries[i].Jid;
                row.Timestamp = Moment(entries[i].Timestamp);
                row.CallId = entries[i].Id;
                row.CallDurationSeconds = entries[i].Seconds;
                row.CallIsVideo = entries[i].IsVideo;
                // The reason is the word the row shows: a call the phone never answered is
                // `missed`, and one that connected is not a reason at all.
                row.CallReason = entries[i].IsMissed ? "missed" : (entries[i].IsIncoming ? "incoming" : "outgoing");

                AddCall(row);
            }

            Diag.Ok("native: " + _calls.Count + " call(s) in the log");
            RaiseCallsScanCompleted();
        }

        /// <summary>
        /// The call log a page just asked for: WhatsApp sent it with the history, so this is
        /// the client's own list re-told rather than a new question.
        /// </summary>
        public void ScanCalls()
        {
            ClearCalls();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/scan calls", NativeConnection.Instance.ScanCallsAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// A status was posted: one row in the feed, with the photo it may carry.
        ///
        /// The row is written before the bytes, so a page that opens on a live status shows
        /// the author and the caption at once and the picture follows. The same encoded
        /// `Message` decode a chat message's media goes through is used here
        /// (`ApplyNativeMedia`), because there is one place the media type is worked out.
        /// </summary>
        private void OnNativeStatusPost(object sender, StatusPost post)
        {
            if (post == null || string.IsNullOrEmpty(post.Id) || string.IsNullOrEmpty(post.Author)) return;

            ChatMessage row = FindStatus(post.Id);
            if (row == null)
            {
                row = new ChatMessage();
                row.Id = post.Id;
                row.ChatId = post.Author;
                row.IsIncoming = true;
                row.Type = MessageType.Text;
                row.Timestamp = Moment(post.Timestamp);
                _statuses.Insert(0, row);
            }

            row.SenderName = DisplayNameForJid(post.Author);
            if (!string.IsNullOrEmpty(post.Text)) row.Text = post.Text;

            ApplyNativeMedia(row, post.Encoded);
            if (row.Media != null && row.Media.IsFetchable) row.IsMediaLoading = true;

            Diag.Ok("native: status " + post.Id + " from " + post.Author);
            RaiseStatusesChanged();
        }

        /// <summary>
        /// The bytes of a status have arrived: they go where every other received media goes
        /// (`IncomingMediaStore` - memory for a picture, the phone's folder for a video) and
        /// into the row that is already on screen.
        /// </summary>
        private void OnNativeStatusMedia(object sender, MediaArrival arrival)
        {
            if (arrival == null || string.IsNullOrEmpty(arrival.Id)) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/status media", ApplyStatusBytesAsync(arrival));
#pragma warning restore 4014
        }

        private async Task ApplyStatusBytesAsync(MediaArrival arrival)
        {
            try
            {
                ChatMessage row = FindStatus(arrival.Id);
                if (row == null) return;

                MediaInfo info = arrival.Info;

                IncomingMediaResult result = await IncomingMediaStore.StoreAsync(arrival.Id,
                    info == null ? null : info.Type,
                    info == null ? null : info.MimeType,
                    info == null ? null : info.FileName,
                    arrival.Bytes);

                if (result == null)
                {
                    row.IsMediaLoading = false;
                    return;
                }

                await ApplyStatusMediaAsync(row, result);
            }
            catch (Exception ex)
            {
                Diag.Failed("DataService/status media", ex);
            }
        }

        /// <summary>
        /// A status whose bytes did not arrive with it - the history brought the photo and
        /// not the file, or an earlier fetch failed: the tap asks WhatsApp for the blob and
        /// puts it in the row, which is the same path a chat bubble's tap takes.
        /// </summary>
        public void RequestStatusMedia(ChatMessage row)
        {
            if (row == null) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/status media request", RequestStatusMediaAsync(row));
#pragma warning restore 4014
        }

        private async Task RequestStatusMediaAsync(ChatMessage row)
        {
            MediaInfo info = row.Media;
            if (info == null || !info.IsFetchable)
            {
                row.IsMediaLoading = false;
                Diag.Ok("status: " + row.Id + " names no key or no address, so it is not fetchable");
                return;
            }

            try
            {
                byte[] bytes = await NativeConnection.Instance.DownloadMediaAsync(info);

                IncomingMediaResult result = await IncomingMediaStore.StoreAsync(row.Id,
                    info.Type, info.MimeType, info.FileName, bytes);

                if (result == null)
                {
                    row.IsMediaLoading = false;
                    return;
                }

                await ApplyStatusMediaAsync(row, result);
            }
            catch (Exception ex)
            {
                row.IsMediaLoading = false;
                Diag.Failed("DataService/status media request", ex);
            }
        }

        /// <summary>Where a status's bytes end up: the same three decisions as a chat bubble's.</summary>
        private async Task ApplyStatusMediaAsync(ChatMessage row, IncomingMediaResult result)
        {
            row.IsMediaLoading = false;
            row.MediaMimeType = result.MimeType ?? row.MediaMimeType;
            row.MediaType = result.MediaType;

            if (!string.IsNullOrEmpty(result.LocalFileName))
            {
                row.MediaFilePath = result.LocalFileName;
                if (!string.IsNullOrEmpty(result.FileName)) row.MediaFileName = result.FileName;
                row.MediaSizeBytes = result.SizeBytes;
                if (string.Equals(result.MediaType, "video", StringComparison.OrdinalIgnoreCase))
                    row.Type = MessageType.Video;
            }
            else
            {
                row.MediaData = result.Base64;
            }

            if (string.Equals(result.MediaType, "image", StringComparison.OrdinalIgnoreCase))
            {
                row.Type = MessageType.Image;
                await row.LoadMediaImageAsync();
            }

            RaiseStatusesChanged();
        }

        /// <summary>
        /// Watching a status: the `read` receipt to the broadcast list that names it, which is
        /// what fills the author's ring (`StatusFeed.Viewed`). The row says it was watched so
        /// the page can draw it as seen; the receipt is sent once per status.
        /// </summary>
        public void MarkStatusViewed(ChatMessage row)
        {
            if (row == null || string.IsNullOrEmpty(row.Id) || string.IsNullOrEmpty(row.ChatId)) return;

            row.IsStatusViewed = true;

#pragma warning disable 4014
            // The delegate and not the task: a `ViewStatusAsync` that throws while it is
            // being *called* - no open session, a chat id that is not a JID - already threw
            // before `RunGuardedAsync` was entered, and that exception left this method on
            // the tap's own thread. This overload catches it where it happens.
            Guarded.RunGuardedAsync("DataService/view status",
                delegate { return NativeConnection.Instance.ViewStatusAsync(row.ChatId, row.Id); });
#pragma warning restore 4014
        }

        /// <summary>
        /// The feed as WhatsApp has it so far. Like the call log there is nothing to ask the
        /// server for; this re-tells what has arrived to a page that just opened.
        /// </summary>
        public void ScanStatuses()
        {
#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/scan statuses", NativeConnection.Instance.ScanStatusesAsync());
#pragma warning restore 4014
        }

        private ChatMessage FindStatus(string id)
        {
            for (int i = 0; i < _statuses.Count; i++)
            {
                if (_statuses[i].Id == id) return _statuses[i];
            }

            return null;
        }

        private void RaiseStatusesChanged()
        {
            var handler = StatusesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// A receipt for something this phone sent: the bubble that said `Sent` becomes
        /// `Delivered`, and then `Read`. The ids come off the wire one per message, and the
        /// chat is the one the receipt names - not the open chat, which may be another one.
        /// </summary>
        private void OnNativeStatus(object sender, MessageStatusUpdate update)
        {
            if (update == null || string.IsNullOrEmpty(update.Id)) return;

            // The ack of a message this phone sent: it is the only thing that makes a
            // bubble say "sent", and a refusal is the one thing that makes it say
            // "failed" without the page having seen an exception. It carries no chat, so
            // the message is found by its id across the conversations in memory.
            if (update.IsAck)
            {
                SetMessageStatusEverywhere(update.Id,
                    update.IsRefused ? MessageStatus.Failed : MessageStatus.Sent);
                return;
            }

            SetMessageStatus(update.Jid, update.Id,
                update.IsRead ? MessageStatus.Read : MessageStatus.Delivered);
        }

        /// <summary>
        /// A wire timestamp in seconds as the local moment it happened. Zero is "the server
        /// did not say", and a row stamped with 1970 would sort itself to the bottom of the
        /// list forever.
        /// </summary>
        private static DateTime Moment(long seconds)
        {
            if (seconds <= 0) return DateTime.Now;

            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds).ToLocalTime();
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
            TrimToCeiling(_chatMessages[message.ChatId]);

            // Find or create contact for this chat
            var contact = FindContact(message.ChatId);
            if (contact == null)
            {
                string name = SavedNameFor(message.ChatId);
                if (string.IsNullOrEmpty(name)) name = message.SenderName;
                if (string.IsNullOrEmpty(name)) name = DisplayNameForJid(message.ChatId);

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
                PublishUnread(contact);
            }
            else
            {
                // A live message is also a name: `notify` is what the person calls
                // themselves, and a row that was a number until now becomes their name. A
                // group's message carries the participant's name, which is not the group's.
                // It does not beat the address book: the name saved on this phone is the
                // one the person chose, and the push name must not overwrite it.
                if (!message.IsGroup && !string.IsNullOrEmpty(message.SenderName)
                    && SavedNameFor(message.ChatId) == null)
                {
                    if (contact.Name != message.SenderName)
                    {
                        contact.Name = message.SenderName;
                        contact.Initials = InitialsFor(contact.Name);
                    }
                }

                // Update the contact preview and move to top
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                contact.LastMessageAt = message.Timestamp;
                if (message.IsIncoming)
                {
                    contact.UnreadCount++;
                    NotificationService.SetUnread(TotalUnread());
                    PublishUnread(contact);
                }

                // The row goes where its new time says, not to the top: the pinned
                // chats stay above it and every other chat keeps its chronological
                // place, as on WhatsApp (the newest conversation first).
                PlaceContact(contact);
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

                NotificationService.RememberSender(message.ChatId, contact.Name, tileAvatar, message.Text);
            }

            NotificationService.SetUnread(TotalUnread());

            // The page of this chat, if it is the one in front: it scrolls and tells
            // the server the message has been read only while the reader was already
            // at the bottom (see ChatPage.OnMessageArrived). It is raised here and not
            // by the native client, because a page cares about a row that is already
            // in the collection it binds.
            var arrived = MessageArrived;
            if (arrived != null) arrived(this, message);

            // Asynchronous decoding of the image: the XAML binding follows MediaImage
            if (message.Type == MessageType.Image)
                await message.LoadMediaImageAsync();
        }

        /// <summary>
        /// A message that arrived now, for the chat it belongs to. It is not the
        /// collection: that is `GetMessages`, which the conversation is bound to. This
        /// is the moment, and one page acts on it.
        /// </summary>
        public event EventHandler<ChatMessage> MessageArrived;

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
        /// The history burst of one chat has ended, which is the last message the sync
        /// had for it. The chat page waits
        /// for it before binding its list, because a bind while the burst is still
        /// inserting re-lays out the list on every frame. The argument is the chat
        /// id, carried in the frame's ChatId.
        /// </summary>
        public event EventHandler<string> HistoryCompleted;

        /// <summary>
        /// The bytes of a voice note are on disk. A page that asked for them because
        /// its play button was tapped plays the note now: without this the first tap
        /// downloaded and stopped there, the bar stayed silent, and the note only
        /// played on a second tap - which reads as a voice note that does not play.
        /// </summary>
        public event EventHandler<ChatMessage> AudioArrived;

        private void RaiseAudioArrived(ChatMessage message)
        {
            var handler = AudioArrived;
            if (handler != null) handler(this, message);
        }

        private void RaiseHistoryCompleted(string chatId)
        {
            var handler = HistoryCompleted;
            if (handler != null) handler(this, chatId);
        }

        /// <summary>
        /// The status broadcast and a channel are not conversations: WhatsApp keeps
        /// status updates in the Status section, and neither can be answered, so a row
        /// for one of them puts a conversation called "Status" next to the people.
        /// The row can arrive from the history sync or come from the copy already on
        /// the phone, and this is the one place both are filtered.
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

            // The name saved on this phone wins, then what the row carries (the sync's
            // name or the push name), then the number.
            string name = SavedNameFor(message.ChatId);
            if (string.IsNullOrEmpty(name)) name = message.SenderName;
            if (string.IsNullOrEmpty(name)) name = NameCache.Book.NameFor(message.ChatId);
            if (string.IsNullOrEmpty(name)) name = DisplayNameForJid(message.ChatId);

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
            if (message.Timestamp >= contact.LastMessageAt)
            {
                // The time orders the list even when the row carries no words (a
                // conversation whose last message is a media the sync did not
                // include): only the preview needs a text to replace.
                contact.LastMessageAt = message.Timestamp;
                if (!string.IsNullOrEmpty(message.Text))
                {
                    contact.LastMessage = message.Text;
                    contact.LastMessageTime = message.FormattedTime;
                }
            }

            // The profile picture. When the row carries it, the bytes are also kept
            // on the phone: at the next opening the list has a face before the sync
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
            // server.js, unreadByChat). The open chat is the exception: a server row
            // that still counts it (the `read` frame has not been processed yet) must
            // not put a number on a row the reader is looking at.
            contact.UnreadCount = message.ChatId == _activeChatId ? 0 : message.UnreadCount;
            contact.IsPinned = ChatPreferences.IsPinned(message.ChatId);
            contact.IsMuted = ChatPreferences.IsMuted(message.ChatId);
            PlaceContact(contact);

            PublishUnread(contact);

            RememberChatRow(message);
        }

        /// <summary>
        /// Renames the rows already in the list from the phone's address book. Called
        /// when that book answers, and once after the cached rows are applied: the two
        /// run in either order, and the name has to land whichever finishes second.
        ///
        /// The order of the list is not touched. It is by activity, and a name does not
        /// change when a conversation happened.
        /// </summary>
        public void ApplyAddressBook()
        {
            int renamed = 0;

            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact contact = _contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;

                string name = SavedNameFor(contact.Id);
                if (name == null || contact.Name == name) continue;

                contact.Name = name;
                contact.Initials = InitialsFor(name);
                renamed++;
            }

            if (renamed > 0)
            {
                Diag.Ok("contacts: " + renamed + " chat(s) renamed from the address book");
            }
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

            // The rows are already in the order the phone shows: every ApplyChat
            // placed its row by its last activity (`PlaceContact`), and the batch
            // order of the sync is NOT that order - it lists all the groups, then
            // all the people. This used to move each row to the index it had in the
            // batch, which put the old order back a moment after the sort.
            SortContacts();
            Diag.OkNow("order after the sync: " + OrderSample());

            _chatRows.Clear();
            _chatRows.AddRange(_freshChatRows);
            _freshChatRows.Clear();

#pragma warning disable 4014
            ChatCache.SaveAsync(_chatRows);
#pragma warning restore 4014
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
                    {
                        target.Type = MessageType.Audio;
                        // The note is on disk and the bar can play it: whoever asked
                        // for it hears it now instead of having to tap again.
                        RaiseAudioArrived(target);
                    }
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

        /// <summary>
        /// The conversation list has finished arriving, with how many rows it
        /// carried. It is raised after the last conversation the sync had, so this is
        /// the only place that can say the list was answered and was empty - which is
        /// what tells an empty account from a connection that never came up.
        /// </summary>
        public event EventHandler<int> ChatListCompleted;

        private void RaiseChatListCompleted(int rows)
        {
            var handler = ChatListCompleted;
            if (handler != null) handler(this, rows);
        }

        /// <summary>A call-log entry.</summary>
        private void AddCall(ChatMessage message)
        {
            if (string.IsNullOrEmpty(message.ChatId)) return;

            _calls.Add(new CallLogEntry
            {
                ChatId = message.ChatId,
                Name = SavedNameFor(message.ChatId) ?? (string.IsNullOrEmpty(message.SenderName)
                    ? DisplayNameForJid(message.ChatId)
                    : message.SenderName),
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
        /// Realigns the chat preview to the last remaining message: after a
        /// revocation or an edit the preview would stay the old one.
        /// </summary>
        /// <summary>
        /// The answer for an attachment: the bubble changes from Sending
        /// to Sent or Failed. It is written on the stored message and not on a
        /// page's copy, because the answer can arrive while another chat is on
        /// screen - or before the page that owns the bubble exists at all.
        /// </summary>
        public void SetMessageStatus(string chatId, string messageId, MessageStatus status)
        {
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(messageId)) return;

            ObservableCollection<ChatMessage> messages;
            if (!_chatMessages.TryGetValue(chatId, out messages)) return;

            foreach (var message in messages)
            {
                if (message.Id != messageId) continue;
                message.Status = status;
                return;
            }
        }

        /// <summary>
        /// The same, for a message whose chat the caller does not know - which is what an ack
        /// is: the server answers our stanza, not a conversation, and the id is the only name
        /// it carries. Ids are minted by this device, so one match is the whole search.
        /// </summary>
        private void SetMessageStatusEverywhere(string messageId, MessageStatus status)
        {
            if (string.IsNullOrEmpty(messageId)) return;

            foreach (var chat in _chatMessages)
            {
                foreach (var message in chat.Value)
                {
                    if (message.Id != messageId) continue;
                    message.Status = status;
                    return;
                }
            }
        }

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

        /// <summary>
        /// The name saved in the phone's address book for a chat, or null. It is the
        /// first source every caller tries: it is the name the person chose for that
        /// contact, and the one WhatsApp itself shows.
        ///
        /// The answer is also written into `NameBook` as a saved name, so the precedence
        /// the rest of the app applies (saved over push) holds and `NameCache` keeps it
        /// on the phone for the next launch, before the address book answers again.
        /// </summary>
        private static string SavedNameFor(string jid)
        {
            string name = ContactDirectory.NameForJid(jid);
            if (string.IsNullOrEmpty(name)) return null;

            NameCache.RememberSavedName(jid, name);
            return name;
        }

        /// <summary>Name shown for a JID when we do not know its name.</summary>
        public static string DisplayNameForJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "?";

            // The device comes off first: a JID that arrived as `15551230000:5@s.whatsapp.net`
            // used to name the row `15551230000:5`, and a chat list full of device numbers is
            // one nobody can read (`JidUtils.Normalize`).
            jid = JidUtils.Normalize(jid);

            // What the sync and the messages taught: the address book first, then the name the
            // contact uses, and - for a LID whose pair is known - the real phone number.
            string known = NameCache.Book.NameFor(jid);
            if (!string.IsNullOrEmpty(known)) return known;

            string phone = NameCache.Book.PhoneNumberFor(jid);
            if (phone != null) jid = phone;

            string user = jid.Split('@')[0];
            if (jid.EndsWith("@g.us")) return string.Format(Loc.Get("DataService_Group", "Group {0}"), user);
            if (user.Length >= 8 && user.All(char.IsDigit)) return "+" + user;

            // A `@lid` with no known number is not a number: its digits are an identifier, and
            // printing them as `+...` is exactly the wrong phone number the user saw.
            if (jid.EndsWith("@lid")) return user;

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

        /// <summary>
        /// The row of a chat, made when the list does not have it yet.
        ///
        /// Why it exists: a tile on the Start screen is tapped from outside the app, and a
        /// cold start opens the chat before the list has been filled - from the cache or
        /// from WhatsApp. A chat opened that way has to be a row like any other, or the page
        /// it opens on has nothing to name it by. The name carried in the tile's arguments
        /// is used when the chat's own is not known yet; the sync renames it like every row.
        /// </summary>
        public Contact ContactFor(string chatId, string name)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            Contact contact = FindContact(chatId);
            if (contact != null) return contact;

            string known = SavedNameFor(chatId);
            if (string.IsNullOrEmpty(known)) known = NameCache.Book.NameFor(chatId);
            if (string.IsNullOrEmpty(known)) known = name;
            if (string.IsNullOrEmpty(known)) known = DisplayNameForJid(chatId);

            contact = new Contact
            {
                Id = chatId,
                Name = known,
                Initials = InitialsFor(known),
                IsPinned = ChatPreferences.IsPinned(chatId),
                IsMuted = ChatPreferences.IsMuted(chatId)
            };

            _contacts.Insert(0, contact);
            _contactIndex[chatId] = contact;

            return contact;
        }

        /// <summary>
        /// The row's unread count changed: its pinned tile says so, if the chat has one.
        ///
        /// One place, because the count is written from several: a message into a new row, a
        /// message into an existing one, the sync's own number, the two read paths and a
        /// deletion. A tile that missed one of them keeps a number nobody can clear.
        /// </summary>
        private static void PublishUnread(Contact contact)
        {
            if (contact == null || string.IsNullOrEmpty(contact.Id)) return;

            string avatar = contact.AvatarData;
            if (string.IsNullOrEmpty(avatar)) avatar = AvatarCache.Get(contact.Id);

            PinnedTile.Update(contact.Id, contact.Name, contact.UnreadCount, avatar);
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
        /// In the right place and not at the bottom: the sync does not promise the
        /// order, and reopening a chat means the old
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
            TrimToCeiling(list);
        }

        private static void TrimToCeiling(ObservableCollection<ChatMessage> list)
        {
            // while and not if: a burst can add several at once, and one trim per
            // add would leave the list over the ceiling until the next message.
            while (list.Count > MaxMessagesPerChat) list.RemoveAt(0);
        }

        public void AddMessage(string chatId, ChatMessage message)
        {
            if (!_chatMessages.ContainsKey(chatId))
            {
                _chatMessages[chatId] = new ObservableCollection<ChatMessage>();
            }
            _chatMessages[chatId].Add(message);
            TrimToCeiling(_chatMessages[chatId]);

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
                    PublishUnread(contact);
                }

                // The row goes where its time says: a history message is not news,
                // and putting it at the top would lift an old conversation above
                // the recent ones (the sort is PlaceContact's).
                PlaceContact(contact);
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
            if (contact != null) contact.UnreadCount = 0;

            // The tile stops giving this chat a turn: its message is read, and a queue
            // that kept it would keep showing a conversation nobody is waiting for.
            if (!string.IsNullOrEmpty(chatId)) NotificationService.Forget(chatId);

            // And the chat's own tile says the number is gone.
            PublishUnread(contact);

            SendRead(chatId);
            NotificationService.SetUnread(TotalUnread());
        }

        /// <summary>
        /// Every conversation at once: the reader cleared the whole list, not one
        /// chat. The receipt is the same "read" the chat page sends - the protocol has
        /// no separate read-all - so a chat with nothing unread is left alone instead
        /// of paying for a receipt of its own.
        /// </summary>
        public void ClearAllUnread()
        {
            var rows = new List<Contact>();
            for (int i = 0; i < _contacts.Count; i++)
            {
                var contact = _contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                if (contact.UnreadCount > 0) rows.Add(contact);
            }

            if (rows.Count == 0) return;

            // The counts go to zero together and the badge is rewritten once:
            // ClearUnread on its own would recompute the total for every row.
            for (int i = 0; i < rows.Count; i++)
            {
                rows[i].UnreadCount = 0;
                PublishUnread(rows[i]);
            }
            NotificationService.ForgetAll();
            NotificationService.SetUnread(TotalUnread());
            for (int i = 0; i < rows.Count; i++) SendRead(rows[i].Id);

            Diag.Ok("read all: " + rows.Count + " chat(s)");
        }

        /// <summary>
        /// Tells the other side that a conversation has been read, or remembers to
        /// tell it. The check is on the connection, not on the count alone: a
        /// socket the OS already closed still looks connected, and the frame
        /// written into it is lost. A read that cannot go out now is queued and
        /// retried when a connection is established, so it is never lost.
        /// </summary>
        private void SendRead(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            if (!NativeConnection.Instance.IsConnected)
            {
                // A read that cannot go out now is queued and retried when an account is
                // open again, so it is never lost.
                _pendingReads.Add(chatId);
                return;
            }

            _pendingReads.Remove(chatId);
#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/read", MarkReadAsync(chatId));
#pragma warning restore 4014
        }

        /// <summary>
        /// The receipt for a whole chat: every message of it that came from the other side.
        ///
        /// One receipt per batch rather than one per message - the node carries a list of
        /// ids, and a chat opened after a day away has a hundred of them, which is a hundred
        /// frames if this is written the obvious way.
        /// </summary>
        private async Task MarkReadAsync(string chatId)
        {
            var ids = new List<string>();
            ObservableCollection<ChatMessage> messages = GetMessages(chatId);

            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage message = messages[i];
                if (message == null || !message.IsIncoming) continue;
                if (message.Status == MessageStatus.Read) continue;
                if (string.IsNullOrEmpty(message.Id)) continue;

                ids.Add(message.Id);
            }

            await NativeConnection.Instance.MarkReadAsync(chatId, ids);
        }

        /// <summary>
        /// A live connection exists: every read that could not be sent goes out
        /// now, in the order it was made.
        /// </summary>
        private void OnNativeConnectionStatus(object sender, string status)
        {
            if (!NativeConnection.Instance.IsConnected) return;

            OnConnectionEstablished(sender, EventArgs.Empty);
        }

        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            if (_pendingReads.Count == 0) return;

            var chats = new List<string>(_pendingReads);
            _pendingReads.Clear();
            for (int i = 0; i < chats.Count; i++) SendRead(chats[i]);
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
            NotificationService.Forget(chatId);
            NotificationService.SetUnread(TotalUnread());

#pragma warning disable 4014
            MessageCache.DeleteAsync(chatId);
#pragma warning restore 4014
        }

        /// <summary>
        /// The three counts the home tab strip shows as badges, read out of the same
        /// collections the lists bind: unread messages, statuses not watched yet, and
        /// missed calls in the log. They are answers about the present, so they are
        /// counted when asked instead of kept in a field that could drift.
        /// </summary>
        public int UnreadChats()
        {
            return TotalUnread();
        }

        public int UnseenStatuses()
        {
            int total = 0;
            for (int i = 0; i < _statuses.Count; i++)
            {
                ChatMessage row = _statuses[i];
                if (row != null && !row.IsStatusViewed) total++;
            }
            return total;
        }

        public int MissedCalls()
        {
            int total = 0;
            for (int i = 0; i < _calls.Count; i++)
            {
                CallLogEntry row = _calls[i];
                // Two vocabularies meet here: the old server said `timeout`, the native
                // client says `missed`. A badge that only knew the first was a badge that
                // never counted a missed call.
                if (row != null && (string.Equals(row.Reason, "timeout", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.Reason, "missed", StringComparison.OrdinalIgnoreCase))) total++;
            }
            return total;
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
        /// so the relative order of the others does not change. It only moves the pin:
        /// the date order is what `PlaceContact` keeps (see there).
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

        /// <summary>
        /// Puts a row where the list as WhatsApp shows it has it: the pinned chats first,
        /// then every conversation by its last activity, newest above oldest.
        ///
        /// Why not `Insert(0)` and a pinned pass as before: the chat list arrives in the
        /// order of WhatsApp's history sync, which groups conversations by type - all the
        /// groups, then all the people - and that is not the order the phone shows. The
        /// row is moved to the count of rows that belong above it, and because every
        /// other row is already in that order one move is enough (`ResortContacts` keeps
        /// that invariant when a pin changes).
        /// </summary>
        private void PlaceContact(Contact contact)
        {
            int at = _contacts.IndexOf(contact);
            if (at < 0) return;

            int target = 0;
            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact other = _contacts[i];
                if (other == null || other == contact) continue;
                if (ComesBefore(other, contact)) target++;
            }

            if (at != target) _contacts.Move(at, target);
        }

        /// <summary>
        /// Orders the whole list once, by activity, pinned first. `PlaceContact` keeps
        /// that order while rows arrive; this is the guard at the end of a batch, where
        /// one row that never answered with a time is enough to leave the list in the
        /// order the sync sent it (all the groups, then all the people). A stable
        /// selection sort over the collection: equal times keep their relative place.
        /// </summary>
        private void SortContacts()
        {
            for (int i = 0; i < _contacts.Count; i++)
            {
                int best = i;
                for (int j = i + 1; j < _contacts.Count; j++)
                {
                    if (ComesBefore(_contacts[j], _contacts[best])) best = j;
                }

                if (best != i) _contacts.Move(best, i);
            }
        }

        /// <summary>
        /// Whether `left` belongs above `right` in the list: a pinned row above an
        /// unpinned one, and otherwise the most recent activity above the oldest. A tie
        /// keeps the order the rows already have, so a batch does not shuffle equal
        /// times (a conversation with no message has `DateTime.MinValue`).
        /// </summary>
        private static bool ComesBefore(Contact left, Contact right)
        {
            if (left.IsPinned != right.IsPinned) return left.IsPinned;
            return left.LastMessageAt > right.LastMessageAt;
        }

        /// <summary>The head of the list, for the log: it says whether the order the
        /// phone paints is the order this service built.</summary>
        private string OrderSample()
        {
            string text = "";
            for (int i = 0; i < _contacts.Count && i < 6; i++)
            {
                Contact row = _contacts[i];
                if (row == null) continue;
                if (text.Length > 0) text += " | ";
                text += row.Name + "@" + row.LastMessageAt.ToString("dd/MM HH:mm");
            }
            return text;
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
