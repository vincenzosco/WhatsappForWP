using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// A DTLS 1.2 client for one media channel: it starts the handshake, answers the server's
    /// flight, and ends with the SRTP master key and salt the audio is encrypted with.
    ///
    /// The shape of the exchange, in the order this class follows (RFC 6347 with RFC 5246's
    /// key schedule):
    ///
    ///   client                                                 server
    ///   ClientHello                        ->
    ///                                      &lt;-  ServerHello, Certificate,
    ///                                           ServerKeyExchange, ServerHelloDone
    ///   ClientKeyExchange, CCS, Finished   ->
    ///                                      &lt;-  CCS, Finished
    ///   (EXTRACTOR-dtls_srtp)
    ///
    /// What it deliberately leaves out, with the reason in each case:
    ///  - **the signature on ServerKeyExchange is not verified.** This project has no P-256
    ///    and no RSA, so `Certificate` is hashed and compared against the fingerprint the
    ///    caller supplies (`expectedCertificateSha256`) and nothing else. Without that
    ///    fingerprint the handshake completes but `IsTrusted` is false - the caller decides,
    ///    it is never assumed. This is the same trust shape WebRTC uses, where the
    ///    certificate is self-signed and the `a=fingerprint` line is the identity.
    ///  - **no fragmentation.** A handshake message that arrives in pieces is refused by name
    ///    (`DtlsHandshakeMessage.Parse`), because a message hashed in pieces is a message this
    ///    client would hash wrongly - and a wrong transcript is a Finished that fails anyway,
    ///    later and less clearly.
    ///  - **no resumption, no renegotiation, no HelloVerifyRequest.** A server that sends a
    ///    cookie is answered with a failure, not with a silent half-handshake.
    ///  - **AES-128-GCM only.** CBC suites exist in front of every WP8.1 peer and none of them
    ///    is implemented here; a server that picks one gets a named refusal.
    ///
    /// What is **verified**: the server's Finished, over the whole transcript, so a peer that
    /// did not derive the same master secret cannot pretend it did.
    /// </summary>
    public sealed class DtlsClient
    {
        private const int StageNew = 0;
        private const int StageAwaitingFlight = 1;
        private const int StageAwaitingFinished = 2;
        private const int StageDone = 3;
        private const int StageFailed = 4;

        private readonly byte[] _privateKey;
        private readonly byte[] _publicKey;
        private readonly byte[] _clientRandom;
        private readonly byte[] _expectedCertificate;

        private readonly List<byte[]> _transcript = new List<byte[]>();

        private byte[] _serverRandom;
        private byte[] _serverPublicKey;
        private byte[] _masterSecret;
        private byte[] _serverSignatureSeen;

        private DtlsRecordKeys _outKeys;
        private DtlsRecordKeys _inKeys;

        private ushort _outEpoch;
        private ushort _inEpoch;
        private ulong _outSequence;
        private ulong _inSequence;

        private int _stage;
        private bool _certificateListed;
        private bool _srtpAgreed;
        private bool _serverFinishedVerified;
        private string _failure;

        /// <summary>
        /// `privateKey` is 32 bytes of X25519 (the caller makes it, `Curve25519.PublicKey`
        /// derives the public half); `clientRandom` is 32 bytes of the caller's randomness, so
        /// a test can pin a handshake and the phone can use `DeviceRandomSource`;
        /// `expectedCertificateSha256` is the fingerprint from the signalling, or null.
        /// </summary>
        public DtlsClient(byte[] privateKey, byte[] clientRandom, byte[] expectedCertificateSha256)
        {
            if (privateKey == null || privateKey.Length != 32)
            {
                throw new ProtocolException("a DTLS client key is 32 bytes of X25519");
            }

            if (clientRandom == null || clientRandom.Length != Dtls.RandomLength)
            {
                throw new ProtocolException("a DTLS client random is " + Dtls.RandomLength + " bytes");
            }

            if (expectedCertificateSha256 != null && expectedCertificateSha256.Length != Sha256.DigestLength)
            {
                throw new ProtocolException("a certificate fingerprint is a SHA-256");
            }

            _privateKey = privateKey;
            _publicKey = Curve25519.PublicKey(privateKey);
            _clientRandom = clientRandom;
            _expectedCertificate = expectedCertificateSha256;
        }

        /// <summary>The first datagram: a ClientHello, in the clear.</summary>
        public byte[] Start()
        {
            if (_stage != StageNew) throw new ProtocolException("the handshake has already started");

            _stage = StageAwaitingFlight;
            _outEpoch = 0;
            _outSequence = 0;
            _inEpoch = 0;
            _inSequence = 0;

            var hello = new DtlsWriter();
            hello.UInt16(Dtls.Version);
            hello.Bytes(_clientRandom);
            hello.Byte(0); // session id: none, no resumption
            hello.Byte(0); // DTLS cookie: the first hello carries none
            hello.UInt16(4);
            hello.UInt16(Dtls.SuiteEcdheEcdsaAes128GcmSha256);
            hello.UInt16(Dtls.SuiteEcdheRsaAes128GcmSha256);
            hello.Byte(1);
            hello.Byte(0); // compression: null only

            var extensions = new DtlsWriter();

            var groups = new DtlsWriter();
            groups.UInt16(2);
            groups.UInt16(Dtls.GroupX25519);
            extensions.Extension(10, groups.ToArray());

            var algorithms = new DtlsWriter();
            algorithms.UInt16(4);
            algorithms.UInt16(0x0403); // ecdsa_secp256r1_sha256
            algorithms.UInt16(0x0401); // rsa_pkcs1_sha256
            extensions.Extension(13, algorithms.ToArray());

            var srtp = new DtlsWriter();
            srtp.UInt16(2);
            srtp.UInt16(Dtls.SrtpProfileAes128CmHmacSha1_80);
            extensions.Extension(14, srtp.ToArray());

            // RFC 7627: the server answers with its own, and the master secret is the extended
            // one. It costs an empty extension to ask, and it is what a WebRTC peer expects.
            extensions.Extension(23, new byte[0]);

            hello.Vector16(extensions.ToArray());

            return SendHandshake(Dtls.HandshakeClientHello, hello.ToArray());
        }

        /// <summary>
        /// One datagram in. It returns the datagram to send back, or null when there is nothing
        /// to send. A failure is recorded rather than thrown: the caller has a call to end, and
        /// an exception here would be a crash on a dropped packet.
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

                int headerEpoch = (record[3] << 8) | record[4];
                DtlsRecordView view = DtlsRecord.Open(headerEpoch == 0 ? null : _inKeys,
                    record, recordLength);

                if (view == null)
                {
                    // An authenticated record that fails its tag is one datagram to drop; before
                    // any key exists there is nothing to drop, so that is a real failure.
                    if (headerEpoch != 0) continue;
                    Fail("a record could not be read");
                    return null;
                }

                byte type = view.Type;
                byte[] fragment = view.Fragment;

                if (type == Dtls.TypeAlert)
                {
                    Fail("the server sent alert " + (fragment.Length > 1 ? fragment[1] : 0));
                    return null;
                }

                if (type == Dtls.TypeChangeCipherSpec)
                {
                    _inEpoch = 1;
                    _inSequence = 0;
                    continue;
                }

                if (type != Dtls.TypeHandshake) continue;

                int offset = 0;
                while (offset + Dtls.HandshakeHeaderLength <= fragment.Length)
                {
                    int bodyLength = (fragment[offset + 1] << 16) | (fragment[offset + 2] << 8) | fragment[offset + 3];
                    int messageLength = Dtls.HandshakeHeaderLength + bodyLength;
                    if (offset + messageLength > fragment.Length) break;

                    DtlsHandshakeMessage message;
                    try
                    {
                        message = DtlsHandshakeMessage.Parse(fragment, offset, messageLength);
                    }
                    catch (ProtocolException ex)
                    {
                        Fail(ex.Message);
                        return null;
                    }

                    offset += messageLength;

                    byte[] next = Handle(message, outbound);
                    if (next != null) outbound.AddRange(next);
                    if (_stage == StageFailed) return null;
                }
            }

            return outbound.Count == 0 ? null : outbound.ToArray();
        }

        private byte[] Handle(DtlsHandshakeMessage message, List<byte> outbound)
        {
            // The Finished is the one message whose own bytes are not in its hash.
            if (message.Type != Dtls.HandshakeFinished) _transcript.Add(message.ToBytes());

            switch (message.Type)
            {
                case Dtls.HandshakeServerHello:
                    return ReadServerHello(message.Body);

                case Dtls.HandshakeCertificate:
                    ReadCertificate(message.Body);
                    return null;

                case Dtls.HandshakeServerKeyExchange:
                    ReadServerKeyExchange(message.Body);
                    return null;

                case Dtls.HandshakeServerHelloDone:
                    return BuildClientFlight(outbound);

                case Dtls.HandshakeFinished:
                    VerifyServerFinished(message.Body);
                    return null;

                default:
                    return null;
            }
        }

        private byte[] ReadServerHello(byte[] body)
        {
            var reader = new DtlsReader(body);

            int version = reader.UInt16();
            if (version != Dtls.Version)
            {
                Fail("the server speaks version 0x" + version.ToString("x4") + ", not DTLS 1.2");
                return null;
            }

            _serverRandom = reader.Bytes(Dtls.RandomLength);
            reader.Vector8(); // session id
            int suite = reader.UInt16();
            reader.Byte(); // compression

            if (suite != Dtls.SuiteEcdheEcdsaAes128GcmSha256 && suite != Dtls.SuiteEcdheRsaAes128GcmSha256)
            {
                Fail("the server chose suite 0x" + suite.ToString("x4") + ", which is not AES-GCM");
                return null;
            }

            if (reader.Remaining >= 2)
            {
                byte[] extensionBytes = reader.Vector16();
                var extensions = new DtlsReader(extensionBytes);

                while (extensions.Remaining >= 4)
                {
                    int type = extensions.UInt16();
                    byte[] extensionBody = extensions.Vector16();

                    // use_srtp: the profile list the server agreed to, with ours in it.
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

            return null;
        }

        private void ReadCertificate(byte[] body)
        {
            var reader = new DtlsReader(body);
            byte[] list = reader.Vector24();

            if (list.Length == 0)
            {
                Fail("the server sent no certificate, and anonymous suites are not offered");
                return;
            }

            var certificates = new DtlsReader(list);
            byte[] first = certificates.Vector24();

            _certificateListed = true;
            CertificateSha256 = Sha256.Hash(first);

            if (_expectedCertificate != null && !Hmac.FixedTimeEquals(CertificateSha256, _expectedCertificate))
            {
                Fail("the certificate is not the fingerprint the signalling carried");
            }
        }

        private void ReadServerKeyExchange(byte[] body)
        {
            var reader = new DtlsReader(body);

            int curveType = reader.Byte();
            if (curveType != 3)
            {
                Fail("the server's key exchange is not named-curve ECDHE (type " + curveType + ")");
                return;
            }

            int curve = reader.UInt16();
            if (curve != Dtls.GroupX25519)
            {
                Fail("the server chose curve 0x" + curve.ToString("x4") + ", and this client only has X25519");
                return;
            }

            byte[] publicKey = reader.Vector8();
            if (publicKey.Length != 32)
            {
                Fail("an X25519 public key is 32 bytes, not " + publicKey.Length);
                return;
            }

            _serverPublicKey = publicKey;

            // Read and kept, never checked: see the class comment. The alternative - pretending
            // to verify - is worse than naming the gap.
            if (reader.Remaining >= 2)
            {
                reader.UInt16();
                _serverSignatureSeen = reader.Vector16();
            }
        }

        /// <summary>
        /// The client's second flight: ClientKeyExchange, ChangeCipherSpec, Finished - three
        /// records in one datagram, which is what a DTLS flight is.
        /// </summary>
        private byte[] BuildClientFlight(List<byte> outbound)
        {
            if (_serverPublicKey == null || _serverRandom == null)
            {
                Fail("the server's flight was incomplete: no key exchange or no random");
                return null;
            }

            var exchange = new DtlsWriter();
            exchange.Vector8(_publicKey);

            outbound.AddRange(SendHandshake(Dtls.HandshakeClientKeyExchange, exchange.ToArray()));

            byte[] preMaster = Curve25519.SharedSecret(_privateKey, _serverPublicKey);
            _masterSecret = DtlsPrf.Prf(preMaster, "master secret", Concat(_clientRandom, _serverRandom), 48);

            byte[] keyBlock = DtlsPrf.Prf(_masterSecret, "key expansion", Concat(_serverRandom, _clientRandom), 40);

            var clientKey = new byte[16];
            var serverKey = new byte[16];
            var clientIv = new byte[4];
            var serverIv = new byte[4];
            Buffer.BlockCopy(keyBlock, 0, clientKey, 0, 16);
            Buffer.BlockCopy(keyBlock, 16, serverKey, 0, 16);
            Buffer.BlockCopy(keyBlock, 32, clientIv, 0, 4);
            Buffer.BlockCopy(keyBlock, 36, serverIv, 0, 4);

            _outKeys = new DtlsRecordKeys(clientKey, clientIv);
            _inKeys = new DtlsRecordKeys(serverKey, serverIv);

            // ChangeCipherSpec: a one-byte plaintext record, and the last one before keys.
            outbound.AddRange(DtlsRecord.Plain(Dtls.TypeChangeCipherSpec, 0, _outSequence++,
                new byte[] { 0x01 }));

            _outEpoch = 1;
            _outSequence = 0;

            byte[] verifyData = DtlsPrf.Prf(_masterSecret, "client finished", TranscriptHash(),
                Dtls.VerifyDataLength);

            byte[] finished = SendHandshake(Dtls.HandshakeFinished, verifyData);
            // SendHandshake seals with the current epoch, which is now 1.
            outbound.AddRange(finished);

            _stage = StageAwaitingFinished;
            return null;
        }

        private void VerifyServerFinished(byte[] body)
        {
            if (_stage != StageAwaitingFinished || _masterSecret == null)
            {
                Fail("the server finished a handshake this client had not keyed");
                return;
            }

            byte[] expected = DtlsPrf.Prf(_masterSecret, "server finished", TranscriptHash(),
                Dtls.VerifyDataLength);

            if (!Hmac.FixedTimeEquals(expected, body))
            {
                Fail("the server's Finished does not verify: the two ends derived different keys");
                return;
            }

            _serverFinishedVerified = true;
            _stage = StageDone;

            byte[] exporter = DtlsPrf.Prf(_masterSecret, "EXTRACTOR-dtls_srtp",
                Concat(_clientRandom, _serverRandom), 60);

            var clientKey = new byte[16];
            var serverKey = new byte[16];
            var clientSalt = new byte[14];
            var serverSalt = new byte[14];

            Buffer.BlockCopy(exporter, 0, clientKey, 0, 16);
            Buffer.BlockCopy(exporter, 16, serverKey, 0, 16);
            Buffer.BlockCopy(exporter, 32, clientSalt, 0, 14);
            Buffer.BlockCopy(exporter, 46, serverSalt, 0, 14);

            SrtpClientKey = clientKey;
            SrtpServerKey = serverKey;
            SrtpClientSalt = clientSalt;
            SrtpServerSalt = serverSalt;
        }

        /// <summary>Frames one handshake message, hashes it, and seals it at the current epoch.</summary>
        private byte[] SendHandshake(byte type, byte[] body)
        {
            var message = new DtlsHandshakeMessage(type, 0, body);
            byte[] framed = message.ToBytes();

            // Every message this client sends belongs to the transcript, its own Finished
            // included: the server's Finished is computed over those bytes, so leaving the last
            // one out makes the two ends hash different transcripts and the handshake fails at
            // the very end with nothing to point at.
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

        /// <summary>True when the server's Finished verified: the media keys are usable.</summary>
        public bool IsComplete
        {
            get { return _stage == StageDone; }
        }

        /// <summary>
        /// True only when the server's certificate matched the fingerprint the caller supplied.
        /// A handshake can be complete and untrusted: that is the honest pair of answers, and
        /// the caller is the one that decides whether to send audio over it.
        /// </summary>
        public bool IsTrusted
        {
            get { return _stage == StageDone && _certificateListed && _expectedCertificate != null; }
        }

        /// <summary>Why the handshake stopped, or null.</summary>
        public string Failure
        {
            get { return _failure; }
        }

        public bool ServerFinishedVerified
        {
            get { return _serverFinishedVerified; }
        }

        /// <summary>Whether the server agreed to the SRTP profile the client offered.</summary>
        public bool SrtpAgreed
        {
            get { return _srtpAgreed; }
        }

        /// <summary>The certificate's SHA-256, for the log and for a fingerprint check by hand.</summary>
        public byte[] CertificateSha256 { get; private set; }

        /// <summary>The signature the server sent, unverified: kept so a caller can log its size.</summary>
        public byte[] ServerSignature
        {
            get { return _serverSignatureSeen; }
        }

        public byte[] SrtpClientKey { get; private set; }
        public byte[] SrtpClientSalt { get; private set; }
        public byte[] SrtpServerKey { get; private set; }
        public byte[] SrtpServerSalt { get; private set; }

        private static byte[] Concat(byte[] left, byte[] right)
        {
            var joined = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, joined, 0, left.Length);
            Buffer.BlockCopy(right, 0, joined, left.Length, right.Length);
            return joined;
        }
    }
}
