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
            // its device, because this one is shown to a person.
            message.Sender = JidUtils.Normalize(incoming.Sender);

            if (!_chats.AddIncoming(message)) return;

            Diag.Ok("native: " + incoming.Id + " from " + incoming.From);

            HistoryConversation row = _chats.Find(message.Jid);
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

            NativeSession session = OpenSession();

            if (OutgoingMessage.IsGroup(jid))
            {
                return await SendGroupAsync(session, jid, Message.Text(body), OutgoingMessage.TextType, null, body);
            }

            IList<KeyValuePair<string, SessionRecord>> devices = await DevicesAsync(session, jid);

            string id = OutgoingMessage.Id(_random);
            uint timestamp = NowSeconds();

            await session.SendAsync(OutgoingMessage.Node(jid, id, body, devices, timestamp));

            _chats.AddOutgoing(jid, id, body, timestamp);
            Diag.Ok("native: " + id + " to " + jid + " over " + devices.Count + " device(s)");

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

            await session.SendAsync(OutgoingMessage.Node(jid, id, payload, OutgoingMessage.MediaType,
                mediaWord, devices, timestamp));

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

            await session.SendAsync(OutgoingMessage.Group(jid, id, payload, type, mediaType, record,
                group.AddressingMode, distributions, timestamp));

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

            return deviceJid.StartsWith("99:", StringComparison.OrdinalIgnoreCase);
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

            return devices;
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
                Diag.Ok("native: message " + incoming.Id + " carries nothing this client reads yet");
                return;
            }

            var message = new HistoryMessage();
            message.Jid = incoming.Chat;
            message.Id = incoming.Id;
            message.Timestamp = incoming.Timestamp;
            message.Sender = JidUtils.Normalize(incoming.Sender);
            message.Encoded = incoming.Content.Encode();

            if (_chats.AddIncoming(message))
            {
                Diag.Ok("native: " + info.Type + " " + incoming.Id + " from " + incoming.From);

                HistoryConversation row = _chats.Find(message.Jid);
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
        public async Task<byte[]> RequestAvatarAsync(string jid, bool preview)
        {
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a picture needs somebody to ask about");

            NativeSession session = OpenSession();

            BinaryNode answer = await RequestAsync(session, ProfilePictures.Query(
                OutgoingMessage.Id(_random), jid, preview ? ProfilePictures.Preview : ProfilePictures.Image));

            string url = ProfilePictures.Read(answer);
            if (url == null)
            {
                Diag.Ok("native: " + jid + " has no picture");
                return null;
            }

            byte[] jpeg = await new WinRtMediaFetcher().GetAsync(url);
            Diag.Ok("native: " + jpeg.Length + " byte(s) of picture for " + jid);

            return jpeg;
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

            ApplyCalls(sync);
            ApplyStatuses(sync);

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
    }
}
