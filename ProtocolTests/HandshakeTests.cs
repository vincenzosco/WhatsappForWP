using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Noise;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    /// <summary>
    /// The handshake's two halves that are testable without a server: the protobuf
    /// messages, whose bytes are pinned, and the framing, whose split across messages
    /// is the kind of thing a single-message test never sees.
    ///
    /// The Noise arithmetic itself has no offline test here - the primitives have their
    /// vectors and `--connect` is what exercises the key schedule against WhatsApp. That
    /// gap is named in the plan rather than papered over.
    /// </summary>
    public static class HandshakeTests
    {
        [Test]
        public static void The_client_hello_pins_its_bytes()
        {
            var ephemeral = new byte[32];
            for (int i = 0; i < ephemeral.Length; i++) ephemeral[i] = (byte)i;

            byte[] message = HandshakeMessage.EncodeClientHello(ephemeral);

            // 0x12 = field 2, wire type 2 (the outer HandshakeMessage); 0x22 = the inner
            // message is 34 bytes; 0x0a = field 1, wire type 2; 0x20 = the key is 32.
            Assert.AreEqual(36, message.Length);
            Assert.AreEqual(0x12, (int)message[0]);
            Assert.AreEqual(0x22, (int)message[1]);
            Assert.AreEqual(0x0a, (int)message[2]);
            Assert.AreEqual(0x20, (int)message[3]);

            var recovered = new byte[32];
            Buffer.BlockCopy(message, 4, recovered, 0, 32);
            Assert.ByteArraysEqual(ephemeral, recovered);
        }

        [Test]
        public static void A_server_hello_is_read_back()
        {
            byte[] ephemeral = Filled(32, 0x11);
            byte[] staticKey = Filled(32, 0x22);
            byte[] payload = Filled(64, 0x33);

            byte[] inner = new ProtoWriter()
                .Bytes(1, ephemeral)
                .Bytes(2, staticKey)
                .Bytes(3, payload)
                .ToArray();

            byte[] message = new ProtoWriter().Bytes(3, inner).ToArray();

            Hello hello = HandshakeMessage.DecodeServerHello(message);
            Assert.ByteArraysEqual(ephemeral, hello.Ephemeral);
            Assert.ByteArraysEqual(staticKey, hello.Static);
            Assert.ByteArraysEqual(payload, hello.Payload);
        }

        [Test]
        public static void A_field_the_reader_does_not_know_is_stepped_over()
        {
            byte[] inner = new ProtoWriter().Bytes(1, Filled(32, 0x44)).ToArray();

            // Field 7 as a varint and field 8 as bytes: both unknown, both skippable.
            byte[] message = new ProtoWriter()
                .Varint(7, 123456)
                .Bytes(8, Filled(5, 0x55))
                .Bytes(3, inner)
                .ToArray();

            Hello hello = HandshakeMessage.DecodeServerHello(message);
            Assert.ByteArraysEqual(Filled(32, 0x44), hello.Ephemeral);
        }

        [Test]
        public static void A_message_without_a_server_hello_is_refused()
        {
            byte[] inner = new ProtoWriter().Bytes(1, Filled(32, 0x66)).ToArray();
            byte[] message = new ProtoWriter().Bytes(2, inner).ToArray();

            try
            {
                HandshakeMessage.DecodeServerHello(message);
            }
            catch (ProtocolException error)
            {
                Assert.IsTrue(error.Message.IndexOf("serverHello", StringComparison.Ordinal) >= 0);
                return;
            }

            throw new AssertionException("a clientHello-only message was accepted as a serverHello");
        }

        [Test]
        public static void The_accumulator_splits_a_frame_that_arrives_in_pieces()
        {
            var accumulator = new FrameAccumulator();

            // Two frames on the stream: a three-byte payload then a one-byte one.
            // `00 00 03 aa bb cc` `00 00 01 dd`

            // The length alone: three bytes are on their way, none of the payload is.
            Assert.AreEqual(0, accumulator.Append(new byte[] { 0x00, 0x00 }).Count);
            Assert.AreEqual(2, accumulator.PendingLength);

            // The length and two of the three payload bytes: still nothing complete, and
            // a reader that jumped the gun here would hand out a truncated frame.
            Assert.AreEqual(0, accumulator.Append(new byte[] { 0x03, 0xaa, 0xbb }).Count);
            Assert.AreEqual(5, accumulator.PendingLength);

            // The rest of the first frame AND a whole second one, in one message.
            List<byte[]> frames = accumulator.Append(new byte[] { 0xcc, 0x00, 0x00, 0x01, 0xdd });
            Assert.AreEqual(2, frames.Count);
            Assert.ByteArraysEqual(new byte[] { 0xaa, 0xbb, 0xcc }, frames[0]);
            Assert.ByteArraysEqual(new byte[] { 0xdd }, frames[1]);
            Assert.AreEqual(0, accumulator.PendingLength);
        }

        [Test]
        public static void The_intro_is_written_once_and_only_on_the_first_frame()
        {
            byte[] ephemeralPrivate = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            var noise = new NoiseHandshake(ephemeralPrivate, WhatsappApp.Protocol.Crypto.Curve25519.PublicKey(ephemeralPrivate));

            byte[] hello = noise.BuildClientHello();
            byte[] first = noise.EncodeFrame(hello);
            byte[] second = noise.EncodeFrame(hello);

            Assert.AreEqual(4, NoiseHandshake.IntroHeader.Length);
            Assert.ByteArraysEqual(NoiseHandshake.IntroHeader, new byte[] { first[0], first[1], first[2], first[3] });
            Assert.AreEqual(hello.Length, (first[4] << 16) | (first[5] << 8) | first[6]);
            Assert.AreEqual(4 + 3 + hello.Length, first.Length);

            // No intro on the second frame, so its length sits at the front.
            Assert.AreEqual(hello.Length, (second[0] << 16) | (second[1] << 8) | second[2]);
            Assert.AreEqual(3 + hello.Length, second.Length);
        }

        [Test]
        public static void A_server_certificate_reads_the_right_field_into_the_right_name()
        {
            // The leaf and intermediate details of the certificate WhatsApp's server sent
            // on 2026-10-10, copied out of the live probe. They are frozen here on
            // purpose: the field numbers of `NoiseCertificate.Details` have no readable
            // .proto, and a wrong one is invisible until a signature is verified against
            // the wrong bytes. What proved these numbers was this capture - the leaf's key
            // field came back equal to the static key the handshake had just decrypted.
            CertChain.Details leaf = CertChain.DecodeDetails(Hex(
                "08ea0210031a202638544c3739df67f8190f89a09c9c1c4d8b72e577aa1eb8f2"
                + "4cd584de16b52520d0b98bd20628d0f3a3d706"));

            Assert.AreEqual((ulong)362, leaf.Serial);
            Assert.AreEqual((ulong)3, leaf.IssuerSerial);
            // The key field, spelled out: in the live run these same 32 bytes came back
            // from the Noise handshake as the server's static key, which is what proves
            // this is the key field and not the issuer or a serial.
            Assert.AreEqual("2638544c3739df67f8190f89a09c9c1c4d8b72e577aa1eb8f24cd584de16b525",
                Assert.Hex(leaf.Key));
            Assert.AreEqual((ulong)1782766800, leaf.NotBefore);
            Assert.AreEqual((ulong)1793653200, leaf.NotAfter);

            CertChain.Details intermediate = CertChain.DecodeDetails(Hex(
                "080310001a201c51a9ac303994c6c8d0b92ea1878a533476599cc599fbea35997d9a"
                + "a90cce62208091aebe0628ffdeb7dc06"));

            Assert.AreEqual((ulong)3, intermediate.Serial);
            Assert.AreEqual((ulong)0, intermediate.IssuerSerial);
            Assert.AreEqual("1c51a9ac303994c6c8d0b92ea1878a533476599cc599fbea35997d9aa90cce62",
                Assert.Hex(intermediate.Key));
            Assert.AreEqual((ulong)1741392000, intermediate.NotBefore);
            Assert.AreEqual((ulong)1804463999, intermediate.NotAfter);

            // The two relationships the reference checks, so the fixture cannot pass with
            // the names swapped between the two certs either.
            Assert.AreEqual(intermediate.Serial, leaf.IssuerSerial);
            Assert.AreEqual(0, (int)intermediate.IssuerSerial);
            Assert.IsTrue(leaf.NotBefore < leaf.NotAfter);
            Assert.IsTrue(intermediate.NotBefore < intermediate.NotAfter);
        }

        private static byte[] Filled(int length, byte value)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = value;
            return bytes;
        }

        private static byte[] Hex(string text)
        {
            var bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(2 * i, 2), 16);
            return bytes;
        }
    }
}
