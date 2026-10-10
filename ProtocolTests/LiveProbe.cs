using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Noise;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    /// <summary>
    /// The one thing here that is not offline: it opens the real endpoint and runs the
    /// handshake against WhatsApp.
    ///
    /// The first version of it asked for a `<stream:features>` node and got four bytes
    /// that are not a node - which is what moved this milestone onto the protobuf
    /// `HandshakeMessage` and the Noise handshake. This version sends the client hello
    /// the way the reference does, reads the reply, and decrypts it.
    ///
    /// The checkpoint it checks is: **the server's static key and certificate chain
    /// decrypt under keys derived from our own X25519, HKDF and AES-GCM.** If they do,
    /// the primitives and the key schedule are right against the party that matters.
    /// The certificate signatures are not verified yet - that is XEdDSA and it is
    /// deferred on purpose (see the plan).
    ///
    /// The random keys come from the platform here, which is allowed: only the core
    /// under `WhatsappApp/Protocol` must avoid `System.Security.Cryptography`, because
    /// the WP8.1 projection has none.
    ///
    /// Run with: `node tools/test-protocol.js --connect`.
    /// </summary>
    public static class LiveProbe
    {
        private const int TimeoutMilliseconds = 20000;

        public static int Run()
        {
            var transport = new DesktopTransport(TimeoutMilliseconds);

            try
            {
                Console.WriteLine("connecting to " + WhatsAppSocket.DefaultUrl);
                transport.ConnectAsync(WhatsAppSocket.DefaultUrl).Wait();
                Console.WriteLine("connected");

                byte[] ephemeralPrivate = RandomKey();
                byte[] ephemeralPublic = Curve25519.PublicKey(ephemeralPrivate);
                byte[] staticPrivate = RandomKey();
                byte[] staticPublic = Curve25519.PublicKey(staticPrivate);

                var noise = new NoiseHandshake(ephemeralPrivate, ephemeralPublic);

                byte[] hello = noise.BuildClientHello();
                byte[] helloFrame = noise.EncodeFrame(hello);
                Console.WriteLine("sending clientHello: " + hello.Length
                    + " bytes of protobuf, " + helloFrame.Length + " on the wire");
                transport.SendAsync(helloFrame).Wait();

                var accumulator = new FrameAccumulator();
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(TimeoutMilliseconds);
                bool handshaken = false;
                int messages = 0;

                while (!handshaken && DateTime.UtcNow < deadline)
                {
                    byte[] raw = transport.ReceiveAsync().Result;
                    if (raw == null)
                    {
                        Console.WriteLine("the server closed the connection");
                        break;
                    }

                    messages++;
                    Console.WriteLine("message " + messages + ": " + raw.Length + " bytes: " + Hex(raw, 24));

                    // The server writes each handshake message as its own frame (a
                    // 3-byte length and then that many bytes) inside one WebSocket
                    // message. A message that is exactly one frame is handled on its
                    // own; anything else goes through the accumulator, so a frame split
                    // across messages is still read.
                    bool exactlyOneFrame = raw.Length >= 3
                        && ((raw[0] << 16) | (raw[1] << 8) | raw[2]) == raw.Length - 3;

                    if (exactlyOneFrame || accumulator.PendingLength > 0)
                    {
                        List<byte[]> frames = accumulator.Append(raw);
                        if (frames.Count == 0)
                        {
                            Console.WriteLine("  (no complete frame yet, "
                                + accumulator.PendingLength + " byte(s) pending)");
                        }

                        for (int i = 0; i < frames.Count && !handshaken; i++)
                        {
                            handshaken = Report(noise, frames[i], staticPrivate, staticPublic);
                        }

                        continue;
                    }

                    // What the server sends before it has a hello from us, seen in both
                    // runs: `88 02 03 f3`. Read as a frame those first three bytes would
                    // announce a 8.9 MB message, which no handshake message is, so it is
                    // not a frame fragment either. It is reported and set aside, and it
                    // stays an open question in the plan: nothing is claimed about what
                    // it means, only that it cannot be the head of a frame.
                    Console.WriteLine("  UNCLASSIFIED: " + raw.Length
                        + " bytes that are not a frame and are not parsed: " + Hex(raw, 8));
                }

                if (!handshaken)
                {
                    Console.WriteLine();
                    Console.WriteLine("PROBE FAILED: no decodable handshake message arrived"
                        + " (messages: " + messages + ", pending: " + accumulator.PendingLength + " bytes)");
                    return 1;
                }

                return 0;
            }
            catch (Exception error)
            {
                Console.WriteLine("PROBE FAILED: " + error.GetType().Name + ": " + error.Message);

                Exception cause = error.InnerException;
                if (cause != null) Console.WriteLine("  caused by: " + cause.GetType().Name + ": " + cause.Message);

                return 1;
            }
            finally
            {
                transport.Close();
            }
        }

        /// <summary>Decrypts one frame as the server's half of the handshake.</summary>
        private static bool Report(NoiseHandshake noise, byte[] frame, byte[] staticPrivate, byte[] staticPublic)
        {
            Console.WriteLine("  frame of " + frame.Length + " bytes: " + Hex(frame, 24));
            if (frame.Length == 0) return false;

            // No compression flag here: that byte belongs to a WABinary node, and this
            // is a handshake frame, which is raw protobuf. The first probe applied the
            // node rule to it and called a serverHello "compressed".
            ServerHandshake handshake;
            try
            {
                handshake = noise.ProcessServerHello(frame, staticPrivate, staticPublic);
            }
            catch (ProtocolException error)
            {
                Console.WriteLine("  not a serverHello: " + error.Message);
                return false;
            }

            Console.WriteLine();
            Console.WriteLine("decrypted WhatsApp's handshake with our own keys");
            Console.WriteLine("  server static key: " + Hex(handshake.StaticKey, 32));
            Console.WriteLine("  certificate leaf: " + Describe(handshake.Certificates.Leaf));
            Console.WriteLine("  certificate intermediate: " + Describe(handshake.Certificates.Intermediate));

            // What the reference checks, minus the signatures (XEdDSA is deferred):
            // the leaf vouches for exactly the key the handshake produced, the leaf was
            // issued by the intermediate, and the intermediate in turn by the root whose
            // serial is zero. These four are what pin the certificate field numbers, and
            // they are checked against the server rather than against a fixture.
            if (handshake.Certificates.Leaf == null || handshake.Certificates.Intermediate == null)
            {
                Console.WriteLine("PROBE FAILED: the certificate chain is missing a half");
                return false;
            }

            CertChain.Details leaf = CertChain.DecodeDetails(handshake.Certificates.Leaf.Details);
            CertChain.Details intermediate = CertChain.DecodeDetails(handshake.Certificates.Intermediate.Details);

            Console.WriteLine("  leaf serial " + leaf.Serial + ", issued by " + leaf.IssuerSerial
                + ", valid " + leaf.NotBefore + ".." + leaf.NotAfter);
            Console.WriteLine("  intermediate serial " + intermediate.Serial
                + ", issued by " + intermediate.IssuerSerial
                + ", valid " + intermediate.NotBefore + ".." + intermediate.NotAfter);
            Console.WriteLine("  intermediate key: " + Hex(intermediate.Key, 8) + "...");

            // The whole chain, so a failure can be reproduced offline as a fixture
            // instead of by asking the server again.
            Console.WriteLine("  leaf details: " + Hex(handshake.Certificates.Leaf.Details, 128));
            Console.WriteLine("  leaf signature: " + Hex(handshake.Certificates.Leaf.Signature, 128));
            Console.WriteLine("  intermediate details: " + Hex(handshake.Certificates.Intermediate.Details, 128));
            Console.WriteLine("  intermediate signature: " + Hex(handshake.Certificates.Intermediate.Signature, 128));

            // And the check that makes the handshake authenticated: the signatures, the
            // issuer serials, the key the leaf names and both validity windows.
            try
            {
                CertificateVerifier.Verify(handshake.Certificates, handshake.StaticKey, DateTime.UtcNow);
            }
            catch (ProtocolException error)
            {
                Console.WriteLine("PROBE FAILED: the certificate chain did not verify: " + error.Message);
                return false;
            }

            if (!SameBytes(leaf.Key, handshake.StaticKey))
            {
                Console.WriteLine("PROBE FAILED: the leaf does not vouch for the decrypted static key");
                return false;
            }

            if (leaf.IssuerSerial != intermediate.Serial)
            {
                Console.WriteLine("PROBE FAILED: the leaf's issuer serial is not the intermediate's");
                return false;
            }

            if (intermediate.IssuerSerial != RootIssuerSerial)
            {
                Console.WriteLine("PROBE FAILED: the intermediate was issued by " + intermediate.IssuerSerial
                    + ", not by the root (" + RootIssuerSerial + ")");
                return false;
            }

            if (intermediate.Key == null || intermediate.Key.Length != 32)
            {
                Console.WriteLine("PROBE FAILED: the intermediate's key is not 32 bytes");
                return false;
            }

            Console.WriteLine();
            Console.WriteLine("PROBE OK: WhatsApp's certificate chain VERIFIED - the handshake is"
                + " authenticated, and the static key the certificate names is the one we decrypted");
            return true;
        }

        /// <summary>The root certificate's serial, from the reference's constant.</summary>
        private const ulong RootIssuerSerial = 0;

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }

        private static string Describe(CertChain.Certificate certificate)
        {
            if (certificate == null) return "absent";

            return "details " + (certificate.Details == null ? 0 : certificate.Details.Length)
                + " bytes, signature " + (certificate.Signature == null ? 0 : certificate.Signature.Length) + " bytes";
        }

        /// <summary>A fresh private key, from the platform: the core cannot do this.</summary>
        private static byte[] RandomKey()
        {
            var key = new byte[32];
            var generator = new System.Security.Cryptography.RNGCryptoServiceProvider();
            try
            {
                generator.GetBytes(key);
            }
            finally
            {
                generator.Dispose();
            }

            return key;
        }

        private static string Hex(byte[] bytes, int count)
        {
            if (bytes == null) return "(none)";

            var text = new StringBuilder();
            int take = bytes.Length < count ? bytes.Length : count;

            for (int i = 0; i < take; i++) text.Append(bytes[i].ToString("x2")).Append(' ');
            return text.ToString();
        }
    }
}
