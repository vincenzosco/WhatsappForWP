using System;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
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

        private NativeSession _session;
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
        /// One login and the reading that follows it. Every failure is named and the session
        /// is closed on the way out, so the next `Start` begins from nothing rather than
        /// from half a connection.
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

            var session = new NativeSession(new WebSocketTransport(), new ProtocolLog(), new DeviceRandomSource());
            _session = session;
            Status = "logging in as " + account.Jid;

            try
            {
                await session.LoginAsync(account, WhatsAppSocket.DefaultUrl);

                _lastNode = DateTime.UtcNow;
                Status = "logged in as " + account.Jid;
                Diag.Ok("native: logged in as " + account.Jid);

                await session.GoAvailableAsync(account.PushName);

#pragma warning disable 4014
                Guarded.RunGuardedAsync("NativeConnection/keepalive", KeepAliveAsync(session));
#pragma warning restore 4014

                while (true)
                {
                    BinaryNode node = await session.ReadNodeAsync();
                    _lastNode = DateTime.UtcNow;

                    // The server's liveness probe and the end of the session arrive on the
                    // same channel, and the probe has to be answered first: an unanswered
                    // one is the server closing a connection it thinks is dead (the pairing
                    // of 2026-10-10 was lost exactly this way).
                    if (SessionKeepAlive.IsPing(node))
                    {
                        Diag.Ok("native: the server asked whether this connection is alive");
                        await session.AnswerProbeAsync(node);
                        continue;
                    }

                    string ended = NativeSession.Refusal(node);
                    if (ended != null)
                    {
                        throw new SessionRefusedException("the server ended the session: " + ended);
                    }
                }
            }
            catch (SessionRefusedException refused)
            {
                // WhatsApp unlinked this device, or reset the keys: the account is dead, and
                // keeping it would put the app on a chat list that can never fill. Clearing
                // it and flagging the run is what sends `StartDecision` back to the QR.
                Diag.Ok("native: " + refused.Message);
                Status = refused.Message;

                SessionState.Expired = true;
                _store.Clear();
            }
            catch (Exception error)
            {
                Diag.Failed("NativeConnection/login", error);
                Status = Diag.Describe(error);
            }
            finally
            {
                session.Close();

                if (ReferenceEquals(_session, session)) _session = null;
                _running = false;

                Diag.Ok("native: the session is over (" + Status + ")");
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
                await Task.Delay(TimeSpan.FromSeconds(SessionKeepAlive.PingSeconds));

                if (!session.IsOpen) return;
                if (!SessionKeepAlive.IsStale(_lastNode, DateTime.UtcNow)) continue;

                await session.PingAsync();
            }
        }
    }
}
