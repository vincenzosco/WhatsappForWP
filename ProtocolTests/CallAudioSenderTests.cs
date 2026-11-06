using System.Collections.Generic;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The clock that turns the microphone's bytes into a call's frames, tested where the
    /// phone cannot be: what it must do is a rule about 640 bytes at a time, not about audio.
    /// </summary>
    public static class CallAudioSenderTests
    {
        /// <summary>What the encoder and the path saw, in order.</summary>
        private sealed class Recorder
        {
            public readonly List<short[]> Frames = new List<short[]>();
            public readonly List<byte[]> Packets = new List<byte[]>();

            /// <summary>False makes every packet refused, the way an untrusted handshake does.</summary>
            public bool Accept = true;

            /// <summary>How many frames the encoder refuses, from the front.</summary>
            public int RefuseFrames;

            public CallAudioSender Sender()
            {
                return new CallAudioSender(Encode, Send);
            }

            private byte[] Encode(short[] samples, int count)
            {
                if (Frames.Count < RefuseFrames)
                {
                    Frames.Add(null);
                    return null;
                }

                var copy = new short[count];
                System.Array.Copy(samples, copy, count);
                Frames.Add(copy);

                return new byte[] { 0x0F, (byte)count };
            }

            private bool Send(byte[] packet)
            {
                Packets.Add(packet);
                return Accept;
            }
        }

        /// <summary>640 bytes of PCM whose sample `i` is `value + i`, so a frame's order is checkable.</summary>
        private static byte[] Pcm(int samples, int first)
        {
            var bytes = new byte[samples * 2];

            for (int i = 0; i < samples; i++)
            {
                int value = first + i;
                bytes[i * 2] = (byte)(value & 0xff);
                bytes[i * 2 + 1] = (byte)((value >> 8) & 0xff);
            }

            return bytes;
        }

        [Test]
        public static void OneFrameIsThreeHundredAndTwentySamples()
        {
            var recorder = new Recorder();
            CallAudioSender sender = recorder.Sender();

            int frames = sender.Push(Pcm(320, 0), 640);

            Assert.AreEqual(1, frames, "640 bytes of PCM is one 20 ms frame");
            Assert.AreEqual(1, sender.Frames, "one frame encoded");
            Assert.AreEqual(1, sender.Sent, "one packet taken by the path");
            Assert.AreEqual(320, recorder.Frames[0].Length, "the encoder sees 320 samples");
            Assert.AreEqual(0, sender.Held, "nothing is left over");
        }

        [Test]
        public static void TheSamplesAreReadLittleEndian()
        {
            var recorder = new Recorder();
            CallAudioSender sender = recorder.Sender();

            // One sample of 0x7FFF and the rest of the frame at zero.
            var pcm = new byte[640];
            pcm[0] = 0xff;
            pcm[1] = 0x7f;

            sender.Push(pcm, pcm.Length);

            Assert.AreEqual((short)32767, recorder.Frames[0][0], "the first sample is 0x7fff");
            Assert.AreEqual((short)0, recorder.Frames[0][1], "the second is the silence that followed");
        }

        [Test]
        public static void AStreamThatArrivesInOtherSizesIsCarriedOver()
        {
            var recorder = new Recorder();
            CallAudioSender sender = recorder.Sender();

            Assert.AreEqual(1, sender.Push(Pcm(500, 0), 1000), "1000 bytes hold one whole frame");
            Assert.AreEqual(360, sender.Held, "the rest of the second frame is held");

            Assert.AreEqual(1, sender.Push(Pcm(140, 500), 280), "280 bytes complete it");
            Assert.AreEqual(0, sender.Held, "and nothing is left");
            Assert.AreEqual(2, sender.Frames, "two frames went out");
        }

        [Test]
        public static void TheTwoHalvesOfASampleCanArriveApart()
        {
            var recorder = new Recorder();
            CallAudioSender sender = recorder.Sender();

            byte[] pcm = Pcm(320, 0);

            // The last sample of the frame is bytes 638 and 639: stopping at 639 holds its
            // low half back, and the high half arrives on its own.
            Assert.AreEqual(0, sender.Push(pcm, 639), "639 bytes are one byte short of a frame");
            Assert.AreEqual(639, sender.Held, "every byte is kept, the odd one included");

            var rest = new byte[1];
            rest[0] = pcm[639];

            Assert.AreEqual(1, sender.Push(rest, 1), "the frame completes");

            Assert.AreEqual((short)319, recorder.Frames[0][319], "the last sample survived the split");
            Assert.AreEqual(0, sender.Held, "no byte was dropped to make the frame");
        }

        [Test]
        public static void MutedSendsSilenceAtTheSameRate()
        {
            var recorder = new Recorder();
            CallAudioSender sender = recorder.Sender();

            sender.Muted = true;

            var loud = new byte[640];
            for (int i = 0; i < loud.Length; i++) loud[i] = 0x7f;

            Assert.AreEqual(1, sender.Push(loud, loud.Length), "a muted call still sends a frame");
            Assert.AreEqual((short)0, recorder.Frames[0][0], "the samples sent are zero");
            Assert.AreEqual(1, sender.Sent, "and the packet went out");
        }

        [Test]
        public static void APacketThePathRefusesIsNotQueued()
        {
            var recorder = new Recorder();
            recorder.Accept = false;

            CallAudioSender sender = recorder.Sender();

            sender.Push(Pcm(640, 0), 1280);

            Assert.AreEqual(2, sender.Refused, "both packets were refused");
            Assert.AreEqual(0, sender.Sent, "none was taken");

            recorder.Accept = true;

            Assert.AreEqual(1, sender.Push(Pcm(320, 0), 640), "the next frame goes out normally");
            Assert.AreEqual(1, sender.Sent, "and is not held back by the two before it");
        }

        [Test]
        public static void AnEncoderThatRefusesIsCountedAndTheStreamContinues()
        {
            var recorder = new Recorder();
            recorder.RefuseFrames = 1;

            CallAudioSender sender = recorder.Sender();

            Assert.AreEqual(1, sender.Push(Pcm(320, 0), 640), "the frame was consumed");
            Assert.AreEqual(1, sender.Dropped, "and counted as dropped");
            Assert.AreEqual(0, sender.Sent, "nothing was sent for it");

            sender.Push(Pcm(320, 0), 640);

            Assert.AreEqual(1, sender.Sent, "the frame after it is sent");
            Assert.AreEqual(0, sender.Held, "the stream kept its place");
        }

        [Test]
        public static void OneSecondIsFiftyFrames()
        {
            var recorder = new Recorder();
            CallAudioSender sender = recorder.Sender();

            sender.Push(Pcm(320 * 50, 0), 640 * 50);

            Assert.AreEqual(50, sender.Frames, "fifty frames");
            Assert.AreEqual(50 * CallAudioSender.FrameMilliseconds, 1000, "are one second");
        }

        [Test]
        public static void ResetForgetsTheHalfFrame()
        {
            var recorder = new Recorder();
            CallAudioSender sender = recorder.Sender();

            sender.Push(Pcm(100, 0), 200);
            Assert.AreEqual(200, sender.Held, "half a frame is held");

            sender.Reset();

            Assert.AreEqual(0, sender.Held, "a new call starts on a frame boundary");
        }
    }
}
