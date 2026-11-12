using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// One attempt at linking this phone to an account, from the first byte to the account itself.
    ///
    /// The job is split in two methods because something has to appear on screen in between:
    ///
    /// - `StartAsync` connects, introduces this phone, and returns the strings that become the
    ///   QR codes;
    /// - `WaitForPairAsync` waits for the other phone to scan one of them and returns the
    ///   account, or null when the codes ran out.
    ///
    /// Two safety ideas run through the whole file:
    ///
    /// - the reply from the server is checked before it is trusted (an HMAC with a secret only
    ///   this phone has, and the account's own signature over this phone's key);
    /// - an unread task is never left behind, because a failure nobody reads becomes a crash
    ///   report about something that was actually an ordinary timeout.
    /// </summary>
    public sealed class PairingSession
    {
        /// <summary>The server that owns accounts and pairings.</summary>
        public const string ServerJid = "s.whatsapp.net";

        // The connection, the identity this attempt uses, where to write log lines, and the
        // source of random bytes.
        private readonly WhatsAppSocket _socket;
        private readonly Credentials _credentials;
        private readonly ILog _log;
        private readonly IRandomSource _random;

        /// <summary>When a node last arrived: what the keep-alive reads to know a ping is due.</summary>
        private DateTime _lastNode;

        /// <summary>The keep-alive, and the flag that ends it (see <see cref="KeepAliveAsync"/>).</summary>
        private Task _keepAlive;
        private bool _closed;

        public PairingSession(ITransport transport, Credentials credentials, ILog log, IRandomSource random)
        {
            // Both are required: without an identity there is nothing to pair, and without
            // randomness the keys would not be safe.
            if (credentials == null) throw new ProtocolException("a pairing session needs credentials to pair");
            if (random == null) throw new ProtocolException("a pairing session needs a source of randomness");

            _credentials = credentials;
            _socket = new WhatsAppSocket(transport, log, random);
            _log = log == null ? new SilentLog() : log;
            _random = random;
        }

        /// <summary>The credentials this session is trying to get paired, filled in as the server answers.</summary>
        public Credentials Credentials
        {
            get { return _credentials; }
        }

        /// <summary>
        /// Connects, performs the handshake, and waits for the server to ask for a pairing.
        /// Returns one string per QR code, in the order the reference client shows them.
        ///
        /// The request is acknowledged before the codes are handed back: the server waits for
        /// that acknowledgement, so a code shown without it would be a code the server had
        /// already stopped waiting for.
        /// </summary>
        public async Task<IList<string>> StartAsync()
        {
            _log.Info("pair: starting secure WebSocket and Noise handshake");

            // The handshake sets up encryption and proves the server's identity.
            await _socket.HandshakeAsync(WhatsAppSocket.DefaultUrl, _credentials);
            _log.Info("pair: Noise handshake completed; waiting for pair-device");

            // From here the connection may stay quiet for minutes while the user fetches the
            // other phone, so something has to keep it alive.
            StartKeepAlive();

            // Read nodes until the pairing request arrives.
            while (true)
            {
                BinaryNode node = await ReadAsync();

                // The server's own liveness probe arrives here too, and it has to be answered
                // before anything else: an unanswered probe is the server hanging up.
                if (SessionKeepAlive.IsPing(node))
                {
                    await AnswerPingAsync(node);
                    continue;
                }

                BinaryNode pairDevice = node.Child("pair-device");
                if (pairDevice == null)
                {
                    // Anything else on the way in is reported and stepped over: the pairing
                    // request is what this method is waiting for.
                    _log.Info("whatsapp: <" + node.Tag + "> arrived before the pairing request");
                    continue;
                }

                // Tell the server we received the request, then turn its refs into QR strings.
                await AcknowledgeAsync(node);
                IList<string> codes = CodesFor(pairDevice, _credentials);

                _log.Info("whatsapp: the server asked for a pairing (" + codes.Count + " code(s))");
                return codes;
            }
        }

        /// <summary>
        /// Waits for the server to accept a scan and returns the account.
        ///
        /// Returns null when the time runs out, and the session is over when it does: the codes
        /// have expired by then, so the caller must start again with a new identity.
        /// </summary>
        public async Task<Credentials> WaitForPairAsync(TimeSpan timeout)
        {
            // A deadline instead of a timer: each loop asks how much time is left.
            DateTime deadline = DateTime.UtcNow.Add(timeout);

            while (true)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    _log.Info("whatsapp: the pairing did not happen in time");
                    Close();
                    return null;
                }

                Task<BinaryNode> next = _socket.ReadNodeAsync();

                // A read that never answers is what a pairing nobody scanned looks like, so the
                // wait is bounded here too: wait for the read and for the remaining time, and
                // whichever finishes first decides.
                if (await Task.WhenAny(next, Task.Delay(remaining)) != next)
                {
                    _log.Info("whatsapp: the pairing timed out");

                    // The read that lost the race is still pending, and `Close` will fault it.
                    // Its failure is taken here so it is not reported as an unhandled exception.
                    Observe(next);

                    Close();
                    return null;
                }

                BinaryNode node;
                try
                {
                    node = await next;
                }
                catch (Exception error)
                {
                    // A transport that gives up and a server that drops the connection both look
                    // like this, and both mean the same here: no pairing happened.
                    _log.Info("whatsapp: the connection ended before the pairing: " + error.Message);
                    Close();
                    return null;
                }

                _lastNode = DateTime.UtcNow;

                if (SessionKeepAlive.IsPing(node))
                {
                    await AnswerPingAsync(node);
                    continue;
                }

                BinaryNode pairSuccess = node.Child("pair-success");
                if (pairSuccess != null) return await AcceptAsync(node, pairSuccess);
            }
        }

        /// <summary>Ends the connection on purpose. The keep-alive reads `_closed` and stops.</summary>
        public void Close()
        {
            _closed = true;
            _socket.Close();
        }

        /// <summary>
        /// One node, and the clock the keep-alive reads. Every read goes through here, so
        /// "nothing has arrived for a while" is a fact this class records itself.
        /// </summary>
        private async Task<BinaryNode> ReadAsync()
        {
            BinaryNode node = await _socket.ReadNodeAsync();
            _lastNode = DateTime.UtcNow;
            return node;
        }

        /// <summary>
        /// The answer to the server's probe, in whichever of the two shapes it takes (see
        /// `SessionKeepAlive.PingAnswer`). This is a frame and never an exception: a probe that
        /// cannot be answered means the connection is ending, and throwing here would abort a
        /// pairing that was working.
        /// </summary>
        private Task AnswerPingAsync(BinaryNode ping)
        {
            _log.Info("whatsapp: the server asked whether this connection is alive");
            return _socket.SendAsync(SessionKeepAlive.PingAnswer(ping, _random));
        }

        private void StartKeepAlive()
        {
            _lastNode = DateTime.UtcNow;
            _keepAlive = KeepAliveAsync();
        }

        /// <summary>
        /// Sends a ping after a quiet period, so the server keeps the connection open.
        ///
        /// It only sends, never reads: the two loops above are the only reader this socket may
        /// have, and a second reader would take frames out of order. Its own failures are
        /// caught here, because the reader is the one that reports the connection ending.
        /// </summary>
        private async Task KeepAliveAsync()
        {
            try
            {
                while (!_closed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(SessionKeepAlive.PingSeconds));

                    if (_closed) return;
                    if (!SessionKeepAlive.IsStale(_lastNode, DateTime.UtcNow)) continue;

                    await _socket.SendAsync(SessionKeepAlive.Ping(_random));
                }
            }
            catch (Exception error)
            {
                _log.Info("whatsapp: the keep-alive stopped: " + error.Message);
            }
        }

        /// <summary>
        /// Takes the fault of a task nobody is going to await. Without this, a read abandoned by
        /// a timeout would fault with no observer and be reported as an unhandled failure,
        /// instead of being the ordinary timeout it already was.
        /// </summary>
        private static void Observe(Task task)
        {
            task.ContinueWith(
                delegate(Task finished) { AggregateException ignored = finished.Exception; },
                TaskContinuationOptions.ExecuteSynchronously);
        }

        /// <summary>The acknowledgement the server waits for, with no content of our own.</summary>
        private Task AcknowledgeAsync(BinaryNode request)
        {
            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", request.Attrs["from"]);
            iq.Attrs.Add("id", request.Attrs["id"]);
            iq.Attrs.Add("type", "result");

            return _socket.SendAsync(iq);
        }

        /// <summary>
        /// The codes, one string per `&lt;ref&gt;` the server sent. A node with no ref is
        /// refused rather than shown as an empty QR: there is nothing to scan, and the page
        /// could not tell that apart from a code that is simply wrong.
        ///
        /// It is a function of a node rather than of a connection, so a test can pin the shape
        /// the server sends.
        /// </summary>
        public static IList<string> CodesFor(BinaryNode pairDevice, Credentials credentials)
        {
            if (pairDevice == null) throw new ProtocolException("there is no pairing request to read");

            var codes = new List<string>();
            IList<BinaryNode> children = pairDevice.Children();

            if (children != null)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    BinaryNode child = children[i];
                    if (child == null || child.Tag != "ref") continue;

                    byte[] content = child.Bytes();
                    if (content == null || content.Length == 0) continue;

                    // A ref plus this phone's keys become the text the QR encodes.
                    codes.Add(PairingQr.ForRefs(content, credentials));
                }
            }

            if (codes.Count == 0) throw new ProtocolException("the pairing request carries no ref");
            return codes;
        }

        /// <summary>
        /// The account: check the reply, sign this device's half, tell the server, and hand back
        /// the credentials with the account in them.
        ///
        /// The order matters. The HMAC proves the reply was built for this phone's secret; the
        /// account signature proves the details - including this phone's identity key - were
        /// signed by the account. Only then is there anything worth signing back.
        /// </summary>
        private async Task<Credentials> AcceptAsync(BinaryNode request, BinaryNode pairSuccess)
        {
            BinaryNode deviceIdentityNode = pairSuccess.Child("device-identity");
            if (deviceIdentityNode == null || deviceIdentityNode.Bytes() == null)
            {
                throw new ProtocolException("the pairing reply carries no device identity");
            }

            AdvSignedDeviceIdentityContainer container =
                AdvSignedDeviceIdentityContainer.Decode(deviceIdentityNode.Bytes());

            bool hosted = container.AccountType == AdvDeviceIdentity.Hosted;

            // Compare the HMAC the server sent with the one this phone can compute. The
            // comparison takes constant time, so an attacker cannot learn the secret by timing.
            byte[] expected = AdvSignatures.DetailsHmac(_credentials.AdvSecret, container.Details, hosted);
            if (container.Hmac == null || !Hmac.FixedTimeEquals(expected, container.Hmac))
            {
                throw new ProtocolException("the pairing reply was not signed for this device's ADV secret");
            }

            AdvSignedDeviceIdentity identity = AdvSignedDeviceIdentity.Decode(container.Details);
            if (!AdvSignatures.VerifyAccountSignature(identity, _credentials.IdentityPublicKey, hosted))
            {
                throw new ProtocolException("the account's signature does not cover this device's identity key");
            }

            // The account has signed this phone; now this phone signs its own details.
            identity.DeviceSignature = AdvSignatures.SignDevice(
                identity, _credentials.IdentityPrivateKey, _credentials.IdentityPublicKey, hosted);

            // Kept, because this is the only moment the server hands it over: every message that
            // opens a session carries it, and a phone that lost it cannot be trusted by a
            // recipient it has never written to.
            _credentials.DeviceIdentity = identity.Encode();

            BinaryNode device = pairSuccess.Child("device");
            if (device != null)
            {
                _credentials.Jid = device.Attrs["jid"];
                _credentials.Lid = device.Attrs["lid"];
            }

            if (string.IsNullOrEmpty(_credentials.Jid))
            {
                throw new ProtocolException("the pairing reply names no account");
            }

            BinaryNode platform = pairSuccess.Child("platform");
            if (platform != null) _credentials.Platform = platform.Attrs["name"];

            BinaryNode business = pairSuccess.Child("biz");
            if (business != null) _credentials.PushName = business.Attrs["name"];

            await SendDeviceSignatureAsync(request.Attrs["id"], identity);

            _log.Info("whatsapp: paired as " + _credentials.Jid);
            return _credentials;
        }

        /// <summary>
        /// The reply that ends the pairing: this phone's signature over its own details, with the
        /// account's signature key left out, because the server already has it.
        /// </summary>
        private Task SendDeviceSignatureAsync(string requestId, AdvSignedDeviceIdentity identity)
        {
            AdvDeviceIdentity details = AdvDeviceIdentity.Decode(identity.Details);

            var deviceIdentity = new BinaryNode("device-identity");
            deviceIdentity.Attrs.Add("key-index", details.KeyIndex.ToString());
            deviceIdentity.Content = identity.EncodeForServer();

            var pairDeviceSign = new BinaryNode("pair-device-sign");
            pairDeviceSign.Content = new List<BinaryNode> { deviceIdentity };

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", ServerJid);
            iq.Attrs.Add("type", "result");
            iq.Attrs.Add("id", requestId);
            iq.Content = new List<BinaryNode> { pairDeviceSign };

            return _socket.SendAsync(iq);
        }
    }
}
