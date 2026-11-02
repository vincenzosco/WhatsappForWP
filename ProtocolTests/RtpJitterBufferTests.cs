using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The playout order of a voice call: what arrives, what comes out, and what is thrown
    /// away.
    ///
    /// The cases that matter are the ones a real socket produces and a loopback test never
    /// does - packets overtaking each other, a duplicate from a relay, one that arrives
    /// after its slot was played, and the sequence number wrapping from 65535 to 0.
    /// </summary>
    public static class RtpJitterBufferTests
    {
        private const uint Ssrc = 0x0BADF00D;

        [Test]
        public static void NothingIsReadyUntilThePreRollIsFull()
        {
            var buffer = new RtpJitterBuffer(3);

            Assert.IsTrue(buffer.Push(Packet(0)), "the first packet is kept");
            Assert.IsTrue(buffer.Take() == null, "one packet is not enough to start: it is the wait");
            Assert.IsTrue(buffer.Push(Packet(1)), "the second is kept");
            Assert.IsTrue(buffer.Take() == null, "two are still filling");
            Assert.IsTrue(buffer.Push(Packet(2)), "the third");

            Assert.AreEqual(0, buffer.Take().SequenceNumber, "the first three release in order");
            Assert.AreEqual(1, buffer.Take().SequenceNumber);
            Assert.AreEqual(2, buffer.Take().SequenceNumber);
            Assert.IsTrue(buffer.Take() == null, "and then the buffer is empty");
        }

        [Test]
        public static void PacketsComeOutInOrderWhateverOrderTheyArrive()
        {
            var buffer = new RtpJitterBuffer(1);

            Assert.IsTrue(buffer.Push(Packet(0)));
            Assert.IsTrue(buffer.Push(Packet(2)), "two overtook one");
            Assert.IsTrue(buffer.Push(Packet(1)), "one arrived after two");

            Assert.AreEqual(0, buffer.Take().SequenceNumber);
            Assert.AreEqual(1, buffer.Take().SequenceNumber, "the reordered packet is played in its place");
            Assert.AreEqual(2, buffer.Take().SequenceNumber);
        }

        [Test]
        public static void ADuplicateIsDroppedAndPlayedOnce()
        {
            var buffer = new RtpJitterBuffer(1);

            Assert.IsTrue(buffer.Push(Packet(0)));
            Assert.IsTrue(buffer.Push(Packet(1)));
            Assert.IsFalse(buffer.Push(Packet(1)), "the relay delivered it twice");
            Assert.AreEqual(1, buffer.Dropped, "the copy is counted as dropped");

            Assert.AreEqual(0, buffer.Take().SequenceNumber);
            Assert.AreEqual(1, buffer.Take().SequenceNumber);
            Assert.IsTrue(buffer.Take() == null, "and the duplicate is not played a second time");
        }

        [Test]
        public static void APacketOlderThanThePlayoutIsDropped()
        {
            var buffer = new RtpJitterBuffer(1);

            buffer.Push(Packet(0));
            Assert.AreEqual(0, buffer.Take().SequenceNumber, "the playout is now waiting on one");

            Assert.IsFalse(buffer.Push(Packet(0)), "a packet for audio already heard is not deliverable");
            Assert.AreEqual(1, buffer.Dropped);

            Assert.IsTrue(buffer.Push(Packet(1)), "the one it wants is still accepted");
            Assert.AreEqual(1, buffer.Take().SequenceNumber);
        }

        [Test]
        public static void TheSequenceWrapsFromTheLastToTheFirst()
        {
            var buffer = new RtpJitterBuffer(1);

            Assert.IsTrue(buffer.Push(Packet(65534)));
            Assert.IsTrue(buffer.Push(Packet(65535)));
            Assert.IsTrue(buffer.Push(Packet(0)), "zero after 65535 is the next packet, not an old one");
            Assert.IsTrue(buffer.Push(Packet(1)));

            Assert.AreEqual(0, buffer.Dropped, "the wrap is not a hole");

            Assert.AreEqual(65534, buffer.Take().SequenceNumber);
            Assert.AreEqual(65535, buffer.Take().SequenceNumber);
            Assert.AreEqual(0, buffer.Take().SequenceNumber, "the call continues across the wrap");
            Assert.AreEqual(1, buffer.Take().SequenceNumber);
        }

        [Test]
        public static void APacketFromBeforeTheWrapIsLateAfterIt()
        {
            var buffer = new RtpJitterBuffer(1);

            buffer.Push(Packet(0));
            buffer.Push(Packet(1));
            buffer.Take();
            buffer.Take();

            Assert.IsFalse(buffer.Push(Packet(65535)), "65535 after zero is the packet from before it");
            Assert.AreEqual(1, buffer.Dropped);
        }

        [Test]
        public static void SkipCountsAGapAsLostAndTheNextPacketFollows()
        {
            var buffer = new RtpJitterBuffer(1);

            buffer.Push(Packet(0));
            Assert.AreEqual(0, buffer.Take().SequenceNumber);

            buffer.Push(Packet(2));
            Assert.IsTrue(buffer.Take() == null, "the one at the head of the wait is missing");

            Assert.IsTrue(buffer.Skip(), "the playout gave up on it");
            Assert.AreEqual(1, buffer.Lost, "one packet lost");

            Assert.AreEqual(2, buffer.Take().SequenceNumber, "and what was held behind it plays");
        }

        [Test]
        public static void SkippingWithNothingMissingDoesNothing()
        {
            var buffer = new RtpJitterBuffer(1);

            Assert.IsFalse(buffer.Skip(), "nothing held, nothing to skip");

            buffer.Push(Packet(0));
            Assert.IsFalse(buffer.Skip(), "the packet the playout wants is here: this is a Take");
            Assert.AreEqual(0, buffer.Lost);
            Assert.AreEqual(0, buffer.Take().SequenceNumber);
        }

        [Test]
        public static void ANullPacketIsRefused()
        {
            var buffer = new RtpJitterBuffer(1);

            Assert.IsFalse(buffer.Push(null));
            Assert.AreEqual(0, buffer.Pending);
        }

        [Test]
        public static void APrerollOfZeroIsABugNotABuffer()
        {
            Assert.Throws<ProtocolException>(delegate { new RtpJitterBuffer(0); });
        }

        [Test]
        public static void ResetForgetsTheCallIncludingItsCounters()
        {
            var buffer = new RtpJitterBuffer(1);

            buffer.Push(Packet(0));
            buffer.Push(Packet(0));
            Assert.AreEqual(1, buffer.Dropped);

            buffer.Reset();

            Assert.AreEqual(0, buffer.Pending);
            Assert.AreEqual(0, buffer.Dropped);
            Assert.AreEqual(0, buffer.Lost);
            Assert.IsTrue(buffer.Push(Packet(0)), "the next call starts again from wherever it starts");
            Assert.AreEqual(0, buffer.Take().SequenceNumber);
        }

        /// <summary>One packet with its sequence number carried in the payload, so a test can see which it got.</summary>
        private static RtpPacket Packet(int sequence)
        {
            byte[] payload = new byte[] { (byte)(sequence & 0xff) };

            return RtpPacket.Opus(sequence, (uint)(sequence * RtpPacket.OpusFrameTicks), Ssrc, payload);
        }
    }
}
