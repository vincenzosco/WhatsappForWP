using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The RTP header, both ways.
    ///
    /// What is pinned: the twelve bytes and their meaning, the Opus clock, and the three
    /// things a naive reader gets wrong - the CSRC list, the extension word and the padding
    /// byte, each of which moves where the payload starts. A packet whose payload starts four
    /// bytes early plays four bytes of header as sound.
    /// </summary>
    public static class RtpTests
    {
        private const uint Ssrc = 0xDEADBEEF;

        [Test]
        public static void APacketRoundTripsItsHeaderAndPayload()
        {
            byte[] payload = new byte[] { 1, 2, 3, 4, 5 };
            byte[] bytes = RtpPacket.Opus(513, 96000, Ssrc, payload).ToBytes();

            RtpPacket read = RtpPacket.FromBytes(bytes, bytes.Length);

            Assert.AreEqual(RtpPacket.OpusPayloadType, read.PayloadType);
            Assert.AreEqual(513, read.SequenceNumber);
            Assert.AreEqual(96000u, read.Timestamp);
            Assert.AreEqual(Ssrc, read.Ssrc);
            Assert.AreEqual(payload.Length, read.Payload.Length);
            Assert.AreEqual(5, (int)read.Payload[4]);
        }

        [Test]
        public static void TheHeaderIsVersionTwoAndTwelveBytesLong()
        {
            byte[] bytes = RtpPacket.Opus(1, 0, Ssrc, new byte[] { 9 }).ToBytes();

            Assert.AreEqual(13, bytes.Length, "twelve of header, one of payload");
            Assert.AreEqual(0x80, (int)bytes[0], "version 2, no padding, no extension, no CSRC");
            Assert.AreEqual(RtpPacket.OpusPayloadType, (int)bytes[1], "the payload type, marker clear");
        }

        [Test]
        public static void TheOpusClockMovesNineHundredSixtyForAFrame()
        {
            // 48 kHz at 20 ms. At the encoder's 16 kHz the frame is 320 samples, and a
            // packet built with that number plays the audio a third too fast.
            Assert.AreEqual(960, RtpPacket.OpusFrameTicks);
            Assert.AreEqual(48000, RtpPacket.OpusClockRate);
        }

        [Test]
        public static void TheSequenceNumberWrapsInsteadOfGrowing()
        {
            byte[] bytes = RtpPacket.Opus(70000, 0, Ssrc, new byte[0]).ToBytes();
            RtpPacket read = RtpPacket.FromBytes(bytes, bytes.Length);

            Assert.AreEqual(70000 & 0xffff, read.SequenceNumber, "sixteen bits, as the header has");
        }

        [Test]
        public static void ACsrcListMovesWhereThePayloadStarts()
        {
            byte[] payload = new byte[] { 7, 7 };
            byte[] plain = RtpPacket.Opus(1, 0, Ssrc, payload).ToBytes();

            // The same packet with one CSRC: CC=1, four bytes of it, then the payload.
            var withCsrc = new byte[plain.Length + 4];
            withCsrc[0] = 0x81;
            System.Buffer.BlockCopy(plain, 1, withCsrc, 1, plain.Length - 1);
            System.Buffer.BlockCopy(plain, 12, withCsrc, 16, payload.Length);

            RtpPacket read = RtpPacket.FromBytes(withCsrc, withCsrc.Length);

            Assert.IsTrue(read != null, "a packet with a CSRC list is readable");
            Assert.AreEqual(2, read.Payload.Length, "the CSRC is not payload");
            Assert.AreEqual(7, (int)read.Payload[0]);
        }

        [Test]
        public static void AHeaderExtensionMovesItToo()
        {
            byte[] payload = new byte[] { 4, 4, 4 };
            byte[] plain = RtpPacket.Opus(1, 0, Ssrc, payload).ToBytes();

            // X=1, one 32-bit word of extension after four bytes of its own header.
            var withExtension = new byte[plain.Length + 8];
            withExtension[0] = 0x90;
            System.Buffer.BlockCopy(plain, 1, withExtension, 1, plain.Length - 1);
            withExtension[14] = 0x00;
            withExtension[15] = 0x01;
            System.Buffer.BlockCopy(plain, 12, withExtension, 20, payload.Length);

            RtpPacket read = RtpPacket.FromBytes(withExtension, withExtension.Length);

            Assert.AreEqual(3, read.Payload.Length);
            Assert.AreEqual(4, (int)read.Payload[0]);
        }

        [Test]
        public static void APaddingByteIsCountedOffTheEnd()
        {
            byte[] payload = new byte[] { 5, 5 };
            byte[] plain = RtpPacket.Opus(1, 0, Ssrc, payload).ToBytes();

            // P=1, then two bytes of padding whose last byte says how many.
            var padded = new byte[plain.Length + 2];
            padded[0] = 0xa0;
            System.Buffer.BlockCopy(plain, 1, padded, 1, plain.Length - 1);
            padded[plain.Length] = 0x00;
            padded[plain.Length + 1] = 0x02;

            RtpPacket read = RtpPacket.FromBytes(padded, padded.Length);

            Assert.AreEqual(2, read.Payload.Length, "the padding is not payload");
        }

        [Test]
        public static void WhatIsNotAVersionTwoPacketIsRefused()
        {
            byte[] bytes = RtpPacket.Opus(1, 0, Ssrc, new byte[] { 1 }).ToBytes();
            bytes[0] = 0x40;

            Assert.IsTrue(RtpPacket.FromBytes(bytes, bytes.Length) == null, "version 1 is not this packet");
            Assert.IsTrue(RtpPacket.FromBytes(new byte[4], 4) == null, "and eleven bytes is not a header");
            Assert.IsTrue(RtpPacket.FromBytes(null, 0) == null, "no bytes, no packet");
        }

        [Test]
        public static void BuildingAPacketWithNoPayloadIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { RtpPacket.Opus(1, 0, Ssrc, null); });
        }
    }
}
