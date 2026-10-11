using System;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.Transport;
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

        private NativeSession _session;
        private PreKeyStore _preKeys;
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

                    await ReadAsync(session);
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
        private async Task ReadAsync(NativeSession session)
        {
            while (true)
            {
                BinaryNode node = await session.ReadNodeAsync();
                _lastNode = DateTime.UtcNow;

                if (SessionKeepAlive.IsPing(node))
                {
                    Diag.Ok("native: the server asked whether this connection is alive");
                    await session.AnswerProbeAsync(node);
                    continue;
                }

                string ended = NativeSession.Refusal(node);
                if (ended == null) continue;

                if (ConnectionPolicy.AccountIsGone(node))
                {
                    throw new SessionRefusedException("WhatsApp ended this account: " + ended);
                }

                throw new ProtocolException("the connection ended: " + ended);
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

            ISessionStorage storage = await FileSessionStorage.OpenAsync();
            _preKeys = new PreKeyStore(storage);

            return _preKeys;
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
}
