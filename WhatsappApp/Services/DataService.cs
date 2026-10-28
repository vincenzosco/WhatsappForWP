using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WhatsappApp.Controls;
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

        // How long the list waits after the last row changed before it is written. Long
        // enough that a burst of messages is one write, short enough that a run killed a
        // moment later still left the row on the phone (see ScheduleListSave).
        private static readonly TimeSpan ListSaveQuiet = TimeSpan.FromSeconds(3);

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

        /// <summary>
        /// Whether the pinned tiles have been published since the login completed. One sweep
        /// per run and not one per status change: the connection says something every few
        /// seconds, and the sweep asks WhatsApp a question per pinned chat.
        /// </summary>
        private bool _pinnedTilesAfterLogin;

        // The chat-list rows as the server last sent them (for the cache) and the
        // ones that are arriving now.
        private readonly List<ChatMessage> _freshChatRows = new List<ChatMessage>();

        // The pairs of rows already named as one chat in the log, so a duplicate the
        // fold cannot take is written once and not once per refresh.
        private readonly HashSet<string> _namedDuplicates =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>How many duplicate pairs the log names: a bounded log, like the rest.</summary>
        private const int NamedDuplicateLimit = 32;

        // Whether a chat list has started and not finished. The batch cannot answer that
        // itself: once the cached snapshot has registered its rows it is never empty again.
        private bool _batchOpen;

        // Index by id: without this every incoming message scanned the whole
        // contact list (FirstOrDefault) to find the chat.
        private readonly Dictionary<string, Contact> _contactIndex =
            new Dictionary<string, Contact>();
        private Contact _selectedContact;
        private string _connectionStatus;
        private bool _isServerRunning;
        private string _activeChatId;
        private bool _listening;

        // Whether a write of the chat list is already waiting for the quiet to pass: a
        // burst of messages is one write, not one per row. Set and cleared with Interlocked
        // because the write runs on whichever thread the message arrived on.
        private int _listSaveDue;

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
            NativeConnection.Instance.MessageRevoked += OnMessageRevoked;
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

            // And the status feed of the last day. Why it needs a copy of its own: the
            // statuses arrive with the history sync, and WhatsApp sends that burst once,
            // at the pairing - so without this every start opened the status tab empty and
            // it stayed empty until somebody posted again.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/cached statuses", LoadCachedStatusesAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// The status feed as it was when the app was last open, minus what is older than
        /// the day a status lives. Each row goes in only if the connection has not already
        /// told the same one: the sync can finish before this file is read.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCachedStatusesAsync()
        {
            List<ChatMessage> cached = await StatusCache.LoadAsync();
            if (cached.Count == 0) return;

            int added = 0;
            for (int i = 0; i < cached.Count; i++)
            {
                ChatMessage row = cached[i];
                if (row == null) continue;
                if (FindStatus(row.Id) != null) continue;

                InsertStatusInOrder(row);
                added++;
            }

            if (added == 0) return;

            RaiseStatusesChanged();
            Diag.Ok("status: " + added + " status(es) of the last day are back on screen");
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

            // The burst of history is pushed once per link, at the pairing
            // (`NativeConnection`: "WhatsApp sends it once, at the pairing"). A start
            // that opens on a copy written by a sync that had finished has nothing
            // left to wait for, and the conversation page is the one that needs to
            // know: it waited twenty seconds for that burst before binding a chat, so
            // every start after the first was twenty seconds of an empty screen for a
            // burst that was never coming. The messages themselves are on the phone
            // from this build on (the save at the end of `OnNativeHistoryFinished`).
            if (cached.HistoryComplete) HistoryComplete = true;

            for (int i = 0; i < cached.Chats.Count; i++)
            {
                // A copy that a torn write left with a null row is one row this app cannot
                // draw, not a list that stops loading at it.
                if (cached.Chats[i] == null) continue;

                ApplyChat(cached.Chats[i]);
            }
            SortContacts();

            // The address book may have answered before there were rows to rename: the
            // other order (rows first) is covered by ContactDirectory calling this too.
            ApplyAddressBook();

            Diag.OkNow("order after the cached list: " + OrderSample());

            // And the conversations this file does not name, given back from their tails.
            // The screen draws `chats.json` and WhatsApp names the list again only at a
            // link, so a tail with no row is a conversation with no way back on screen
            // (see RestoreRowsFromTheirTailsAsync).
            await RestoreRowsFromTheirTailsAsync();

            PublishBadge();

            // And the tiles this phone has on Start are drawn again from what the copy says.
            // Without this a pinned tile kept the card the build that composed it left
            // behind, and a change to how the card is drawn reached the phone only when the
            // next message happened to arrive in that chat - which is why the fix of
            // 23 October 2026 could ship and leave the Start screen exactly as it was.
            PublishPinnedTiles();
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
            //
            // The flag and not `_freshChatRows.Count == 0`: the cached rows are in the
            // batch, so that test was only ever true when there was nothing to clear - a
            // guard that could not fire, and the batch of the phone run of 2026-10-22 was
            // the previous session's rows followed by the new ones.
            if (!_batchOpen) BeginChatList();

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

            // A moment that cannot be one is no moment: the row goes to the bottom of the
            // list and the copy written back to disk does not carry the wrong date forward.
            if (!ActivityTime.IsSane(message.Timestamp, DateTime.Now)) message.Timestamp = DateTime.MinValue;

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

            // The sync is the other way a chat becomes a row with a count: a tile pinned on
            // a chat the copy did not have yet gets its picture now, not at the next message.
            PublishPinnedTiles();

            // What the sync brought, now that it has nothing left to send: see
            // SaveSnapshotAsync for why this is not left to the pages.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/save snapshot", SaveSnapshotAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// The list and the conversations as they are now, written to the phone for the
        /// next start.
        ///
        /// Why the app has to say it and the sync alone will not do: a run of this app
        /// can end without a sync. `BackgroundSync` reads the backlog in a process of
        /// its own, draws the tile and closes (a message that arrives with the app closed
        /// is announced that way and no other), and everything it read died with that
        /// process - while WhatsApp counts those messages as delivered and never sends
        /// them again (`the backlog ended with no chat and no history`, the run of
        /// 2026-10-22). The phone that opened next found neither the backlog nor a row
        /// that mentioned it: the tile had said a message arrived and the list showed
        /// nothing new. The same for the app itself: a suspension can be a termination.
        ///
        /// Written here rather than in each caller so there is one shape of the file, and
        /// one place that decides what it holds: the rows in the order the list shows them,
        /// and the tail of every conversation that has one.
        /// </summary>
        public async Task SaveSnapshotAsync()
        {
            // Both taken in one go, without an await in the middle: the rows and the
            // messages written here have to be one instant of the list, not the list as it
            // was before the first write and as it is after the last.
            List<ChatMessage> rows = RowsForTheList();
            List<KeyValuePair<string, ChatMessage[]>> tails = TailsForTheConversations();

            // A list with no rows is not a snapshot of anything: the copy on the phone
            // stays, and the next start shows it instead of nothing.
            if (rows.Count == 0) return;

            // **The list first, the conversations after it**, and the order is the whole
            // point. Every tail is a read and a write of its own file, one at a time through
            // `MessageCache`'s queue: on the phone of 23 October 2026 56 of them took the
            // better part of two minutes (56 `messages_*.json` against 169 rows), and the list
            // was written *after* them. A close - which is what closing the app is - landed in
            // the middle, `chats.json` was never written at all, and the next start drew an
            // empty screen until a message arrived carrying a row of its own. The list is what
            // that screen draws; the tails are a convenience.
            await ChatCache.SaveAsync(rows, HistoryComplete);

            Diag.Ok("cache: the list written, " + rows.Count + " row(s)");

            for (int i = 0; i < tails.Count; i++)
            {
                // Merged with the file and not written over it: this process may hold
                // only the messages that arrived while it was up, and the tail that was
                // already on the phone is still the conversation.
                await MessageCache.AppendAsync(tails[i].Key, tails[i].Value);
            }

            // Named, because it is the second half of the snapshot and a close can land in
            // the middle of it: a run whose log stops at the line above lost only the tails.
            Diag.Ok("cache: " + tails.Count + " conversation(s) written");
        }

        /// <summary>
        /// The rows of the list as they are now, in the order the list shows them.
        ///</summary>
        private List<ChatMessage> RowsForTheList()
        {
            var rows = new List<ChatMessage>();

            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact contact = _contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;

                var row = new ChatMessage();
                row.ChatId = contact.Id;
                row.SenderName = contact.Name;
                row.Text = contact.LastMessage;
                row.Timestamp = contact.LastMessageAt;
                row.IsGroup = contact.Id.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
                rows.Add(row);
            }

            return rows;
        }

        /// <summary>
        /// The conversations that have a tail in memory, keyed on their chat.
        ///</summary>
        private List<KeyValuePair<string, ChatMessage[]>> TailsForTheConversations()
        {
            var tails = new List<KeyValuePair<string, ChatMessage[]>>();

            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact contact = _contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;

                ObservableCollection<ChatMessage> messages;
                if (!_chatMessages.TryGetValue(contact.Id, out messages)) continue;
                if (messages == null || messages.Count == 0) continue;

                var tail = new ChatMessage[messages.Count];
                messages.CopyTo(tail, 0);
                tails.Add(new KeyValuePair<string, ChatMessage[]>(contact.Id, tail));
            }

            return tails;
        }

        /// <summary>
        /// The list is written a few seconds after the last row changed, on its own.
        ///
        /// Why it cannot wait for the end of the run: every writer this service had was an
        /// ending - the end of a sync, a suspension, the end of a background check - and a
        /// run does not always end that way. On 24 October 2026 the app was stopped from the
        /// debugger with a row that had just arrived: no suspension, so no snapshot, and the
        /// next start drew the list of the day before (2 rows against 56 `messages_*.json`
        /// on the phone). A crash, a force close and a battery pulled out all end the same
        /// way. Three seconds of quiet after the last change is a write that has already
        /// happened by the time any of those do.
        ///
        /// Only the list: the conversations' tails stay where they are, at the ending. This
        /// is the cheap half of the snapshot (one small file), and the reason it can run
        /// while the app is in use at all.
        /// </summary>
        private void ScheduleListSave()
        {
            if (Interlocked.CompareExchange(ref _listSaveDue, 1, 0) != 0) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/save list", SaveListSoonAsync());
#pragma warning restore 4014
        }

        private async Task SaveListSoonAsync()
        {
            await Task.Delay(ListSaveQuiet);

            // Cleared before the write and not after: a change that arrives while this one
            // is being written has to be able to ask for another, and a change that arrives
            // before the snapshot below is in it.
            Interlocked.Exchange(ref _listSaveDue, 0);

            try
            {
                await SaveListAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("DataService/save list", ex);
            }
        }

        /// <summary>The list alone, with no tails: the half a run that is killed must not lose.</summary>
        private async Task SaveListAsync()
        {
            List<ChatMessage> rows = RowsForTheList();
            if (rows.Count == 0) return;

            await ChatCache.SaveAsync(rows, HistoryComplete);
            Diag.Ok("cache: the list written, " + rows.Count + " row(s), mid-run");
        }

        /// <summary>
        /// The conversations the list lost, given back from their tails.
        ///
        /// Why it is needed: the screen draws `chats.json` and nothing else, while the
        /// history that fills it arrives once, at the pairing. On the phone of 24 October
        /// 2026 that file held 2 rows against 56 `messages_*.json`: every conversation was
        /// still on the phone, 54 of them were not on the list, and no server was going to
        /// name them again (`the backlog ended with no chat and no history`). A tail is a
        /// row - who, when, the last words - so a chat with a tail and no row is a row this
        /// app can draw.
        ///
        /// Cheap when there is nothing to do: one directory listing, and the reading starts
        /// only when there are more tail files than rows. A chat deleted from this phone
        /// loses its tail too (`MessageCache.DeleteAsync`), so the two counts stay
        /// comparable.
        /// </summary>
        private async Task RestoreRowsFromTheirTailsAsync()
        {
            int tails = await MessageCache.CountAsync();
            if (tails <= _contacts.Count) return;

            List<KeyValuePair<string, List<ChatMessage>>> all = await MessageCache.TailsAsync();

            int rows = 0;
            for (int i = 0; i < all.Count; i++)
            {
                string chatId = all[i].Key;
                if (string.IsNullOrEmpty(chatId)) continue;
                if (FindContact(chatId) != null) continue;

                ChatMessage last = NewestMessage(all[i].Value);
                if (last == null) continue;

                var row = new ChatMessage();
                row.ChatId = chatId;
                row.Text = last.Text;
                row.Timestamp = last.Timestamp;
                row.IsGroup = chatId.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);

                // The name is not the tail's: the step that names a row - the address book,
                // the LID pairs, the number - is `ApplyChat`, and this row takes it there.
                // So does the refusal: a hidden chat or a channel is not a row.
                ApplyChat(row);

                if (FindContact(chatId) != null) rows++;
            }

            if (rows == 0) return;

            SortContacts();
            Diag.Ok("cache: " + rows + " conversation(s) given back by their tails (" + tails
                + " on the phone)");

            // Straight to disk: the rows this just recovered are the ones a kill in the next
            // second would take away again.
            ScheduleListSave();
        }

        /// <summary>The newest message of a tail, or null for one that holds none.</summary>
        private static ChatMessage NewestMessage(List<ChatMessage> messages)
        {
            if (messages == null) return null;

            ChatMessage newest = null;
            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage message = messages[i];
                if (message == null) continue;
                if (newest == null || message.Timestamp > newest.Timestamp) newest = message;
            }

            return newest;
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

                    // The name: a row called `Gruppo 1203...` or `+39389...` is a description
                    // of its address, and it must not beat the name the other row carries.
                    string name = ChatListMerge.BestName(keep.Name, keep.Id, gone.Name, gone.Id);
                    if (!string.IsNullOrEmpty(name) && name != keep.Name)
                    {
                        keep.Name = name;
                        keep.Initials = InitialsFor(name);
                    }

                    keep.UnreadCount += gone.UnreadCount;

                    // The merged row has a new number and the tile of the chat is the one
                    // place that had not heard: the count went up here and the badge kept
                    // the old one until somebody wrote in that chat again.
                    PublishUnread(keep);

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

            PublishBadge();
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
                // A null entry is a row this app cannot draw: skipping it leaves the log it
                // came with, where throwing would leave the page on its spinner forever.
                if (entries[i] == null) continue;

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
            bool inserted = row == null;
            if (row == null)
            {
                row = new ChatMessage();
                row.Id = post.Id;
                row.ChatId = post.Author;
                row.IsIncoming = true;
                row.Type = MessageType.Text;
                row.Timestamp = Moment(post.Timestamp);
                InsertStatusInOrder(row);
            }

            row.SenderName = DisplayNameForJid(post.Author);
            if (!string.IsNullOrEmpty(post.Text)) row.Text = post.Text;

            ApplyNativeMedia(row, post.Encoded);

            // The spinner says the photo is on its way, and a row that already draws one is not
            // waiting for anything: the sync repeats the same status in every chunk, so this
            // line put a photo that had already arrived back under a spinner - and the viewer,
            // which asks for what is missing, asked again.
            bool waiting = row != null
                && string.IsNullOrEmpty(row.MediaData)
                && string.IsNullOrEmpty(row.MediaFilePath);
            if (waiting && row.Media != null && row.Media.IsFetchable) row.IsMediaLoading = true;

            Diag.Ok("native: status " + post.Id + " from " + post.Author);
            RaiseStatusesChanged();

            // A new update is the one change worth a write: the feed is what the next
            // start draws before any connection exists, and the media keys it carries
            // are what makes a tap on a restored status download its photo.
            if (inserted)
            {
                SaveStatusSnapshot();

                // And it is announced, once per row and never for the user's own: the
                // toast is the only thing that puts an update in the notification area
                // while the app is closed (`BackgroundSync` reads the same rows in its
                // own process, and the watermark in `NotificationService` is what keeps
                // the two from announcing the same one twice).
                string mine = ChatIdFor(NativeConnection.Instance.Jid);
                if (string.IsNullOrEmpty(mine)
                    || !string.Equals(ChatIdFor(post.Author), mine, StringComparison.OrdinalIgnoreCase))
                {
                    NotificationService.ShowStatus(row.SenderName, row.Timestamp);
                }
            }
        }

        /// <summary>
        /// Puts a status where its time says: newest first.
        ///
        /// Why not `Insert(0)`: the feed has three arrivals and only one of them is the
        /// newest. A live status is, the copy on the phone is older, and the history's chunk
        /// is a whole day of them arriving at once - so inserting every arrival at the top
        /// put an update from yesterday above the one somebody posted a minute ago. The row
        /// is placed after every status that is at least as new, which keeps the order the
        /// feed is read in even when the sync re-sends its chunks.
        /// </summary>
        private void InsertStatusInOrder(ChatMessage row)
        {
            int at = 0;
            while (at < _statuses.Count)
            {
                ChatMessage other = _statuses[at];
                if (other == null || other.Timestamp < row.Timestamp) break;
                at++;
            }

            _statuses.Insert(at, row);
        }

        /// <summary>
        /// How long the feed has to stand still before it is written.
        ///
        /// Why the wait: the history burst inserts one row per frame, and a write per row
        /// rewrote the whole file dozens of times on the 512 MB phone the cache exists for -
        /// the JSON of the day serialised once per status, on the thread that is also drawing
        /// the feed. A second and a half is longer than the gap between two rows of the burst
        /// and shorter than a person's patience with a value that is only a photograph of the
        /// screen (`StatusCache`): the next connection re-tells the feed anyway.
        /// </summary>
        private const int StatusSnapshotQuietMilliseconds = 1500;

        /// <summary>How many times the feed has changed since the file was last written.</summary>
        private int _statusSnapshotRequested;

        /// <summary>The count the file on the phone was written at.</summary>
        private int _statusSnapshotWritten;

        /// <summary>One writer at a time: two of them would race on the same temp file.</summary>
        private bool _statusSnapshotRunning;

        /// <summary>
        /// The feed into its file on the phone, once the burst is done.
        ///
        /// The write is coalesced: a hundred statuses of a sync are one file write and not a
        /// hundred, which is what a write per insert was. The list is copied on the UI thread
        /// when the write starts, so the file is one instant of the feed and not the feed as
        /// it was before the first write and after the last.
        /// </summary>
        private void SaveStatusSnapshot()
        {
            _statusSnapshotRequested++;
            StartStatusSnapshotWriter();
        }

        /// <summary>Starts the writer, unless one is already waiting for the feed to settle.</summary>
        private void StartStatusSnapshotWriter()
        {
            if (_statusSnapshotRunning) return;
            _statusSnapshotRunning = true;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/status snapshot", WriteStatusSnapshotAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// Waits for the feed to stand still, writes it, and writes it again only if it changed
        /// while the file was being written.
        /// </summary>
        private async Task WriteStatusSnapshotAsync()
        {
            try
            {
                while (true)
                {
                    // One quiet window: the version is taken before the wait, so a row that
                    // arrives while it runs makes the wait start over instead of being lost.
                    int version;
                    do
                    {
                        version = _statusSnapshotRequested;
                        await Task.Delay(StatusSnapshotQuietMilliseconds);
                    }
                    while (version != _statusSnapshotRequested);

                    var snapshot = new List<ChatMessage>(_statuses);
                    await StatusCache.SaveAsync(snapshot);

                    _statusSnapshotWritten = version;
                    if (version == _statusSnapshotRequested) return;
                }
            }
            finally
            {
                _statusSnapshotRunning = false;

                // The flag is cleared after the last check, so a request that arrives in
                // that window would have found the writer busy and returned: it is asked
                // for again here instead of being dropped.
                if (_statusSnapshotRequested != _statusSnapshotWritten) StartStatusSnapshotWriter();
            }
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

            // The flag is raised here and not by the page that asks: a request takes a
            // moment to be sent, and the screen that draws the row in that moment has to
            // find it already spinning. Without this the first look at a status whose
            // photo is on its way is the "could not be loaded" panel with a button that
            // is live while the download it would repeat is already running.
            row.IsMediaLoading = true;

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

            // The one thing in the feed that changes without a new status: which updates
            // were already watched. It is on the phone so a restart does not put the badge
            // back on a status somebody already saw.
            SaveStatusSnapshot();

            // And it is announced, because two things count it: the ring of the row, and
            // the number on the Status tab (`UnseenStatuses`). Neither is told by the row's
            // own property change, which reaches only the bubble that bound it.
            RaiseStatusesChanged();

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
        /// Drops the updates whose day is over.
        ///
        /// The feed is pruned when it is read from the phone and when it is written, and
        /// neither of those happens to a list that is already on screen: a tab left open
        /// across midnight kept offering a status nobody can open any more, and the round
        /// trip it answers with is a download of a file WhatsApp has already taken down.
        /// Called by the pane when the tab comes to the front, which is the one moment
        /// somebody is looking at the list. The day is `StatusCache.LifetimeHours` in both
        /// places, so the screen and the file cannot disagree about when an update ends.
        /// </summary>
        public void PruneExpiredStatuses()
        {
            DateTime cutoff = DateTime.Now.AddHours(-StatusCache.LifetimeHours);

            int removed = 0;
            for (int i = _statuses.Count - 1; i >= 0; i--)
            {
                ChatMessage row = _statuses[i];

                // A row with no time is not a row with an old time: an empty one has
                // nothing to draw and nothing to open.
                if (row == null || row.Timestamp < cutoff)
                {
                    _statuses.RemoveAt(i);
                    removed++;
                }
            }

            if (removed == 0) return;

            RaiseStatusesChanged();
            SaveStatusSnapshot();
            Diag.Ok("status: " + removed + " update(s) lived their day and left the feed");
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
            // The route is the other half of the same news: a status that arrived while the
            // app was closed has no badge of its own, so this is the only place the tile's
            // destination learns that tapping it should open the Status tab.
            RefreshLaunchRoute();

            var handler = StatusesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        /// <summary>
        /// A message taken back by whoever wrote it: the row goes, and it goes the same way
        /// as a message deleted here - one removal for both, so the copy on the phone cannot
        /// disagree with the screen. What differs is only who asked.
        /// </summary>
        private void OnMessageRevoked(object sender, MessageRevocation revocation)
        {
            if (revocation == null || string.IsNullOrEmpty(revocation.Jid)) return;

            RemoveMessage(revocation.Jid, revocation.Id);
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

            // `seconds` is an unvalidated number off the wire: in milliseconds (a value a
            // swapped build wrote), or simply corrupt, it is past the last moment a DateTime
            // can hold, and `AddSeconds` throws where a row should simply have no date.
            if (seconds > LastSecond) return DateTime.MinValue;

            return Epoch.AddSeconds(seconds).ToLocalTime();
        }

        /// <summary>1970-01-01T00:00:00Z, the zero of a wire timestamp.</summary>
        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>The last second a DateTime can hold, in Unix seconds (9999-12-31T23:59:59Z).</summary>
        private const long LastSecond = 253402300799L;

        private async void OnNetworkMessageReceived(object sender, ChatMessage message)
        {
            if (message == null) return;

            // Ignore system/handshake messages
            if (message.Type == MessageType.System) return;

            // One spelling per chat: a message that arrives under a `@lid` (or with a device
            // still on it) belongs to the row filed under the number - the row the reader
            // opened, and the one the preview of this conversation is on.
            message.ChatId = ChatIdFor(message.ChatId);

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
                    // The count has one owner: the native chat list increments its row as it
                    // takes the message in, and `ApplyChat` copies that number here. Counting
                    // again on this path is how the first message of a chat read 2, and how a
                    // count the reader had cleared came back and grew. The chat on screen is
                    // the exception it has always been: nothing on it is unread.
                    contact.UnreadCount = message.ChatId == _activeChatId
                        ? 0
                        : NativeConnection.Instance.UnreadFor(message.ChatId);
                    PublishBadge();
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

            PublishBadge();

            // The list a start draws is this one:
            ScheduleListSave();

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
            // The rule is the protocol's (`JidUtils.IsConversation`), not a second copy of it
            // here: a chat list keyed on anything the server names had a row called "0" in
            // it - `0@s.whatsapp.net`, the address an offline marker comes from - next to the
            // status list and the channels WhatsApp no longer calls chats.
            return !JidUtils.IsConversation(chatId);
        }

        /// <summary>
        /// A chat-list row: the conversation exists in WhatsApp even if in this
        /// session we never received a message from it.
        /// </summary>
        private void ApplyChat(ChatMessage message)
        {
            ApplyChat(message, true);
        }

        /// <summary>
        /// The name the list shows for a row: the one saved on this phone first, then what
        /// the row carries (the sync's name or the push name), then the number. Shared by the
        /// row that is applied and the refresh's question of whether it would change
        /// anything, so the two can never disagree about what the row says.
        /// </summary>
        private string NameFor(ChatMessage message)
        {
            string name = SavedNameFor(message.ChatId);
            if (string.IsNullOrEmpty(name)) name = message.SenderName;
            if (string.IsNullOrEmpty(name)) name = NameCache.Book.NameFor(message.ChatId);
            if (string.IsNullOrEmpty(name)) name = DisplayNameForJid(message.ChatId);
            return name;
        }

        /// <summary>
        /// The same, with the one choice the refresh makes: whether this row is news.
        ///
        /// A row re-read by `RefreshChatList` is not - it is what the list already says - and
        /// registering it as a fresh row would rewrite the list file every thirty seconds.
        /// </summary>
        private void ApplyChat(ChatMessage message, bool remember)
        {
            if (message == null) return;

            // One spelling per chat, before anything is keyed on it: the row, its messages,
            // its count and its pin are all keyed on `ChatId`.
            message.ChatId = ChatIdFor(message.ChatId);

            if (string.IsNullOrEmpty(message.ChatId)) return;
            if (IsNotAConversation(message.ChatId)) return;

            // A chat deleted from this phone does not come back with the server list:
            // the decision lives on the phone (ChatPreferences) and only a new
            // message undoes it.
            if (ChatPreferences.IsHidden(message.ChatId)) return;

            var contact = FindContact(message.ChatId);

            // The name saved on this phone wins, then what the row carries (the sync's
            // name or the push name), then the number.
            string name = NameFor(message);

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
            //
            // And only when the time it brings can be one. A row dated in the future
            // (the copy on disk that opened the list on 2026-10-22 carried several, a
            // day/month swap of the real dates) is not newer than anything: it keeps
            // the time the row already had, and a row with no believable time at all
            // stays at the bottom until the server sends one that can be true.
            bool believable = ActivityTime.IsSane(message.Timestamp, DateTime.Now);

            if (believable && message.Timestamp >= contact.LastMessageAt)
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
            else if (!believable && string.IsNullOrEmpty(contact.LastMessage)
                && !string.IsNullOrEmpty(message.Text))
            {
                // It cannot say when, but it can say what: the row shows the words and
                // no date, which is honest, instead of a date that is not one.
                contact.LastMessage = message.Text;
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

            if (remember) RememberChatRow(message);
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

            // The row is on the phone from now, not from the next graceful ending: the run
            // of 24 October 2026 was stopped from the debugger and took its rows with it
            // (see ScheduleListSave).
            ScheduleListSave();
        }

        /// <summary>
        /// A chat list is about to arrive. The rows of the cached snapshot went
        /// through ApplyChat at startup, and each of them registered itself in the
        /// batch: without this the batch - and so the file written back, and the
        /// order taken from it - is the previous session, not the server one.
        /// </summary>
        public void BeginChatList()
        {
            _batchOpen = true;
            _freshChatRows.Clear();
        }

        /// <summary>
        /// The list has finished arriving: the rows go where their time says, and the
        /// batch that just arrived restarts from scratch.
        ///
        /// The copy on the phone is not written here any more. It used to be, out of the
        /// batch, which meant the file only ever held what a sync brought - and a sync
        /// happens at a link and then never again, so every later message, and every
        /// message a background check read, was a row the file never heard of
        /// (`SaveSnapshotAsync` is that writer now, and it is the only one).
        /// </summary>
        private void RememberChatList()
        {
            _batchOpen = false;

            if (_freshChatRows.Count == 0) return;

            // The rows are already in the order the phone shows: every ApplyChat
            // placed its row by its last activity (`PlaceContact`), and the batch
            // order of the sync is NOT that order - it lists all the groups, then
            // all the people. This used to move each row to the index it had in the
            // batch, which put the old order back a moment after the sort.
            SortContacts();
            Diag.OkNow("order after the sync: " + OrderSample());

            _freshChatRows.Clear();
        }

        private async Task ApplyMedia(ChatMessage message, IncomingMediaResult result)
        {
            // Which row the bytes belong to. Two callers mean two different answers: the push
            // path builds a stand-in whose `RelatedMessageId` is the id in the stanza, and a
            // tap hands the bubble itself, whose `RelatedMessageId` is empty because it is a
            // field of revoke and edit. Reading only the second is why a photo asked for by a
            // tap downloaded, stored, and left its bubble spinning: no row ever matched.
            string want = string.IsNullOrEmpty(message.RelatedMessageId)
                ? message.Id
                : message.RelatedMessageId;

            if (string.IsNullOrEmpty(want)) return;

            var list = GetMessages(message.ChatId);
            for (int i = 0; i < list.Count; i++)
            {
                var target = list[i];
                if (target == null || target.Id != want) continue;

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
            // The log is complete now, so this is the one moment a missed call can be
            // announced: `OnNativeCalls` rebuilds the whole list from the sync, and a toast
            // while it was half built would be a call with the wrong name or none.
            AnnounceMissedCalls();

            // A missed call is the other thing a tap must be able to reach, and it arrives
            // here: no badge of its own, only the log.
            RefreshLaunchRoute();

            var handler = CallsScanCompleted;
            if (handler != null) handler(this, EventArgs.Empty);
        }

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

        /// <summary>
        /// A message is deleted on this phone and nowhere else: gone from the conversation,
        /// gone from the copy the next start reads, and the chat's preview stepped back to
        /// what is left.
        ///
        /// Why the file is rewritten and not appended to: `MessageCache.AppendAsync` merges
        /// by id, so a deletion it never sees would be undone by the copy already on the
        /// phone, and a message that comes back after a restart is worse than one the user
        /// could not delete at all. A conversation with nothing left deletes the file
        /// instead, because `SaveAsync` refuses to write an empty one.
        /// </summary>
        public void RemoveMessage(string chatId, string messageId)
        {
            chatId = ChatIdFor(chatId);
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(messageId)) return;

            ObservableCollection<ChatMessage> messages;
            if (!_chatMessages.TryGetValue(chatId, out messages)) return;

            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] == null || messages[i].Id != messageId) continue;
                messages.RemoveAt(i);
                break;
            }

            RefreshPreview(chatId);

            var contact = FindContact(chatId);
            if (contact != null) PlaceContact(contact);

#pragma warning disable 4014
            Guarded.RunGuardedAsync("DataService/remove message", RewriteTailAsync(chatId, messages));
#pragma warning restore 4014
        }

        /// <summary>The conversation as it is now, written over the copy on the phone.</summary>
        private static async Task RewriteTailAsync(string chatId, ObservableCollection<ChatMessage> messages)
        {
            if (messages == null || messages.Count == 0)
            {
                await MessageCache.DeleteAsync(chatId);
                return;
            }

            await MessageCache.SaveAsync(chatId, new List<ChatMessage>(messages));
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
            // The number behind the address, not the address itself. A row can still be
            // keyed on a `@lid` - the pair can arrive after the chat does - and
            // `ContactDirectory` matches phone numbers, refusing a LID on purpose (its digits
            // are an identifier, not a number). `Canonical` answers the phone number when the
            // pair is known and the address itself otherwise, so a LID with no pair still
            // answers null here and the row keeps the name it had.
            string name = ContactDirectory.NameForJid(NameCache.Book.Canonical(jid));
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

            // A `@lid` with no known number is not a number: its digits are an identifier, and
            // printing them as `+...` is exactly the wrong phone number the user saw. The
            // check comes **before** the digits one and not after it: a LID's user part is a
            // long run of digits, so it satisfied the branch below and the guard written for
            // it never ran (`JidUtils.IsLid`).
            if (JidUtils.IsLid(jid)) return user;

            if (user.Length >= 8 && user.All(char.IsDigit)) return "+" + user;

            return string.IsNullOrEmpty(user) ? "?" : user;
        }

        /// <summary>
        /// The letter of an avatar circle, from a display name.
        ///
        /// It serves the chat rows (`Contact.Initials`) and the status rows
        /// (`ChatMessage.Initials`): one rule, because the two lists draw the same circle
        /// and two rules would give the same person two letters.
        /// </summary>
        public static string InitialsFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            string trimmed = name.Trim();

            // A push name of spaces is empty once trimmed, and `Substring(0, 1)` on it is an
            // exception that takes the whole row - and the batch it arrived in - down.
            if (trimmed.Length == 0) return "?";

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
            Contact found = FindContactExact(id);
            if (found != null) return found;

            // The same chat under its other spelling (`@lid` whose number is known, a device,
            // `@c.us`): a message that arrived one way has to find the row filed the other
            // way, or it creates a second row for a conversation the list already shows.
            string canonical = ChatIdFor(id);
            if (canonical == id) return null;

            return FindContactExact(canonical);
        }

        /// <summary>The row filed under exactly this id, or null.</summary>
        private Contact FindContactExact(string id)
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

            // The row is filed under the canonical address, and the tile's argument can be the
            // other spelling: a chat opened from its tile under a `@lid` whose number is known
            // would otherwise be the second row for that person.
            chatId = ChatIdFor(chatId);
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
        /// Every pinned chat is published once: its tile is composed from the picture and the
        /// count this phone knows now.
        ///
        /// Why it is a sweep and not only the message path: `PinnedTile.Update` is called
        /// when a count changes, and a start changes no count - so a tile composed by an older
        /// build stayed on the Start screen until somebody wrote in that chat. The shell keeps
        /// its own copy of the tile, which is why the code had to be told to draw it again.
        ///
        /// A chat whose tile was removed from Start is skipped: the pin in the preferences can
        /// outlive the tile, and drawing one that is not there is a tile the platform refuses.
        /// </summary>
        private void PublishPinnedTiles()
        {
            int drawn = 0;

            for (int i = 0; i < _contacts.Count; i++)
            {
                var contact = _contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                if (!contact.IsPinned) continue;
                if (!PinnedTile.IsPinned(contact.Id)) continue;

                // The count is not known here and is not invented: the copy on the phone has
                // no unread numbers (`ChatCache.Slim` drops them on purpose), so a zero would
                // not be "nobody is waiting", it would be "this start does not know" - and
                // publishing it cleared the badge of every pinned chat at every start.
                PublishUnread(contact, false);
                drawn++;
            }

            if (drawn > 0) Diag.Ok("pinned tiles: " + drawn + " drawn again from this phone's copy");
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
            PublishUnread(contact, true);
        }

        /// <summary>
        /// The same, saying whether the count is one this phone knows. The badge is only
        /// rewritten by a caller that knows it.
        /// </summary>
        private static void PublishUnread(Contact contact, bool countKnown)
        {
            if (contact == null || string.IsNullOrEmpty(contact.Id)) return;

            // La foto grande della tile, una volta per chat: la riga porta `AvatarData`, che e'
            // la preview 96x96 della lista, e una tile composta da quella e' una faccia
            // ingrandita 3,5 volte (vedi `PinnedTile.AskForPicture`). Ogni publish la chiede,
            // e la chat che l'ha gia' ricevuta risponde dal suo dizionario senza toccare la
            // rete; il budget delle richieste e' dentro `AskForPicture`.
            PinnedTile.AskForPicture(contact.Id, contact.Name, contact.UnreadCount,
                contact.LastMessage, countKnown);

            string avatar = contact.AvatarData;
            if (string.IsNullOrEmpty(avatar)) avatar = AvatarCache.Get(contact.Id);

            PinnedTile.Update(contact.Id, contact.Name, contact.UnreadCount, avatar,
                contact.LastMessage, countKnown);
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
            chatId = ChatIdFor(chatId);
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
        /// A new link, in this same process: the history of this account has not
        /// arrived yet, so nothing may claim it has.
        ///
        /// The flag is per account and not per process - it says what WhatsApp said
        /// about the link that is open now - and a phone linked while the app runs
        /// would otherwise keep the answer of the link before it, which lets a
        /// conversation opened during the new sync bind to nothing.
        /// </summary>
        public void BeginHistory()
        {
            HistoryComplete = false;
            _historyRequested.Clear();
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
            chatId = ChatIdFor(chatId);
            if (!_chatMessages.ContainsKey(chatId))
            {
                _chatMessages[chatId] = new ObservableCollection<ChatMessage>();
            }
            _chatMessages[chatId].Add(message);
            TrimToCeiling(_chatMessages[chatId]);

            // A message this phone is adding has just happened: when its own time is not
            // one that can be true (a corrupt `t`, a clock that jumped), it happened now, and
            // saying so keeps both the bubble and the row out of the future.
            if (!ActivityTime.IsSane(message.Timestamp, DateTime.Now)) message.Timestamp = DateTime.Now;

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
                    PublishBadge();
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

            // And the chat list's own count, or the next message would hand back the pile
            // that was just read: `ApplyChat` copies the row's number onto the contact.
            NativeConnection.Instance.MarkChatRead(chatId);

            // The tile stops giving this chat a turn: its message is read, and a queue
            // that kept it would keep showing a conversation nobody is waiting for.
            if (!string.IsNullOrEmpty(chatId)) NotificationService.Forget(chatId);

            // And the chat's own tile says the number is gone.
            PublishUnread(contact);

            SendRead(chatId);
            PublishBadge();
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
            // The chat list's counts go with them: a message that arrives next is one, not
            // the pile that was read.
            NativeConnection.Instance.MarkAllChatsRead();

            NotificationService.ForgetAll();
            PublishBadge();
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

            // And the pinned tiles, once the account is really in. The start's sweep runs
            // before the login, so the picture it asks for has nobody to answer it: the tile
            // then shows the chat list's 96x96 preview, drawn large, which is the blurry face
            // of 23 October 2026 - and it stays that way for the whole run unless a message
            // arrives in that chat, because a publish is what asks again. This is the publish
            // that was missing; `PinnedTile.AskForPicture` refuses to ask before this point.
            //
            // Only in the process that draws: the background check logs in on a budget of
            // about fifteen seconds of reading, and a full-size picture per pinned chat is
            // not what it is there for. Its tile is the app's, set by `NotificationService`.
            if (_pinnedTilesAfterLogin || !NativeConnection.Instance.IsLoggedIn) return;
            if (!UiThread.HasWindow) return;

            _pinnedTilesAfterLogin = true;
            PublishPinnedTiles();
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

            // Taken before it is removed: the files of a video, a note or a document are on
            // the phone and nothing else knows which ones belonged to this conversation.
            ObservableCollection<ChatMessage> goneMessages = null;
            _chatMessages.TryGetValue(chatId, out goneMessages);

            _chatMessages.Remove(chatId);
            _historyRequested.Remove(chatId);

            if (goneMessages != null)
            {
#pragma warning disable 4014
                Guarded.RunGuardedAsync("DataService/delete media",
                    IncomingMediaStore.DeleteAsync(goneMessages));
#pragma warning restore 4014
            }

            ChatPreferences.Hide(chatId);
            NotificationService.Forget(chatId);
            PublishBadge();

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

        /// <summary>
        /// The badge of the Calls tab: the missed calls that arrived since the log was last
        /// on screen.
        ///
        /// Why not every missed call in the log: that log is WhatsApp's and it keeps them
        /// for good, so the count never went down - a badge that says the same number every
        /// time somebody looks at it is a badge nobody looks at. `SessionService.MarkOpened`
        /// writes the visit (`HomePage.Show`), and the number falls to zero when the tab is
        /// opened - which is also the moment the route that sends a tap there stops applying
        /// (`LaunchRoute` uses the same mark).
        /// </summary>
        public int MissedCalls()
        {
            DateTime seen = SessionService.OpenedAt(AppSection.Calls);

            int total = 0;
            for (int i = 0; i < _calls.Count; i++)
            {
                CallLogEntry row = _calls[i];
                // Two vocabularies meet here: the old server said `timeout`, the native
                // client says `missed`. A badge that only knew the first was a badge that
                // never counted a missed call.
                if (row == null || !row.IsMissed) continue;
                if (row.Timestamp > seen) total++;
            }
            return total;
        }

        /// <summary>
        /// One toast per missed call the phone has not announced yet, oldest first, so a
        /// wake that found two tells them in the order they happened.
        ///
        /// The watermark is `NotificationService`'s own (`ShowMissedCall`); this only reads
        /// the log in order, because the log lives here.
        /// </summary>
        private void AnnounceMissedCalls()
        {
            var missed = new List<CallLogEntry>();
            for (int i = 0; i < _calls.Count; i++)
            {
                CallLogEntry row = _calls[i];
                if (row != null && row.IsMissed) missed.Add(row);
            }

            missed.Sort(delegate (CallLogEntry left, CallLogEntry right)
            {
                return left.Timestamp.CompareTo(right.Timestamp);
            });

            for (int i = 0; i < missed.Count; i++)
            {
                CallLogEntry row = missed[i];
                string name = row.Name;
                if (string.IsNullOrEmpty(name)) name = DisplayNameForJid(row.ChatId);
                NotificationService.ShowMissedCall(name, row.Timestamp);
            }
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
        /// The newest update nobody has watched, or `default(DateTime)` when there is none.
        ///
        /// It is what `LaunchRoute` compares against the last time the Status tab was on
        /// screen: the tile is the only thing a tap on the Start screen can carry, so what
        /// arrived after that moment is the whole of the signal.
        /// </summary>
        public DateTime NewestUnseenStatus()
        {
            DateTime newest = default(DateTime);
            for (int i = 0; i < _statuses.Count; i++)
            {
                ChatMessage row = _statuses[i];
                if (row != null && !row.IsStatusViewed && row.Timestamp > newest) newest = row.Timestamp;
            }
            return newest;
        }

        /// <summary>The newest missed call in the log, or `default(DateTime)` when there is none.</summary>
        public DateTime NewestMissedCall()
        {
            DateTime newest = default(DateTime);
            for (int i = 0; i < _calls.Count; i++)
            {
                CallLogEntry row = _calls[i];
                if (row != null && row.IsMissed && row.Timestamp > newest) newest = row.Timestamp;
            }
            return newest;
        }

        /// <summary>
        /// Writes down where a tap on the tile - or on the lock screen, which shows the
        /// same thing - should land, for the start that consumes it.
        ///
        /// Called with the badge, because that is the moment the phone starts advertising
        /// something new. The rule itself is `LaunchRoute.Decide`, which is tested on the
        /// desktop; this only feeds it the two dates and the two "last looked at" marks,
        /// and the clock it compares them in is the one the rows carry.
        /// </summary>
        public void RefreshLaunchRoute()
        {
            SessionService.RememberRoute(LaunchRoute.Decide(
                NewestUnseenStatus(), SessionService.OpenedAt(AppSection.Status),
                NewestMissedCall(), SessionService.OpenedAt(AppSection.Calls)));
        }

        /// <summary>
        /// What the phone shows about the unread, in one place: the tile, the badge, and the
        /// destination recorded for the next tap on them. The count and the route are the
        /// same fact seen twice, so they are written together - eight call sites that each
        /// remembered one of the two is how they would come to disagree.
        /// </summary>
        private void PublishBadge()
        {
            NotificationService.SetUnread(TotalUnread());
            RefreshLaunchRoute();
        }

        /// <summary>
        /// The one spelling of a chat every row and every message is keyed on: the number for
        /// a `@lid` whose pair is known, no device, `@c.us` as `s.whatsapp.net`
        /// (`ChatListMerge`, which is where the rule is written and tested).
        /// </summary>
        public string ChatIdFor(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return chatId;

            string canonical = ChatListMerge.CanonicalId(chatId, NameCache.Book);
            return string.IsNullOrEmpty(canonical) ? chatId : canonical;
        }

        /// <summary>
        /// How often the list is re-read from what the client already knows: thirty seconds,
        /// and it is a ceiling and not a schedule (`HomePage` starts the timer). The socket is
        /// live, so this is the net under a stanza that never arrived.
        /// </summary>
        public const int ListRefreshSeconds = 30;

        /// <summary>
        /// The list, re-read from this device's own client: every row applied again, two rows
        /// that are one chat folded, and the order the times ask for.
        ///
        /// Why it exists: a stanza off the socket was the only writer the list had, so a name
        /// learned after its row (`NativeConnection.NameGroupsAsync`), a pair learned after
        /// the row it would fold, or a row the server has and this list never heard of (a
        /// stanza that never arrived) stayed wrong until the app was restarted. It asks
        /// WhatsApp nothing - it walks rows already in memory - which is what lets it run on
        /// a phone with no data and still be the truth this device has.
        /// </summary>
        public void RefreshChatList()
        {
            IList<WhatsappApp.Protocol.WaProto.HistoryConversation> rows = NativeConnection.Instance.ChatRows;
            int applied = 0;
            int keptGroups = 0;
            int keptPeople = 0;

            for (int i = 0; i < rows.Count; i++)
            {
                HistoryConversation row = rows[i];
                if (row == null) continue;

                if (RefreshChatRow(row))
                {
                    applied++;
                    continue;
                }

                // Named and not only counted: a group that kept its subject is a different
                // answer from a person who kept their name, and a tick that changes nothing
                // has to be readable in the log - otherwise "the refresh is running" and "the
                // refresh is dead" look the same from the outside.
                if (row.IsGroup) keptGroups++;
                else keptPeople++;
            }

            int folded = FoldDuplicateChats();

            // Named whether or not anything moved: the fold fixes what it can see, and what it
            // cannot leave alone (the chat on screen, a pair the client has not learned yet)
            // is the state a report has to be able to name.
            NameDuplicateRows();

            // The feed named and not only counted: a bare zero is the same line for a phone
            // whose statuses have not arrived yet and for a pass that forgot to look, and only
            // one of those is news. An empty feed says so in words.
            string statuses = _statuses.Count == 0
                ? "the status feed is empty"
                : _statuses.Count + " status row(s) kept";

            // One line per tick, changed or not, with the status feed's rows in it as well: the
            // two caches are read on the same pass of the eye, and a quiet tick that says how
            // many rows it left alone is the proof the timer is alive.
            Diag.Ok("chats: " + rows.Count + " row(s) re-read, " + applied + " applied, "
                + folded + " folded, " + keptGroups + " group(s) and " + keptPeople
                + " person(s) left as they were, " + statuses);

            if (applied == 0 && folded == 0) return;

            for (int i = 0; i < _contacts.Count; i++)
            {
                if (_contacts[i] != null) PlaceContact(_contacts[i]);
            }
        }

        /// <summary>
        /// One row of the native list, applied the way a sync's row is applied - the same
        /// `ApplyChat`, so a name, a preview, a count and a position cannot disagree with the
        /// path that made the row. False for a row this list must not draw, and for one that
        /// says exactly what the list already shows (`WouldChangeTheRow`).
        /// </summary>
        private bool RefreshChatRow(WhatsappApp.Protocol.WaProto.HistoryConversation row)
        {
            if (row == null || string.IsNullOrEmpty(row.Jid)) return false;

            string chatId = ChatIdFor(row.Jid);
            if (string.IsNullOrEmpty(chatId)) return false;
            if (IsNotAConversation(chatId)) return false;

            var message = new ChatMessage();
            message.ChatId = chatId;
            message.SenderName = row.Name;
            message.UnreadCount = row.UnreadCount;
            message.IsGroup = row.IsGroup;
            message.Type = MessageType.Text;

            long at = Math.Max(row.LastMessageTimestamp, row.ConversationTimestamp);
            message.Timestamp = at > 0 ? Moment(at) : DateTime.MinValue;
            if (!ActivityTime.IsSane(message.Timestamp, DateTime.Now)) message.Timestamp = DateTime.MinValue;

            if (row.Messages.Count > 0) message.Text = row.Messages[row.Messages.Count - 1].Text;

            // The list is only touched when the row would say something new. The refresh runs
            // every thirty seconds, and re-applying a row that has not moved re-raises every
            // property on it and moves it in a list the reader may be looking at, for nothing.
            Contact contact = FindContact(chatId);
            if (contact != null && !WouldChangeTheRow(contact, message)) return false;

            ApplyChat(message, false);
            return true;
        }

        /// <summary>
        /// Whether re-applying this row would change what the list shows: the name, the
        /// preview or the unread count. False for a row that is already on screen exactly as
        /// the client holds it.
        ///
        /// Why it exists: the refresh walked every row through `ApplyChat` on every tick - a
        /// property change per row, a move in the ordered collection and a notification for
        /// the count - for rows that carry nothing new. A list of three hundred chats was
        /// redrawn every thirty seconds and a row could move under the reader's finger while
        /// he scrolled. The comparison is `ApplyChat`'s own rules, so a row can never be
        /// skipped when those rules would write something: the name it would show, the count
        /// the badge would carry, and the preview and the time the row would land at.
        /// </summary>
        private bool WouldChangeTheRow(Contact contact, ChatMessage message)
        {
            string name = NameFor(message);
            if (contact.Name != name || contact.Initials != InitialsFor(name)) return true;

            // Drawn on the same row, and read from the same row: a count that moved is a
            // change even when the preview did not, or the badge waits for a message.
            int unread = message.ChatId == _activeChatId ? 0 : message.UnreadCount;
            if (contact.UnreadCount != unread) return true;

            // The picture. A row from the client carries none, so this is the one place the
            // bytes already on the phone reach a row that lost them (a start after a cache
            // that dropped its decode), and `ApplyChat` looks them up for the same reason.
            string avatar = message.AvatarData;
            if (string.IsNullOrEmpty(avatar)) avatar = AvatarCache.Get(message.ChatId);
            if (!string.IsNullOrEmpty(avatar) && contact.AvatarData != avatar) return true;

            bool believable = ActivityTime.IsSane(message.Timestamp, DateTime.Now);

            if (believable && message.Timestamp >= contact.LastMessageAt)
            {
                // The time orders the list even when the row carries no words (a
                // conversation whose last message is a media the sync did not include).
                if (message.Timestamp != contact.LastMessageAt) return true;

                return !string.IsNullOrEmpty(message.Text)
                    && (contact.LastMessage != message.Text
                        || contact.LastMessageTime != message.FormattedTime);
            }

            // It cannot say when, but it can say what: the row shows the words and no date.
            return !believable && string.IsNullOrEmpty(contact.LastMessage)
                && !string.IsNullOrEmpty(message.Text);
        }

        /// <summary>
        /// Names, in the log, any two rows that are one chat under two addresses.
        ///
        /// Why a line and not only the fold: `FoldDuplicateChats` cannot take every pair - the
        /// chat on screen is left alone while it is open, and two rows whose pair the client
        /// has not learned yet are two rows it does not know are one - and a duplicate nobody
        /// writes down is a list that is wrong on a phone nobody can look at. Each pair is
        /// named once, not once per tick of the refresh.
        /// </summary>
        private void NameDuplicateRows()
        {
            var firstByCanonical = new Dictionary<string, string>(StringComparer.Ordinal);

            for (int i = 0; i < _contacts.Count; i++)
            {
                Contact row = _contacts[i];
                if (row == null || string.IsNullOrEmpty(row.Id)) continue;

                string canonical = ChatIdFor(row.Id);
                if (string.IsNullOrEmpty(canonical)) continue;

                string first;
                if (firstByCanonical.TryGetValue(canonical, out first))
                {
                    if (_namedDuplicates.Count >= NamedDuplicateLimit) continue;
                    if (!_namedDuplicates.Add(first + " " + row.Id)) continue;

                    Diag.Ok("chats: " + row.Id + " and " + first + " are the same chat ("
                        + canonical + ") and the list holds both");
                    continue;
                }

                firstByCanonical[canonical] = row.Id;
            }
        }

        /// <summary>
        /// The rows that are one chat, made one: the same person under a `@lid` and a number,
        /// or a device still on the address.
        ///
        /// It reuses `ChatMerge` because that is already the one place that knows how to move
        /// a row - its messages, its count, its pin, its mute - and the page that may have it
        /// open. The chat on screen is left alone: the page holds the message collection it
        /// was bound to, and folding it under the reader would empty the conversation he is
        /// reading. It is folded at the first refresh after he leaves.
        /// </summary>
        private int FoldDuplicateChats()
        {
            var keepByCanonical = new Dictionary<string, Contact>(StringComparer.Ordinal);
            var snapshot = new List<Contact>(_contacts);
            int folded = 0;

            for (int i = 0; i < snapshot.Count; i++)
            {
                Contact contact = snapshot[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                if (contact.Id == _activeChatId) continue;

                string canonical = ChatIdFor(contact.Id);
                if (string.IsNullOrEmpty(canonical)) continue;

                Contact keep;
                if (!keepByCanonical.TryGetValue(canonical, out keep))
                {
                    keepByCanonical[canonical] = contact;
                    continue;
                }

                // The row filed under the canonical id keeps the conversation; when neither
                // is (an old row and an older one), the newer preview stays.
                Contact from;
                Contact to;

                if (contact.Id == canonical && keep.Id != canonical)
                {
                    from = keep;
                    to = contact;
                    keepByCanonical[canonical] = contact;
                }
                else if (keep.Id == canonical || keep.LastMessageAt >= contact.LastMessageAt)
                {
                    from = contact;
                    to = keep;
                }
                else
                {
                    from = keep;
                    to = contact;
                    keepByCanonical[canonical] = contact;
                }

                if (from.Id == _activeChatId) continue;

                // Named before the merge, so the log says which two addresses were one chat.
                Diag.Ok("chats: " + from.Id + " is the same chat as " + to.Id + ", folding");
                OnChatMerged(this, new ChatMerge(from.Id, to.Id));
                folded++;
            }

            return folded;
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
                    // `Contacts` is a public collection, so a row nobody inserted through
                    // `AddContact` can be a null, and the sort of the whole list died on it.
                    if (_contacts[j] == null || _contacts[best] == null) continue;

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

        /// <summary>
        /// A row from outside the list - the new-chat dialog's number, a row a page built.
        ///
        /// It answers the row the list has now, which is not always the one it was handed:
        /// the id is canonicalised first (`ChatIdFor`) and a chat that is already there keeps
        /// its row. This is the last door a row can come in by, and a second row for one chat
        /// is two previews, two counts and a conversation that is only half of itself.
        /// </summary>
        public Contact AddContact(Contact contact)
        {
            if (contact == null) return null;

            string chatId = ChatIdFor(contact.Id);
            if (string.IsNullOrEmpty(chatId)) return null;

            contact.Id = chatId;

            Contact existing = FindContact(chatId);
            if (existing != null) return existing;

            _contacts.Insert(0, contact);
            _contactIndex[contact.Id] = contact;
            return contact;
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
