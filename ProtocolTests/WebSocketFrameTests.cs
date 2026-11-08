using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Transport;

namespace ProtocolTests
{
    public static class WebSocketFrameTests
    {
        [Test]
        public static void ClientFramesMaskTheirPayloadAndDecodeToTheOriginalBytes()
        {
            byte[] payload = { 0x01, 0x02, 0x03 };
            byte[] mask = { 0x37, 0xfa, 0x21, 0x3d };
            byte[] expected = { 0x82, 0x83, 0x37, 0xfa, 0x21, 0x3d, 0x36, 0xf8, 0x22 };

            byte[] encoded = WebSocketFrameCodec.EncodeClient(2, payload, mask);
            Assert.ByteArraysEqual(expected, encoded);

            WebSocketFrame frame = WebSocketFrameCodec.Decode(encoded);
            Assert.AreEqual((byte)2, frame.Opcode);
            Assert.IsTrue(frame.IsFinal);
            Assert.ByteArraysEqual(payload, frame.Payload);
        }

        [Test]
        public static void ExtendedPayloadLengthsRoundTrip()
        {
            byte[] mask = { 1, 2, 3, 4 };
            byte[] shortExtended = new byte[126];
            byte[] longExtended = new byte[65536];
            shortExtended[125] = 0x7e;
            longExtended[65535] = 0x7f;

            byte[] encoded126 = WebSocketFrameCodec.EncodeClient(2, shortExtended, mask);
            byte[] encoded127 = WebSocketFrameCodec.EncodeClient(2, longExtended, mask);

            Assert.AreEqual(126, encoded126[1] & 0x7f);
            Assert.AreEqual(127, encoded127[1] & 0x7f);
            Assert.AreEqual(encoded126.Length, WebSocketFrameCodec.GetFrameLength(encoded126, 0, encoded126.Length));
            Assert.AreEqual(encoded127.Length, WebSocketFrameCodec.GetFrameLength(encoded127, 0, encoded127.Length));
            Assert.ByteArraysEqual(shortExtended, WebSocketFrameCodec.Decode(encoded126).Payload);
            Assert.ByteArraysEqual(longExtended, WebSocketFrameCodec.Decode(encoded127).Payload);
        }

        [Test]
        public static void FrameLengthWaitsForTheWholeHeader()
        {
            byte[] prefix = { 0x82, 0xfe, 0x00, 0x7e, 1, 2, 3 };

            Assert.AreEqual(-1, WebSocketFrameCodec.GetFrameLength(prefix, 0, 1));
            Assert.AreEqual(-1, WebSocketFrameCodec.GetFrameLength(prefix, 0, 5));
            Assert.AreEqual(-1, WebSocketFrameCodec.GetFrameLength(prefix, 0, 7));
        }

        [Test]
        public static void ReservedBitsAndInvalidControlFramesAreRefused()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketFrameCodec.Decode(new byte[] { 0xc2, 0x00 });
            });
            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketFrameCodec.Decode(new byte[] { 0x09, 0x00 });
            });
            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketFrameCodec.EncodeClient(9, new byte[126], new byte[4]);
            });
        }

        [Test]
        public static void NonCanonicalAndUnboundedLengthsAreRefused()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketFrameCodec.Decode(new byte[] { 0x82, 126, 0, 125 });
            });

            byte[] highBitLength = { 0x82, 127, 0x80, 0, 0, 0, 0, 0, 0, 0 };
            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketFrameCodec.GetFrameLength(highBitLength, 0, highBitLength.Length);
            });
        }

        [Test]
        public static void FragmentedBinaryMessagesReassembleAroundControlFrames()
        {
            var assembler = new WebSocketMessageAssembler();
            WebSocketFrame first = ServerFrame(2, false, new byte[] { 1, 2 });
            WebSocketFrame ping = ServerFrame(9, true, new byte[] { 0x55 });
            WebSocketFrame last = ServerFrame(0, true, new byte[] { 3, 4 });

            Assert.IsTrue(assembler.Accept(first) == null);
            Assert.IsTrue(assembler.Accept(ping) == null);
            Assert.ByteArraysEqual(new byte[] { 1, 2, 3, 4 }, assembler.Accept(last));
        }

        [Test]
        public static void ContinuationsAndNestedMessagesAreValidated()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                new WebSocketMessageAssembler().Accept(ServerFrame(0, true, new byte[] { 1 }));
            });

            var assembler = new WebSocketMessageAssembler();
            assembler.Accept(ServerFrame(2, false, new byte[] { 1 }));
            Assert.Throws<ProtocolException>(delegate
            {
                assembler.Accept(ServerFrame(2, true, new byte[] { 2 }));
            });
        }

        [Test]
        public static void TextProtocolMessagesAreNotReturnedAsBinaryNodes()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                new WebSocketMessageAssembler().Accept(ServerFrame(1, true, new byte[] { 0x41 }));
            });
        }

        private static WebSocketFrame ServerFrame(byte opcode, bool isFinal, byte[] payload)
        {
            var frame = new byte[2 + payload.Length];
            frame[0] = (byte)((isFinal ? 0x80 : 0) | opcode);
            frame[1] = (byte)payload.Length;
            Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
            return WebSocketFrameCodec.Decode(frame);
        }
    }
}
