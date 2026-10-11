using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Services
{
    /// <summary>
    /// This phone's account, brought online: the login that follows a pairing, and the
    /// reading that keeps the connection a connection.
    ///
    /// Why it exists as a service and not inside a page: pairing and starting the app are
    /// two different moments that need the same thing. `PairPage` has just written an
    /// account and the app has to come online with it; `App.OnLaunched` finds an account
    /// from a previous run and has to come online with that one. Both of them are the same
    /// call, and neither of them owns a socket loop.
    ///
    /// What the device taught (2026-10-10, Lumia): the pairing handshake is complete -
    /// `pair-success` verified against the ADV secret and the account signature, the device
    /// signed, the account written - and the phone that scanned the code still answered
    /// `Impossibile collegare il dispositivo`, because nothing ever logged in with what had
    /// just been written. `whatsmeow` says the same thing in its own way: `expectDisconnect`
    /// after the signature, then a *new* connection with the login payload.
    ///
    /// What it does not do yet, and Task 6 owns: the nodes the server pushes after the
    /// login are logged and not interpreted (the conversations, the message backlog), and
    /// presence waits for a push name the pairing of a personal account does not carry.
    /// </summary>
    public sealed class NativeConnection
    {
        private static NativeConnection _instance;

        public static NativeConnection Instance
        {
            get
            {
                if (_instance == null) _instance = new NativeConnection();
                return _instance;
            }
        }

        private readonly CredentialStore _store = new CredentialStore(new NativeCredentialStorage());
        private readonly IRandomSource _random = new DeviceRandomSource();
        private readonly SeenMessages _seen = new SeenMessages(NativeChats.SeenLimit);
        private readonly NativeChats _chats = new NativeChats();

        /// <summary>The answers this client is waiting for, by the id of the query it sent.</summary>
        private readonly Dictionary<string, TaskCompletionSource<BinaryNode>> _waiters =
            new Dictionary<string, TaskCompletionSource<BinaryNode>>();

        private NativeSession _session;
        private Credentials _account;
        private ISessionStorage _storage;
        private PreKeyStore _preKeys;
        private SessionStore _sessions;
        private bool _running;
        private DateTime _lastNode;

        private NativeConnection()
        {
            Status = "no account opened yet";
        }

        /// <summary>True while an account is open. False before a login and after the end of one.</summary>
        public bool IsConnected
        {
            get
            {
                NativeSession session = _session;
                return session != null && session.IsOpen;
            }
        }

        /// <summary>The last thing that happened, for the Diagnostics page.</summary>
        public string Status { get; private set; }

        /// <summary>
        /// The chats this client knows about, already ordered and de-duplicated. The store the
        /// pages bind reads it; nothing else should be a second source of the same list.
        /// </summary>
        public NativeChats Chats
        {
            get { return _chats; }
        }

        /// <summary>A chat row is new or has changed: the history brought it, or a message did.</summary>
        public event EventHandler<HistoryConversation> ChatArrived;

        /// <summary>A message that has just arrived and is worth showing.</summary>
        public event EventHandler<HistoryMessage> MessageArrived;

        /// <summary>The server said the history is all here (`NativeChats.IsSyncComplete`).</summary>
        public event EventHandler<HistorySync> HistoryFinished;

        /// <summary>The status line changed, for the screens that show it.</summary>
        public event EventHandler<string> ConnectionStatusChanged;

        /// <summary>
        /// A receipt arrived for something this phone sent: the message was delivered, or it
        /// was read.
        /// </summary>
        public event EventHandler<MessageStatusUpdate> MessageStatusChanged;

        /// <summary>How long a query the server never answers is waited for before it is given up on.</summary>
        public const int AnswerSeconds = 20;

        /// <summary>
        /// Opens the stored account, if there is one, and starts reading.
        ///
        /// Safe to call from more than one place - the startup and the page that just
        /// paired both call it - and a no-op when there is no account at all: an install
        /// with no session shows the QR, and a login attempt written into its log would
        /// only be noise.
        /// </summary>
        public void Start()
        {
            if (_running) return;

            if (!_store.HasSession)
            {
                Status = "no account stored";
                return;
            }

            _running = true;
            Guarded.RunGuardedAsync("NativeConnection/start", RunAsync());
        }

        /// <summary>
        /// The account, kept open: one login, the reading that follows it, and - when the
        /// connection ends for a reason that is not the account's death - the wait and the
        /// next login.
        ///
        /// Why the retry is a loop around the whole connection and not a `goto` inside the
        /// reader: everything the login set up (the noise handshake, the pre-key floor check,
        /// presence) has to happen again on a new socket, and the only thing that must not
        /// happen twice is clearing the account.
        /// </summary>
        private async Task RunAsync()
        {
            Credentials account;
            try
            {
                account = _store.Load();
            }
            catch (ProtocolException error)
            {
                // HasSession said yes and the read said no: a store that changed under us,
                // and nothing to log in with.
                Diag.Failed("NativeConnection/load", error);
                Status = Diag.Describe(error);
                _running = false;
                return;
            }

            int attempt = 0;

            while (_running)
            {
                NativeSession session = null;
                try
                {
                    session = new NativeSession(new WebSocketTransport(), new ProtocolLog(), _random);
                    _session = session;
                    Status = "logging in as " + account.Jid;

                    await session.LoginAsync(account, WhatsAppSocket.DefaultUrl);

                    _account = account;
                    _lastNode = DateTime.UtcNow;
                    Status = "logged in as " + account.Jid;
                    Diag.Ok("native: logged in as " + account.Jid);

                    await PublishPreKeysAsync(session, account);
                    await session.GoAvailableAsync(account.PushName);

#pragma warning disable 4014
                    Guarded.RunGuardedAsync("NativeConnection/keepalive", KeepAliveAsync(session));
#pragma warning restore 4014

                    // A connection that worked resets the patience: the next drop waits a
                    // second again, not the half minute an hour of retrying had reached.
                    attempt = 0;

                    await ReadAsync(session, account);
                }
                catch (SessionRefusedException refused)
                {
                    // WhatsApp took this account away: the phone was unlinked from another
                    // device, the primary is gone, or it is banned. Keeping the account would
                    // put the app on a chat list that can never fill, so it is cleared and the
                    // run is flagged - which is what sends `StartDecision` back to the QR.
                    Diag.Ok("native: " + refused.Message);
                    Status = refused.Message;

                    SessionState.Expired = true;
                    _store.Clear();

                    break;
                }
                catch (Exception error)
                {
                    Diag.Failed("NativeConnection/connection", error);
                    Status = Diag.Describe(error);
                }
                finally
                {
                    if (session != null) session.Close();
                    if (ReferenceEquals(_session, session)) _session = null;
                }

                if (!_running) break;

                attempt++;
                int delay = ConnectionPolicy.ReconnectDelaySeconds(attempt);
                Status = Status + " - retrying in " + delay + "s";
                Diag.Ok("native: " + Status);

                await Task.Delay(TimeSpan.FromSeconds(delay));
            }

            _running = false;
            Diag.Ok("native: the account is closed (" + Status + ")");
        }

        /// <summary>
        /// Reads until the connection ends. The liveness probe is answered before anything
        /// else is asked of the node, because an unanswered one is the server closing a
        /// connection it believes is dead - which is how the pairing of 2026-10-10 was lost.
        ///
        /// A node that is neither a probe nor a refusal is left to the tasks that interpret
        /// it (the classifier, the history sync, the message path); this loop owns only the
        /// two answers that stop it.
        /// </summary>
        private async Task ReadAsync(NativeSession session, Credentials account)
        {
            while (true)
            {
                BinaryNode node = await session.ReadNodeAsync();
                _lastNode = DateTime.UtcNow;

                InboundKind kind = InboundRouter.Classify(node);

                if (kind == InboundKind.Probe)
                {
                    Diag.Ok("native: the server asked whether this connection is alive");
                    await session.AnswerProbeAsync(node);
                    continue;
                }

                if (kind == InboundKind.Refusal)
                {
                    string ended = NativeSession.Refusal(node);

                    if (ConnectionPolicy.AccountIsGone(node))
                    {
                        throw new SessionRefusedException("WhatsApp ended this account: " + ended);
                    }

                    throw new ProtocolException("the connection ended: " + ended);
                }

                if (kind == InboundKind.Ack || (kind == InboundKind.Ignored && node.Tag == "iq"))
                {
                    CompleteWaiter(node);
                    continue;
                }

                if (kind == InboundKind.Receipt)
                {
                    AnnounceReceipt(node);
                    continue;
                }

                try
                {
                    if (kind == InboundKind.Message)
                    {
                        await ReadMessageAsync(session, account, node);
                        continue;
                    }
                }
                catch (Exception error)
                {
                    // One stanza that cannot be read is one stanza: a session dropped over a
                    // message nobody can decrypt would turn a single bad frame into a phone
                    // that cannot stay online. The failure is named with the id it carries.
                    Diag.Failed("NativeConnection/message", error);
                    continue;
                }

                // The rest is named and not read yet: naming what arrived is what makes the
                // next device run's log worth reading, which is exactly what the run of
                // 2026-10-10 lacked.
                Diag.Ok("native: " + kind + " (" + node.Tag + ") arrived, and nothing reads it yet");
            }
        }

        /// <summary>
        /// One `message` stanza: acknowledged, opened, and - depending on what it carries -
        /// counted as a chat row, turned into the history, or left for the media path.
        ///
        /// The acknowledgement comes first and on purpose: the server re-sends a stanza
        /// nobody acknowledged, so a message answered after a long decrypt is a message a
        /// slow phone receives twice.
        /// </summary>
        private async Task ReadMessageAsync(NativeSession session, Credentials account, BinaryNode node)
        {
            IncomingMessage incoming = IncomingMessage.Read(node, account,
                await SessionsAsync(), await PreKeysAsync(), _seen, _random);

            await session.SendAsync(IncomingMessage.Ack(node));

            if (incoming == null) return;

            HistorySyncNotification history = HistorySyncNotification.From(incoming.Content);
            if (history != null)
            {
                ApplyHistory(history);
                return;
            }

            // A copy of something this account sent from another device: shown by the phone
            // that wrote it, never by this one (see IncomingMessage.IsOurEcho).
            if (incoming.IsOurEcho) return;

            if (incoming.Text == null)
            {
                Diag.Ok("native: message " + incoming.Id + " carries no words (media arrives with the media path)");
                return;
            }

            var message = new HistoryMessage();
            message.Id = incoming.Id;
            message.Jid = incoming.From;
            message.Text = incoming.Text;
            message.Timestamp = incoming.Timestamp;
            message.IsFromMe = false;

            if (!_chats.AddIncoming(message)) return;

            Diag.Ok("native: " + incoming.Id + " from " + incoming.From);

            HistoryConversation row = _chats.Find(incoming.From);
            if (row != null && ChatArrived != null) ChatArrived(this, row);
            if (MessageArrived != null) MessageArrived(this, message);

            // A pre-key message spent one of this device's one-time keys, and a server with
            // none left stops handing this phone out to peers.
            await PublishPreKeysAsync(session, account);
        }

        /// <summary>
        /// Asks the server something and waits for its answer, on the one socket both share.
        ///
        /// Why a waiter table and not a blocking read: every answer arrives in the same
        /// stream as the messages, on the loop that owns the socket, so the caller that wants
        /// one answer cannot be the loop - it has to leave a place for the loop to put it.
        /// The timeout is what keeps a server that never answers from parking the caller
        /// forever; the waiter is removed either way.
        /// </summary>
        private async Task<BinaryNode> RequestAsync(NativeSession session, BinaryNode query)
        {
            string id = query.Attrs["id"];
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a query with no id has no answer to wait for");

            var completion = new TaskCompletionSource<BinaryNode>();
            lock (_waiters) _waiters[id] = completion;

            try
            {
                await session.SendAsync(query);

                Task<BinaryNode> answer = completion.Task;
                Task finished = await Task.WhenAny(answer, Task.Delay(TimeSpan.FromSeconds(AnswerSeconds)));
                if (finished != answer) throw new ProtocolException("the server did not answer " + id);

                return await answer;
            }
            finally
            {
                lock (_waiters) _waiters.Remove(id);
            }
        }

        /// <summary>
        /// Hands an `iq` answer to whoever asked for it. An `error` answers with the Node
        /// itself so the caller's failure sentence carries what the server said, instead of a
        /// timeout twenty seconds later.
        /// </summary>
        private void CompleteWaiter(BinaryNode node)
        {
            string id = node.Attrs["id"];
            if (string.IsNullOrEmpty(id)) return;

            TaskCompletionSource<BinaryNode> completion;
            lock (_waiters)
            {
                if (!_waiters.TryGetValue(id, out completion)) return;
            }

            if (node.Attrs["type"] == "error")
            {
                completion.TrySetException(new ProtocolException("the server refused " + id + ": " + node));
                return;
            }

            completion.TrySetResult(node);
        }

        /// <summary>A receipt off the wire, as the status of the messages it names.</summary>
        private void AnnounceReceipt(BinaryNode node)
        {
            if (MessageStatusChanged == null) return;

            string jid = Conversation.From(node);
            IList<string> ids = Conversation.Ids(node);
            bool isRead = Conversation.IsRead(node);

            for (int i = 0; i < ids.Count; i++)
            {
                var update = new MessageStatusUpdate();
                update.Jid = jid;
                update.Id = ids[i];
                update.IsRead = isRead;

                MessageStatusChanged(this, update);
            }
        }

        /// <summary>
        /// A text to one chat: this device's message, the peer's devices, a session each.
        ///
        /// The devices are asked for on every send rather than cached, because a contact who
        /// linked a new phone - or removed one - would otherwise be written to at the device
        /// list they had yesterday. What is *not* re-done is the session: an established one
        /// is what the ratchet is for (`DeviceDirectory.Session`).
        /// </summary>
        public async Task<string> SendTextAsync(string jid, string body)
        {
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a message needs a chat to go to");
            if (string.IsNullOrEmpty(body)) throw new ProtocolException("there is nothing to send");

            NativeSession session = _session;
            if (session == null || !session.IsOpen) throw new ProtocolException("there is no open account to send on");

            BinaryNode answer = await RequestAsync(session, DeviceDirectory.Query(jid, _random));
            IList<KeyValuePair<string, PreKeyBundle>> bundles = DeviceDirectory.Bundles(answer);
            if (bundles.Count == 0) throw new ProtocolException("the server knows no device of " + jid);

            SessionStore store = await SessionsAsync();
            var devices = new List<KeyValuePair<string, SessionRecord>>();
            for (int i = 0; i < bundles.Count; i++)
            {
                devices.Add(new KeyValuePair<string, SessionRecord>(bundles[i].Key,
                    DeviceDirectory.Session(bundles[i].Key, bundles[i].Value, _account, store, _random)));
            }

            string id = OutgoingMessage.Id(_random);
            uint timestamp = NowSeconds();

            await session.SendAsync(OutgoingMessage.Node(jid, id, body, devices, timestamp));

            _chats.AddOutgoing(jid, id, body, timestamp);
            Diag.Ok("native: " + id + " to " + jid + " over " + devices.Count + " device(s)");

            return id;
        }

        /// <summary>Marks a chat's messages read: the receipt is what stops the peer's phone showing them unread.</summary>
        public async Task MarkReadAsync(string jid, IList<string> ids)
        {
            if (ids == null || ids.Count == 0) return;

            NativeSession session = _session;
            if (session == null || !session.IsOpen) return;

            await session.SendAsync(IncomingMessage.Receipt(jid, ids, "read", NowSeconds()));
        }

        /// <summary>This phone is typing in a chat, or has stopped.</summary>
        public async Task SendTypingAsync(string jid, bool typing)
        {
            NativeSession session = _session;
            if (session == null || !session.IsOpen) return;

            string ourJid = _account == null ? null : _account.Jid;
            await session.SendAsync(Conversation.Typing(ourJid, jid,
                typing ? Conversation.Composing : Conversation.Paused));
        }

        /// <summary>Says this phone is here. The push name is what the server passes on.</summary>
        public async Task SendPresenceAsync(string state)
        {
            NativeSession session = _session;
            if (session == null || !session.IsOpen) return;

            await session.GoAvailableAsync(_account == null ? null : _account.PushName);
        }

        private static uint NowSeconds()
        {
            return (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// The history the server just sent, into the chat list.
        ///
        /// Two shapes, both the reference's: a bootstrap carries the compressed payload
        /// inline, and every later chunk is a download behind the media path (the notification
        /// carries a mediaKey and a directPath, and the bytes are decrypted like any other
        /// media). The download half is not written yet, and it says so instead of pretending
        /// the history was empty.
        /// </summary>
        private void ApplyHistory(HistorySyncNotification notification)
        {
            if (notification.InlinePayload == null)
            {
                Diag.Ok("native: history sync type " + notification.SyncType
                    + " is behind a media download, which this build cannot fetch yet");
                return;
            }

            HistorySync sync = HistorySync.Decode(Zlib.Inflate(notification.InlinePayload));

            _chats.ApplyHistory(sync);
            Diag.Ok("native: history sync type " + sync.SyncType + " carried "
                + sync.Conversations.Count + " conversation(s), " + _chats.Rows.Count + " row(s) now");

            for (int i = 0; i < sync.Conversations.Count; i++)
            {
                HistoryConversation row = _chats.Find(sync.Conversations[i].Jid);
                if (row != null && ChatArrived != null) ChatArrived(this, row);
            }

            if (_chats.IsSyncComplete && HistoryFinished != null) HistoryFinished(this, sync);
        }

        /// <summary>
        /// Publishes one-time pre-keys when the server is running low on them, once per
        /// login.
        ///
        /// A failure here is named and survived: a phone that cannot publish pre-keys can
        /// still send and read, it is *being written to for the first time* that stops
        /// working - and that is not worth ending a working connection over.
        /// </summary>
        private async Task PublishPreKeysAsync(NativeSession session, Credentials account)
        {
            try
            {
                PreKeyStore store = await PreKeysAsync();
                if (!ConnectionPolicy.NeedsPreKeys(store.Remaining)) return;

                await session.SendAsync(PreKeyUpload.Node(account, store, _random, PreKeyUpload.RefillCount));
                Diag.Ok("native: published " + PreKeyUpload.RefillCount + " pre-key(s), "
                    + store.Remaining + " in the store");
            }
            catch (Exception error)
            {
                Diag.Failed("NativeConnection/prekeys", error);
            }
        }

        /// <summary>
        /// The pre-key store, opened once and kept: it reads every blob in the folder when it
        /// opens, so opening it per login would re-read the whole protocol directory every
        /// time the Wi-Fi blinked.
        /// </summary>
        private async Task<PreKeyStore> PreKeysAsync()
        {
            if (_preKeys != null) return _preKeys;

            _preKeys = new PreKeyStore(await StorageAsync());

            return _preKeys;
        }

        /// <summary>
        /// Where sessions and pre-keys live: the phone's own folder, opened once. Both stores
        /// read every blob when they open, so a second copy would double the bytes in memory
        /// and could drift from the first.
        /// </summary>
        private async Task<SessionStore> SessionsAsync()
        {
            if (_sessions != null) return _sessions;

            _sessions = new SessionStore(await StorageAsync(), _random);

            return _sessions;
        }

        private async Task<ISessionStorage> StorageAsync()
        {
            if (_storage == null) _storage = await FileSessionStorage.OpenAsync();

            return _storage;
        }

        /// <summary>
        /// Keeps the connection alive while the reader waits: one ping per silent interval.
        ///
        /// It is a task of its own because the reader is blocked on a frame and cannot also
        /// look at the clock - and it deliberately only **sends**. Reading is the other
        /// task's job, and two readers on one socket would take the frames out of order
        /// (the transport is documented as one reader, one writer).
        /// </summary>
        private async Task KeepAliveAsync(NativeSession session)
        {
            while (session.IsOpen)
            {
                await Task.Delay(TimeSpan.FromSeconds(SessionKeepAlive.PingSeconds));

                if (!session.IsOpen) return;
                if (!ConnectionPolicy.ConnectionIsStale(_lastNode, DateTime.UtcNow)) continue;

                await session.PingAsync();
            }
        }
    }

    /// <summary>
    /// The news a receipt brings: which message, in which chat, and whether it was read.
    ///
    /// It is deliberately not the `ChatMessage` itself: the caller that owns the bubble is
    /// the app (`Services/DataService`), and a protocol event that carried a UI model would
    /// put a `BitmapImage` in the core.
    /// </summary>
    public sealed class MessageStatusUpdate
    {
        /// <summary>The chat the message belongs to.</summary>
        public string Jid;

        /// <summary>The message the receipt names.</summary>
        public string Id;

        /// <summary>True when it says read and not merely delivered.</summary>
        public bool IsRead;
    }
}
