using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Media;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The answer to asking WhatsApp for somebody's picture.
    ///
    /// Why a value and not a `byte[]` with an exception: two of the three answers are
    /// ordinary and not faults. `Refused` is the server answering `iq type="error"` - a LID
    /// it will not draw, a contact who hid theirs - and `NoSession` is the chat list's pass
    /// asking before the login has happened. Read as exceptions they cost two first-chance
    /// throws per refusal and one per row before the login, and the debugger prints every
    /// one of them. `Jpeg` is null for both, which is what the callers already draw.
    /// </summary>
    public sealed class AvatarAnswer
    {
        /// <summary>The JPEG, or null when there is no picture to draw.</summary>
        public byte[] Jpeg;

        /// <summary>No account yet: nothing was asked, and the caller should wait for the login.</summary>
        public bool NoSession;

        /// <summary>The server answered with an error: this chat has no picture to give.</summary>
        public bool Refused;
    }

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
        private readonly NativeChats _chats = new NativeChats(NameCache.Book);

        /// <summary>
        /// The keys the primary has shared for its app state, in memory for this run.
        ///
        /// What they are for: the names saved in the primary's address book. They travel in
        /// the app state, encrypted, and a device without a key can read none of it - which is
        /// why the phone shares them when it links a device (`MessageProtocol.AppStateSyncKeyShare`)
        /// and why they are kept with the account between runs (`Credentials.AppStateKeys`).
        /// </summary>
        private readonly AppStateKeyset _appStateKeys = new AppStateKeyset();

        /// <summary>
        /// The group messages that arrived before the sender key of their member.
        ///
        /// A key and the message it belongs to are two stanzas, and the order they arrive in
        /// is the server's, not the sender's: a `skmsg` that lands first used to be refused
        /// and answered with a retry receipt, so it appeared only if the sender sent it again
        /// (`IncomingRefusal.WaitsForSenderKey`, answered by `IncomingMessage.ReadGroup`).
        /// It waits here instead, unacknowledged, and the next stanza that brings that key
        /// opens it (`RetryHeldAsync`).
        /// </summary>
        private readonly HeldGroupMessages _held = new HeldGroupMessages();

        /// <summary>The answers this client is waiting for, by the id of the query it sent.</summary>
        private readonly Dictionary<string, TaskCompletionSource<BinaryNode>> _waiters =
            new Dictionary<string, TaskCompletionSource<BinaryNode>>();

        /// <summary>
        /// The queries whose refusal is an answer rather than a failure - the picture asks,
        /// where `iq type="error"` means "this chat has none to give" and a thrown exception
        /// only exists to be caught by the row that asked.
        /// </summary>
        private readonly HashSet<string> _refusalIsAnswer = new HashSet<string>();

        /// <summary>
        /// The messages this phone has written to the socket and the server has not yet
        /// acknowledged, by id.
        ///
        /// Why it is kept at all: an `<ack>` is the only thing on the wire that says the
        /// server has the message. Without this set the ack arrived, matched nothing and was
        /// dropped, so the bubble had to answer from the state of the socket - and on
        /// 2026-10-22 an operator watched a message that never left the phone sit there with
        /// a tick next to it. The id is what tells an ack for our message apart from an ack
        /// for one of our queries, which travels through the same branch.
        /// </summary>
        private readonly HashSet<string> _awaitingAck = new HashSet<string>();

        /// <summary>
        /// The same ids in the order they were written, so an overflow forgets the oldest
        /// one instead of the whole table: emptying it dropped the ids of messages still
        /// waiting for their ack, and their ack then matched nothing - the bubble went to
        /// "failed" after twenty seconds for a message the server had taken. Entries left
        /// behind by an ack are dropped at the next send, so this does not grow on its own.
        /// </summary>
        private readonly Queue<string> _ackOrder = new Queue<string>();

        /// <summary>
        /// How many unanswered ids are remembered before the oldest one is forgotten. A
        /// phone sends a handful of messages per minute; this is the ceiling that keeps a
        /// phone that sent thousands into a server that never answered from growing a set
        /// forever.
        /// </summary>
        private const int MaxAwaitingAck = 200;

        /// <summary>
        /// The calls the history has brought, since the app opened.
        ///
        /// They are not asked for and they cannot be: WhatsApp puts the call log in the
        /// history sync (`HistorySync.callLogRecords`, field 13), which is why a call made
        /// while the phone was off is here the next time the app opens. What this holds is
        /// therefore the answer to "what has arrived", and `ScanCallsAsync` is that answer
        /// re-told to whoever just opened the page.
        /// </summary>
        private IList<CallLogEntryModel> _calls = new List<CallLogEntryModel>();

        /// <summary>
        /// The statuses that have arrived, since the app opened, newest first.
        ///
        /// They come from two places and hold the same shape either way: the history sync
        /// carries a `status@broadcast` conversation with the last day's updates in it, and a
        /// status posted while the app is open arrives as a live message. Ids are what keeps
        /// the history's re-sent chunks from showing the same update twice.
        /// </summary>
        private IList<StatusPost> _statuses = new List<StatusPost>();

        private NativeSession _session;

        /// <summary>Set when a login completes on the open socket, and cleared when that socket ends.</summary>
        private bool _loggedIn;

        private Credentials _account;
        private ISessionStorage _storage;
        private Task<ISessionStorage> _storageOpening;
        private PreKeyStore _preKeys;
        private SessionStore _sessions;
        private bool _running;

        /// <summary>
        /// The run of the loop that is alive, so a restart can wait for it instead of
        /// starting a second login beside it (see <see cref="RestartAsync"/>).
        /// </summary>
        private Task _loop;

        /// <summary>
        /// Which run of the loop is the current one. A restart bumps it so the loop that
        /// is still leaving knows it has been replaced and stops instead of retrying
        /// beside the new one (see <see cref="RestartAsync"/>).
        /// </summary>
        private int _generation;
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

        /// <summary>
        /// True once this account's login has completed on the open socket.
        ///
        /// Why it is not `IsConnected`: the session is opened before the login begins and
        /// stays open while it runs, so `IsConnected` answers yes for a socket that no
        /// account has finished logging in on. A caller that wants something the account
        /// owns - a chat's full picture, for one - needs the second answer, and used to
        /// learn the difference from a `ProtocolException`: `there is no open account to
        /// send on`, in `PinnedTile/picture` at every start (24 October 2026).
        /// </summary>
        public bool IsLoggedIn
        {
            get
            {
                NativeSession session = _session;
                return _loggedIn && session != null && session.IsOpen;
            }
        }

        private string _status = "";

        /// <summary>
        /// The last thing that happened, in the client's own words: the Diagnostics page
        /// shows it, the account screen shows it, and the bar on the chat list reads it to
        /// know whether the socket is up.
        ///
        /// Every assignment goes through this setter, because those screens are **told**
        /// when it changes. It used to be an auto-property with an event nobody raised: a
        /// page that showed it would keep saying "connecting" for as long as it stayed
        /// open, however connected the client was.
        /// </summary>
        public string Status
        {
            get { return _status; }
            private set
            {
                if (_status == value) return;
                _status = value;

                var handler = ConnectionStatusChanged;
                if (handler != null) handler(this, value);
            }
        }

        /// <summary>
        /// This account's own JID, once it is open. It is what a message this phone sends is
        /// signed with, and what the pages show as "me".
        /// </summary>
        public string Jid
        {
            get
            {
                Credentials account = _account;
                return account == null ? null : account.Jid;
            }
        }

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

        /// <summary>
        /// A chat that was keyed on a `@lid` is now keyed on its phone number. The pages that
        /// keep a row per JID (`DataService`, with its preferences and message collections)
        /// move theirs on this event.
        /// </summary>
        public event EventHandler<ChatMerge> ChatMerged;

        /// <summary>A message that has just arrived and is worth showing.</summary>
        public event EventHandler<HistoryMessage> MessageArrived;

        /// <summary>The server said the history is all here (`NativeChats.IsSyncComplete`).</summary>
        public event EventHandler<HistorySync> HistoryFinished;

        /// <summary>The status line changed, for the screens that show it.</summary>
        public event EventHandler<string> ConnectionStatusChanged;

        /// <summary>
        /// The bytes of a media message, fetched and decrypted: a photo, a voice note, a
        /// document or a video, with the message it belongs to.
        /// </summary>
        public event EventHandler<MediaArrival> MediaArrived;

        /// <summary>
        /// The call log, every time a history sync brings more of it.
        /// </summary>
        public event EventHandler<IList<CallLogEntryModel>> CallsScanned;

        /// <summary>
        /// A status was posted: somebody else's update, from the history or live. It is the
        /// row, and the photo's bytes follow on <see cref="StatusMediaArrived"/> when it has
        /// one.
        /// </summary>
        public event EventHandler<StatusPost> StatusArrived;

        /// <summary>
        /// The bytes of a status, fetched and decrypted. `Jid` is the author and `Id` is the
        /// status message, because a status has no chat to be found in.
        /// </summary>
        public event EventHandler<MediaArrival> StatusMediaArrived;

        /// <summary>
        /// A receipt arrived for something this phone sent: the message was delivered, or it
        /// was read.
        /// </summary>
        public event EventHandler<MessageStatusUpdate> MessageStatusChanged;

        /// <summary>
        /// Somebody took a message back. The stanza is a message like any other whose payload
        /// is the revoke, so it is read on the same loop and announced here; the row it names
        /// is the listener's to remove.
        /// </summary>
        public event EventHandler<MessageRevocation> MessageRevoked;

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
            int generation = ++_generation;

            // The loop runs for the life of the process, and it answers for its own
            // fault: `Guarded` records what it cannot survive, so nobody has to await
            // it here - which is what the pragma is for (C# 5 will not let a `void`
            // method express this). The task is kept anyway, because a restart has to
            // know when the old login has actually left.
#pragma warning disable 4014
            _loop = Guarded.RunGuardedAsync("NativeConnection/start", RunAsync(generation));
