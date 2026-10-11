using System;
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

            BinaryNode answer = await ReadNodeAsync();

            string refusal = Refusal(answer);
            if (refusal != null)
            {
                IsOpen = false;
                throw new SessionRefusedException("the server refused the login: " + refusal);
            }

            _log.Info("whatsapp: logged in as " + account.Jid);
            return answer;
        }

        /// <summary>Reads one node the server sent. The socket writes the line that names it.</summary>
        public Task<BinaryNode> ReadNodeAsync()
        {
            if (!IsOpen) throw new ProtocolException("there is no open session to read from");
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
}
