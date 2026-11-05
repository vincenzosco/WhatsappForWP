using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The certificate a DTLS server signs with: a P-256 key and the self-signed X.509 that
    /// carries it. The private half never leaves this object, and the public half is what
    /// `DtlsClient` checks a ServerKeyExchange signature against.
    ///
    /// Why a generated certificate: `DtlsClient` refuses an RSA suite by name and verifies the
    /// server's signature with `EcP256`, so the certificate has to hold a P-256 key. There is no
    /// store to take one from on this platform, and a fingerprint the signalling carries is what
    /// makes it trustworthy anyway - the names and dates in it are decoration.
    /// </summary>
    public sealed class DtlsIdentity
    {
        private readonly byte[] _privateScalar;

        /// <summary>
        /// `privateScalar` is 32 bytes, 1..n-1; `commonName` names the certificate and defaults
        /// to "dtls".
        /// </summary>
        public DtlsIdentity(byte[] privateScalar, string commonName)
        {
            if (privateScalar == null || privateScalar.Length != EcP256.CoordinateLength)
            {
                throw new ProtocolException("a certificate key is " + EcP256.CoordinateLength
                    + " bytes of P-256");
            }

            PublicPoint = EcP256.PublicPoint(privateScalar);

            if (PublicPoint == null)
            {
                throw new ProtocolException("a certificate key is 1..n-1");
            }

            _privateScalar = new byte[privateScalar.Length];
            Buffer.BlockCopy(privateScalar, 0, _privateScalar, 0, privateScalar.Length);

            Certificate = X509.SelfSignedP256(privateScalar, commonName);
        }

        /// <summary>The 65-byte uncompressed point inside the certificate.</summary>
        public byte[] PublicPoint { get; private set; }

        /// <summary>The DER certificate the ServerHello's Certificate message carries.</summary>
        public byte[] Certificate { get; private set; }

        /// <summary>The server's signature over `message`, ECDSA-SHA256, DER encoded.</summary>
        public byte[] Sign(byte[] message)
        {
            return EcP256.Sign(message, _privateScalar);
        }
    }

    /// <summary>
    /// The server end of a DTLS 1.2 handshake (RFC 6347): it answers a ClientHello with its
    /// certificate and an ephemeral X25519 key, checks the client's Finished, and ends with the
    /// SRTP keys the audio is encrypted with.
    ///
    /// Why it was missing and what it costs to have: an ICE-lite agent is always the controlled
    /// side of a traversal, and the end that answers a traversal is the end that answers the
    /// handshake. Without this file a call could answer connectivity checks and then have nothing
    /// to say, which is exactly the shape the media design document described.
    ///
    /// The suite is the one `DtlsClient` offers here: ECDHE_ECDSA with AES-128-GCM-SHA256. The
    /// server authenticates with a P-256 key from `DtlsIdentity` and its own ephemeral X25519
    /// key, so the two sides end up with the same four pieces of key material and neither can
    /// choose them alone.
    ///
    /// What it does not do: no HelloVerifyRequest (the peer is authenticated by the call's
    /// signalling, and a media socket that answers a cookie costs a round trip on a phone's
    /// network), no session resumption, no renegotiation, no retransmission timer - the last one
    /// is `Resend` and the caller's clock, as in `DtlsClient`.
    /// </summary>
    public sealed class DtlsServer
    {
        private const int StageAwaitingHello = 0;
        private const int StageAwaitingFinished = 1;
        private const int StageDone = 2;
        private const int StageFailed = 3;

        private readonly DtlsIdentity _identity;
        private readonly byte[] _privateKey;
        private readonly byte[] _publicKey;
        private readonly byte[] _serverRandom;

        private readonly List<byte[]> _transcript = new List<byte[]>();

        private byte[] _clientRandom;
        private byte[] _clientPublicKey;
        private byte[] _masterSecret;
        private byte[] _flight;

        private DtlsRecordKeys _inKeys;
        private DtlsRecordKeys _outKeys;

        private ushort _outEpoch;
        private ulong _outSequence;
        private ushort _outHandshakeSequence;

        private bool _extendedMasterSecret;
        private bool _srtpAgreed;
        private bool _clientFinishedVerified;

        private int _stage;
        private string _failure;

        /// <summary>
        /// `identity` signs the handshake, `privateKey` is 32 bytes of X25519 for this handshake
        /// (the caller makes it, or takes it from `DeviceRandomSource`) and `serverRandom` is 32
        /// bytes of the caller's randomness, so a test can pin a handshake.
        /// </summary>
        public DtlsServer(DtlsIdentity identity, byte[] privateKey, byte[] serverRandom)
        {
            if (identity == null) throw new ProtocolException("a DTLS server needs a certificate");
            if (privateKey == null || privateKey.Length != 32)
            {
                throw new ProtocolException("a DTLS server key is 32 bytes of X25519");
            }

            if (serverRandom == null || serverRandom.Length != Dtls.RandomLength)
            {
                throw new ProtocolException("a DTLS server random is " + Dtls.RandomLength + " bytes");
            }

            _identity = identity;
            _privateKey = privateKey;
            _publicKey = Curve25519.PublicKey(privateKey);
            _serverRandom = serverRandom;
            _stage = StageAwaitingHello;
        }

        /// <summary>True once the client's Finished verified and the media keys are usable.</summary>
        public bool IsComplete
        {
            get { return _stage == StageDone; }
        }

        /// <summary>Why the handshake stopped, or null.</summary>
        public string Failure
        {
            get { return _failure; }
        }

        /// <summary>Whether the client offered extended_master_secret and got the extended form.</summary>
        public bool ExtendedMasterSecret
        {
            get { return _extendedMasterSecret; }
        }

        /// <summary>Whether the client offered the SRTP profile this project speaks.</summary>
        public bool SrtpAgreed
        {
            get { return _srtpAgreed; }
        }

        public bool ClientFinishedVerified
        {
            get { return _clientFinishedVerified; }
        }

        /// <summary>The certificate this server sent, whose SHA-256 the client pins.</summary>
        public byte[] Certificate
        {
            get { return _identity.Certificate; }
        }

        /// <summary>The client's direction of the SRTP keys: what this server reads with.</summary>
        public byte[] SrtpClientKey { get; private set; }

        public byte[] SrtpClientSalt { get; private set; }

        /// <summary>The server's direction: what this server writes with.</summary>
        public byte[] SrtpServerKey { get; private set; }

        public byte[] SrtpServerSalt { get; private set; }

        /// <summary>
        /// One datagram in; the datagram to send back, or null. A failure is recorded rather than
        /// thrown: the caller has a call to end, and a hostile datagram must not become an
        /// exception on the call's thread.
        /// </summary>
        public byte[] Receive(byte[] datagram)
        {
            if (datagram == null) throw new ProtocolException("a datagram is needed");
            if (_stage == StageFailed || _stage == StageDone) return null;

            var outbound = new List<byte>();
            int at = 0;

            while (at + Dtls.RecordHeaderLength <= datagram.Length)
            {
                int declared = (datagram[at + 11] << 8) | datagram[at + 12];
                int recordLength = Dtls.RecordHeaderLength + declared;
                if (at + recordLength > datagram.Length) break;

                var record = new byte[recordLength];
                Buffer.BlockCopy(datagram, at, record, 0, recordLength);
                at += recordLength;

                int epoch = (record[3] << 8) | record[4];
                DtlsRecordView view = DtlsRecord.Open(epoch == 0 ? null : _inKeys,
                    record, recordLength);

                if (view == null)
                {
                    // An authenticated record that fails its tag is one datagram to drop; before
                    // any key exists there is nothing to have failed, so that is a real failure.
                    if (epoch != 0) continue;
                    Fail("a record could not be read");
                    return null;
                }

                if (view.Type == Dtls.TypeAlert)
                {
                    Fail("the client sent alert " + (view.Fragment.Length > 1 ? view.Fragment[1] : 0));
                    return null;
                }

                // ChangeCipherSpec carries nothing to read: the switch to epoch 1 happens when the
                // client's Finished verifies, which is what actually proves the keys agree.
                if (view.Type != Dtls.TypeHandshake) continue;

                byte[] next;
                try
                {
                    next = Read(view.Fragment);
                }
                catch (ProtocolException ex)
                {
                    Fail(ex.Message);
                    return null;
                }

                if (next != null) outbound.AddRange(next);
                if (_stage == StageFailed || _stage == StageDone) break;
            }

            return outbound.Count == 0 ? null : outbound.ToArray();
        }

        /// <summary>
        /// The last flight again, while the client is still silent: a datagram lost on the way is
        /// answered by the caller's retransmission timer, and this class keeps no clock.
        /// </summary>
        public byte[] Resend()
        {
            if (_stage != StageAwaitingFinished) return null;

            return _flight;
        }

        private byte[] Read(byte[] fragment)
        {
            DtlsHandshakeMessage message;
            try
            {
                message = DtlsHandshakeMessage.Parse(fragment, 0, fragment.Length);
            }
            catch (ProtocolException)
            {
                return null;
            }

            if (message.Type == Dtls.HandshakeFinished)
            {
                return VerifyClientFinished(message);
            }

            if (_stage == StageAwaitingFinished && message.Type == Dtls.HandshakeClientHello)
            {
                // A repeated hello is a lost flight, not a new handshake: re-sending what was
                // already built keeps the transcript the two ends hashed the same.
                return _flight;
            }

            // A repeated key exchange is a lost datagram, not a second one. The bytes are
            // already in the transcript, and hashing them again would make this side's hash
            // differ from the client's - the client's Finished would then fail to verify for a
            // reason that reads like a key disagreement but is a retransmission.
            if (message.Type == Dtls.HandshakeClientKeyExchange && _masterSecret != null)
            {
                return null;
            }

            _transcript.Add(message.ToBytes());

            switch (message.Type)
            {
                case Dtls.HandshakeClientHello:
                    return ReadClientHello(message.Body);

                case Dtls.HandshakeClientKeyExchange:
                    ReadClientKeyExchange(message.Body);
                    return null;

                default:
                    return null;
            }
        }

        private byte[] ReadClientHello(byte[] body)
        {
            if (_stage != StageAwaitingHello)
            {
                Fail("a second hello arrived in the middle of the handshake");
                return null;
            }

            var reader = new DtlsReader(body);

            int version = reader.UInt16();
            if (version != Dtls.Version)
            {
                Fail("the client speaks version 0x" + version.ToString("x4") + ", not DTLS 1.2");
                return null;
            }

            _clientRandom = reader.Bytes(Dtls.RandomLength);
            reader.Vector8(); // session id: no resumption here
            reader.Vector8(); // cookie: this server never asked for one

            byte[] suites = reader.Vector16();
            bool offersSuite = false;

            for (int i = 0; i + 1 < suites.Length; i += 2)
            {
                int suite = (suites[i] << 8) | suites[i + 1];
                if (suite == Dtls.SuiteEcdheEcdsaAes128GcmSha256) offersSuite = true;
            }

            if (!offersSuite)
            {
                Fail("the client offered no suite this server can do (ECDHE_ECDSA, AES-128-GCM)");
                return null;
            }

            reader.Vector8(); // compression: the null one is the only one offered

            if (reader.Remaining >= 2)
            {
                byte[] extensionBytes = reader.Vector16();
                var extensions = new DtlsReader(extensionBytes);

                while (extensions.Remaining >= 4)
                {
                    int type = extensions.UInt16();
                    byte[] extensionBody = extensions.Vector16();

                    if (type == 23) _extendedMasterSecret = true;

                    if (type == 14 && extensionBody.Length >= 4)
                    {
                        int profileLength = (extensionBody[0] << 8) | extensionBody[1];

                        for (int i = 0; i + 1 < profileLength && 2 + i + 1 < extensionBody.Length; i += 2)
                        {
                            int profile = (extensionBody[2 + i] << 8) | extensionBody[3 + i];
                            if (profile == Dtls.SrtpProfileAes128CmHmacSha1_80) _srtpAgreed = true;
                        }
                    }
                }
            }

            _flight = BuildServerFlight();
            _stage = StageAwaitingFinished;

            return _flight;
        }

        /// <summary>ServerHello, Certificate, ServerKeyExchange and ServerHelloDone, as one flight.</summary>
        private byte[] BuildServerFlight()
        {
            var random = new DtlsWriter();
            random.UInt16(Dtls.Version);
            random.Bytes(_serverRandom);
            random.Vector8(new byte[0]);
            random.UInt16(Dtls.SuiteEcdheEcdsaAes128GcmSha256);
            random.Byte(0);

            var extensions = new DtlsWriter();
            if (_extendedMasterSecret) extensions.Extension(23, new byte[0]);
            if (_srtpAgreed)
            {
                // use_srtp: the profiles, then an empty srtp_mki (RFC 5764, section 4.1.1).
                extensions.Extension(14, new byte[] { 0x00, 0x02, 0x00, 0x07, 0x00 });
            }

            random.Vector16(extensions.ToArray());

            byte[] hello = HandshakeRecord(Dtls.HandshakeServerHello, random.ToArray());

            var certificate = new DtlsWriter();
            var certificateList = new DtlsWriter();
            certificateList.Vector24(_identity.Certificate);
            certificate.Vector24(certificateList.ToArray());

            byte[] certificateMessage = HandshakeRecord(Dtls.HandshakeCertificate, certificate.ToArray());

            // ECDHE with X25519: curve_type named_curve, the group, and the public key. The
            // signature covers both randoms and these bytes, and the client recomputes the run
            // from the message it received.
            var exchange = new DtlsWriter();
            exchange.Byte(3);
            exchange.UInt16(Dtls.GroupX25519);
            exchange.Vector8(_publicKey);

            byte[] parameters = exchange.ToArray();

            byte[] signed = new byte[Dtls.RandomLength * 2 + parameters.Length];
            Buffer.BlockCopy(_clientRandom, 0, signed, 0, Dtls.RandomLength);
            Buffer.BlockCopy(_serverRandom, 0, signed, Dtls.RandomLength, Dtls.RandomLength);
            Buffer.BlockCopy(parameters, 0, signed, Dtls.RandomLength * 2, parameters.Length);

            exchange.UInt16(0x0403); // ecdsa_secp256r1_sha256
            exchange.Vector16(_identity.Sign(signed));

            byte[] exchangeMessage = HandshakeRecord(Dtls.HandshakeServerKeyExchange, exchange.ToArray());

            byte[] done = HandshakeRecord(Dtls.HandshakeServerHelloDone, new byte[0]);

            return Combine(hello, certificateMessage, exchangeMessage, done);
        }

        private void ReadClientKeyExchange(byte[] body)
        {
            if (_clientRandom == null || _serverRandom == null)
            {
                Fail("the client's key exchange arrived before its hello");
                return;
            }

            var reader = new DtlsReader(body);
            byte[] publicKey = reader.Vector8();

            if (publicKey.Length != 32)
            {
                Fail("an X25519 public key is 32 bytes, not " + publicKey.Length);
                return;
            }

            _clientPublicKey = publicKey;

            byte[] preMaster = Curve25519.SharedSecret(_privateKey, publicKey);

            _masterSecret = _extendedMasterSecret
                ? DtlsPrf.Prf(preMaster, "extended master secret", TranscriptHash(), 48)
                : DtlsPrf.Prf(preMaster, "master secret",
                    Combine(_clientRandom, _serverRandom), 48);

            byte[] keyBlock = DtlsPrf.Prf(_masterSecret, "key expansion",
                Combine(_serverRandom, _clientRandom), 40);

            var clientKey = new byte[16];
            var serverKey = new byte[16];
            var clientIv = new byte[4];
            var serverIv = new byte[4];

            Buffer.BlockCopy(keyBlock, 0, clientKey, 0, 16);
            Buffer.BlockCopy(keyBlock, 16, serverKey, 0, 16);
            Buffer.BlockCopy(keyBlock, 32, clientIv, 0, 4);
            Buffer.BlockCopy(keyBlock, 36, serverIv, 0, 4);

            _inKeys = new DtlsRecordKeys(clientKey, clientIv);
            _outKeys = new DtlsRecordKeys(serverKey, serverIv);
        }

        /// <summary>
        /// The client's Finished: the transcript it hashed, the label, and twelve bytes. A
        /// mismatch is the one failure that says the two ends derived different keys, which is
        /// why it is worth its own sentence in the log.
        /// </summary>
        private byte[] VerifyClientFinished(DtlsHandshakeMessage message)
        {
            if (_masterSecret == null)
            {
                Fail("the client finished a handshake it never keyed");
                return null;
            }

            byte[] body = message.Body;

            byte[] expected = DtlsPrf.Prf(_masterSecret, "client finished",
                TranscriptHash(), Dtls.VerifyDataLength);

            if (!Hmac.FixedTimeEquals(expected, body))
            {
                Fail("the client's Finished does not verify: the two ends derived different keys");
                return null;
            }

            _clientFinishedVerified = true;

            // The client's own Finished is part of the transcript the server's is computed over,
            // and the server's is not part of its own - the asymmetry is TLS's, not a choice. The
            // bytes added are the ones that arrived: a re-framed copy would carry this server's
            // numbering instead of the client's, and the two hashes would disagree.
            _transcript.Add(message.ToBytes());

            var outbound = new List<byte>();

            outbound.AddRange(DtlsRecord.Plain(Dtls.TypeChangeCipherSpec, 0, _outSequence++,
                new byte[] { 0x01 }));

            _outEpoch = 1;
            _outSequence = 0;

            byte[] verifyData = DtlsPrf.Prf(_masterSecret, "server finished",
                TranscriptHash(), Dtls.VerifyDataLength);

            outbound.AddRange(HandshakeRecord(Dtls.HandshakeFinished, verifyData));

            byte[] exporter = DtlsPrf.Prf(_masterSecret, "EXTRACTOR-dtls_srtp",
                Combine(_clientRandom, _serverRandom), 60);

            var clientKey = new byte[16];
            var serverKey = new byte[16];
            var clientSalt = new byte[14];
            var serverSalt = new byte[14];

            Buffer.BlockCopy(exporter, 0, clientKey, 0, 16);
            Buffer.BlockCopy(exporter, 16, serverKey, 0, 16);
            Buffer.BlockCopy(exporter, 32, clientSalt, 0, 14);
            Buffer.BlockCopy(exporter, 46, serverSalt, 0, 14);

            SrtpClientKey = clientKey;
            SrtpClientSalt = clientSalt;
            SrtpServerKey = serverKey;
            SrtpServerSalt = serverSalt;

            _stage = StageDone;

            return outbound.ToArray();
        }

        /// <summary>Frames one handshake message, hashes it, and seals it at the current epoch.</summary>
        private byte[] HandshakeRecord(byte type, byte[] body)
        {
            var message = new DtlsHandshakeMessage(type, _outHandshakeSequence++, body);
            byte[] framed = message.ToBytes();

            _transcript.Add(framed);

            if (_outEpoch == 0)
            {
                return DtlsRecord.Plain(Dtls.TypeHandshake, 0, _outSequence++, framed);
            }

            return DtlsRecord.Seal(_outKeys, Dtls.TypeHandshake, _outEpoch, _outSequence++, framed);
        }

        /// <summary>SHA-256 over every handshake message so far, headers included.</summary>
        private byte[] TranscriptHash()
        {
            int total = 0;
            for (int i = 0; i < _transcript.Count; i++) total += _transcript[i].Length;

            var all = new byte[total];
            int at = 0;

            for (int i = 0; i < _transcript.Count; i++)
            {
                Buffer.BlockCopy(_transcript[i], 0, all, at, _transcript[i].Length);
                at += _transcript[i].Length;
            }

            return Sha256.Hash(all);
        }

        private void Fail(string reason)
        {
            if (_failure == null) _failure = reason;
            _stage = StageFailed;
        }

        private static byte[] Combine(params byte[][] parts)
        {
            int total = 0;
            for (int i = 0; i < parts.Length; i++) total += parts[i].Length;

            var joined = new byte[total];
            int at = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                Buffer.BlockCopy(parts[i], 0, joined, at, parts[i].Length);
                at += parts[i].Length;
            }

            return joined;
        }
    }

    /// <summary>
    /// `DtlsServer` as the transport's handshake: this side answers, so `Start` has nothing to
    /// send and the peer speaks first.
    /// </summary>
    public sealed class DtlsServerHandshake : IMediaHandshake
    {
        private readonly DtlsServer _server;

        public DtlsServerHandshake(DtlsServer server)
        {
            if (server == null) throw new ProtocolException("a server handshake needs a server");

            _server = server;
        }

        /// <summary>Null: a DTLS server waits for the client's hello.</summary>
        public byte[] Start()
        {
            return null;
        }

        public byte[] Receive(byte[] datagram)
        {
            return _server.Receive(datagram);
        }

        public bool IsComplete
        {
            get { return _server.IsComplete; }
        }

        /// <summary>
        /// A server has no peer certificate to pin: the client is authenticated by the Finished,
        /// which proves it derived the same keys. True once that happened.
        /// </summary>
        public bool IsTrusted
        {
            get { return _server.IsComplete; }
        }

        public string Failure
        {
            get { return _server.Failure; }
        }

        /// <summary>The server writes with the server key and reads with the client's.</summary>
        public byte[] LocalKey
        {
            get { return _server.SrtpServerKey; }
        }

        public byte[] LocalSalt
        {
            get { return _server.SrtpServerSalt; }
        }

        public byte[] RemoteKey
        {
            get { return _server.SrtpClientKey; }
        }

        public byte[] RemoteSalt
        {
            get { return _server.SrtpClientSalt; }
        }
    }
}
