using System.Collections.Generic;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The clock that turns arriving packets into what the speaker plays, tested where the
    /// phone cannot be: a tick is a rule about 640 bytes, not about a sound card.
    /// </summary>
    public static class CallAudioPlayoutTests
    {
        /// <summary>The source of frames and the decoder, as the playout sees them.</summary>
        private sealed class Source
        {
            /// <summary>What `Take` answers, in order. A null entry is a tick with nothing due.</summary>
            public readonly Queue<byte[]> Packets = new Queue<byte[]>();

            public int Skips;
            public int Decoded;
            public readonly List<byte[]> Taken = new List<byte[]>();

            /// <summary>
            /// A packet is missing in front of the queue, the way a jitter buffer holds the ones
            /// behind a lost sequence: `Take` answers nothing until the playout gives up on it.
            /// </summary>
            public bool GapWaiting;

            /// <summary>Whether a packet is already queued behind the missing one: the hole cannot fill.</summary>
            public bool Behind;

            /// <summary>How many packets the decoder refuses, from the front.</summary>
            public int RefusePackets;

            /// <summary>What a decoded packet is worth in bytes, when it is not a whole frame.</summary>
            public int DecodedBytes = CallAudioPlayout.FrameBytes;

            public CallAudioPlayout Playout()
            {
                return new CallAudioPlayout(Decode, Take, SkipGap);
            }

            /// <summary>The same, told whether a packet is already queued behind the hole.</summary>
            public CallAudioPlayout PlayoutWithBehind()
            {
                return new CallAudioPlayout(Decode, Take, SkipGap, delegate { return Behind; });
            }

            private byte[] Take()
            {
                if (GapWaiting) return null;
                if (Packets.Count == 0) return null;

                return Packets.Dequeue();
            }

            private bool SkipGap()
            {
                Skips++;

                // Giving up on the gap moves the buffer on, as the jitter buffer does: from
                // here the packet that was behind the missing one is the next to play.
                if (!GapWaiting) return false;

                GapWaiting = false;
                return true;
            }

            private byte[] Decode(byte[] packet)
            {
                if (Decoded < RefusePackets)
                {
                    Decoded++;
                    return null;
                }

                Decoded++;

                var pcm = new byte[DecodedBytes];
                for (int i = 0; i < pcm.Length; i++) pcm[i] = packet[0];

                return pcm;
            }
        }

        private static byte[] Packet(byte value)
        {
            return new byte[] { value };
        }

        /// <summary>
        /// One tick is twenty milliseconds whatever the buffer is doing: 640 bytes of sixteen-bit
        /// mono at 16 kHz, which is what `MediaStreamSource` is told each sample lasts.
        /// </summary>
        [Test]
        public static void EveryTickIsTwentyMillisecondsOfPcm()
        {
            var source = new Source();
            CallAudioPlayout playout = source.Playout();

            for (int i = 0; i < 10; i++)
            {
                byte[] frame = playout.Tick();

                Assert.AreEqual(CallAudioPlayout.FrameBytes, frame.Length,
                    "tick " + i + " is one whole frame");
            }

            Assert.AreEqual(10, playout.Played, "ten ticks played");
            Assert.AreEqual(10 * CallAudioPlayout.FrameMilliseconds, 200, "which is 200 ms");
        }

        /// <summary>The frames arrive in the order they were spoken.</summary>
        [Test]
        public static void ThePacketsArePlayedInTheOrderTheyAreDue()
        {
            var source = new Source();
            source.Packets.Enqueue(Packet(1));
            source.Packets.Enqueue(Packet(2));
            source.Packets.Enqueue(Packet(3));

            CallAudioPlayout playout = source.Playout();

            Assert.AreEqual((byte)1, playout.Tick()[0], "the first packet plays first");
            Assert.AreEqual((byte)2, playout.Tick()[0], "then the second");
            Assert.AreEqual((byte)3, playout.Tick()[0], "then the third");
            Assert.AreEqual(0, playout.Waited, "nothing had to be waited for");
        }

        /// <summary>
        /// A packet that has not arrived is not a lost one: the tick plays silence, and the
        /// gap is only called lost once the three ticks of grace are spent.
        /// </summary>
        [Test]
        public static void AGapIsWaitedForThreeTicksAndThenCalledLost()
        {
            var source = new Source();
            source.GapWaiting = true;
            source.Packets.Enqueue(Packet(7));

            CallAudioPlayout playout = source.Playout();

            for (int i = 0; i < CallAudioPlayout.MissesBeforeGap; i++)
            {
                byte[] frame = playout.Tick();

                Assert.AreEqual(CallAudioPlayout.FrameBytes, frame.Length, "silence is a frame");
                Assert.AreEqual((byte)0, frame[0], "and it is silent");
                Assert.AreEqual(i + 1, playout.Misses, "the wait is counted");
            }

            Assert.AreEqual(0, source.Skips, "the gap was not yet called lost");
            Assert.AreEqual(CallAudioPlayout.MissesBeforeGap, playout.Waited,
                "every tick of the wait carried the silence the speaker is fed");

            byte[] played = playout.Tick();

            Assert.AreEqual(1, source.Skips, "the fourth tick calls the gap lost");
            Assert.AreEqual(1, playout.Skipped, "and it is counted");
            Assert.AreEqual((byte)7, played[0], "the packet behind the gap plays");
        }

        /// <summary>
        /// A hole with a packet already queued behind it cannot fill: the packets after it
        /// arrived first. The playout gives up on the first tick instead of the third, which is
        /// the difference between a lost packet costing twenty milliseconds and sixty - and the
        /// sixty would be silence played over audio already in hand.
        /// </summary>
        [Test]
        public static void AGapWithPacketsBehindItIsCalledLostAtOnce()
        {
            var source = new Source();
            source.GapWaiting = true;
            source.Behind = true;
            source.Packets.Enqueue(Packet(8));

            CallAudioPlayout playout = source.PlayoutWithBehind();

            byte[] played = playout.Tick();

            Assert.AreEqual(1, source.Skips, "the first tick gives up on the hole");
            Assert.AreEqual(1, playout.Skipped, "and it is counted");
            Assert.AreEqual((byte)8, played[0], "the packet behind it plays at once");
            Assert.AreEqual(0, playout.Waited, "no silence is played over audio already in hand");
            Assert.AreEqual(1, playout.Played, "one tick, one frame");
        }

        /// <summary>
        /// A packet the decoder refuses is one tick of silence: the clock of a call does not
        /// stop for one packet, and the next one is played normally.
        /// </summary>
        [Test]
        public static void APacketThatDoesNotDecodeIsSilenceAndTheClockGoesOn()
        {
            var source = new Source();
            source.RefusePackets = 1;
            source.Packets.Enqueue(Packet(4));
            source.Packets.Enqueue(Packet(5));

            CallAudioPlayout playout = source.Playout();

            byte[] first = playout.Tick();

            Assert.AreEqual(CallAudioPlayout.FrameBytes, first.Length, "a refused packet is a frame");
            Assert.AreEqual((byte)0, first[0], "of silence");
            Assert.AreEqual(1, playout.Undecodable, "and it is counted");

            Assert.AreEqual((byte)5, playout.Tick()[0], "the packet after it plays");
        }

        /// <summary>
        /// A decoder that answers less than twenty milliseconds is padded instead of shortening
        /// the tick: the speaker is fed a fixed size and the stream never changes shape.
        /// </summary>
        [Test]
        public static void AShortFrameIsPaddedToTwentyMilliseconds()
        {
            var source = new Source();
            source.DecodedBytes = 100;
            source.Packets.Enqueue(Packet(9));

            CallAudioPlayout playout = source.Playout();

            byte[] frame = playout.Tick();

            Assert.AreEqual(CallAudioPlayout.FrameBytes, frame.Length, "the frame is still whole");
            Assert.AreEqual((byte)9, frame[0], "what the decoder gave is played");
            Assert.AreEqual((byte)0, frame[100], "and the rest of the tick is silence");
            Assert.AreEqual(1, playout.Padded, "the padding is counted");
        }

        /// <summary>
        /// Nothing at all is a call that has not started speaking yet, and it plays silence
        /// rather than ending the speaker's stream.
        /// </summary>
        [Test]
        public static void NothingArrivingPlaysSilence()
        {
            var source = new Source();
            CallAudioPlayout playout = source.Playout();

            for (int i = 0; i < 20; i++) playout.Tick();

            Assert.AreEqual(20, playout.Played, "every tick carried a frame");
            Assert.AreEqual(20, playout.Waited, "all of them silence");
            Assert.AreEqual(0, playout.Undecodable, "no packet was refused, because none arrived");
        }

        /// <summary>A new call starts on a clean clock, with no wait owed to the last one.</summary>
        [Test]
        public static void ResetForgetsTheWait()
        {
            var source = new Source();
            CallAudioPlayout playout = source.Playout();

            playout.Tick();
            playout.Tick();
            Assert.AreEqual(2, playout.Misses, "two ticks are waiting");

            playout.Reset();

            Assert.AreEqual(0, playout.Misses, "a new call waits for nothing");
            Assert.AreEqual(0, playout.Played, "and its counters start again");
        }
    }
}