#pragma warning restore 4014
        }

        /// <summary>
        /// Closes whatever account is open and opens the stored one again: the
        /// "link a different account" path, where the credentials on disk have just
        /// changed under a loop that read them once.
        ///
        /// Why the generation: `Stop` only sets a flag, and the loop that is inside a
        /// `ReadAsync` reaches its end a moment later. Starting the next loop beside it
        /// would leave two logins on one account - the old one retrying on a socket it
        /// no longer has. A loop whose generation is no longer the current one stops
        /// instead of retrying, and does not touch `_running` on its way out.
        /// </summary>
        public async Task RestartAsync()
        {
            Stop();

            // `Stop` only asks the loop to end; the old login may still be inside its read,
            // and a second login started beside it is two sessions on one account - the
            // stale one replacing `_session` under every send. The loop's own task is what
            // says it has left. The wait is bounded, because a socket that does not answer
            // must not park the pairing that asked for the restart.
            Task loop = _loop;
            if (loop != null)
            {
                await Task.WhenAny(loop, Task.Delay(TimeSpan.FromSeconds(5)));
            }

            Start();
        }

        /// <summary>
        /// Ends the connection on purpose. A background task is one check and not a
        /// session: without this the login loop would sit in its wait and keep the
        /// process alive until the system kills it.
        ///
        /// The flag is what the loop reads between attempts; closing the session is
        /// what ends the read that is happening now, and its failure is the loop
        /// leaving, not a fault.
        /// </summary>
        public void Stop()
        {
            _running = false;

            // A loop that is between attempts must not come back: the generation is what
            // it compares itself to (see RestartAsync).
            _generation++;

            NativeSession session = _session;
            if (session != null) session.Close();

            Status = "stopped on request";
            Diag.Ok("native: stopped on request");
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
        private async Task RunAsync(int generation)
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

            while (_running && generation == _generation)
            {
                NativeSession session = null;
                try
                {
                    session = new NativeSession(new WebSocketTransport(), new ProtocolLog(), _random);
                    _session = session;
                    Status = "logging in as " + account.Jid;

                    BinaryNode welcome = await session.LoginAsync(account, WhatsAppSocket.DefaultUrl);
                    RememberServerLid(account, welcome);

                    _account = account;
                    _loggedIn = true;
                    _lastNode = DateTime.UtcNow;
                    Status = "logged in as " + account.Jid;
                    Diag.Ok("native: logged in as " + account.Jid);

                    await AnnounceClientStateAsync(session);
                    await AdoptSignedPreKeyAsync(account);
                    await PublishPreKeysAsync(session, account);
                    await session.GoAvailableAsync(account.PushName);

                    // The primary's address book, as the app state carries it. Off the login's
                    // own path and not awaited: it is one more query on a socket that is
                    // already useful, and a sync that fails (or that has no key yet) must not
                    // hold up the reading of messages.
                    RememberStoredAppStateKeys(account);
#pragma warning disable 4014
                    Guarded.RunGuardedAsync("NativeConnection/app state", SyncAppStateAsync(session));
#pragma warning restore 4014

                    // The subjects of the groups this list already has. Until this ran only at
                    // the end of a sync, a device that had been linked for days had no sync to
                    // wait for and kept `Gruppo 1203...` for ever.
                    NameTheGroups();

#pragma warning disable 4014
                    Guarded.RunGuardedAsync("NativeConnection/keepalive", KeepAliveAsync(session));
#pragma warning restore 4014

                    // A connection that worked resets the patience: the next drop waits a
                    // second again, not the half minute an hour of retrying had reached.
                    attempt = 0;

                    await ReadAsync(session, account);
                }
                catch (ClientRefusedException refused)
                {
                    // The server refuses the *client*, not the account: this loop that
                    // retried it every second was the "reconnect loop that never says
                    // why" its own comment warns about. There is nothing to retry until
                    // the client announces a version the server accepts, so the run stops
                    // and the sentence is the status.
                    Diag.Ok("native: " + refused.Message);
                    Status = refused.Message;
                    break;
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

                    // The socket is gone, so is the login on it. Set before the status below,
                    // because that status is what a listener reads to decide whether there is
                    // an account to ask anything of.
                    if (!IsConnected) _loggedIn = false;
                }

                if (!_running || generation != _generation) break;

                attempt++;
                int delay = ConnectionPolicy.ReconnectDelaySeconds(attempt);
                Status = Status + " - retrying in " + delay + "s";
                Diag.Ok("native: " + Status);

                await Task.Delay(TimeSpan.FromSeconds(delay));
            }

            // Only the loop that is still the current one clears the flag: a restart
            // that already started another loop must not be stopped by this tail.
            if (generation == _generation) _running = false;
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
                // The loop shares the ui thread with the pages - the socket was opened
                // from it and every continuation comes back to it - so it hands the
                // thread back once per node. Without this line a burst the server sends
                // in one message (the backlog it answers `offline_batch` with) is one
                // uninterrupted piece of work, and the phone stops answering for as long
                // as the server keeps talking.
                await Task.Yield();

                Diag.Step("native: waiting for a frame");
                BinaryNode node = await session.ReadNodeAsync();
                _lastNode = DateTime.UtcNow;

                InboundKind kind = InboundRouter.Classify(node);
                Diag.Step("native: a " + kind + " frame (" + (node == null ? "no node" : node.Tag) + ")");

                if (kind == InboundKind.Probe)
                {
                    Diag.Ok("native: the server asked whether this connection is alive");
                    Diag.Step("native: answering the probe");
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

                // `node` can be null here - `InboundRouter.Classify` answers Ignored for a
                // read that produced nothing, which is what the log line above already
                // handles - and reading `Tag` off it tore down the connection for a frame
                // the loop only had to skip.
                if (kind == InboundKind.Ack || (kind == InboundKind.Ignored && node != null && node.Tag == "iq"))
                {
                    CompleteWaiter(node);

                    // The same `ack` that answers a query also answers a message this phone
                    // sent. It is offered to the messages first: an id nobody is waiting on
                    // is not a message of this run, and nothing happens.
                    if (kind == InboundKind.Ack) AnnounceAck(node);
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
                        Diag.Step("native: reading message " + node.Attrs["id"]);
                        await ReadMessageAsync(session, account, node);

                        // A stanza can be the one that brings a member's sender key, and
                        // with it the answer to a message of theirs that landed first.
                        await RetryHeldAsync(session, account, node);
                        continue;
                    }
                }
                catch (Exception error)
                {
                    // One stanza that cannot be read is one stanza: a session dropped over a
                    // message nobody can decrypt would turn a single bad frame into a phone
                    // that cannot stay online. The failure is named with the id it carries.
                    // The answer to it - a retry receipt - is sent by ReadMessageAsync, where
                    // C# 5 allows the await (a catch block may not contain one).
                    Diag.Failed("NativeConnection/message", error);
                    continue;
                }

                if (kind == InboundKind.Notification)
                {
                    Diag.Step("native: reading a notification");

                    // The notification path is read for the same reason the message path is
                    // survived, with one exception: the refusal of this client is a version
                    // problem, and retrying it as a network one is a reconnect loop that never
                    // says why.
                    try
                    {
                        await ReadNotificationAsync(session, node);
                    }
                    catch (ClientRefusedException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        Diag.Failed("NativeConnection/notification", error);
                    }

                    continue;
                }

                // The rest is named and not read yet: naming what arrived is what makes the
                // next device run's log worth reading, which is exactly what the run of
                // 2026-10-10 lacked.
                //
                // The tag is asked for the way the line above asks for it: this is the
                // branch a node that is *not there* falls into (`Classify` answers Ignored
                // for it), and reading `Tag` off it here was an exception in the one place
                // whose whole job is to survive the frame - which the loop above turns into
                // a closed socket and a new login.
                Diag.Ok("native: " + kind + " (" + (node == null ? "no node" : node.Tag)
                    + ") arrived, and nothing reads it yet");
            }
        }

        /// <summary>
        /// One `ib`/`notification` off the wire: the channel the server's bootstrap, its
        /// routing addresses and the two commands that decide whether this login continues
        /// all arrive on.
        ///
        /// Why it is read now and not only named: every start of the app on 2026-10-17
        /// logged `native: Notification (ib) arrived, and nothing reads it yet` and then lost
        /// the connection about two seconds later, four runs in a row - so the one frame that
        /// could say why was the frame the log did not name. The child's tag is what the
        /// reference dispatches on (`encrypt`, `mediaretry`, `server_sync`, `offline`,
        /// `edge_routing`, `downgrade_webclient` and the rest), so it is what this log names
        /// now, together with the node itself.
        ///
        /// No acknowledgement goes out for these: the reference acks the stanzas that carry a
        /// message and leaves `ib` to its own handlers, which either answer with a node (the
        /// backlog preview) or with nothing at all.
        /// </summary>
        private async Task ReadNotificationAsync(NativeSession session, BinaryNode node)
        {
            string type = InboundRouter.NotificationType(node);
            string text = node == null ? "" : node.ToString();
            if (text.Length > 240) text = text.Substring(0, 240) + "...";

            Diag.Ok("native: a notification arrived (" + (type == null ? "no child" : type) + "): " + text);

            if (ClientState.IsClientRefusal(node))
            {
                // The server is refusing the *client*, not the account: the reference ends the
                // connection here with a multi-device mismatch. It is thrown so the loop's own
                // failure line carries the sentence - a retry on its own reads as a network
                // problem, and this is a version problem.
                throw new ClientRefusedException("the server refused this client: the multi-device " +
                    "upgrade is not available to the version this app announces (" + ClientState.Downgrade + ")");
            }

            if (type == ClientState.OfflinePreview)
            {
                // The server asks how much backlog this client wants; a client that answers
                // nothing is a client it has no size for.
                await session.SendAsync(ClientState.OfflineBatch());
                Diag.Ok("native: asked the server for " + ClientState.OfflineBatchCount + " message(s) of the backlog");
                return;
            }

            if (type == ClientState.Offline)
            {
                // The reference flushes its buffered events here: this is the last frame of
                // the bootstrap, so everything before it is this account's state.
                Diag.Ok("native: the server's backlog is over");

                // And if it is over with nothing in it, that is the answer to "why is the
                // screen empty": WhatsApp sends the history when the device is linked and
                // never again, so an account whose first sync was missed has to be linked
                // again to get it. The line exists so the next device run says which of the
                // two it is instead of showing a blank list.
                if (_chats.Rows.Count == 0 && !_chats.IsSyncComplete)
                {
                    Diag.Ok("native: the backlog ended with no chat and no history: this device has "
                        + "to be linked again to receive the history (WhatsApp sends it once, at the pairing)");
                }

                return;
            }

            if (type == ClientState.EdgeRouting)
            {
                // The reference keeps these addresses in its credentials. This client has no
                // field for them yet, and the line is what makes that a decision rather than
                // an omission.
                Diag.Ok("native: the server sent the edge routing for this client, which is not stored yet");
                return;
            }
        }

        /// <summary>
        /// The account the server says it opened, when it says more than this phone stored.
        ///
        /// `success` carries the account's `lid`, and the reference writes it into its own
        /// credentials on every login (`@whiskeysockets/baileys`, `Socket/socket.js`, the
        /// `CB:success` handler: `creds.update`, `me.lid`). This client decides whether a
        /// stanza is this account's own copy by comparing its user against the number *and*
        /// the LID (`IncomingMessage.IsMine`), and the history sync is applied only when that
        /// answer is yes - so a LID the server hands out and this phone keeps the older one of
        /// would make the account's own sync look like a contact's and drop it.
        ///
        /// A save that fails is undone and named: a better LID is not worth the connection.
        /// </summary>
        private void RememberServerLid(Credentials account, BinaryNode welcome)
        {
            if (account == null || welcome == null) return;

            string lid = welcome.Attrs["lid"];
            if (string.IsNullOrEmpty(lid) || lid == account.Lid) return;

            string before = account.Lid;
            account.Lid = lid;

            try
            {
                _store.Save(account);
                Diag.Ok("native: the server's LID replaced " + (before == null ? "(none)" : before));
            }
            catch (Exception error)
            {
                account.Lid = before;
                Diag.Failed("NativeConnection/lid", error);
            }
        }

        /// <summary>
        /// The two sentences a companion owes the server the moment its login is answered:
        /// that it is active, and which session it is.
        ///
        /// Why the first one is not optional: `ClientPayload.Login` asks to be brought up
        /// **passively** (`passive` and `pull` are 1 in the payload this app sends), and the
        /// reference completes that login with `sendPassiveIq('active')` on the `success` node
        /// (`@whiskeysockets/baileys`, `Socket/socket.js`). A device that logs in and then says
        /// nothing stays the passive half of its own login - which is what the phone shows:
        /// the login, two `ib` nodes, and the server hanging up about two seconds later, at
        /// every start.
        ///
        /// A failure here is named and survived: a sentence the server did not get is worth a
        /// log line, and it is not worth ending a connection that may still carry the history.
        /// </summary>
        private async Task AnnounceClientStateAsync(NativeSession session)
        {
            try
            {
                await session.SendAsync(ClientState.PassiveIq(ClientState.Active, _random));
                Diag.Ok("native: the client is active (the passive iq went out)");

                DateTime now = DateTime.UtcNow;
                await session.SendAsync(ClientState.UnifiedSession(now));
                Diag.Ok("native: unified session " + ClientState.UnifiedSessionId(now));
            }
            catch (Exception error)
            {
                Diag.Failed("NativeConnection/client state", error);
            }
        }

        /// <summary>
        /// One `message` stanza: opened, acknowledged, and - depending on what it carries -
        /// counted as a chat row, turned into the history, or left for the media path.
        ///
        /// The order is the whole point: the read comes first and the acknowledgement after
        /// it. The ack is what stops the server re-sending, so acked-before-read would turn
        /// any failed open into a permanent loss - and when the stanza was the history sync,
        /// the chat list would never come back. A stanza that did not open is answered with
        /// `IncomingMessage.RetryReceipt`, which asks the sender to encrypt it again.
        /// </summary>
        private async Task ReadMessageAsync(NativeSession session, Credentials account, BinaryNode node)
        {
            IncomingMessage incoming = null;
            Exception failure = null;

            try
            {
                incoming = IncomingMessage.Read(node, account,
                    await SessionsAsync(), await PreKeysAsync(), _seen, _random);
            }
            catch (Exception error)
            {
                // C# 5 refuses an await inside a catch, so the failure is carried out of it
                // and answered below: one message that did not open must not take the
                // connection, and it must not be acknowledged as if it had.
                failure = error;
            }

            if (incoming != null && incoming.Refused == IncomingRefusal.WaitsForSenderKey)
            {
                // Not a failure and not an exception: the message is kept until the key it
                // needs arrives, which is usually one stanza later. It is not acknowledged
                // either - the server still owning it is what makes a phone that ends before
                // the key comes get it back - and it is not asked again, which would put the
                // message in a race with the key the sender has already sent.
                if (_held.Hold(incoming.Chat, incoming.Sender, incoming.Id, node))
                {
                    Diag.Ok("native: " + incoming.Id + " waits for the sender key of "
                        + incoming.Sender + " in " + incoming.Chat);
                }

                return;
            }

            if (failure != null)
            {
                Diag.Failed("NativeConnection/message", failure);

                // The answer is a retry, not an acknowledgement: the server re-sends what it
                // was not told was read, while an `ack` here tells it the message arrived and
                // the message is then gone for good - which is how a failed open could lose
                // the one history sync this account will ever get.
                await AskAgainAsync(session, node, account);
                return;
            }

            HistorySyncNotification history = incoming == null ? null : HistorySyncNotification.From(incoming.Content);
            if (history != null)
            {
                // The sync is this account's own: it is a peer-routed self stanza, sent by
                // another device of this account, and the reference reads it only from there.
                // The same stanza from a contact is a contact writing this phone's chat list,
                // its call log and its status feed, which is why the check is here, before
                // anything the notification carries is fetched or applied.
                if (!IncomingMessage.IsMine(incoming.From, account))
                {
                    Diag.Ok("native: a history sync from " + incoming.From
                        + " is not this account's, so it is left alone");
                    await session.SendAsync(IncomingMessage.Ack(node));
                    return;
                }

                // The acknowledgement of a sync comes **after** the sync is in the list, and
                // never before. The ack is what tells the server the stanza is delivered, and
                // WhatsApp pushes the story of a device once per link: an ack sent at the top
                // threw the rest of the chat list away the moment the blob behind it did not
                // download (or the process ended before it did). That is the phone of
                // 2026-10-23, where the bootstrap's five conversations were acked, the chunk
                // after them never arrived, and no reconnection ever brought it back.
                //
                // A sync that did not land is asked for again instead, like any stanza this
                // phone could not read: the server re-sends what nobody acknowledged.
                if (await ApplyHistoryAsync(history)) await session.SendAsync(IncomingMessage.Ack(node));
                else await AskAgainAsync(session, node, account);

                return;
            }

            // The acknowledgement comes after the read, and it is what proves the read.
            // A duplicate is acknowledged too: it was read once already, and the second copy
            // is what proves the first ack was lost. This line is below the history sync on
            // purpose, and not only because a sync is answered above: whoever moves it up is
            // one `if` away from acking a sync that never landed, and the guard
            // (`tools/check-history-sync.js`) reads that order.
            await session.SendAsync(IncomingMessage.Ack(node));

            if (incoming == null) return;

            if (incoming.Refused == IncomingRefusal.AlreadyRead)
            {
                // The key this copy needed was spent the first time the message arrived, and a
                // retry cannot bring it back - every run the debugger stops leaves a few of
                // these. Asking for the stanza again was a loop that never ended (24 October
                // 2026: three group messages asked for on every connection, each one a red
                // line); the ack above has just stopped the re-delivery, and there is nothing
                // read to show.
                Diag.Ok("native: " + incoming.Id + " was already read; the copy is acknowledged");
                return;
            }

            if (incoming.Refused == IncomingRefusal.Unreadable)
            {
                // The ciphertext opened and what came out is not a message: the bytes are
                // damaged, and a retry brings the same bytes. Four group messages were asked for
                // on every connection of 24 October 2026 for exactly this - the stanza is
                // acknowledged above, so the server stops bringing it back, and the line names
                // it once for the log.
                Diag.Ok("native: " + incoming.Id + " in " + incoming.Chat
                    + " opened and is not a message; the copy is acknowledged");
                return;
            }

            // The keys the primary shares with a device it has just linked. Read here, before
            // the rule below drops this account's own copies: the share **is** one of ours (the
            // phone writes it), and dropping it was dropping the only way to read the account's
            // own app state - the names saved in the primary's address book.
            LearnAppStateKeys(session, incoming, account);

            // A message taken back: its payload is the `ProtocolMessage` of type revoke and
            // the only thing it says is the id of another message. It has no bubble of its
            // own - the row it names is what goes. Read before the echo rule below would
            // matter and before the empty-content rule after it: a revoke carries no text and
            // no media, so every later check would treat it as a stanza with nothing in it.
            string revoked = MessageProtocol.RevokedId(
                incoming.Content == null ? null : incoming.Content.ProtocolMessage);
            if (revoked != null)
            {
                Diag.Ok("native: " + incoming.From + " took back " + revoked + " in " + incoming.Chat);

                var revocation = new MessageRevocation();
                revocation.Jid = incoming.Chat;
                revocation.Id = revoked;

                var listener = MessageRevoked;
                if (listener != null) listener(this, revocation);
                return;
            }

            // A copy of something this account sent from another device: shown by the phone
            // that wrote it, never by this one (see IncomingMessage.IsOurEcho).
            if (incoming.IsOurEcho) return;

            // Who wrote it, before it is shown: the name the stanza carries (`notify`) and the
            // phone number behind a LID (`sender_pn`/`participant_pn`). Both are on every
            // message and neither was read anywhere, which is why a chat list full of people
            // who had written looked like a list of phone numbers.
            LearnFrom(incoming);

            if (StatusFeed.IsStatus(incoming.From))
            {
                // A status is not a chat: a row for `status@broadcast` would sit at the top of
                // the list under the name "status" for every contact who posted this morning
                // (`StatusFeed.IsStatus`; Review Focus 5). It goes to the feed instead, and
                // the chat list never hears about it.
                await AnnounceStatusAsync(incoming);
                return;
            }

            if (incoming.Content == null && incoming.Text == null)
            {
                // A group stanza that carried nothing but a member's sender key: it was
                // stored on the way in, and there is nothing to show. Saying so keeps it out
                // of the media path, which would report it as a message it cannot read.
                Diag.Ok("native: a sender key arrived for " + incoming.From);
                return;
            }

            if (incoming.Text == null)
            {
                await ReadMediaAsync(incoming);
                return;
            }

            var message = new HistoryMessage();
            message.Id = incoming.Id;
            // The chat, not the address it came from: the stanza keeps the sender's device
            // and a row keyed on it would be a row per device (`IncomingMessage.Chat`).
            message.Jid = incoming.Chat;
            message.Text = incoming.Text;
            message.Timestamp = incoming.Timestamp;
            message.IsFromMe = false;
            // Who wrote it, for a group: the row's chat is the group, so the sender has to
            // travel with the message or the bubble would say the group wrote it. Without
            // its device, because this one is shown to a person, and on the canonical
            // address, so a bubble and a contact row key on the same JID.
            message.Sender = NameCache.Book.Canonical(incoming.Sender);
            // And what they call themselves, which is the only name a one-to-one chat has:
            // the history carries no name for a person (`ApplyChat` falls back to it).
            message.SenderName = incoming.PushName;

            if (!_chats.AddIncoming(message)) return;

            Diag.Ok("native: " + incoming.Id + " from " + incoming.From);

            HistoryConversation row = _chats.Find(message.Jid);
            if (row != null) _chats.RefreshName(row);
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
            return await RequestAsync(session, query, false);
        }

        /// <summary>
        /// The same, with the one choice that matters to a caller: whether an `error` answer
        /// is handed back as the node ("the answer to this question is no") or thrown ("the
        /// query failed"). See `_refusalIsAnswer`.
        /// </summary>
        private async Task<BinaryNode> RequestAsync(NativeSession session, BinaryNode query, bool refusalIsAnswer)
        {
            string id = query.Attrs["id"];
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a query with no id has no answer to wait for");

            var completion = new TaskCompletionSource<BinaryNode>();
            lock (_waiters)
            {
                _waiters[id] = completion;
                if (refusalIsAnswer) _refusalIsAnswer.Add(id);
            }

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
                lock (_waiters)
                {
                    _waiters.Remove(id);
                    _refusalIsAnswer.Remove(id);
                }
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
                bool anAnswer;
                lock (_waiters) anAnswer = _refusalIsAnswer.Contains(id);

                if (!anAnswer)
                {
                    completion.TrySetException(new ProtocolException("the server refused " + id + ": " + node));
                    return;
                }
            }

            completion.TrySetResult(node);
        }

        /// <summary>
        /// Keeps an id until the server acks it, so the ack can be recognised as ours.
        /// Registered before the stanza is written: the answer can only come after the
        /// write, but it can come before the write's `await` has returned.
        /// </summary>
        private void AwaitAck(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            lock (_awaitingAck)
            {
                // An id that has been acked is gone from the set and its place in the
                // order is stale; dropping those first keeps the queue bounded.
                while (_ackOrder.Count > 0 && !_awaitingAck.Contains(_ackOrder.Peek())) _ackOrder.Dequeue();

                while (_awaitingAck.Count >= MaxAwaitingAck && _ackOrder.Count > 0)
                {
                    _awaitingAck.Remove(_ackOrder.Dequeue());
                }

                _awaitingAck.Add(id);
                _ackOrder.Enqueue(id);
            }
        }

        /// <summary>An id the server will never ack, because the write that carried it failed.</summary>
        private void ForgetAck(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            lock (_awaitingAck) _awaitingAck.Remove(id);
        }

        /// <summary>
        /// One message written to the socket, with its id remembered until the server says
        /// it has it. The write failing and the server refusing are two different things, and
        /// both end with the bubble saying so; what used to happen was neither being said.
        /// </summary>
        private async Task WriteAsync(NativeSession session, string id, BinaryNode node)
        {
            AwaitAck(id);

            try
            {
                await session.SendAsync(node);
            }
            catch (Exception)
            {
                ForgetAck(id);
                throw;
            }
        }

        /// <summary>
        /// The server's answer for a message this phone sent: accepted (Sent), or refused
        /// with a code (Failed). Anything else it acks - a query, a read receipt of its own -
        /// is not in the set and is left to whoever asked for it.
        /// </summary>
        private void AnnounceAck(BinaryNode node)
        {
            string id = node == null ? null : node.Attrs["id"];
            if (string.IsNullOrEmpty(id)) return;

            bool ours;
            lock (_awaitingAck)
            {
                ours = _awaitingAck.Remove(id);
            }

            if (!ours) return;

            bool refused = node.Attrs["type"] == "error";

            if (refused)
            {
                string code = node.Attrs["error"];
                if (string.IsNullOrEmpty(code)) code = node.Attrs["code"];
                if (string.IsNullOrEmpty(code)) code = "no code";

                Diag.Failed("native: the server refused message " + id,
                    new ProtocolException("the server would not take it (" + code + ")"));
            }
            else
            {
                Diag.Ok("native: the server has message " + id);
            }

            if (MessageStatusChanged == null) return;

            var update = new MessageStatusUpdate();
            update.Id = id;
            update.IsAck = true;
            update.IsRefused = refused;

            MessageStatusChanged(this, update);
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

            NativeSession session = OpenSession();

            if (OutgoingMessage.IsGroup(jid))
            {
                return await SendGroupAsync(session, jid, Message.Text(body), OutgoingMessage.TextType, null, body);
            }

            IList<KeyValuePair<string, SessionRecord>> devices = await DevicesAsync(session, jid);

            string id = OutgoingMessage.Id(_random);
            uint timestamp = NowSeconds();

            await WriteAsync(session, id, OutgoingMessage.Node(jid, id, body, devices, timestamp, _random));

            _chats.AddOutgoing(jid, id, body, timestamp);
            Diag.Ok("native: " + id + " to " + jid + " over " + devices.Count + " device(s)");

            return id;
        }

        /// <summary>
        /// Takes a message back for everybody: a stanza of its own whose payload is the
        /// `ProtocolMessage` of type revoke, sent the way a text is.
        ///
        /// Why it goes through the same write: a revoke is an ordinary message to the
        /// server. It has an id, it is encrypted device by device, and the bubble that stays
        /// on screen while it is on its way is the one that already showed the message. Its
        /// id is not added to the chat's preview: the preview is stepped back when the
        /// message is deleted locally, and a row saying nothing would be worse than the
        /// sentence it replaces.
        /// </summary>
        public async Task<string> RevokeAsync(string jid, string messageId)
        {
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a revoke needs the chat it happened in");
            if (string.IsNullOrEmpty(messageId)) throw new ProtocolException("a revoke needs the message it takes back");

            NativeSession session = OpenSession();
            Message payload = Message.Revoke(jid, messageId);

            // A group takes the sender key and the distribution; one person takes the device
            // list. Both end in one stanza, which is the same one a text would be.
            if (OutgoingMessage.IsGroup(jid))
            {
                return await SendGroupAsync(session, jid, payload, OutgoingMessage.TextType, null, "");
            }

            IList<KeyValuePair<string, SessionRecord>> devices = await DevicesAsync(session, jid);

            string id = OutgoingMessage.Id(_random);
            uint timestamp = NowSeconds();

            await WriteAsync(session, id, OutgoingMessage.Node(jid, id, payload,
                OutgoingMessage.TextType, null, devices, timestamp, _random));

            Diag.Ok("native: revoke " + messageId + " as " + id + " to " + jid);
            return id;
        }

        /// <summary>
        /// A photo, a voice note, a video or a document: encrypted here, uploaded to
        /// WhatsApp's own media host, and then sent as a message that says where it is.
        ///
        /// Three round trips and not one, because each answers a different question: the
        /// device list asks *who*, the media connection asks *where* (and hands out the
        /// token, which has a lifetime, so it is asked for per send rather than cached), and
        /// the upload itself answers with the address the message carries.
        ///
        /// The bytes are handed over whole: the media format puts the signature of the
        /// ciphertext after it, so a sender cannot stream it either
        /// (`WhatsappApp.Protocol.Media.MediaUpload`).
        /// </summary>
        public async Task<string> SendMediaAsync(string jid, byte[] file, string mediaType, string mimeType,
            string fileName, string caption, uint seconds, bool voiceNote)
        {
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a message needs a chat to go to");
            if (file == null || file.Length == 0) throw new ProtocolException("there is no file to send");
            if (string.IsNullOrEmpty(mediaType)) throw new ProtocolException("a file needs its media type");

            NativeSession session = OpenSession();

            BinaryNode answer = await RequestAsync(session, MediaConn.Query(OutgoingMessage.Id(_random)));
            MediaConnInfo conn = MediaConn.Read(answer);

            UploadedMedia uploaded = await MediaUpload.SendAsync(new WinRtMediaFetcher(), conn, file,
                mediaType, mimeType, fileName, _random);

            Message payload = MessageFor(uploaded, caption, seconds, voiceNote);
            string mediaWord = MediaWord(mediaType, voiceNote);
            string rowText = RowText(uploaded, caption);

            // The upload comes first and the routing after it: a group message is the same
            // file on the same host, and only the stanza that names it differs.
            if (OutgoingMessage.IsGroup(jid))
            {
                return await SendGroupAsync(session, jid, payload, OutgoingMessage.MediaType, mediaWord, rowText);
            }

            IList<KeyValuePair<string, SessionRecord>> devices = await DevicesAsync(session, jid);

            string id = OutgoingMessage.Id(_random);
            uint timestamp = NowSeconds();

            await WriteAsync(session, id, OutgoingMessage.Node(jid, id, payload, OutgoingMessage.MediaType,
                mediaWord, devices, timestamp, _random));

            _chats.AddOutgoing(jid, id, rowText, timestamp);
            Diag.Ok("native: " + id + " (" + mediaType + ", " + file.Length + " bytes) to " + jid
                + " over " + devices.Count + " device(s)");

            return id;
        }

        /// <summary>
        /// The group devices this phone has not handed its sender key to yet, per group and
        /// per run.
        ///
        /// Why a memory at all: the distribution has to reach every member device once, and
        /// a phone that re-sent twenty small encrypted nodes with every message would spend
        /// its battery on the group's membership. Why it is only in memory: after a restart
        /// every device gets the key once more, which costs one message and cannot leave
        /// anybody unable to read - the alternative is a file that can disagree with the
        /// server's own idea of who is in the group.
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _senderKeysSent =
            new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// One message into a group: the metadata, this device's sender key, one device list
        /// for every member at once, one distribution per device that has not had it, and
        /// one group ciphertext for all of them.
        /// </summary>
        private async Task<string> SendGroupAsync(NativeSession session, string jid, Message payload,
            string type, string mediaType, string rowText)
        {
            GroupInfo group = GroupDirectory.Read(jid,
                await RequestAsync(session, GroupDirectory.Query(OutgoingMessage.Id(_random), jid)));

            SenderKeyRecord record = await SenderKeyAsync(jid);
            IList<string> devices = await MemberDevicesAsync(session, group);

            SessionStore store = await SessionsAsync();

            HashSet<string> handed;
            lock (_senderKeysSent)
            {
                if (!_senderKeysSent.TryGetValue(jid, out handed))
                {
                    handed = new HashSet<string>();
                    _senderKeysSent.Add(jid, handed);
                }
            }

            var targets = new List<string>();
            for (int i = 0; i < devices.Count; i++)
            {
                lock (_senderKeysSent)
                {
                    if (handed.Contains(devices[i])) continue;
                }

                targets.Add(devices[i]);
            }

            // The sessions for the devices that need the key, and a bundle asked for in one
            // request for the ones this phone has never talked to. A device the server has no
            // bundle for is skipped, named: a group message must not be abandoned because one
            // member cannot be reached (`AGroupWithAMemberWeCannotReachStillSends`).
            var distributions = new List<KeyValuePair<string, SessionRecord>>();

            if (targets.Count > 0)
            {
                var missing = new List<string>();
                for (int i = 0; i < targets.Count; i++)
                {
                    SessionRecord existing = store.Load(targets[i]);
                    if (existing == null || !existing.HasSession) missing.Add(targets[i]);
                }

                IList<KeyValuePair<string, PreKeyBundle>> bundles = null;
                if (missing.Count > 0)
                {
                    bundles = DeviceDirectory.Bundles(await RequestAsync(session,
                        DeviceDirectory.BundleQuery(missing, _random)));
                }

                for (int i = 0; i < targets.Count; i++)
                {
                    PreKeyBundle bundle = Find(bundles, targets[i]);

                    if (store.Load(targets[i]) == null && bundle == null)
                    {
                        Diag.Ok("native: no bundle for " + targets[i] + ", so it is not handed the key");
                        continue;
                    }

                    distributions.Add(new KeyValuePair<string, SessionRecord>(targets[i],
                        DeviceDirectory.Session(targets[i], bundle, _account, store, _random)));
                }
            }

            string id = OutgoingMessage.Id(_random);
            uint timestamp = NowSeconds();

            await WriteAsync(session, id, OutgoingMessage.Group(jid, id, payload, type, mediaType, record,
                group.AddressingMode, distributions, timestamp, _random));

            // The chain has moved: `Encrypt` advanced the iteration in memory, and this file
            // is the only thing that says which one the next message uses. A record that is
            // not written back leaves every later message at iteration 0 - the same key and
            // the same IV as the first - which the other members refuse as already read.
            store.SaveSenderKey(SenderKeyName(jid, _account == null ? null : _account.Jid), record);

            lock (_senderKeysSent)
            {
                for (int i = 0; i < distributions.Count; i++) handed.Add(distributions[i].Key);
            }

            _chats.AddOutgoing(jid, id, rowText, timestamp);
            Diag.Ok("native: " + id + " to group " + group.DisplayName + " (" + devices.Count
                + " device(s), " + distributions.Count + " key(s) handed out)");

            return id;
        }

        /// <summary>
        /// This device's sender key for a group: the one on disk when there is one, and a
        /// new chain otherwise - a chain that then keeps its `keyId` for as long as the file
        /// lives, which is what lets a member read message 400 the way it read message 1.
        /// </summary>
        private async Task<SenderKeyRecord> SenderKeyAsync(string groupJid)
        {
            SessionStore store = await SessionsAsync();
            string name = SenderKeyName(groupJid, _account == null ? null : _account.Jid);

            SenderKeyRecord record = store.LoadSenderKey(name);
            if (record != null) return record;

            record = SenderKeyRecord.Create(groupJid, _random);
            store.SaveSenderKey(name, record);

            return record;
        }

        /// <summary>
        /// Where a member's sender key lives: the group and **the device that wrote it**.
        ///
        /// The device and not the phone number, because sender keys are per device - a
        /// contact with a phone and a tablet writes with two chains - and a name that stopped
        /// at the number would have the second device's messages read with the first's chain
        /// and refused for an iteration that never existed.
        /// </summary>
        private static string SenderKeyName(string groupJid, string deviceJid)
        {
            return groupJid + "/" + deviceJid;
        }

        /// <summary>
        /// Every device of every member, and of this account's own other devices, minus the
        /// one this code is running on.
        ///
        /// One `usync` for the whole group: the device list is asked for per member in one
        /// request, which is the reference's own way (`getUSyncDevices(participantsList, ...)`).
        /// A hosted device (WhatsApp's own server-side ones) is skipped, and so is device 99,
        /// which is not a device but a placeholder the server puts in a list.
        /// </summary>
        private async Task<IList<string>> MemberDevicesAsync(NativeSession session, GroupInfo group)
        {
            IList<string> listed = DeviceDirectory.Devices(
                await RequestAsync(session, DeviceDirectory.Query(group.Participants, _random)));

            var devices = new List<string>();
            for (int i = 0; i < listed.Count; i++)
            {
                if (IsOurDevice(listed[i])) continue;
                if (IsHosted(listed[i])) continue;

                devices.Add(listed[i]);
            }

            return devices;
        }

        /// <summary>Whether a device JID is this phone: nobody sends a message to themselves.</summary>
        private bool IsOurDevice(string deviceJid)
        {
            return _account != null && (deviceJid == _account.Jid || deviceJid == _account.Lid);
        }

        /// <summary>
        /// WhatsApp's server-side devices (`@hosted` and `@hosted.lid`) and the placeholder
        /// device 99: the reference leaves all three out of a group fan-out
        /// (`messages-send.js`: `isHostedLidUser`, `isHostedPnUser`, `device !== 99`).
        /// </summary>
        private static bool IsHosted(string deviceJid)
        {
            if (string.IsNullOrEmpty(deviceJid)) return true;
            if (deviceJid.EndsWith("@hosted", StringComparison.OrdinalIgnoreCase)) return true;
            if (deviceJid.EndsWith("@hosted.lid", StringComparison.OrdinalIgnoreCase)) return true;

            // The device is inside the JID (`name:99@server`), so the check parses it: a
            // prefix test on the string matched the phone number, never the device.
            return JidUtils.IsPlaceholderDevice(deviceJid);
        }

        /// <summary>The bundle of a device out of a bundle answer, or null.</summary>
        private static PreKeyBundle Find(IList<KeyValuePair<string, PreKeyBundle>> bundles, string deviceJid)
        {
            if (bundles == null) return null;

            for (int i = 0; i < bundles.Count; i++)
            {
                if (bundles[i].Key == deviceJid) return bundles[i].Value;
            }

            return null;
        }

        /// <summary>The message shape that matches the file: the four builders, chosen once.</summary>
        private static Message MessageFor(UploadedMedia uploaded, string caption, uint seconds, bool voiceNote)
        {
            if (uploaded.MediaType == MediaKeys.Image) return MediaUpload.Image(uploaded, caption);
            if (uploaded.MediaType == MediaKeys.Video) return MediaUpload.Video(uploaded, caption, seconds);
            if (uploaded.MediaType == MediaKeys.Audio) return MediaUpload.Audio(uploaded, seconds, voiceNote);

            return MediaUpload.Document(uploaded, caption);
        }

        /// <summary>The `mediatype` attribute: the reference's `getMediaType`, `ptt` included.</summary>
        private static string MediaWord(string mediaType, bool voiceNote)
        {
            if (mediaType == MediaKeys.Audio && voiceNote) return "ptt";

            return mediaType;
        }

        /// <summary>What the chat row shows: the caption, or the file's own name.</summary>
        private static string RowText(UploadedMedia uploaded, string caption)
        {
            if (!string.IsNullOrEmpty(caption)) return caption;
            if (!string.IsNullOrEmpty(uploaded.FileName)) return uploaded.FileName;

            return "[" + uploaded.MediaType + "]";
        }

        /// <summary>The session a send needs, or the sentence that says why there is none.</summary>
        private NativeSession OpenSession()
        {
            NativeSession session = _session;
            if (session == null || !session.IsOpen) throw new ProtocolException("there is no open account to send on");

            return session;
        }

        /// <summary>
        /// The sessions one chat's devices need, one per device, asked for on the wire every
        /// time.
        ///
        /// Why per send and not cached: a contact who linked a new phone - or removed one -
        /// would otherwise be written to at the device list they had yesterday. What is *not*
        /// re-done is the session itself: an established one is what the ratchet is for
        /// (`DeviceDirectory.Session`).
        /// </summary>
        private async Task<IList<KeyValuePair<string, SessionRecord>>> DevicesAsync(NativeSession session, string jid)
        {
            // Two questions, and they are two different answers. `Query` is the `usync`
            // device list (`user > devices > device-list > device[id]`); only
            // `BundleQuery` is answered with the keys (`registration`, `identity`,
            // `skey`, `key`). This used to hand the device list to `Bundles`, which is
            // the group path's second answer read as if it were the first: the first
            // `user.Child("registration")` is null there, so a text to one person ended
            // in a NullReferenceException before a stanza existed.
            //
            // The rule itself is `DirectSend`, in the protocol core: the desktop runner
            // tests it end to end, because this service needs the Windows Phone runtime
            // and the runner cannot build it.
            BinaryNode devicesAnswer = await RequestAsync(session, DeviceDirectory.Query(jid, _random));

            string ourJid = _account == null ? null : _account.Jid;
            string ourLid = _account == null ? null : _account.Lid;
            IList<string> targets = DirectSend.Targets(devicesAnswer, ourJid, ourLid);

            SessionStore store = await SessionsAsync();
            IList<string> missing = DirectSend.WithoutSession(targets, store);

            BinaryNode bundleAnswer = null;
            if (missing.Count > 0)
            {
                bundleAnswer = await RequestAsync(session, DeviceDirectory.BundleQuery(missing, _random));
            }

            DirectSendPlan plan = DirectSend.Plan(targets, bundleAnswer, _account, store, _random);

            // A device the server hands no key for is skipped and named, exactly as the
            // group path does: one unreachable device must not take the message.
            for (int i = 0; i < plan.Skipped.Count; i++)
            {
                Diag.Ok("native: no bundle for " + plan.Skipped[i] + ", so it is not written to");
            }

            if (plan.Devices.Count == 0) throw new ProtocolException("the server handed no key for " + jid);

            return plan.Devices;
        }

        /// <summary>
        /// The reader has seen this chat. The chat list keeps its own count so that a message
        /// arriving next is one, not the pile that was already read; the receipt below is the
        /// other half of the same decision.
        /// </summary>
        public void MarkChatRead(string jid)
        {
            _chats.MarkRead(jid);
        }

        /// <summary>Every conversation at once, for "read all".</summary>
        public void MarkAllChatsRead()
        {
            _chats.MarkAllRead();
        }

        /// <summary>How many messages of this chat are waiting, as the chat list counts them.</summary>
        public int UnreadFor(string jid)
        {
            return _chats.UnreadFor(jid);
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

        /// <summary>
        /// Says where this phone is: `available`, or `unavailable` when the app is going
        /// away. The push name is what the server passes on.
        ///
        /// Why the state is read and not assumed: the suspension sends `unavailable`, and
        /// this method used to answer every call with `available` - so the one moment the
        /// phone was frozen also told WhatsApp it was still there.
        /// </summary>
        public async Task SendPresenceAsync(string state)
        {
            NativeSession session = _session;
            if (session == null || !session.IsOpen) return;

            string type = state == SessionKeepAlive.Unavailable
                ? SessionKeepAlive.Unavailable
                : SessionKeepAlive.Available;

            await session.GoPresenceAsync(_account == null ? null : _account.PushName, type);
        }

        /// <summary>
        /// How long a resume waits for the answer to its ping. Shorter than the keep-alive's
        /// ceiling: the app has just come back and a person is looking at a list that has not
        /// changed, so a connection that does not answer promptly is one to replace rather
        /// than one to wait on.
        /// </summary>
        private const int ResumeAnswerSeconds = 8;

        /// <summary>
        /// The app has come back to the foreground: is this connection still a connection?
        ///
        /// Why it is not left to the keep-alive: WP8.1 freezes the process while the app is
        /// suspended, and the socket is usually gone by the time it resumes - closed by the OS
        /// or dropped by the server for being silent. Nothing observes a socket closed from
        /// the outside, so the read sits in a frame that will never come and the app keeps
        /// drawing the list it had: "it does not update", which looks like a broken app and
        /// is a connection nobody checked.
        ///
        /// True when the server answered, which is also when it is worth telling it that this
        /// phone is here again.
        /// </summary>
        public async Task<bool> CheckAsync()
        {
            NativeSession session = _session;

            if (!_running)
            {
                Start();
                return false;
            }

            if (session == null || !session.IsOpen) return false;

            DateTime sent = DateTime.UtcNow;

            try
            {
                await session.PingAsync();
            }
            catch (Exception error)
            {
                // The send itself failed: the read that is waiting on this socket is about to
                // fail too, and the loop above takes it from there.
                Diag.Failed("NativeConnection/check", error);
                return false;
            }

            for (int waited = 0; waited < ResumeAnswerSeconds; waited++)
            {
                if (!session.IsOpen) return false;

                if (_lastNode > sent)
                {
                    Diag.Ok("native: the connection answered after the resume");
                    return true;
                }

                await Task.Delay(1000);
            }

            Diag.Ok("native: nothing came back after the resume - dialling again");
            Status = "the connection was lost while suspended - dialling again";

#pragma warning disable 4014
            Guarded.RunGuardedAsync("NativeConnection/restart", RestartAsync());
#pragma warning restore 4014

            return false;
        }

        private static uint NowSeconds()
        {
            return (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// The answer to a stanza this phone could not take: a `retry` receipt, which asks the
        /// sender to send it again, instead of the `ack` that would throw it away.
        ///
        /// Why it is one method and not two copies: the two callers are the stanza that did
        /// not open and the history sync that did not land, and both of them must leave the
        /// stanza unacknowledged for the same reason (the server re-sends what nobody
        /// acknowledged). A failure of the receipt itself is named and survived: the stanza is
        /// still unacknowledged, which is the answer that matters.
        /// </summary>
        private async Task AskAgainAsync(NativeSession session, BinaryNode node, Credentials account)
        {
            try
            {
                await session.SendAsync(IncomingMessage.RetryReceipt(node, NowSeconds(),
                    account == null ? 0 : account.RegistrationId));
                Diag.Ok("native: asked " + node.Attrs["from"] + " to send " + node.Attrs["id"] + " again");
            }
            catch (Exception again)
            {
                Diag.Failed("NativeConnection/retry receipt", again);
            }
        }

        /// <summary>
        /// The messages of a group member that are waiting for their key, read now if it
        /// arrived.
        ///
        /// Why the trigger is every group stanza and not the one that carried the key: the key
        /// can come in a stanza of its own, inside `participants` of one that also carries a
        /// message, or in the copy another device of the same member sent - and all three end
        /// in the same store under the same name. Asking the store is one lookup, and a lookup
        /// with nothing to read is the ordinary case.
        ///
        /// Why the order matters: they are read oldest first, and each one goes through the
        /// path a stanza off the wire goes through - the ack, the row, the media - because by
        /// then it is exactly that. One that still cannot be opened - a chain that moved past it
        /// while the key was missing - is answered like any other stanza that did not open, with
        /// a retry receipt, and the one whose key is gone again is held again by
        /// `ReadMessageAsync`.
        /// </summary>
        private async Task RetryHeldAsync(NativeSession session, Credentials account, BinaryNode node)
        {
            if (node == null) return;

            string groupJid = node.Attrs["from"];
            string participant = node.Attrs["participant"];
            if (!GroupDirectory.IsGroup(groupJid) || string.IsNullOrEmpty(participant)) return;
            if (!_held.Has(groupJid, participant)) return;

            SessionStore store = await SessionsAsync();
            if (store.LoadSenderKey(groupJid + "/" + participant) == null) return;

            IList<BinaryNode> held = _held.Drain(groupJid, participant);
            if (held.Count == 0) return;

            Diag.Ok("native: the sender key of " + participant + " in " + groupJid
                + " arrived, reading " + held.Count + " held message(s)");

            for (int i = 0; i < held.Count; i++)
            {
                await ReadMessageAsync(session, account, held[i]);
            }
        }

        /// <summary>
        /// A message whose payload is media: the row first, then the bytes.
        ///
        /// The row goes out before the download is even started, because the bubble - a photo
        /// with its caption, or a voice note with its seconds - is what a person sees while
        /// the bytes are still coming, and a chat that stayed empty for the length of a 4 MB
        /// download would look like a message that never arrived.
        ///
        /// Every failure here is named and survived. A photo that cannot be downloaded - the
        /// phone lost its connection half way, the blob was taken down, the signature does not
        /// match - is a bubble that stays without its picture, and it must not be a session
        /// that ends. The message was acknowledged before this ran, so the server will not
        /// send it again either way.
        ///
        /// The encoded `Message` travels on the row, so the app reads a photo the same way
        /// whether it came in live or came out of the history: one decoder, one place the
        /// media type is worked out (`MediaMessages.Of`).
        /// </summary>
        private async Task ReadMediaAsync(IncomingMessage incoming)
        {
            MediaInfo info = MediaMessages.Of(incoming.Content);
            if (info == null)
            {
                // The chat is still a chat. A sticker, a poll, an edit, a contact card or a
                // media this build has no reader for says the conversation exists and just
                // moved, and dropping the row with the message left a group able to write
                // without ever appearing in the list (the phone of 2026-10-23). No bubble is
                // added: the row takes the time and the place, and says nothing else.
                NoteChatOf(incoming);

                Diag.Ok("native: message " + incoming.Id + " carries nothing this client reads yet");
                return;
            }

            var message = new HistoryMessage();
            message.Jid = incoming.Chat;
            message.Id = incoming.Id;
            message.Timestamp = incoming.Timestamp;
            message.Sender = NameCache.Book.Canonical(incoming.Sender);
            message.SenderName = incoming.PushName;
            message.Encoded = incoming.Content.Encode();

            if (_chats.AddIncoming(message))
            {
                Diag.Ok("native: " + info.Type + " " + incoming.Id + " from " + incoming.From);

                HistoryConversation row = _chats.Find(message.Jid);
                if (row != null) _chats.RefreshName(row);
                if (row != null && ChatArrived != null) ChatArrived(this, row);
                if (MessageArrived != null) MessageArrived(this, message);
            }

            if (!info.IsFetchable)
            {
                Diag.Ok("native: the media of " + incoming.Id + " names no key or no address");
                return;
            }

            byte[] bytes;
            try
            {
                bytes = await MediaDownload.FetchAsync(new WinRtMediaFetcher(), info.MediaKey,
                    info.Type, info.DirectPath, info.Url, info.FileSha256, info.FileEncSha256, info.FileLength);
            }
            catch (Exception error)
            {
                // The bubble stays, without its picture. A tap asks again (the media info is
                // on the bubble), so a download that failed once is not a message that can
                // never be opened.
                Diag.Failed("NativeConnection/media " + incoming.Id, error);
                return;
            }

            Diag.Ok("native: " + bytes.Length + " byte(s) of " + info.Type + " for " + incoming.Id);

            AnnounceMedia(message.Jid, incoming.Id, info, bytes);
        }

        /// <summary>
        /// A chat that arrived with a message this build cannot draw: the row, and no bubble.
        ///
        /// False cases are the addresses that are not conversations - `status@broadcast`, a
        /// channel and the placeholder the server writes for "no account" - so this never puts
        /// a row on the list that WhatsApp itself would not show.
        /// </summary>
        private void NoteChatOf(IncomingMessage incoming)
        {
            if (incoming == null || incoming.Chat == null) return;
            if (!JidUtils.IsConversation(incoming.Chat)) return;
            if (!_chats.NoteIncoming(incoming.Chat, incoming.Timestamp)) return;

            HistoryConversation row = _chats.Find(incoming.Chat);
            if (row != null) _chats.RefreshName(row);
            if (row != null && ChatArrived != null) ChatArrived(this, row);

            Diag.Ok("native: " + incoming.Chat + " is in the list from a message this build does not draw");
        }

        /// <summary>The bytes of a media message, for the app to put in the bubble.</summary>
        private void AnnounceMedia(string jid, string id, MediaInfo info, byte[] bytes)
        {
            if (MediaArrived == null) return;

            var arrival = new MediaArrival();
            arrival.Jid = jid;
            arrival.Id = id;
            arrival.Info = info;
            arrival.Bytes = bytes;

            MediaArrived(this, arrival);
        }

        /// <summary>
        /// The bytes of a media message this phone already knows about but has not got - a row
        /// from the history, or one whose download failed earlier.
        /// </summary>
        public Task<byte[]> DownloadMediaAsync(MediaInfo info)
        {
            if (info == null) throw new ProtocolException("there is no media to download");
            if (!info.IsFetchable) throw new ProtocolException("this media names no key or no address");

            return MediaDownload.FetchAsync(new WinRtMediaFetcher(), info.MediaKey, info.Type,
                info.DirectPath, info.Url, info.FileSha256, info.FileEncSha256, info.FileLength);
        }

        /// <summary>
        /// Somebody's picture: the query answers a URL, the URL answers the JPEG.
        ///
        /// Two round trips and not one, because that is the shape: the picture query is asked
        /// with `query="url"` and answers a signed, expiring address (`ProfilePictures`), so
        /// the bytes are fetched at once and it is the bytes the caller keeps.
        ///
        /// Null when there is no picture, which is an answer and not a failure - a contact
        /// with no photo is the ordinary case.
        /// </summary>
        public async Task<AvatarAnswer> RequestAvatarAsync(string jid, bool preview)
        {
            var picture = new AvatarAnswer();
            if (string.IsNullOrEmpty(jid)) return picture;

            // Asked before the socket, not after: the chat list's pass runs from the first
            // start, before the login, and one throw per row is what this answer replaces.
            if (!IsLoggedIn)
            {
                picture.NoSession = true;
                return picture;
            }

            NativeSession session = OpenSession();

            BinaryNode answer = await RequestAsync(session, ProfilePictures.Query(
                OutgoingMessage.Id(_random), jid, preview ? ProfilePictures.Preview : ProfilePictures.Image),
                true);

            if (answer.Attrs["type"] == "error")
            {
                // An answer, not a failure: this chat has no picture this account may see.
                picture.Refused = true;
                return picture;
            }

            string url = ProfilePictures.Read(answer);
            if (url == null)
            {
                Diag.Ok("native: " + jid + " has no picture");
                return picture;
            }

            picture.Jpeg = await new WinRtMediaFetcher().GetAsync(url);
            Diag.Ok("native: " + picture.Jpeg.Length + " byte(s) of picture for " + jid);

            return picture;
        }

        /// <summary>
        /// The history the server just sent, into the chat list.
        ///
        /// Two shapes, both the reference's: a bootstrap carries the compressed payload
        /// inline, and every later chunk is a download behind the media path (the notification
        /// carries a mediaKey and a directPath, and `HistoryBlob` decrypts and inflates it
        /// like any other media). A notification that names neither is named in the log - a
        /// field that is missing must not look like a history that was empty.
        ///
        /// The download is awaited, and the answer says whether it landed: the acknowledgement
        /// of the stanza is the caller's, and it goes out only on a true. It is the one thing
        /// the server takes as "this sync is delivered", and WhatsApp sends the story of a
        /// device once per link - so an ack over a blob that never downloaded is the rest of
        /// the chat list gone, with no way to ask for it again.
        /// </summary>
        private async Task<bool> ApplyHistoryAsync(HistorySyncNotification notification)
        {
            HistorySync sync;

            try
            {
                if (notification.InlinePayload != null)
                {
                    sync = HistorySync.Decode(Zlib.Inflate(notification.InlinePayload));
                }
                else if (notification.MediaKey != null && !string.IsNullOrEmpty(notification.DirectPath))
                {
                    byte[] blob = await HistoryBlob.FetchAsync(new WinRtMediaFetcher(), notification);
                    sync = HistorySync.Decode(blob);
                    Diag.Ok("native: " + blob.Length + " byte(s) of downloaded history");
                }
                else
                {
                    // Nothing to fetch: the notification names neither a payload nor a blob.
                    // It is answered (true) and not asked for again - a retry would get the
                    // same empty notification back at every login, which is a conversation
                    // with the server that repairs nothing. The line is what says so in the
                    // log instead of a list that stops at whatever was already there.
                    Diag.Ok("native: history sync type " + notification.SyncType
                        + " carries neither an inline payload nor a mediaKey/directPath");
                    return true;
                }
            }
            catch (Exception error)
            {
                // A blob that will not download, or will not open, must not end the login:
                // this runs on the socket loop every message arrives on, and the rest of the
                // sync still has a chat list to fill. The failure is named with its address,
                // so the phone's log says which blob to look at - and the answer is false, so
                // the stanza stays unacknowledged and the server offers it again.
                Diag.Failed("NativeConnection/history", error);
                return false;
            }

            ApplySync(sync);

            return true;
        }

        /// <summary>
        /// A decoded sync into the pages, in the order they expect: the calls and the statuses
        /// first (each is a list of its own), then the chat rows, then the messages those rows
        /// added, then whoever is listening. Both shapes above end here, so neither one has its
        /// own version of this.
        ///
        /// The messages come after their rows and before the burst is declared closed. They are
        /// the conversation a person opens - a sync that only filled the list is a list of chats
        /// that all open empty - and they come from `_chats.ApplyHistory`'s answer rather than
        /// from the chunk, so a re-delivered chunk hands the app nothing twice.
        /// </summary>
        private void ApplySync(HistorySync sync)
        {
            ApplyCalls(sync);
            ApplyStatuses(sync);
            PersistNames(sync);

            IList<HistoryMessage> fromTheSync = _chats.ApplyHistory(sync);
            Diag.Ok("native: history sync type " + sync.SyncType + " carried "
                + sync.Conversations.Count + " conversation(s), " + _chats.Rows.Count + " row(s) now");

            AnnounceRows();

            if (MessageArrived != null)
            {
                for (int i = 0; i < fromTheSync.Count; i++) MessageArrived(this, fromTheSync[i]);
            }

            Diag.Ok("native: " + fromTheSync.Count + " message(s) of the sync are in the chats");

            if (_chats.IsSyncComplete && HistoryFinished != null) HistoryFinished(this, sync);

            NameTheGroups();
        }

        /// <summary>
        /// Every row, announced: how a name that was learned after its row existed reaches the
        /// screen.
        ///
        /// Every row and not only the ones a chunk named: a sync that carries no conversation
        /// at all still carries names - that is exactly what the push-name chunk is (`syncType`
        /// 4, 438 names and nothing else on the account of 2026-10-22), and a loop over
        /// `sync.Conversations` told the list about none of them, so the names were learned,
        /// saved, and never drawn. `ApplyChat` reads the name it is handed, so a row announced
        /// again is a row renamed.
        /// </summary>
        private void AnnounceRows()
        {
            if (ChatArrived == null) return;

            for (int i = 0; i < _chats.Rows.Count; i++) ChatArrived(this, _chats.Rows[i]);
        }

        /// <summary>How many nameless groups one pass asks the server about.</summary>
        private const int NamelessGroups = 15;

        /// <summary>
        /// The chat list this client holds, for the refresh the app runs while it is open
        /// (`DataService.RefreshChatList`).
        ///
        /// Handed out as it is: the run starts on the UI thread and its continuations stay
        /// there, so a caller walking it from a timer is not racing a writer.
        /// </summary>
        public IList<HistoryConversation> ChatRows
        {
            get { return _chats.Rows; }
        }

        /// <summary>
        /// The groups this process has already asked about. It is a memory and not a flag, so
        /// that a group whose row appears in a later chunk is still named, while a group the
        /// server refuses (one this account left) is asked once and not at every chunk.
        /// </summary>
        private readonly HashSet<string> _groupsAsked = new HashSet<string>();

        /// <summary>
        /// Groups the sync did not name, asked of the server that knows them.
        ///
        /// Why: a group chat is called by its subject, and the subject lives on the server
        /// (`GroupDirectory`). The history carries it for most groups, but one whose row was
        /// made by a live message - or whose chunk arrived without its `name` - stays
        /// "Gruppo 120363412599616890" while the official app shows the subject; on the account
        /// of 2026-10-24 that was three rows of forty.
        ///
        /// Why it is a task of its own and not a step of the sync: the answer is an `iq`, and
        /// an `iq` is read by the same loop this sync is running on, so awaiting one here would
        /// be the loop waiting for itself (`RequestAsync`). It is bounded, and a pass with
        /// nothing to ask for costs one walk over the rows.
        /// </summary>
        private void NameTheGroups()
        {
            // No wait for a sync that has completed: the rows are read from the client's own
            // list, which is filled by the copy on the phone as well. Gating on a sync was
            // why a phone linked for days never asked: it had no sync left to complete.
            NativeSession session = _session;
            if (session == null || !session.IsOpen) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("NativeConnection/group names", NameGroupsAsync(session));
#pragma warning restore 4014
        }

        /// <summary>
        /// One metadata query per group chat that has no subject yet, and the row renamed from
        /// the answer. A group this account has left answers `error`, which is an answer and
        /// not a failure: one line, and the row keeps the name it had.
        /// </summary>
        private async Task NameGroupsAsync(NativeSession session)
        {
            // The addresses are collected before the first query: every `await` below is a
            // window in which the loop that reads the socket can add rows to this list.
            var wanted = new List<string>();
            IList<HistoryConversation> rows = _chats.Rows;
            for (int i = 0; i < rows.Count && wanted.Count < NamelessGroups; i++)
            {
                HistoryConversation row = rows[i];
                if (row == null || !GroupDirectory.IsGroup(row.Jid)) continue;
                if (!string.IsNullOrEmpty(row.Name)) continue;

                // Read, not written: a group is marked asked when the server answers (below),
                // because a query that failed is a question still open.
                lock (_groupsAsked)
                {
                    if (_groupsAsked.Contains(row.Jid)) continue;
                }

                wanted.Add(row.Jid);
            }

            if (wanted.Count == 0) return;

            int named = 0;
            for (int i = 0; i < wanted.Count; i++)
            {
                try
                {
                    BinaryNode answer = await RequestAsync(session,
                        GroupDirectory.Query(OutgoingMessage.Id(_random), wanted[i]), true);

                    // Asked and answered - a subject, or the refusal of a group this account
                    // has left, which is an answer too. A query that threw (no answer, a
                    // timeout) never reaches this line, so the next pass asks again instead
                    // of leaving the row on its fallback for ever.
                    MarkGroupAsked(wanted[i]);

                    GroupInfo group = GroupDirectory.Read(wanted[i], answer);

                    string subject = NameBook.Clean(group.Subject);
                    if (subject == null) continue;

                    HistoryConversation row = _chats.Find(wanted[i]);
                    if (row == null || row.Name == subject) continue;

                    row.Name = subject;
                    named++;

                    if (ChatArrived != null) ChatArrived(this, row);
                }
                catch (Exception error)
                {
                    Diag.Ok("native: no subject for " + wanted[i] + " (" + error.Message + ")");
                }
            }

            Diag.Ok("native: " + named + " of " + wanted.Count + " nameless group(s) took their subject");

            // One pass names at most `NamelessGroups`: the next one takes the next batch, so a
            // list of forty groups is named in a few passes instead of eight of forty for ever.
            // A pass that named nothing has nothing more to do.
            if (named > 0) NameTheGroups();
        }

        /// <summary>Written down only when the server answered: see `NameGroupsAsync`.</summary>
        private void MarkGroupAsked(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return;

            lock (_groupsAsked)
            {
                _groupsAsked.Add(jid);
            }
        }

        /// <summary>
        /// The keys this account's app state is read with, from the store into this run.
        ///
        /// They are kept with the account because the phone shares them **once**: a client that
        /// held them in memory only could read its own address book for one run and never
        /// again.
        /// </summary>
        private void RememberStoredAppStateKeys(Credentials account)
        {
            if (account == null || account.AppStateKeys == null) return;

            for (int i = 0; i < account.AppStateKeys.Count; i++) _appStateKeys.Add(account.AppStateKeys[i]);
        }

        /// <summary>
        /// The keys a device is sent when it is linked (`APP_STATE_SYNC_KEY_SHARE`), stored and
        /// synced.
        ///
        /// Why before the echo check and not after it: this stanza **is** one of this account's
        /// own - the phone writes it - and the rule that keeps a phone from showing its own
        /// messages would throw away the only copy of the keys its app state is readable with.
        /// True when the stanza carried keys, which is what the log line says.
        /// </summary>
        private bool LearnAppStateKeys(NativeSession session, IncomingMessage incoming, Credentials account)
        {
            if (incoming == null || incoming.Content == null || incoming.Content.ProtocolMessage == null) return false;

            MessageProtocol protocol;
            try
            {
                protocol = MessageProtocol.Decode(incoming.Content.ProtocolMessage);
            }
            catch (ProtocolException)
            {
                return false;
            }

            if (protocol.KeyShare == null) return false;

            IList<AppStateKey> shared = AppStateKeys.Read(protocol.KeyShare);
            int added = 0;

            for (int i = 0; i < shared.Count; i++)
            {
                AppStateKey key = shared[i];
                if (!_appStateKeys.Add(key)) continue;

                added++;
                if (account.AppStateKeys == null) break;
                account.AppStateKeys.Add(key);
            }

            if (added > 0 && account.AppStateKeys != null)
            {
                try
                {
                    _store.Save(account);
                }
                catch (Exception error)
                {
                    Diag.Failed("NativeConnection/app-state keys", error);
                }
            }

            Diag.Ok("app-state: " + shared.Count + " key(s) shared, " + added + " new, " + _appStateKeys.Count
                + " held - the primary's address book is readable");

            if (!_appStateKeys.IsEmpty)
            {
#pragma warning disable 4014
                Guarded.RunGuardedAsync("NativeConnection/app state", SyncAppStateAsync(session));
#pragma warning restore 4014
            }

            return shared.Count > 0;
        }

        /// <summary>
        /// The account's own app state, read: the names the primary saved in its address book.
        ///
        /// Why it is not awaited by the login: it is one more query on a socket that is already
        /// reading messages, and a sync that fails - or that has no key yet, the ordinary case
        /// of a the first start after a re-link - must not hold the chat list up. Every failure
        /// ends in a line, because the difference between "this account has no saved names"
        /// and "this device cannot read them" is the whole question the feature answers.
        ///
        /// Only the names are applied. The same collection carries the pins, the mutes and the
        /// archive setting, and those are this phone's own (`ChatPreferences`): a snapshot that
        /// set them would undo what a person did on this device.
        /// </summary>
        private async Task SyncAppStateAsync(NativeSession session)
        {
            if (_appStateKeys.IsEmpty)
            {
                Diag.Ok("app-state: no key has been shared with this device, so the primary's address book cannot be read");
                return;
            }

            IList<AppStateCollection> collections;
            try
            {
                BinaryNode answer = await RequestAsync(session,
                    AppStateSync.Query(OutgoingMessage.Id(_random)), true);

                if (answer == null || answer.Attrs["type"] == "error")
                {
                    Diag.Ok("app-state: the server refused the sync (" + (answer == null ? "no answer" : answer.ToString()) + ")");
                    return;
                }

                collections = AppStateSync.Read(answer);
            }
            catch (Exception error)
            {
                Diag.Failed("NativeConnection/app-state", error);
                return;
            }

            int names = 0;

            for (int i = 0; i < collections.Count; i++)
            {
                AppStateCollection collection = collections[i];
                if (collection.Snapshot == null && collection.Patches.Count == 0) continue;

                AppStateResult applied;
                try
                {
                    applied = AppStateSync.Apply(collection, _appStateKeys);
                }
                catch (ProtocolException error)
                {
                    // A collection that does not verify is not half applied: the names in it
                    // are this account's or nobody's, and a mix is worse than none.
                    Diag.Ok("app-state: " + collection.Name + " was not read: " + error.Message);
                    continue;
                }

                for (int c = 0; c < applied.Contacts.Count; c++)
                {
                    if (RememberSavedContact(applied.Contacts[c])) names++;
                }

                Diag.Ok("app-state: " + collection.Name + " at version " + applied.Version + " carried "
                    + applied.Contacts.Count + " saved contact(s)");
            }

            if (names == 0) return;

            // The rows are on screen already: the sync is a name for a chat that is there.
            AnnounceRows();
            Diag.Ok("app-state: " + names + " name(s) from the primary's address book are in the list");
        }

        /// <summary>
        /// One name from the app state, written into the book under every address the action
        /// names: the index's, the phone number's and the `@lid`'s.
        ///
        /// Three and not one because they are the same person seen from three sides, and a row
        /// keyed on whichever the server used that day has to find the name - `NameBook`
        /// resolves the pair, and writing all three means the pair is not needed to show it.
        /// </summary>
        private static bool RememberSavedContact(SavedContact contact)
        {
            if (contact == null || string.IsNullOrEmpty(contact.Name)) return false;

            bool any = false;

            if (!string.IsNullOrEmpty(contact.LidJid) && !string.IsNullOrEmpty(contact.PnJid))
            {
                NameCache.RememberMapping(contact.LidJid, contact.PnJid);
            }

            // `NameCache` answers nothing - it writes the book and, when the name is news,
            // tells the file to save itself - so what this method reports is whether there
            // was a name to write at all, and that is read off the addresses.
            if (!string.IsNullOrEmpty(contact.Address))
            {
                NameCache.RememberSavedName(contact.Address, contact.Name);
                any = true;
            }

            if (!string.IsNullOrEmpty(contact.PnJid))
            {
                NameCache.RememberSavedName(contact.PnJid, contact.Name);
                any = true;
            }

            if (!string.IsNullOrEmpty(contact.LidJid))
            {
                NameCache.RememberSavedName(contact.LidJid, contact.Name);
                any = true;
            }

            return any;
        }

        /// <summary>
        /// What a sync said about names, written down even before the rows are: the LID pairs
        /// (field 15), the address book the primary shared (field 20) and the push names
        /// (field 7).
        ///
        /// Why here and not only inside `NativeChats`: the books are one (`NameCache.Book`),
        /// but the file that survives a restart is written by this layer. A sync that named a
        /// contact and was not persisted is forgotten at the next launch, and the contact goes
        /// back to being a number until it writes.
        /// </summary>
        private static void PersistNames(HistorySync sync)
        {
            int mappings = 0;
            int saved = 0;

            for (int i = 0; i < sync.LidMappings.Count; i++)
            {
                LidMapping mapping = sync.LidMappings[i];
                if (mapping == null) continue;

                NameCache.RememberMapping(mapping.Lid, mapping.PhoneNumber);
                mappings++;
            }

            for (int i = 0; i < sync.InlineContacts.Count; i++)
            {
                InlineContact contact = sync.InlineContacts[i];
                if (contact == null) continue;

                NameCache.RememberMapping(contact.LidJid, contact.PnJid);

                string name = contact.FullName;
                if (string.IsNullOrEmpty(name)) name = contact.FirstName;
                if (string.IsNullOrEmpty(name)) continue;

                NameCache.RememberSavedName(contact.PnJid, name);
                NameCache.RememberSavedName(contact.LidJid, name);
                saved++;
            }

            for (int i = 0; i < sync.Conversations.Count; i++)
            {
                HistoryConversation conversation = sync.Conversations[i];
                if (conversation == null) continue;

                NameCache.RememberMapping(conversation.LidJid, conversation.PnJid);
            }

            for (int i = 0; i < sync.PushNames.Count; i++)
            {
                HistoryPushName push = sync.PushNames[i];
                if (push == null) continue;

                NameCache.RememberPushName(push.Jid, push.Name);
            }

            if (mappings > 0 || saved > 0 || sync.PushNames.Count > 0)
            {
                Diag.Ok("native: the sync named " + mappings + " LID pair(s), "
                    + saved + " saved contact(s), " + sync.PushNames.Count + " push name(s)");
            }
        }

        /// <summary>
        /// One live message, before it is shown: the name the stanza carries and the phone
        /// number behind a LID.
        ///
        /// Why the message and not a query: `notify` and `sender_pn`/`participant_pn` are on
        /// every stanza, and a client that ignores them has to draw a number for somebody whose
        /// name it was just told. A LID whose pair is learned here folds its row into the
        /// number's (`NativeChats.LearnMapping`) and the pages are told (`ChatMerged`).
        /// </summary>
        private void LearnFrom(IncomingMessage incoming)
        {
            if (incoming == null) return;

            string sender = string.IsNullOrEmpty(incoming.Sender) ? incoming.Chat : incoming.Sender;
            sender = _chats.Names.Canonical(JidUtils.Normalize(sender));
            if (sender == null) return;

            if (!string.IsNullOrEmpty(incoming.PhoneNumber))
            {
                string phone = JidUtils.Normalize(incoming.PhoneNumber);

                NameCache.RememberMapping(sender, phone);

                if (_chats.LearnMapping(sender, phone))
                {
                    string to = _chats.Names.Canonical(sender);
                    Diag.Ok("native: " + sender + " is " + to + ", one chat");

                    if (ChatMerged != null) ChatMerged(this, new ChatMerge(sender, to));
                }
            }

            if (!string.IsNullOrEmpty(incoming.PushName))
            {
                NameCache.RememberPushName(sender, incoming.PushName);
            }
        }

        /// <summary>
        /// The call log a sync brought, added to what is already here and told to whoever is
        /// listening.
        ///
        /// Two syncs both carry the same records - the history is re-sent in chunks and its
        /// call log with it - so the ids are what keeps a call from appearing twice. There is
        /// no ceiling on the list and there does not need to be: it is what WhatsApp sent, and
        /// WhatsApp's own call log is bounded by the phone's storage, not by this client's
        /// memory (the page's rows come from `DataService`, which is where the list a person
        /// sees lives).
        /// </summary>
        private void ApplyCalls(HistorySync sync)
        {
            // This account's own JID, because the record's creator is us on every call we
            // made (`Calls.FromHistory`).
            IList<CallLogEntryModel> arrived = Calls.FromHistory(sync, Jid);
            if (arrived.Count == 0) return;

            for (int i = 0; i < arrived.Count; i++)
            {
                bool known = false;
                for (int k = 0; k < _calls.Count; k++)
                {
                    if (_calls[k].Id == arrived[i].Id) { known = true; break; }
                }

                if (!known) _calls.Add(arrived[i]);
            }

            Diag.Ok("native: " + arrived.Count + " call(s) in the history, " + _calls.Count + " known");

            if (CallsScanned != null) CallsScanned(this, _calls);
        }

        /// <summary>
        /// The call log as it stands, told to whoever just asked for it.
        ///
        /// Why there is nothing to ask the server: the log arrived with the history (see
        /// `ApplyCalls`), and re-asking would be a query WhatsApp has no answer for. A page
        /// that opens before the first sync therefore shows an empty log and fills when the
        /// sync arrives - which is the same moment the chat list fills.
        /// </summary>
        public Task ScanCallsAsync()
        {
            if (CallsScanned != null) CallsScanned(this, _calls);

            Diag.Ok("native: the call log has " + _calls.Count + " entry(ies)");

            return Task.FromResult(0);
        }

        /// <summary>
        /// Refuses a call: the `call` node with a `reject` child, on the one socket.
        /// </summary>
        public async Task RejectCallAsync(string callId, string jid)
        {
            NativeSession session = OpenSession();

            await session.SendAsync(Calls.Reject(callId, jid));
            Diag.Ok("native: refused the call " + callId + " from " + jid);
        }

        /// <summary>
        /// One status off the wire: into the feed, and its photo fetched if it carries one.
        ///
        /// The row goes out before the bytes, exactly as a media message's does, so the page
        /// draws the update and fills in the picture when it arrives. A status whose bytes
        /// cannot be fetched - no key, the blob taken down, the connection died - is still an
        /// update on screen, and a tap asks again (the media info travels with the row).
        /// </summary>
        private async Task AnnounceStatusAsync(IncomingMessage incoming)
        {
            var post = new StatusPost();
            // The author, without their device: it is a name the feed shows.
            post.Author = JidUtils.Normalize(string.IsNullOrEmpty(incoming.Sender)
                ? incoming.From : incoming.Sender);
            post.Id = incoming.Id;
            post.Timestamp = incoming.Timestamp;
            post.Text = incoming.Text;
            if (incoming.Content != null) post.Encoded = incoming.Content.Encode();

            if (!KnownStatus(post.Id)) _statuses.Insert(0, post);

            Diag.Ok("native: a status from " + post.Author + " (" + post.Id + ")");

            if (StatusArrived != null) StatusArrived(this, post);

            if (incoming.Content == null) return;

            MediaInfo info = MediaMessages.Of(incoming.Content);
            if (info == null || !info.IsFetchable) return;

            byte[] bytes;
            try
            {
                bytes = await MediaDownload.FetchAsync(new WinRtMediaFetcher(), info.MediaKey,
                    info.Type, info.DirectPath, info.Url, info.FileSha256, info.FileEncSha256, info.FileLength);
            }
            catch (Exception error)
            {
                // The update stays on screen without its picture. A tap asks again, and the
                // log says which one failed rather than the feed going quiet.
                Diag.Failed("NativeConnection/status " + post.Id, error);
                return;
            }

            AnnounceStatusMedia(post.Author, post.Id, info, bytes);
        }

        /// <summary>The bytes of a status, told to whoever draws it.</summary>
        private void AnnounceStatusMedia(string author, string id, MediaInfo info, byte[] bytes)
        {
            if (StatusMediaArrived == null) return;

            var arrival = new MediaArrival();
            arrival.Jid = author;
            arrival.Id = id;
            arrival.Info = info;
            arrival.Bytes = bytes;

            StatusMediaArrived(this, arrival);
        }

        /// <summary>
        /// The statuses a sync brought, each one only the first time it is seen.
        ///
        /// The history is re-sent in chunks with its status conversation in every one of
        /// them, so the id is what keeps an update from being drawn two or three times.
        /// </summary>
        private void ApplyStatuses(HistorySync sync)
        {
            IList<StatusPost> arrived = StatusFeed.FromHistory(sync);
            if (arrived.Count == 0) return;

            int fresh = 0;
            for (int i = 0; i < arrived.Count; i++)
            {
                if (KnownStatus(arrived[i].Id)) continue;

                _statuses.Add(arrived[i]);
                fresh++;

                if (StatusArrived != null) StatusArrived(this, arrived[i]);
            }

            Diag.Ok("native: " + arrived.Count + " status(es) in the history, " + fresh + " new");
        }

        private bool KnownStatus(string id)
        {
            if (string.IsNullOrEmpty(id)) return true;

            for (int i = 0; i < _statuses.Count; i++)
            {
                if (_statuses[i].Id == id) return true;
            }

            return false;
        }

        /// <summary>The statuses that have arrived, newest first, for a page that just opened.</summary>
        public IList<StatusPost> StatusPosts
        {
            get { return _statuses; }
        }

        /// <summary>
        /// The feed as it stands, told to whoever just asked for it. Like the call log there
        /// is nothing to ask the server: statuses arrive with the history and with the live
        /// messages, and a page that opens before either shows an empty feed until one does.
        /// </summary>
        public Task ScanStatusesAsync()
        {
            if (StatusArrived != null)
            {
                for (int i = 0; i < _statuses.Count; i++) StatusArrived(this, _statuses[i]);
            }

            Diag.Ok("native: the feed has " + _statuses.Count + " status(es)");

            return Task.FromResult(0);
        }

        /// <summary>
        /// Watches a status: the `read` receipt to the broadcast list that names it, which is
        /// what fills the author's ring (`StatusFeed.Viewed`).
        /// </summary>
        public async Task ViewStatusAsync(string author, string id)
        {
            NativeSession session = OpenSession();

            await session.SendAsync(StatusFeed.Viewed(author, id));
            Diag.Ok("native: watched the status " + id + " by " + author);
        }

        /// <summary>
        /// Makes the signed pre-key in the store the account's, and says so when it had to.
        ///
        /// Why it is not left to the upload: the upload only runs when the one-time pre-keys are
        /// running out, so a store that still has plenty would never be looked at - and a store
        /// left from a previous pairing holds this account's own signed pre-key id over the
        /// previous identity's private key. The responder path compares the id a message names
        /// and nothing else, so that pair agrees on paper and half the agreement is the wrong
        /// key: the sender's root is not this phone's root, and every message fails its MAC
        /// (the phone of 2026-10-18, message after message, with no line saying why).
        /// </summary>
        private async Task AdoptSignedPreKeyAsync(Credentials account)
        {
            try
            {
                PreKeyStore store = await PreKeysAsync();
                if (!PreKeyUpload.AdoptSignedPreKey(account, store)) return;

                Diag.Ok("native: the signed pre-key in the store was not this account's "
                    + "(same id, another key), so it was replaced - sessions started against the "
                    + "published key were failing every MAC");
            }
            catch (Exception error)
            {
                Diag.Failed("NativeConnection/signed-pre-key", error);
            }
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

                // The ids are taken back if the write fails: they were published into the
                // store before the node could be sent, and counting them as keys the server
                // has would keep `Remaining` above the floor for ever - the phone would stop
                // refilling while the server's supply ran down.
                var published = new List<uint>();
                BinaryNode upload = PreKeyUpload.Node(account, store, _random, PreKeyUpload.RefillCount, published);

                try
                {
                    await session.SendAsync(upload);
                }
                catch (Exception sendFailure)
                {
                    for (int i = 0; i < published.Count; i++) store.Unpublish(published[i]);

                    Diag.Failed("NativeConnection/prekeys-send", sendFailure);
                    return;
                }
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

        /// <summary>
        /// The storage, opened once - and opened **once**, which is the part that needs more
        /// than a null check: the first call leaves an `await` in the middle of the open, and
        /// a second caller that arrives meanwhile would open a second copy over the same
        /// folder. Two copies read every blob at their own open, so a session one writes is a
        /// session the other never sees, and the next message starts a new ratchet for a peer
        /// that already has one. The task is kept instead of the result so both callers await
        /// the same open.
        /// </summary>
        private async Task<ISessionStorage> StorageAsync()
        {
            if (_storage != null) return _storage;

            Task<ISessionStorage> opening = _storageOpening;
            if (opening == null)
            {
                opening = OpenStorageAsync();
                _storageOpening = opening;
            }

            return await opening;
        }

        /// <summary>One open, shared by every caller; a failed open is retried by the next.</summary>
        private async Task<ISessionStorage> OpenStorageAsync()
        {
            try
            {
                ISessionStorage opened = await FileSessionStorage.OpenAsync();
                _storage = opened;
                return opened;
            }
            finally
            {
                _storageOpening = null;
            }
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
                Diag.Step("native: keepalive waiting");
                await Task.Delay(TimeSpan.FromSeconds(SessionKeepAlive.PingSeconds));

                if (!session.IsOpen) return;
                if (!ConnectionPolicy.ConnectionIsStale(_lastNode, DateTime.UtcNow)) continue;

                Diag.Step("native: sending a ping");
                DateTime sent = DateTime.UtcNow;
                await session.PingAsync();

                // And then wait for something to come back. Sending into a socket the
                // other end has already dropped works: the bytes go out and nothing ever
                // answers, which is why "the ping was sent" was never the same as "the
                // connection is alive" and why this loop could sit on a dead connection
                // for hours while the app showed nothing new.
                while (session.IsOpen
                    && _lastNode <= sent
                    && !ConnectionPolicy.PingUnanswered(sent, _lastNode, DateTime.UtcNow))
                {
                    await Task.Delay(1000);
                }

                if (session.IsOpen && ConnectionPolicy.PingUnanswered(sent, _lastNode, DateTime.UtcNow))
                {
                    // Closing is what ends the read the other task is waiting in; the loop
                    // above takes that as the connection to be made again.
                    Diag.Ok("native: the ping went unanswered for " + ConnectionPolicy.AnswerSeconds
                        + " s - the connection is gone");
                    Status = "the server stopped answering - dialling again";
                    session.Close();
                    return;
                }
            }
        }
    }

    /// <summary>
    /// A media message's bytes, with the message they belong to.
    ///
    /// The bytes and not a decoded image: what to do with a voice note, a video and a PDF is
    /// three different things, and the app is where those three are known.
    /// </summary>
    public sealed class MediaArrival
    {
        /// <summary>The chat it arrived in.</summary>
        public string Jid;

        /// <summary>The message it belongs to, which is what the bubble is found by.</summary>
        public string Id;

        /// <summary>What the media says about itself: type, MIME type, file name, seconds.</summary>
        public MediaInfo Info;

        /// <summary>The decrypted file.</summary>
        public byte[] Bytes;
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

        /// <summary>
        /// True when this is the server's answer for a message this phone sent, and not a
        /// receipt about a peer's. An ack names no chat (it is about our own stanza), so the
        /// handler finds the bubble by the id alone.
        /// </summary>
        public bool IsAck;

        /// <summary>True when that answer is a refusal: the server would not take it.</summary>
        public bool IsRefused;
    }

    /// <summary>
    /// A message somebody took back: the chat it happened in and the id of the message.
    ///
    /// The id is the whole of it - a revoke carries no words, no media and no hint of who
    /// wrote the message it names - which is why the row is found by the id inside the chat
    /// and not by anything else.
    /// </summary>
    public sealed class MessageRevocation
    {
        /// <summary>The chat the revoked message belongs to.</summary>
        public string Jid;

        /// <summary>The message that was taken back.</summary>
        public string Id;
    }
}
