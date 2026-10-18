using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The login a device that already has an account runs.
    ///
    /// Why it exists: pairing ends with an account and nothing else, and the account is
    /// not online until the device comes back with it. The reference
    /// (`whatsmeow/pair.go`, `handlePairSuccess`) is explicit - `expectDisconnect()`
    /// right after the signature, and then the client connects **again** and sends the
    /// `login` payload, which is what makes the companion a linked device rather than a
    /// half-finished handshake. The device run of 2026-10-10 is what a missing login
    /// looks like on a phone: `diag.log` ends at
    /// `paired as 393892672185:2@s.whatsapp.net` with no line after it, and the phone
    /// that scanned the code answers `Impossibile collegare il dispositivo`.
    ///
    /// It is one step and not a session: <see cref="LoginAsync"/> opens the connection
    /// and answers the server's first word, `IsOpen` says whether it is still worth
    /// reading, and the loop that keeps reading belongs to the caller
    /// (`Services/NativeConnection.cs`), because it is the caller that decides what an
    /// arriving node means.
    /// </summary>
    public sealed class NativeSession
    {
        /// <summary>
        /// How many nodes the server may say before it answers the login and still be read.
        /// A login is answered by `success`; the bootstrap travels on `ib`, and some of those
        /// frames arrive before the answer. Twelve is a ceiling, not an expectation: a server
        /// that never says `success` must end as a named failure, not as a reader that waits.
        /// </summary>
        public const int LoginFrameLimit = 12;

        /// <summary>
        /// The nodes that arrived before the login was answered, in the order they came.
        ///
        /// Why they are kept instead of dropped: `ib` carries `offline_preview` (the question
        /// "how much backlog do you want?"), `offline`, and the edge routing - and the loop
        /// that answers them starts only after this class has returned. Dropping them is how a
        /// login ends with a healthy socket and no backlog, which on the phone looks exactly
        /// like an app that logs in and then shows nothing.
        /// </summary>
        private readonly Queue<BinaryNode> _beforeLogin = new Queue<BinaryNode>();

        private readonly WhatsAppSocket _socket;
        private readonly ILog _log;
        private readonly IRandomSource _random;

        public NativeSession(ITransport transport, ILog log, IRandomSource random)
        {
            if (transport == null) throw new ProtocolException("a session needs a transport");
            if (random == null) throw new ProtocolException("a session needs a source of randomness");

            _socket = new WhatsAppSocket(transport, log, random);
            _log = log == null ? new SilentLog() : log;
            _random = random;
        }

        /// <summary>Whether the connection is up and worth reading. False once it is over.</summary>
        public bool IsOpen { get; private set; }

        /// <summary>The account this session opened, or null before a login.</summary>
        public string Jid { get; private set; }

        /// <summary>
        /// Opens the account: connect, handshake, and read the server's first word. The
        /// payload is the login one and not the pairing one, because `Credentials.IsPaired`
        /// is what the socket switches on - so credentials with no JID are refused here,
        /// before the transport is touched, rather than offering the server a device that
        /// is asking to be paired all over again.
        /// </summary>
        public async Task<BinaryNode> LoginAsync(Credentials account, string url)
        {
            if (account == null) throw new ProtocolException("a login needs the account it opens");
            if (!account.IsPaired) throw new ProtocolException("a login needs a paired account, and these credentials name no JID");
            if (IsOpen) throw new ProtocolException("this session is already open");

            await _socket.HandshakeAsync(url, account);

            IsOpen = true;
            Jid = account.Jid;

            // The answer is `success`, and it is not assumed to be the first node: the
            // bootstrap can speak first (see `_beforeLogin`). Everything that is not the
            // answer is kept for the reader the caller starts, so no frame is lost in the
            // one moment where nothing is reading yet.
            for (int read = 0; read < LoginFrameLimit; read++)
            {
                BinaryNode answer = await _socket.ReadNodeAsync();

                string refusal = Refusal(answer);
                if (refusal != null)
                {
                    IsOpen = false;
                    throw new SessionRefusedException("the server refused the login: " + refusal);
                }

                if (answer != null && answer.Tag == "success")
                {
                    _log.Info("whatsapp: logged in as " + account.Jid);
                    return answer;
                }

                if (answer != null)
                {
                    _beforeLogin.Enqueue(answer);
                    _log.Info("whatsapp: <" + answer.Tag + "> arrived before the login was answered, and is read after it");
                }
            }

            IsOpen = false;
            throw new ProtocolException("the server did not answer the login in " + LoginFrameLimit + " node(s)");
        }

        /// <summary>
        /// Reads one node the server sent. The socket writes the line that names it.
        ///
        /// What arrived before `success` is handed back first, in the order it came: the
        /// bootstrap's `ib` is a frame like any other to the loop that reads this, and a
        /// queue is what lets the login read ahead without stealing it.
        /// </summary>
        public Task<BinaryNode> ReadNodeAsync()
        {
            if (!IsOpen) throw new ProtocolException("there is no open session to read from");
            if (_beforeLogin.Count > 0) return Answered(_beforeLogin.Dequeue());

            return _socket.ReadNodeAsync();
        }

        public Task SendAsync(BinaryNode node)
        {
            if (!IsOpen) throw new ProtocolException("there is no open session to send on");
            return _socket.SendAsync(node);
        }

        /// <summary>
        /// The keep-alive: an `iq` the server answers, which is the only thing that tells a
        /// phone a connection nothing is moving on is still there.
        /// </summary>
        public Task PingAsync()
        {
            return SendAsync(SessionKeepAlive.Ping(_random));
        }

        /// <summary>
        /// The answer to the server's liveness probe, whichever shape it takes (see
        /// `SessionKeepAlive.PingAnswer`): an `iq` result when the probe carries an id, and
        /// the keep-alive query when it does not, which is what WhatsApp's own probe looks
        /// like.
        /// </summary>
        public Task AnswerProbeAsync(BinaryNode probe)
        {
            return SendAsync(SessionKeepAlive.PingAnswer(probe, _random));
        }

        /// <summary>
        /// Says this phone is here. Deliberately tolerant of a name that is not known yet:
        /// `SessionKeepAlive.Presence` refuses to build the node without one, because a
        /// phone that has not said who it is cannot go available - and after a pairing on a
        /// personal account the name is simply not in the `pair-success` (only the business
        /// blob carries one). Saying so in the log is better than throwing away the session
        /// over a node Task 6 will fill from the server's own answer.
        /// </summary>
        public Task GoAvailableAsync(string pushName)
        {
            if (string.IsNullOrEmpty(pushName))
            {
                _log.Info("whatsapp: no push name yet, so presence is not sent");
                return Done();
            }

            return SendAsync(SessionKeepAlive.Presence(pushName, SessionKeepAlive.Available));
        }

        public void Close()
        {
            IsOpen = false;
            _socket.Close();
        }

        /// <summary>
        /// The server saying no, as a rule instead of as a shape spread over the loop.
        ///
        /// Both refusals WhatsApp sends are here: `failure` carries `reason` (401 for an
        /// account that is gone, 405 for a client older than the floor - the one the first
        /// live pairing met) and `stream:error` carries `code`. A node that will not name
        /// its reason is still a refusal: the whole node is the description rather than
        /// nothing at all.
        /// </summary>
        public static string Refusal(BinaryNode node)
        {
            if (node == null) return null;
            if (node.Tag == "failure") return NamedReason(node, "reason");
            if (node.Tag == "stream:error") return NamedReason(node, "code");

            return null;
        }

        private static string NamedReason(BinaryNode node, string attribute)
        {
            string value = node.Attrs[attribute];
            if (string.IsNullOrEmpty(value)) return node.ToString();

            return value;
        }

        private static Task Done()
        {
            var completion = new TaskCompletionSource<bool>();
            completion.SetResult(true);
            return completion.Task;
        }

        /// <summary>A node that was already read, as the task the caller awaits.</summary>
        private static Task<BinaryNode> Answered(BinaryNode node)
        {
            var completion = new TaskCompletionSource<BinaryNode>();
            completion.SetResult(node);
            return completion.Task;
        }
    }

    /// <summary>
    /// WhatsApp has taken this account away: the phone unlinked the device, or the keys
    /// were reset. It is a distinct type because the answer to it is not a retry - the
    /// stored account is dead, and the app has to clear it and show the QR again
    /// (`Services/NativeConnection.cs`), which is exactly what `SessionState.Expired`
    /// exists for.
    /// </summary>
    public sealed class SessionRefusedException : ProtocolException
    {
        public SessionRefusedException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The server refuses this **client**, not the account: `downgrade_webclient` means the
    /// version this app announces is older than the multi-device floor, and the reference
    /// ends the connection on it (see `ClientState.IsClientRefusal`).
    ///
    /// It is its own type so the notification path can survive a malformed `ib` while still
    /// letting this one end the connection: a refusal retried as if it were a network problem
    /// is a phone that reconnects forever and never says why.
    /// </summary>
    public sealed class ClientRefusedException : ProtocolException
    {
        public ClientRefusedException(string message) : base(message)
        {
        }
    }
}
