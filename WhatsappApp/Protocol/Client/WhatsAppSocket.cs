using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Noise;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Drives the conversation with WhatsApp: connects, runs the Noise handshake, and
    /// then reads and writes nodes over the encrypted channel.
    ///
    /// It deliberately knows nothing about WebSockets or WinRT - that is the transport's
    /// half - and nothing about logins or messages: what it owns is the rule that makes
    /// the rest testable, and the one place the payload goes out. A node is decoded from
    /// a whole frame, a frame that does not decode is an exception rather than a node
    /// nobody understands, and once the handshake has split, every frame is encrypted
    /// and every read is a decryption.
    ///
    /// **The first exchange is not a node.** The plan expected `<stream:features>`; the
    /// live probe showed four bytes that are neither a node nor a protobuf nor a
    /// length-prefixed frame, and the reference explains why: it is a protobuf
    /// `HandshakeMessage`, and the Noise handshake travels inside it. See the plan's
    /// "What execution changed about this plan".
    ///
    /// **The certificate chain is verified before the `ClientPayload` goes out.** The
    /// payload carries this device's identity and the ref it is pairing with; sending it
    /// to a peer that has not proved it is WhatsApp is the one mistake here that cannot
    /// be undone, which is why `HandshakeAsync` fails rather than continues when the
    /// chain does not check out.
    /// </summary>
    public sealed class WhatsAppSocket
    {
        /// <summary>The endpoint WhatsApp Web speaks on.</summary>
        public const string DefaultUrl = "wss://web.whatsapp.com/ws/chat";

        private readonly ITransport _transport;
        private readonly ILog _log;
        private readonly IRandomSource _random;

        private readonly FrameAccumulator _accumulator = new FrameAccumulator();
        private readonly Queue<byte[]> _frames = new Queue<byte[]>();

        private NoiseHandshake _noise;

        /// <summary>
        /// Whether the handshake has **split** - the point after which whole frames are
        /// encrypted. It is not the same as "a handshake object exists": between the two
        /// the handshake's own messages travel with their fields encrypted one by one, and
        /// a socket that decrypted those as transport frames would fail with a tag that
        /// does not match - which is exactly what the live pairing run did once.
        /// </summary>
        private bool _channelEncrypted;

        public WhatsAppSocket(ITransport transport, ILog log)
            : this(transport, log, null)
        {
        }

        public WhatsAppSocket(ITransport transport, ILog log, IRandomSource random)
        {
            if (transport == null) throw new ProtocolException("a socket needs a transport");

            _transport = transport;
            _log = log == null ? new SilentLog() : log;
            _random = random;
        }

        /// <summary>Whether the handshake has split: from here the frames are encrypted.</summary>
        public bool IsEncrypted
        {
            get { return _channelEncrypted; }
        }

        /// <summary>
        /// Connects, runs the Noise handshake, verifies the server's certificate chain,
        /// and sends the `ClientPayload` that goes with the credentials it was handed:
        /// the pairing payload for a device with no account, the login payload for one
        /// that has it.
        ///
        /// The two keys that identify this device - the Noise key the handshake
        /// authenticates with and the identity key that signs - come from the
        /// credentials, because they outlive the connection. The ephemeral key is drawn
        /// here, because it must not.
        /// </summary>
        public async Task HandshakeAsync(string url, Credentials credentials)
        {
            if (credentials == null) throw new ProtocolException("a handshake needs the device's credentials");
            if (_random == null) throw new ProtocolException("a handshake needs a source of randomness");
            if (_noise != null) throw new ProtocolException("this socket has already handshaken");

            await _transport.ConnectAsync(url);
            _log.Info("whatsapp: connected to " + url);

            byte[] ephemeralPrivate = _random.Bytes(Curve25519.KeyLength);
            _noise = new NoiseHandshake(ephemeralPrivate, Curve25519.PublicKey(ephemeralPrivate));

            await _transport.SendAsync(_noise.EncodeFrame(_noise.BuildClientHello()));
            _log.Info("whatsapp: clientHello sent");

            ServerHandshake server = _noise.ProcessServerHello(
                await ReadFrameAsync(), credentials.NoisePrivateKey, credentials.NoisePublicKey);

            CertificateVerifier.Verify(server.Certificates, server.StaticKey, DateTime.UtcNow);
            _log.Info("whatsapp: the server's certificate chain is verified");

            byte[] payload = credentials.IsPaired ? LoginPayload(credentials) : PairingPayload(credentials);

            byte[] finish = HandshakeMessage.EncodeClientFinish(
                server.EncryptedStaticKey, _noise.Encrypt(payload));

            await _transport.SendAsync(_noise.EncodeFrame(finish));
            _noise.FinishInit();
            _channelEncrypted = true;

            _log.Info("whatsapp: clientFinish sent, the channel is encrypted");
        }

        /// <summary>
        /// Connects and returns the first node the server sends.
        ///
        /// Kept for the driver's own milestone: on a real connection the Noise handshake
        /// runs before any WABinary node, so a caller reaches this only after
        /// <see cref="HandshakeAsync"/>. It is here because the transport and the codec
        /// are what it exercises.
        /// </summary>
        public async Task<BinaryNode> ConnectAsync(string url)
        {
            await _transport.ConnectAsync(url);
            _log.Info("whatsapp: connected to " + url);

            BinaryNode first = await ReadNodeAsync();
            _log.Info("whatsapp: first node is <" + first.Tag + ">");
            return first;
        }

        /// <summary>
        /// Reads one frame, decrypting it once the handshake has split.
        ///
        /// A WebSocket message is not a frame: it can carry a piece of one, a whole one
        /// or several, which is why every read goes through the accumulator. A closed
        /// connection is a <see cref="ProtocolException"/> rather than an empty frame.
        ///
        /// A read that fails names what was still on its way. The server hanging up on this
        /// phone has looked the same every time - a short message, then `0x80072EFE` - and
        /// the bytes of the frame it interrupted are the only place that says whether it
        /// stopped in the middle of a node or between two of them. Without the line, the
        /// log of a run that died at the bootstrap is a login and an exception.
        /// </summary>
        public async Task<byte[]> ReadFrameAsync()
        {
            while (_frames.Count == 0)
            {
                byte[] message;
                try
                {
                    message = await _transport.ReceiveAsync();
                }
                catch (Exception error)
                {
                    if (_accumulator.PendingLength > 0)
                    {
                        _log.Info("whatsapp: the connection ended with " + _accumulator.PendingLength
                            + " byte(s) of a frame still arriving: " + _accumulator.PendingHex(16));
                    }

                    _log.Info("whatsapp: the connection ended while waiting for a frame ("
                        + error.Message + ")");
                    throw;
                }

                if (message == null || message.Length == 0)
                {
                    throw new ProtocolException("the server closed the connection");
                }

                _log.Info("whatsapp: " + message.Length + " bytes arrived, "
                    + _accumulator.PendingLength + " frame byte(s) pending");

                List<byte[]> complete = _accumulator.Append(message);
                for (int i = 0; i < complete.Count; i++) _frames.Enqueue(complete[i]);
            }

            byte[] frame = _frames.Dequeue();
            return _channelEncrypted ? _noise.Decrypt(frame) : frame;
        }

        /// <summary>Reads one node, refusing an empty frame instead of decoding it.</summary>
        public async Task<BinaryNode> ReadNodeAsync()
        {
            byte[] payload = await ReadFrameAsync();
            if (payload == null || payload.Length == 0)
            {
                throw new ProtocolException("the server sent an empty frame");
            }

            BinaryNode node = NodeReader.Decode(payload);
            _log.Info("whatsapp: read <" + node.ToString() + "> (" + payload.Length + " bytes)");
            return node;
        }

        /// <summary>
        /// Sends one node. Once the handshake has split it is framed and encrypted;
        /// before that it goes out as the driver's milestone sent it, because there is no
        /// key to encrypt with yet.
        /// </summary>
        public Task SendAsync(BinaryNode node)
        {
            if (node == null) throw new ProtocolException("nothing to send");

            byte[] encoded = NodeWriter.Encode(node);
            return _transport.SendAsync(_channelEncrypted ? _noise.EncodeFrame(encoded) : encoded);
        }

        public void Close()
        {
            _transport.Close();
        }

        /// <summary>
        /// The companion asking to be paired: `ClientPayload` with the device's identity,
        /// its signed pre-key and the `DeviceProps` blob, and without a `username` -
        /// having no account yet is exactly what that absence says.
        /// </summary>
        public static byte[] PairingPayload(Credentials credentials)
        {
            return ClientPayload.Registration(
                credentials.RegistrationId,
                credentials.IdentityPublicKey,
                credentials.SignedPreKeyId,
                credentials.SignedPreKeyPublic,
                credentials.SignedPreKeySignature,
                DeviceProps.Encode(
                    ClientPayload.AppVersionPrimary,
                    ClientPayload.AppVersionSecondary,
                    ClientPayload.AppVersionTertiary));
        }

        /// <summary>The same device coming back to an account it already has.</summary>
        public static byte[] LoginPayload(Credentials credentials)
        {
            DecodedJid jid = JidUtils.Decode(credentials.Jid);

            ulong user;
            if (!jid.IsJid || !ulong.TryParse(jid.User, out user))
            {
                throw new ProtocolException("the stored account is not a JID: " + credentials.Jid);
            }

            return ClientPayload.Login(user, (uint)jid.Device);
        }
    }
}
