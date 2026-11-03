using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// A DTLS 1.2 client for one media channel: starts the handshake, answers the server, and
    /// ends with the SRTP keys the audio is encrypted with.
    ///
    /// Only ECDHE_ECDSA with AES-128-GCM, because its ServerKeyExchange signature is checked
    /// with `EcP256`: an RSA suite would complete a handshake that proves nothing, and a CBC
    /// suite is refused by name. Fragments are reassembled in sequence order and a cookie
    /// request is answered. No resumption, no renegotiation, no retransmission timers.
    ///
    /// `IsTrusted` needs the signature *and* the fingerprint the caller supplies: the signature
    /// says the peer owns its key, the fingerprint says which key is the right one.
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
        private readonly DtlsHandshakeReassembler _reassembler = new DtlsHandshakeReassembler();

        private byte[] _cookie;
        private byte[] _serverRandom;
        private byte[] _serverPublicKey;
        private byte[] _certificate;
        private byte[] _masterSecret;
        private byte[] _serverSignatureSeen;

        private DtlsRecordKeys _outKeys;
        private DtlsRecordKeys _inKeys;

        private ushort _outEpoch;
        private ushort _inEpoch;
        private ulong _outSequence;
        private ulong _inSequence;

        private int _stage;
        private ushort _outHandshakeSequence;
        private bool _helloVerifyAnswered;
        private bool _certificateListed;
        private bool _srtpAgreed;
        private bool _extendedMasterSecret;
        private bool _signatureVerified;
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

            return BuildClientHello();
        }

        /// <summary>
        /// The ClientHello, built twice in a handshake: once with no cookie, and once more with
        /// the one a HelloVerifyRequest demanded. The random is kept, so the ServerKeyExchange
        /// signature is over the same bytes on both attempts.
        /// </summary>
        private byte[] BuildClientHello()
        {
            _outHandshakeSequence = 0;

            var hello = new DtlsWriter();
            hello.UInt16(Dtls.Version);
            hello.Bytes(_clientRandom);
            hello.Byte(0); // session id: none, no resumption
            hello.Vector8(_cookie); // none on the first hello, the demanded one after
            // One suite, and not two: an RSA suite would complete the handshake and leave its
            // ServerKeyExchange signature unverifiable here (no RSA in this project), which is a
            // handshake that proves nothing. Offering only what can be authenticated is the
            // difference between "the peer has the key" and "the peer sent bytes".
            hello.UInt16(2);
            hello.UInt16(Dtls.SuiteEcdheEcdsaAes128GcmSha256);
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
                    // A fragment, not necessarily a message: a certificate can arrive in pieces,
                    // in any order, in any number of records. `DtlsHandshakeFragment.Parse`
                    // answers null when the record ends inside a fragment, which is a truncated
                    // datagram and not a reason to end the handshake by itself.
                    DtlsHandshakeFragment piece;
                    try
                    {
                        piece = DtlsHandshakeFragment.Parse(fragment, offset, fragment.Length - offset);
                    }
                    catch (ProtocolException ex)
                    {
                        Fail(ex.Message);
                        return null;
                    }

                    if (piece == null) break;
                    offset += piece.FramedLength;

                    try
                    {
                        _reassembler.Accept(piece);
                    }
                    catch (ProtocolException ex)
                    {
                        Fail(ex.Message);
                        return null;
                    }

                    // Only whole messages, and only in sequence: a fragment that completes
                    // nothing hands over nothing, and a message that completes early waits.
                    DtlsHandshakeMessage message;
                    while ((message = _reassembler.TakeNext()) != null)
                    {
                        // A message this client cannot parse is a peer this client cannot talk
                        // to. The reader throws by name ("a handshake message ended before its
                        // own field"), and letting that escape would turn a hostile datagram into
                        // an exception on the call's thread - so it stops the handshake instead.
                        byte[] next;
                        try
                        {
                            next = Handle(message, outbound);
                        }
                        catch (ProtocolException ex)
                        {
                            Fail(ex.Message);
                            return null;
                        }

                        if (next != null) outbound.AddRange(next);
                        if (_stage == StageFailed) return null;
                    }
                }
            }

            return outbound.Count == 0 ? null : outbound.ToArray();
        }

        private byte[] Handle(DtlsHandshakeMessage message, List<byte> outbound)
        {
            // The Finished is the one message whose own bytes are not in its hash, and a
            // HelloVerifyRequest belongs to the handshake that is thrown away, not to the one
            // that continues.
            if (message.Type != Dtls.HandshakeFinished && message.Type != Dtls.HandshakeHelloVerifyRequest)
            {
                _transcript.Add(message.ToBytes());
            }

            switch (message.Type)
            {
                case Dtls.HandshakeHelloVerifyRequest:
                    return ReadHelloVerifyRequest(message.Body);

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

        /// <summary>
        /// The cookie exchange: the server asks, without doing any work, whether this address is
        /// real. The answer is the same ClientHello with the cookie in it, and a handshake that
        /// starts again from nothing - the transcript did not begin until the server could see
        /// the client.
        /// </summary>
        private byte[] ReadHelloVerifyRequest(byte[] body)
        {
            if (_cookie != null)
            {
                Fail("the server asked for a cookie twice");
                return null;
            }

            if (_serverRandom != null)
            {
                Fail("the server asked for a cookie in the middle of a handshake");
                return null;
            }

            var reader = new DtlsReader(body);

            int version = reader.UInt16();
            if (version != Dtls.Version)
            {
                Fail("the HelloVerifyRequest speaks version 0x" + version.ToString("x4") + ", not DTLS 1.2");
                return null;
            }

            byte[] cookie = reader.Vector8();
            if (cookie.Length == 0)
            {
                Fail("the HelloVerifyRequest carries no cookie");
                return null;
            }

            _cookie = cookie;
            _helloVerifyAnswered = true;

            // A cookie starts a new handshake: the transcript goes, the message numbering goes,
            // and so does everything the discarded flight had said. The record sequence numbers
            // stay - they count per epoch, not per handshake (RFC 6347, section 4.2.2).
            _transcript.Clear();
            _reassembler.Reset();
            _serverRandom = null;
            _serverPublicKey = null;
            _certificate = null;
            _certificateListed = false;
            CertificateSha256 = null;
            _srtpAgreed = false;
            _extendedMasterSecret = false;
            _signatureVerified = false;
            _inEpoch = 0;
            _inSequence = 0;

            return BuildClientHello();
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

            if (suite != Dtls.SuiteEcdheEcdsaAes128GcmSha256)
            {
                Fail("the server chose suite 0x" + suite.ToString("x4")
                    + ", and this client only authenticates ECDHE_ECDSA with AES-128-GCM");
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

                    // extended_master_secret: the server says it will derive the master secret
                    // from the handshake and not from the two randoms (RFC 7627). Offering it
                    // and then ignoring the answer is the worst of both - the two ends derive
                    // different keys and fail at the Finished with nothing to point at - so the
                    // answer is read here and used in BuildClientFlight.
                    if (type == 23) _extendedMasterSecret = true;

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
            _certificate = first;
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

            // What the signature covers: both randoms and the parameters above, byte for byte.
            // The length is taken before the signature fields are read, so nothing after the
            // public key can be slid into the signed run.
            int signedLength = reader.Position;

            if (reader.Remaining < 4)
            {
                Fail("the ServerKeyExchange has no signature");
                return;
            }

            reader.UInt16(); // the signature algorithm, which the suite already fixes here
            _serverSignatureSeen = reader.Vector16();

            if (_certificate == null)
            {
                Fail("the ServerKeyExchange arrived before any certificate to check it against");
                return;
            }

            byte[] publicKeyOfSigner = X509.P256Point(_certificate);
            if (publicKeyOfSigner == null)
            {
                Fail("the certificate carries no P-256 key, and only that one can be checked here");
                return;
            }

            var signed = new byte[Dtls.RandomLength * 2 + signedLength];
            Buffer.BlockCopy(_clientRandom, 0, signed, 0, Dtls.RandomLength);
            Buffer.BlockCopy(_serverRandom, 0, signed, Dtls.RandomLength, Dtls.RandomLength);
            Buffer.BlockCopy(body, 0, signed, Dtls.RandomLength * 2, signedLength);

            _signatureVerified = EcP256.Verify(signed, _serverSignatureSeen, publicKeyOfSigner);

            if (!_signatureVerified)
            {
                Fail("the ServerKeyExchange signature does not verify against the certificate");
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

            _masterSecret = _extendedMasterSecret
                ? DtlsPrf.Prf(preMaster, "extended master secret", TranscriptHash(), 48)
                : DtlsPrf.Prf(preMaster, "master secret", Concat(_clientRandom, _serverRandom), 48);

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
            // The client's own messages are numbered too, from zero, and a server reassembling
            // them drops a message that repeats a number it has already seen.
            var message = new DtlsHandshakeMessage(type, _outHandshakeSequence++, body);
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
        /// Whether the server proved it holds the key in its certificate: the ServerKeyExchange
        /// signature verified against the P-256 key inside it.
        /// </summary>
        public bool SignatureVerified
        {
            get { return _signatureVerified; }
        }

        /// <summary>
        /// True only when the handshake finished, the signature verified **and** the certificate
        /// is the one the caller named by fingerprint. A handshake can be complete, signed and
        /// untrusted: that is the honest set of answers, and the caller decides whether to send
        /// audio over it.
        /// </summary>
        public bool IsTrusted
        {
            get
            {
                return _stage == StageDone && _certificateListed && _signatureVerified
                    && _expectedCertificate != null;
            }
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

        /// <summary>Whether the server asked for a cookie and got it: one more round trip.</summary>
        public bool HelloVerifyAnswered
        {
            get { return _helloVerifyAnswered; }
        }

        /// <summary>The cookie the server demanded, or null. Kept for the log and for a test.</summary>
        public byte[] Cookie
        {
            get { return _cookie; }
        }

        /// <summary>Whether the server agreed to the SRTP profile the client offered.</summary>
        public bool SrtpAgreed
        {
            get { return _srtpAgreed; }
        }

        /// <summary>
        /// Whether the server answered the extended_master_secret extension, which decides how
        /// the master secret was derived. Not a detail: the two forms give different keys, so a
        /// client that offers the extension and ignores the answer fails every handshake against
        /// a server that accepts it.
        /// </summary>
        public bool ExtendedMasterSecret
        {
            get { return _extendedMasterSecret; }
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
