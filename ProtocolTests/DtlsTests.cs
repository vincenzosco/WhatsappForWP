using System;
using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// DTLS 1.2 and SRTP against values this code did not produce, and against a peer written
    /// from the same primitives.
    ///
    /// What the vectors pin: the TLS 1.2 PRF, whose expected bytes were produced by Node's
    /// `crypto.createHmac('sha256')` chaining A(n) exactly as RFC 5246 says - an independent
    /// implementation of the same paragraph, so a mistake in the C# shows up as a mismatch
    /// instead of as a handshake that says nothing.
    ///
    /// What the scripted peer pins, and what it cannot: it proves the client's transcript, key
    /// schedule, record protection and exporter agree with a second implementation of the same
    /// specification. It does **not** prove interop with Chrome or with WhatsApp's engine, and
    /// this file never claims it does - no ECDSA verification happens on either side here, so
    /// both ends are trusted by construction (see `DtlsClient`).
    /// </summary>
    public static class DtlsTests
    {
        private static readonly byte[] ClientPrivateKey = Sequence(32, 0x11);
        private static readonly byte[] ClientRandom = Sequence(32, 0x60);

        // ---- vectors produced by OpenSSL through Node's crypto ----------------------------

        private const string EcdsaMessageHex =
            "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

        private const string EcdsaPointHex =
            "041b31c823a49fac40e5ab2191ac4f1465af3121490b7dc5d7b178d7739281ca51" +
            "b89d1a886a2662e6166862ca9c799256445c0654dda8573f85b75068284de5b1";

        private const string EcdsaSignatureHex =
            "3044022055dc336e9528b0bc3fabc8b7fe4541949391f6aadcf525a66d8edb10ddc1f19a" +
            "022029fc7712fba8f8b3d9f92fbc0df9165762a454cbc9cc3ea79bb710286444090d";

        /// <summary>The same P-256 key inside a certificate whose SPKI is the real one.</summary>
        private const string CertificateHex =
            "3081c13070020101300a06082a8648ce3d0403023000300030003059301306072a8648ce3d020106082a8648ce3d0301070342" +
            "00041b31c823a49fac40e5ab2191ac4f1465af3121490b7dc5d7b178d7739281ca51b89d1a886a2662e6166862ca9c7992564" +
            "45c0654dda8573f85b75068284de5b1300a06082a8648ce3d0403020341005a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a" +
            "5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a5a";

        /// <summary>The ephemeral X25519 key of the scripted peer, as Node derived it.</summary>
        private const string ServerX25519PublicHex =
            "cd700e88f9e99b19c1a8a8dcd58182fd101e5e032a69ce317fde23e8ee265c51";

        /// <summary>
        /// OpenSSL's signature over client_random || server_random || curve parameters || the
        /// peer's ephemeral public key, by the P-256 key in `CertificateHex`.
        /// </summary>
        private const string HandshakeSignatureHex =
            "3045022058c54918459782a9996d947ee3984b518fed3a80d8574d6ab15cdd54937d63e1" +
            "022100b29596d3b7bbcb0a524dbfc96e0909faafca0a587f06433b7e5fdc7fa9d83c35";

        // ---- P-256 and X.509 -------------------------------------------------------------

        [Test]
        public static void TheEcdsaVerificationMatchesOpenSsl()
        {
            byte[] message = Bytes(EcdsaMessageHex);
            byte[] signature = Bytes(EcdsaSignatureHex);
            byte[] point = Bytes(EcdsaPointHex);

            Assert.IsTrue(EcP256.Verify(message, signature, point),
                "the P-256 arithmetic verifies a signature OpenSSL produced");

            byte[] tampered = Bytes(EcdsaMessageHex);
            tampered[5] = (byte)(tampered[5] ^ 0x01);
            Assert.IsFalse(EcP256.Verify(tampered, signature, point),
                "one flipped bit and it is not that signature");

            byte[] wrong = Bytes(EcdsaSignatureHex);
            wrong[wrong.Length - 1] = (byte)(wrong[wrong.Length - 1] ^ 0x80);
            Assert.IsFalse(EcP256.Verify(message, wrong, point), "and neither is a changed s");
        }

        [Test]
        public static void APointOffTheCurveIsRefused()
        {
            byte[] point = Bytes(EcdsaPointHex);
            point[64] = (byte)(point[64] ^ 0x01);

            Assert.IsFalse(EcP256.IsOnCurve(point), "the point is no longer on the curve");
            Assert.IsFalse(EcP256.Verify(Bytes(EcdsaMessageHex), Bytes(EcdsaSignatureHex), point),
                "and a key that is not on the curve verifies nothing");
        }

        [Test]
        public static void ACertificateYieldsItsP256Point()
        {
            Assert.AreEqual(EcdsaPointHex, ToHex(X509.P256Point(Bytes(CertificateHex))),
                "the SubjectPublicKeyInfo is walked, not guessed");
            Assert.IsTrue(X509.P256Point(Sequence(200, 0x30)) == null,
                "bytes that are not a certificate are not one");
        }

        [Test]
        public static void TheFixtureKeyIsTheOneThatWasSigned()
        {
            Assert.AreEqual(ServerX25519PublicHex, ToHex(Curve25519.PublicKey(Sequence(32, 0xB1))),
                "the peer's ephemeral key is the one Node signed over");
        }

        // ---- the PRF, against Node -------------------------------------------------------

        [Test]
        public static void ThePrfMatchesAnIndependentImplementation()
        {
            byte[] master = Sequence(48, 0x01);
            byte[] seed = Sequence(64, 0x10);

            Assert.AreEqual(
                "6dc354483ff48a6ba98a83ccfdee82819b5cf48ee004c3d6933695a07f991cfc" +
                "a24211afeaa16b7b4df1abedabac32ec2af84ae03db23ce54d9fb6ff7fc13a7b" +
                "1777e343432c9868304b35f9e37f0d7d876a8cf42afda9fdf9e005f567b7aa29",
                ToHex(DtlsPrf.Prf(master, "key expansion", seed, 96)),
                "P_SHA256 for the key block, byte for byte");
        }

        [Test]
        public static void TheFinishedVectorMatches()
        {
            byte[] master = Sequence(48, 0x01);
            byte[] transcript = Sequence(32, 0x70);

            Assert.AreEqual("6fb45082855ede47f6aedde9",
                ToHex(DtlsPrf.Prf(master, "client finished", transcript, Dtls.VerifyDataLength)),
                "twelve bytes of verify_data, and the label is part of the seed");
        }

        [Test]
        public static void ADifferentLabelIsADifferentRun()
        {
            byte[] master = Sequence(48, 0x01);
            byte[] seed = Sequence(32, 0x70);

            Assert.IsFalse(
                ToHex(DtlsPrf.Prf(master, "client finished", seed, 12)) ==
                ToHex(DtlsPrf.Prf(master, "server finished", seed, 12)),
                "the two Finished values must not collide");
        }

        // ---- the record layer -------------------------------------------------------------

        [Test]
        public static void ARecordComesBackAndABitFlipIsRefused()
        {
            DtlsRecordKeys keys = Keys();
            byte[] fragment = Sequence(40, 0x20);

            byte[] record = DtlsRecord.Seal(keys, Dtls.TypeHandshake, 1, 7, fragment);

            DtlsRecordView view = DtlsRecord.Open(keys, record, record.Length);

            Assert.IsTrue(view != null, "the tag verified");
            Assert.AreEqual(fragment.Length, view.Fragment.Length, "same fragment, without the tag");
            Assert.AreEqual((byte)Dtls.TypeHandshake, view.Type, "the type is in the clear");
            Assert.AreEqual((ushort)1, view.Epoch, "and so is the epoch");
            Assert.AreEqual(7UL, view.Sequence, "and the sequence number");

            record[17] = (byte)(record[17] ^ 0x01);
            Assert.IsTrue(DtlsRecord.Open(keys, record, record.Length) == null,
                "one flipped bit is one record to drop");
        }

        [Test]
        public static void TheSequenceNumberIsAuthenticated()
        {
            DtlsRecordKeys keys = Keys();
            byte[] record = DtlsRecord.Seal(keys, Dtls.TypeHandshake, 1, 7, Sequence(16, 0x30));

            record[10] = (byte)(record[10] ^ 0x01); // the low byte of the sequence

            Assert.IsTrue(DtlsRecord.Open(keys, record, record.Length) == null,
                "the sequence number is in the nonce and in the additional data: rewriting it is detectable");
        }

        [Test]
        public static void ASealedRecordIsSixteenBytesLongerThanItsFragment()
        {
            byte[] record = DtlsRecord.Seal(Keys(), Dtls.TypeHandshake, 1, 0, Sequence(24, 0x40));

            Assert.AreEqual(Dtls.RecordHeaderLength + 24 + AesGcm.TagLength, record.Length,
                "DTLS carries no explicit nonce: the epoch and sequence are already in the header");
        }

        // ---- the handshake, against a scripted peer --------------------------------------

        [Test]
        public static void TheHandshakeFinishesAndBothEndsDeriveTheSameKeys()
        {
            var peer = new ScriptedPeer();
            var client = new DtlsClient(ClientPrivateKey, ClientRandom, peer.CertificateSha256);

            byte[] hello = client.Start();
            byte[] flight = peer.ReadClientHello(hello);

            byte[] answer = client.Receive(flight);
            Assert.IsTrue(answer != null, "the client answered the server's flight");
            Assert.IsFalse(client.IsComplete, "and is not finished until the server says so");

            byte[] closing = peer.ReadClientFlight(answer);
            Assert.IsTrue(closing != null, "the peer read the client's Finished");

            Assert.IsTrue(client.Receive(closing) == null, "nothing left to send");

            Assert.IsTrue(client.IsComplete, "the handshake completed: " + client.Failure);
            Assert.IsTrue(client.ServerFinishedVerified, "the server's Finished verified over the whole transcript");
            Assert.IsTrue(client.SrtpAgreed, "the server agreed to SRTP_AES128_CM_HMAC_SHA1_80");
            Assert.IsTrue(client.SignatureVerified,
                "the ServerKeyExchange signature verified against the certificate's P-256 key");
            Assert.IsTrue(client.IsTrusted, "and the certificate is the fingerprint the caller named");

            Assert.AreEqual(ToHex(peer.SrtpClientKey), ToHex(client.SrtpClientKey), "client SRTP key");
            Assert.AreEqual(ToHex(peer.SrtpClientSalt), ToHex(client.SrtpClientSalt), "client SRTP salt");
            Assert.AreEqual(ToHex(peer.SrtpServerKey), ToHex(client.SrtpServerKey), "server SRTP key");
            Assert.AreEqual(ToHex(peer.SrtpServerSalt), ToHex(client.SrtpServerSalt), "server SRTP salt");

            // And the keys are usable: the client seals a packet with its own master key and
            // the far end, holding the same one, reads it back.
            var sender = new SrtpSession(client.SrtpClientKey, client.SrtpClientSalt, 0x11223344);
            var receiver = new SrtpSession(peer.SrtpClientKey, peer.SrtpClientSalt, 0x11223344);

            byte[] packet = RtpPacket.Opus(1, 960, 0x11223344, Sequence(20, 0x50)).ToBytes();
            byte[] sealedPacket = sender.Seal(packet, packet.Length);
            byte[] opened = receiver.Open(sealedPacket, sealedPacket.Length);

            Assert.IsTrue(opened != null, "the media keys from the exporter open a packet");
        }

        [Test]
        public static void TheCertificateIsTrustedOnlyWhenTheFingerprintIsKnown()
        {
            var peer = new ScriptedPeer();

            var unknown = new DtlsClient(ClientPrivateKey, ClientRandom, null);
            byte[] flight = peer.ReadClientHello(unknown.Start());
            unknown.Receive(flight);

            Assert.IsTrue(unknown.CertificateSha256 != null, "the certificate was hashed");
            Assert.IsFalse(unknown.IsTrusted, "no fingerprint was supplied, so nothing is trusted");
            Assert.AreEqual(ToHex(peer.CertificateSha256), ToHex(unknown.CertificateSha256),
                "and the hash is the certificate's own");

            var stranger = new DtlsClient(ClientPrivateKey, ClientRandom, Sequence(32, 0x99));
            stranger.Receive(peer.ReadClientHello(stranger.Start()));

            Assert.IsFalse(stranger.IsComplete, "a fingerprint that does not match stops the handshake");
            Assert.IsTrue(stranger.Failure != null && stranger.Failure.IndexOf("fingerprint") >= 0,
                "and says so by name: " + stranger.Failure);
        }

        [Test]
        public static void TheExtendedMasterSecretIsUsedWhenTheServerAnswersIt()
        {
            var peer = new ScriptedPeer(true);
            var client = new DtlsClient(ClientPrivateKey, ClientRandom, peer.CertificateSha256);

            client.Receive(peer.ReadClientHello(client.Start()));

            Assert.IsTrue(client.ExtendedMasterSecret, "the server echoed extension 23");
        }

        [Test]
        public static void TheClassicMasterSecretStillFinishesWhenTheServerDoesNotOfferIt()
        {
            var peer = new ScriptedPeer(false);
            var client = new DtlsClient(ClientPrivateKey, ClientRandom, peer.CertificateSha256);

            byte[] answer = client.Receive(peer.ReadClientHello(client.Start()));
            Assert.IsTrue(answer != null, "the client answered a server that does not use EMS");

            byte[] closing = peer.ReadClientFlight(answer);
            client.Receive(closing);

            Assert.IsFalse(client.ExtendedMasterSecret, "extension 23 was not answered");
            Assert.IsTrue(client.IsComplete, "and the handshake still completes: " + client.Failure);
            Assert.AreEqual(ToHex(peer.SrtpClientKey), ToHex(client.SrtpClientKey),
                "the classic key schedule reaches the same SRTP key");
        }

        [Test]
        public static void AMalformedServerHelloIsRefusedAndNotThrown()
        {
            var client = new DtlsClient(ClientPrivateKey, ClientRandom, null);
            client.Start();

            // A ServerHello that stops after its version: a hostile or broken peer, and three
            // bytes where thirty-four are needed. The reader throws by name; the client must
            // answer with a failure, not with an exception on the call's thread.
            var body = new DtlsWriter();
            body.UInt16(Dtls.Version);
            byte[] message = new DtlsHandshakeMessage(Dtls.HandshakeServerHello, 0, body.ToArray()).ToBytes();

            byte[] answer = client.Receive(DtlsRecord.Plain(Dtls.TypeHandshake, 0, 0, message));

            Assert.IsTrue(answer == null, "nothing to send back");
            Assert.IsFalse(client.IsComplete, "and the handshake is over");
            Assert.IsTrue(client.Failure != null, "with a named failure: " + client.Failure);
        }

        [Test]
        public static void AOneByteVectorOfTwoHundredFiftySixBytesIsRefused()
        {
            var writer = new DtlsWriter();
            Assert.Throws<ProtocolException>(delegate { writer.Vector8(new byte[256]); });
        }

        [Test]
        public static void TheSignatureCoversBothRandoms()
        {
            var peer = new ScriptedPeer();

            byte[] otherRandom = Sequence(32, 0x60);
            otherRandom[0] = 0x61;

            var client = new DtlsClient(ClientPrivateKey, otherRandom, peer.CertificateSha256);
            byte[] answer = client.Receive(peer.ReadClientHello(client.Start()));

            Assert.IsTrue(answer == null, "a signature made over other randoms is not a signature for this one");
            Assert.IsTrue(client.Failure != null && client.Failure.IndexOf("signature") >= 0,
                "and it is named: " + client.Failure);
        }

        [Test]
        public static void ATamperedServerFlightIsRefusedAndNotThrown()
        {
            var peer = new ScriptedPeer();
            var client = new DtlsClient(ClientPrivateKey, ClientRandom, peer.CertificateSha256);

            byte[] flight = peer.ReadClientHello(client.Start());
            flight[flight.Length - 1] = (byte)(flight[flight.Length - 1] ^ 0x80);

            byte[] answer = client.Receive(flight);

            Assert.IsTrue(answer == null, "a broken flight produces no answer");
            Assert.IsTrue(client.Failure != null, "and a named failure: " + client.Failure);
        }

        // ---- helpers ---------------------------------------------------------------------

        private static DtlsRecordKeys Keys()
        {
            return new DtlsRecordKeys(Sequence(16, 0x01), Sequence(4, 0xA0));
        }

        private static byte[] Sequence(int count, int start)
        {
            var bytes = new byte[count];
            for (int i = 0; i < count; i++) bytes[i] = (byte)(start + i);
            return bytes;
        }

        private static byte[] Bytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes == null) return "<null>";

            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("x2"));
            return text.ToString();
        }

        /// <summary>
        /// The server half of the handshake, written from the same primitives so that the
        /// client's transcript and key schedule have something to agree with. It is a test
        /// double, not a server: no signature is produced or checked.
        /// </summary>
        private sealed class ScriptedPeer
        {
            private readonly bool _extendedMasterSecret;
            private readonly byte[] _privateKey = Sequence(32, 0xB1);
            private readonly byte[] _publicKey;
            private readonly byte[] _serverRandom = Sequence(32, 0x40);
            private readonly byte[] _certificate = Bytes(CertificateHex);
            private readonly List<byte[]> _transcript = new List<byte[]>();

            private byte[] _clientRandom;
            private byte[] _masterSecret;
            private DtlsRecordKeys _clientKeys;
            private DtlsRecordKeys _serverKeys;
            private ushort _sequence;
            private int _messageSequence;

            public ScriptedPeer()
                : this(true)
            {
            }

            public ScriptedPeer(bool extendedMasterSecret)
            {
                _extendedMasterSecret = extendedMasterSecret;
                _publicKey = Curve25519.PublicKey(_privateKey);
            }

            public byte[] CertificateSha256
            {
                get { return Sha256.Hash(_certificate); }
            }

            public byte[] SrtpClientKey { get; private set; }
            public byte[] SrtpClientSalt { get; private set; }
            public byte[] SrtpServerKey { get; private set; }
            public byte[] SrtpServerSalt { get; private set; }

            /// <summary>Reads the ClientHello and answers with the server's whole flight.</summary>
            public byte[] ReadClientHello(byte[] datagram)
            {
                DtlsRecordView view = DtlsRecord.Open(null, datagram, datagram.Length);
                byte[] body = view.Fragment;

                var hello = DtlsHandshakeMessage.Parse(body, 0, body.Length);
                _transcript.Add(hello.ToBytes());

                var reader = new DtlsReader(hello.Body);
                reader.UInt16();
                _clientRandom = reader.Bytes(32);

                return Flight();
            }

            private byte[] Flight()
            {
                var records = new List<byte[]>();

                var hello = new DtlsWriter();
                hello.UInt16(Dtls.Version);
                hello.Bytes(_serverRandom);
                hello.Byte(0);
                hello.UInt16(Dtls.SuiteEcdheEcdsaAes128GcmSha256);
                hello.Byte(0);

                var extensions = new DtlsWriter();
                var srtp = new DtlsWriter();
                srtp.UInt16(2);
                srtp.UInt16(Dtls.SrtpProfileAes128CmHmacSha1_80);
                extensions.Extension(14, srtp.ToArray());
                if (_extendedMasterSecret) extensions.Extension(23, new byte[0]);
                hello.Vector16(extensions.ToArray());

                records.Add(Record(Dtls.HandshakeServerHello, hello.ToArray()));

                var certificate = new DtlsWriter();
                var chain = new DtlsWriter();
                chain.Vector24(_certificate);
                certificate.Vector24(chain.ToArray());
                records.Add(Record(Dtls.HandshakeCertificate, certificate.ToArray()));

                var exchange = new DtlsWriter();
                exchange.Byte(3);                       // named_curve
                exchange.UInt16(Dtls.GroupX25519);
                exchange.Vector8(_publicKey);
                exchange.UInt16(0x0403);                    // ecdsa_secp256r1_sha256
                exchange.Vector16(Bytes(HandshakeSignatureHex)); // made by OpenSSL over these bytes
                records.Add(Record(Dtls.HandshakeServerKeyExchange, exchange.ToArray()));

                records.Add(Record(Dtls.HandshakeServerHelloDone, new byte[0]));

                return Join(records);
            }

            /// <summary>Reads the client's flight, checks its Finished, and closes the handshake.</summary>
            public byte[] ReadClientFlight(byte[] datagram)
            {
                int at = 0;

                while (at + Dtls.RecordHeaderLength <= datagram.Length)
                {
                    int declared = (datagram[at + 11] << 8) | datagram[at + 12];
                    int length = Dtls.RecordHeaderLength + declared;
                    if (at + length > datagram.Length) break;

                    var record = new byte[length];
                    Buffer.BlockCopy(datagram, at, record, 0, length);
                    at += length;

                    int epoch = (record[3] << 8) | record[4];

                    DtlsRecordView view = DtlsRecord.Open(epoch == 0 ? null : _clientKeys, record, length);
                    if (view == null) return null;

                    byte type = view.Type;
                    byte[] body = view.Fragment;

                    if (type != Dtls.TypeHandshake) continue;

                    var message = DtlsHandshakeMessage.Parse(body, 0, body.Length);

                    if (message.Type == Dtls.HandshakeClientKeyExchange)
                    {
                        var reader = new DtlsReader(message.Body);
                        byte[] clientPublic = reader.Vector8();

                        _transcript.Add(message.ToBytes());

                        byte[] preMaster = Curve25519.SharedSecret(_privateKey, clientPublic);
                        _masterSecret = _extendedMasterSecret
                            ? DtlsPrf.Prf(preMaster, "extended master secret",
                                Sha256.Hash(Join(_transcript)), 48)
                            : DtlsPrf.Prf(preMaster, "master secret",
                                Join(new[] { _clientRandom, _serverRandom }), 48);

                        byte[] keyBlock = DtlsPrf.Prf(_masterSecret, "key expansion",
                            Join(new[] { _serverRandom, _clientRandom }), 40);

                        _clientKeys = new DtlsRecordKeys(Slice(keyBlock, 0, 16), Slice(keyBlock, 32, 4));
                        _serverKeys = new DtlsRecordKeys(Slice(keyBlock, 16, 16), Slice(keyBlock, 36, 4));

                        Export();
                        continue;
                    }

                    if (message.Type == Dtls.HandshakeFinished)
                    {
                        byte[] expected = DtlsPrf.Prf(_masterSecret, "client finished",
                            Sha256.Hash(Join(_transcript)), Dtls.VerifyDataLength);

                        if (!Hmac.FixedTimeEquals(expected, message.Body)) return null;

                        _transcript.Add(message.ToBytes());
                    }
                }

                var outbound = new List<byte[]>();
                outbound.Add(DtlsRecord.Plain(Dtls.TypeChangeCipherSpec, 0, _sequence++, new byte[] { 0x01 }));

                byte[] verify = DtlsPrf.Prf(_masterSecret, "server finished",
                    Sha256.Hash(Join(_transcript)), Dtls.VerifyDataLength);
                byte[] finished = new DtlsHandshakeMessage(Dtls.HandshakeFinished, (ushort)_messageSequence++, verify)
                    .ToBytes();

                outbound.Add(DtlsRecord.Seal(_serverKeys, Dtls.TypeHandshake, 1, 0, finished));
                return Join(outbound);
            }

            private void Export()
            {
                byte[] exporter = DtlsPrf.Prf(_masterSecret, "EXTRACTOR-dtls_srtp",
                    Join(new[] { _clientRandom, _serverRandom }), 60);

                SrtpClientKey = Slice(exporter, 0, 16);
                SrtpServerKey = Slice(exporter, 16, 16);
                SrtpClientSalt = Slice(exporter, 32, 14);
                SrtpServerSalt = Slice(exporter, 46, 14);
            }

            private byte[] Record(byte type, byte[] body)
            {
                byte[] framed = new DtlsHandshakeMessage(type, (ushort)_messageSequence++, body).ToBytes();
                _transcript.Add(framed);

                return DtlsRecord.Plain(Dtls.TypeHandshake, 0, _sequence++, framed);
            }
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, offset, slice, 0, count);
            return slice;
        }

        private static byte[] Join(IList<byte[]> parts)
        {
            int total = 0;
            for (int i = 0; i < parts.Count; i++) total += parts[i].Length;

            var joined = new byte[total];
            int at = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                Buffer.BlockCopy(parts[i], 0, joined, at, parts[i].Length);
                at += parts[i].Length;
            }

            return joined;
        }
    }
}
