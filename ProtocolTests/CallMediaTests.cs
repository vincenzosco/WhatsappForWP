using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The call's audio path: two sessions that share their key material the way two phones
    /// do, and a frame that goes out of one and comes back through the other.
    ///
    /// Why the two sessions are built here and not with a helper of the production code: the
    /// shape of the constructor - one direction in, one out - is the thing being pinned. A
    /// helper that built both from one key would hide exactly the bug this file is for, a
    /// sender and a receiver agreeing on a key they should not share.
    /// </summary>
    public static class CallMediaTests
    {
        private const uint LocalSsrc = 0x01020304;
        private const uint RemoteSsrc = 0x0A0B0C0D;

        private static byte[] Key(byte seed)
        {
            var key = new byte[16];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
            return key;
        }

        private static byte[] Salt(byte seed)
        {
            var salt = new byte[14];
            for (int i = 0; i < salt.Length; i++) salt[i] = (byte)(seed + i);
            return salt;
        }

        private static byte[] Frame(byte value)
        {
            // A real Opus frame is not needed: nothing on the path looks inside it.
            return new byte[] { value, 0x01, 0x02, 0x03, 0x04 };
        }

        /// <summary>This phone: sends with its own key, receives with the other's.</summary>
        private static CallMediaSession Phone()
        {
            return new CallMediaSession(Key(1), Salt(1), LocalSsrc,
                                        Key(0x40), Salt(0x40), RemoteSsrc);
        }

        /// <summary>The other side: the same two keys, the other way round.</summary>
        private static CallMediaSession Peer()
        {
            return new CallMediaSession(Key(0x40), Salt(0x40), RemoteSsrc,
                                        Key(1), Salt(1), LocalSsrc);
        }

        /// <summary>
        /// The frame that goes out comes back, byte for byte, and the jitter buffer had to
        /// wait for its pre-roll before it gave anything back - which is the behaviour a live
        /// call depends on and a burst test would hide.
        /// </summary>
        [Test]
        public static void AFrameSurvivesTheRoundTrip()
        {
            CallMediaSession phone = Phone();
            CallMediaSession peer = Peer();

            byte[] first = phone.Seal(Frame(0x11));
            Assert.IsTrue(first != null, "one sealed packet");
            Assert.AreEqual(2, (first[0] >> 6) & 0x03, "the RTP header is still readable under SRTP");

            Assert.IsTrue(peer.Receive(first, first.Length), "the tag verified and the packet was kept");
            Assert.IsTrue(peer.Take() == null, "one packet is not a pre-roll");
        }

        /// <summary>
        /// Three frames out, three payloads back, in the order they were spoken - which is the
        /// whole point of the pre-roll and the one thing a jitter buffer can lose silently.
        /// </summary>
        [Test]
        public static void ThreeFramesComeBackInTheOrderTheyWereSent()
        {
            CallMediaSession phone = Phone();
            CallMediaSession peer = Peer();

            List<byte[]> sent = new List<byte[]>();
            for (int i = 0; i < 3; i++)
            {
                byte[] frame = Frame((byte)(0x10 + i));
                sent.Add(frame);

                byte[] packet = phone.Seal(frame);
                Assert.IsTrue(peer.Receive(packet, packet.Length), "packet " + i + " was accepted");
            }

            for (int i = 0; i < 3; i++)
            {
                byte[] played = peer.Take();
                Assert.IsTrue(played != null, "frame " + i + " was released");
                Assert.AreEqual(sent[i].Length, played.Length, "frame " + i + " keeps its length");

                for (int b = 0; b < played.Length; b++)
                {
                    Assert.AreEqual(sent[i][b], played[b], "frame " + i + " byte " + b);
                }
            }

            Assert.IsTrue(peer.Take() == null, "and there is nothing else to play");
            Assert.AreEqual(3, peer.Received, "three packets arrived");
            Assert.AreEqual(3, phone.Sent, "three were sent");
        }

        /// <summary>
        /// The clock: Opus over RTP counts at 48 kHz whatever the encoder's own rate, so a
        /// 20 ms frame moves the timestamp by 960 and not by 320.
        /// </summary>
        [Test]
        public static void TheTimestampMovesTwentyMillisecondsPerFrame()
        {
            CallMediaSession phone = Phone();

            Assert.AreEqual(0, (int)phone.NextTimestamp, "a call starts its own clock");

            phone.Seal(Frame(1));
            Assert.AreEqual(RtpPacket.OpusFrameTicks, (int)phone.NextTimestamp,
                "one frame is 20 ms at 48 kHz");
            Assert.AreEqual(1, phone.NextSequence, "and one sequence number");

            phone.Seal(Frame(2));
            Assert.AreEqual(RtpPacket.OpusFrameTicks * 2, (int)phone.NextTimestamp);
            Assert.AreEqual(2, phone.NextSequence);
        }

        /// <summary>A packet that concatenates two frames moves the clock by both of them.</summary>
        [Test]
        public static void ALongerPacketAdvancesTheClockByItsAudio()
        {
            CallMediaSession phone = Phone();
            phone.Seal(Frame(1), 40);

            Assert.AreEqual(RtpPacket.OpusFrameTicks * 2, (int)phone.NextTimestamp,
                "40 ms of audio is two frames of ticks");
        }

        /// <summary>
        /// One flipped byte is one dropped packet: the tag is checked before anything is
        /// decrypted, and the playout never sees it.
        /// </summary>
        [Test]
        public static void ATamperedPacketIsRefusedAndCounted()
        {
            CallMediaSession phone = Phone();
            CallMediaSession peer = Peer();

            byte[] packet = phone.Seal(Frame(1));
            packet[packet.Length - 3] = (byte)(packet[packet.Length - 3] ^ 0x80);

            Assert.IsFalse(peer.Receive(packet, packet.Length), "a broken tag is not accepted");
            Assert.AreEqual(1, peer.Refused, "and it is counted");
            Assert.AreEqual(0, peer.Received, "nothing was received");
            Assert.AreEqual(0, peer.Pending, "nothing reached the playout");
        }

        /// <summary>
        /// A gap the playout gives up on is counted as lost and what is behind it plays: the
        /// alternative is a call that waits forever for one packet.
        /// </summary>
        [Test]
        public static void AGapIsLostWhenThePlayoutSaysSo()
        {
            CallMediaSession phone = Phone();
            CallMediaSession peer = Peer();

            byte[] zero = phone.Seal(Frame(0));
            byte[] one = phone.Seal(Frame(1));

            // The third frame is dropped on the way: the sequence arrives 0, 1, 3.
            phone.Seal(Frame(2));          // this is the one that never arrives
            byte[] three = phone.Seal(Frame(3));

            Assert.IsTrue(peer.Receive(zero, zero.Length));
            Assert.IsTrue(peer.Receive(one, one.Length));
            Assert.IsTrue(peer.Receive(three, three.Length));

            Assert.IsTrue(peer.Take() != null, "frame 0 plays");
            Assert.IsTrue(peer.Take() != null, "frame 1 plays");
            Assert.IsTrue(peer.Take() == null, "frame 2 never arrived, so nothing is next");

            Assert.IsTrue(peer.SkipGap(), "the playout calls the gap lost");
            Assert.AreEqual(1, peer.Lost, "one packet was lost");

            byte[] after = peer.Take();
            Assert.IsTrue(after != null, "frame 3 plays after the gap");
            Assert.AreEqual((byte)3, after[0], "and it is the one that arrived");
        }

        /// <summary>
        /// A peer that used another key cannot even be read: the call is two sessions, and the
        /// wrong one produces a tag that does not verify.
        /// </summary>
        [Test]
        public static void AnotherKeyCannotSpeakIntoThisCall()
        {
            CallMediaSession phone = Phone();
            var stranger = new CallMediaSession(Key(0x70), Salt(0x70), LocalSsrc,
                                                Key(0x80), Salt(0x80), RemoteSsrc);

            byte[] packet = stranger.Seal(Frame(9));

            Assert.IsFalse(phone.Receive(packet, packet.Length), "a stranger's packet is refused");
            Assert.AreEqual(1, phone.Refused);
        }

        /// <summary>
        /// The two threads of a real call: the socket hands a packet in while the player asks
        /// for the next one. The jitter buffer is an ordered list, and a reader inside it while
        /// the socket inserts is a list that moves under the reader - so the receiving half is
        /// one thread at a time, and every packet still reaches the player.
        /// </summary>
        [Test]
        public static void ReceiveAndTakeFromTwoThreadsKeepTheirPlace()
        {
            CallMediaSession phone = Phone();
            CallMediaSession peer = Peer();

            const int Count = 200;
            var packets = new List<byte[]>();
            for (int i = 0; i < Count; i++) packets.Add(phone.Seal(Frame((byte)(i % 251))));

            int played = 0;
            System.Exception failure = null;

            var producer = new System.Threading.Thread(delegate()
            {
                try
                {
                    for (int i = 0; i < packets.Count; i++)
                    {
                        peer.Receive(packets[i], packets[i].Length);
                    }
                }
                catch (System.Exception ex)
                {
                    failure = ex;
                }
            });

            producer.Start();

            try
            {
                // Bounded: a playout that never releases is a failure to report, not a test
                // that hangs.
                for (int attempt = 0; attempt < 200000 && played < Count; attempt++)
                {
                    if (peer.Take() != null) played++;
                    else System.Threading.Thread.Sleep(0);
                }
            }
            catch (System.Exception ex)
            {
                failure = ex;
            }

            producer.Join();

            Assert.IsTrue(failure == null, failure == null ? "no thread threw" : failure.ToString());
            Assert.AreEqual(Count, peer.Received, "every packet arrived");
            Assert.AreEqual(Count, played, "and every one of them was played");
        }

        /// <summary>The two directions are not the same key, so a reflected packet is refused.</summary>
        [Test]
        public static void AReflectedPacketIsRefused()
        {
            CallMediaSession phone = Phone();

            byte[] packet = phone.Seal(Frame(5));

            Assert.IsFalse(phone.Receive(packet, packet.Length),
                "a phone must not be able to read its own outgoing stream");
        }
    }
}
