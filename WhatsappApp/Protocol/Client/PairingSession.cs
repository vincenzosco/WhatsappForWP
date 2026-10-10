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
    /// One pairing attempt: connect, hand the server a fresh identity, show the codes it
    /// issues, and turn the reply into an account.
    ///
    /// It is two steps rather than one because the page has to show something in
    /// between. <see cref="StartAsync"/> ends with the codes on screen;
    /// <see cref="WaitForPairAsync"/> ends with an account or with nothing. A single
    /// method that returned only the account would leave the screen blank for the half
    /// minute a person needs to reach for their phone.
    ///
    /// The codes the server issues are a **list**, and they expire: the reference shows
    /// one at a time, twenty seconds each and sixty when the server sent six, then gives
    /// up. The page owns that rotation - the codes it already holds need no I/O - and
    /// this class owns everything up to the first code and everything after the last.
    ///
    /// Nothing here trusts the reply. The account signature is checked against the
    /// identity key the pairing started with, and the whole reply is authenticated with
    /// the ADV secret, which only this client has; a reply that fails either check is an
    /// exception, not a session.
    /// </summary>
    public sealed class PairingSession
    {
        /// <summary>The server that owns accounts and pairings.</summary>
        public const string ServerJid = "s.whatsapp.net";

        private readonly WhatsAppSocket _socket;
        private readonly Credentials _credentials;
        private readonly ILog _log;

        public PairingSession(ITransport transport, Credentials credentials, ILog log, IRandomSource random)
        {
            if (credentials == null) throw new ProtocolException("a pairing session needs credentials to pair");

            _credentials = credentials;
            _socket = new WhatsAppSocket(transport, log, random);
            _log = log == null ? new SilentLog() : log;
        }

        /// <summary>The credentials this session is trying to get paired, filled in as the server answers.</summary>
        public Credentials Credentials
        {
            get { return _credentials; }
        }

        /// <summary>
        /// Runs the handshake and waits for the server to ask for a pairing, then returns
        /// the QR strings to show - one per code the server issued, in the order the
        /// reference shows them.
        ///
        /// The request is acknowledged before the codes are handed out: the server waits
        /// for that acknowledgement, and a client that showed a code without sending it
        /// would be showing a code the server had already stopped waiting for.
        /// </summary>
        public async Task<IList<string>> StartAsync()
        {
            await _socket.HandshakeAsync(WhatsAppSocket.DefaultUrl, _credentials);

            while (true)
            {
                BinaryNode node = await _socket.ReadNodeAsync();
                BinaryNode pairDevice = node.Child("pair-device");
                if (pairDevice == null)
                {
                    // Anything else on the way in is reported and stepped over: the
                    // pairing request is what this method is waiting for.
                    _log.Info("whatsapp: <" + node.Tag + "> arrived before the pairing request");
                    continue;
                }

                await AcknowledgeAsync(node);
                IList<string> codes = CodesFor(pairDevice, _credentials);

                _log.Info("whatsapp: the server asked for a pairing (" + codes.Count + " code(s))");
                return codes;
            }
        }

        /// <summary>
        /// Waits for the server to accept the pairing and returns the account.
        ///
        /// Returns null when the time runs out, and the session is over when it does:
        /// the codes have expired by then, and the server drops a connection whose codes
        /// nobody scanned. The caller shows the expiry message and starts a new session,
        /// which is a new identity and a new set of codes - there is no way to revive
        /// these.
        /// </summary>
        public async Task<Credentials> WaitForPairAsync(TimeSpan timeout)
        {
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

                // A read that never answers is what a pairing that is never scanned looks
                // like, so the wait is bounded here rather than only at the transport.
                if (await Task.WhenAny(next, Task.Delay(remaining)) != next)
                {
                    _log.Info("whatsapp: the pairing timed out");
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
                    // A transport that runs out of patience, or a server that drops a
                    // connection whose codes nobody scanned, both look like this - and
                    // both mean the same thing here: no pairing happened, and a new
                    // session with new codes is what the caller needs.
                    _log.Info("whatsapp: the connection ended before the pairing: " + error.Message);
                    Close();
                    return null;
                }

                BinaryNode pairSuccess = node.Child("pair-success");
                if (pairSuccess != null) return await AcceptAsync(node, pairSuccess);
            }
        }

        public void Close()
        {
            _socket.Close();
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
        /// The codes, one string per `&lt;ref&gt;` the server sent. A node with no ref in
        /// it is refused rather than shown as an empty QR: there is nothing to scan, and
        /// the page cannot tell that apart from a code that is simply wrong.
        ///
        /// A function of a node rather than of a connection, so the shape the server
        /// sends is pinned by a test instead of by a live run that has already scrolled
        /// away.
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

                    codes.Add(PairingQr.ForRefs(content, credentials));
                }
            }

            if (codes.Count == 0) throw new ProtocolException("the pairing request carries no ref");
            return codes;
        }

        /// <summary>
        /// The account: check the reply, sign this device's half, tell the server, and
        /// hand back the credentials with the account in them.
        ///
        /// The order matters. The HMAC proves the reply was built for this device's ADV
        /// secret; the account signature proves the details - including our identity key
        /// - were signed by the account; only then is there anything worth signing back.
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

            identity.DeviceSignature = AdvSignatures.SignDevice(
                identity, _credentials.IdentityPrivateKey, _credentials.IdentityPublicKey, hosted);

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
        /// The reply that ends the pairing: this device's signature over its own details,
        /// with the account's signature key left out - the server put it there, it does
        /// not need it back.
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
