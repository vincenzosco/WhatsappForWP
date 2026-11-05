using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The server end of a DTLS handshake, checked against the client end this project already
    /// had: the two are driven at each other in memory, and what is asserted is that they finish,
    /// agree on all four pieces of key material, and that the client accepts the certificate
    /// only when the fingerprint is the one it was told to expect.
    ///
    /// Why against our own client rather than recorded flights: the client was pinned against
    /// flights from a real server (`DtlsTests`), so using it as the peer makes it the reference
    /// for the server. It is the same trade `Signal` makes between its two ends.
    /// </summary>
    public static class DtlsServerTests
    {
        private static readonly byte[] ClientEcdheKey = Fill(0x21);

        private static readonly byte[] ServerEcdheKey = Fill(0x15);

        private static readonly byte[] ClientRandom = Fill(0x31);

        private static readonly byte[] ServerRandom = Fill(0x47);

        private static readonly byte[] CertificateScalar = Fill(0x41);

        [Test]
        public static void ASignatureByThisProjectsSignerVerifiesAndADifferentMessageDoesNot()
        {
            byte[] message = Encoding("the server's key exchange");

            byte[] signature = EcP256.Sign(message, CertificateScalar);
            byte[] publicPoint = EcP256.PublicPoint(CertificateScalar);

            Assert.IsTrue(EcP256.IsOnCurve(publicPoint), "the public point has to be on the curve");

            Assert.IsTrue(EcP256.Verify(message, signature, publicPoint),
                "the signature this project makes is one it can check");

            byte[] tampered = new byte[message.Length];
            Buffer.BlockCopy(message, 0, tampered, 0, message.Length);
            tampered[0] ^= 0x01;

            Assert.IsFalse(EcP256.Verify(tampered, signature, publicPoint),
                "one byte changed is a different message");
        }

        [Test]
        public static void TheSelfSignedCertificateCarriesTheKeyItsOwnParserReads()
        {
            var identity = new DtlsIdentity(CertificateScalar, "waopus");

            Assert.ByteArraysEqual(EcP256.PublicPoint(CertificateScalar),
                X509.P256Point(identity.Certificate),
                "the certificate's key is the one the identity signs with");

            Assert.AreEqual(32, Sha256.Hash(identity.Certificate).Length,
                "a fingerprint of it is a SHA-256, which is what the signalling carries");
        }

        [Test]
        public static void AClientAndAServerFinishWithTheSameFourPiecesOfKeyMaterial()
        {
            DtlsClient client = Client(Sha256.Hash(Identity().Certificate));
            DtlsServer server = Server();

            Drive(client, server);

            Assert.AreEqual(null, client.Failure, "the client finished without a failure");
            Assert.AreEqual(null, server.Failure, "and so did the server");

            Assert.IsTrue(client.IsComplete, "the client's Finished was verified by the server");
            Assert.IsTrue(client.ServerFinishedVerified, "and the server's by the client");
            Assert.IsTrue(client.SignatureVerified, "the ServerKeyExchange signature verified");
            Assert.IsTrue(client.IsTrusted, "the certificate is the fingerprint it was given");
            Assert.IsTrue(server.IsComplete);
            Assert.IsTrue(server.ClientFinishedVerified);

            Assert.IsTrue(client.SrtpAgreed, "the suite both ends offer carries the SRTP profile");
            Assert.IsTrue(server.SrtpAgreed);

            Assert.ByteArraysEqual(client.SrtpClientKey, server.SrtpClientKey,
                "the client's key is the one the server reads with");
            Assert.ByteArraysEqual(client.SrtpClientSalt, server.SrtpClientSalt);
            Assert.ByteArraysEqual(client.SrtpServerKey, server.SrtpServerKey,
                "the server's key is the one the client reads with");
            Assert.ByteArraysEqual(client.SrtpServerSalt, server.SrtpServerSalt);

            Assert.IsTrue(client.ExtendedMasterSecret,
                "both ends offered the extended master secret, so both derived that one");
            Assert.IsTrue(server.ExtendedMasterSecret);
        }

        [Test]
        public static void AClientThatPinnedAnotherFingerprintRefusesTheCertificate()
        {
            byte[] wrong = Fill(0x77);

            DtlsClient client = Client(wrong);
            DtlsServer server = Server();

            Drive(client, server);

            Assert.IsFalse(client.IsComplete, "a certificate that is not the pinned one ends it");
            Assert.IsTrue(client.Failure != null && client.Failure.IndexOf("fingerprint") >= 0,
                "and the failure says which check failed, not that the bytes were bad");
            Assert.IsFalse(client.IsTrusted);
        }

        [Test]
        public static void ARepeatedKeyExchangeDoesNotEndTheHandshake()
        {
            DtlsClient client = Client(Sha256.Hash(Identity().Certificate));
            DtlsServer server = Server();

            byte[] flight = server.Receive(client.Start());
            byte[] clientFlight = client.Receive(flight);

            // A flight that arrives in two datagrams, with the first one duplicated: the key
            // exchange has already been hashed, so hashing it again would leave the server's
            // transcript different from the client's and the Finished would not verify.
            int firstRecord = RecordLength(clientFlight, 0);

            var exchange = new byte[firstRecord];
            Buffer.BlockCopy(clientFlight, 0, exchange, 0, firstRecord);

            var finished = new byte[clientFlight.Length - firstRecord];
            Buffer.BlockCopy(clientFlight, firstRecord, finished, 0, finished.Length);

            Assert.IsTrue(server.Receive(exchange) == null, "the key exchange alone needs no answer");
            Assert.IsTrue(server.Receive(exchange) == null, "and a repeat of it is not a new one");
            Assert.AreEqual(null, server.Failure, "the transcript is the one the client hashed");

            byte[] last = server.Receive(finished);

            Assert.AreEqual(null, server.Failure);
            Assert.IsTrue(server.IsComplete, "the client's Finished still verifies");
            Assert.IsTrue(last != null, "and the server answers with its own");
        }

        [Test]
        public static void AServerRefusesAHelloThatIsNotDtls12()
        {
            DtlsClient client = Client(Sha256.Hash(Identity().Certificate));
            DtlsServer server = Server();

            byte[] hello = client.Start();

            // The record header is thirteen bytes and the handshake header twelve, so the
            // ClientHello's version is the first field of its body.
            hello[13 + 12] = 0xfe;
            hello[13 + 12 + 1] = 0xfc;

            byte[] answer = server.Receive(hello);

            Assert.IsTrue(answer == null, "nothing is sent back to a version this server does not speak");
            Assert.IsTrue(server.Failure != null && server.Failure.IndexOf("version") >= 0);
            Assert.IsFalse(server.IsComplete);
        }

        [Test]
        public static void ARepeatedHelloGetsTheSameFlightAndDoesNotRestartTheHandshake()
        {
            DtlsClient client = Client(Sha256.Hash(Identity().Certificate));
            DtlsServer server = Server();

            byte[] hello = client.Start();

            byte[] first = server.Receive(hello);
            byte[] again = server.Receive(hello);

            Assert.IsTrue(first != null && again != null);
            Assert.ByteArraysEqual(first, again,
                "a lost flight is re-sent, not rebuilt over a different transcript");
            Assert.IsFalse(server.IsComplete, "and the handshake is still waiting for the client");
        }

        private static DtlsIdentity Identity()
        {
            return new DtlsIdentity(CertificateScalar, "waopus");
        }

        private static DtlsClient Client(byte[] fingerprint)
        {
            return new DtlsClient(ClientEcdheKey, ClientRandom, fingerprint);
        }

        private static DtlsServer Server()
        {
            return new DtlsServer(Identity(), ServerEcdheKey, ServerRandom);
        }

        /// <summary>
        /// The whole handshake, one datagram at a time, the way a socket would carry it: the
        /// client's hello, the server's flight, the client's key exchange and Finished, and the
        /// server's last flight. It stops on a failure rather than looping.
        /// </summary>
        private static void Drive(DtlsClient client, DtlsServer server)
        {
            byte[] toServer = client.Start();

            for (int i = 0; i < 8; i++)
            {
                if (client.IsComplete || client.Failure != null) return;

                byte[] toClient = server.Receive(toServer);

                if (server.Failure != null) return;
                if (toClient == null) return;

                toServer = client.Receive(toClient);

                if (toServer == null) return;
            }
        }

        /// <summary>The record that starts at `at`: its header plus the length it declares.</summary>
        private static int RecordLength(byte[] datagram, int at)
        {
            int declared = (datagram[at + 11] << 8) | datagram[at + 12];
            return 13 + declared;
        }

        private static byte[] Fill(int value)
        {
            var bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((value + i) & 0xff);
            return bytes;
        }

        private static byte[] Encoding(string text)
        {
            return System.Text.Encoding.UTF8.GetBytes(text);
        }
    }
}
