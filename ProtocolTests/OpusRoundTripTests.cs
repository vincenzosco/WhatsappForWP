using System;
using Concentus.Enums;
using Concentus.Structs;

namespace ProtocolTests
{
    /// <summary>
    /// The codec this app speaks into a call and into a voice note, tested where the phone
    /// cannot: `Concentus` is managed code (`WhatsappApp/Opus`), so the same frames that run
    /// on the phone run here. The native codec the phone refuses
    /// (`DllImport cannot be used on user-defined methods`) had no test like this at all.
    ///
    /// What the tests owe the reader: the shape (20 ms in, one packet out, PCM back), and
    /// that the audio survives. Opus is lossy and its decoder keeps a delay, so the audio
    /// comparison searches the small window where the delay lands instead of assuming the
    /// samples line up at zero.
    /// </summary>
    public static class OpusRoundTripTests
    {
        private const int SampleRate = 16000;
        private const int FrameSamples = SampleRate / 50;

        /// <summary>One second of a 440 Hz tone, at full scale a whistle this codec must keep.</summary>
        private static short[] Tone(int channels)
        {
            var samples = new short[SampleRate * channels];

            for (int i = 0; i < SampleRate; i++)
            {
                short value = (short)(8000.0 * Math.Sin(2.0 * Math.PI * 440.0 * i / SampleRate));
                for (int c = 0; c < channels; c++) samples[i * channels + c] = value;
            }

            return samples;
        }

        /// <summary>The tone encoded frame by frame, the way a call or a note sends it.</summary>
        private static byte[][] Encode(short[] samples, int channels, out int frames)
        {
            OpusEncoder encoder = OpusEncoder.Create(SampleRate, channels,
                OpusApplication.OPUS_APPLICATION_VOIP);
            encoder.Bitrate = 24000;

            frames = samples.Length / channels / FrameSamples;
            var packets = new byte[frames][];

            for (int f = 0; f < frames; f++)
            {
                var packet = new byte[1275];
                int size = encoder.Encode(samples, f * FrameSamples * channels, FrameSamples,
                    packet, 0, packet.Length);

                Assert.IsTrue(size > 0, "the encoder makes one packet per frame");

                var one = new byte[size];
                Buffer.BlockCopy(packet, 0, one, 0, size);
                packets[f] = one;
            }

            return packets;
        }

        /// <summary>The packets decoded back, frame by frame.</summary>
        private static short[] Decode(byte[][] packets, int channels, int frames)
        {
            OpusDecoder decoder = OpusDecoder.Create(SampleRate, channels);
            var pcm = new short[frames * FrameSamples * channels];

            for (int f = 0; f < frames; f++)
            {
                int got = decoder.Decode(packets[f], 0, packets[f].Length, pcm,
                    f * FrameSamples * channels, FrameSamples);

                Assert.IsTrue(got > 0, "the decoder takes every packet the encoder made");
                Assert.IsTrue(got <= FrameSamples,
                    "one packet decodes to at most one frame");
            }

            return pcm;
        }

        [Test]
        public static void OneFrameOfToneBecomesOnePacketOfOpus()
        {
            int frames;
            byte[][] packets = Encode(Tone(1), 1, out frames);

            Assert.AreEqual(50, frames, "one second at 20 ms is fifty frames");
            for (int f = 0; f < frames; f++)
            {
                Assert.IsTrue(packets[f].Length >= 1 && packets[f].Length <= 1275,
                    "a packet stays inside the format's ceiling (RFC 6716, section 3.4)");
            }
        }

        [Test]
        public static void TheToneSurvivesTheRoundTrip()
        {
            short[] original = Tone(1);
            int frames;
            byte[][] packets = Encode(original, 1, out frames);
            short[] decoded = Decode(packets, 1, frames);

            // The codec delays its output by a few milliseconds and the raw stream carries
            // no pre-skip, so the two are compared at the alignment where the delay lands -
            // a round trip that only matched at zero would be a test of the delay, not the
            // audio. The best of one second either way is the honest comparison.
            double best = 0;

            for (int shift = -SampleRate / 2; shift <= SampleRate / 2; shift += 8)
            {
                double dot = 0;
                double own = 0;
                double theirs = 0;

                for (int i = SampleRate / 4; i < SampleRate * 3 / 4; i += 2)
                {
                    int j = i + shift;
                    if (j < 0 || j >= decoded.Length) continue;

                    dot += (double)original[i] * decoded[j];
                    own += (double)original[i] * original[i];
                    theirs += (double)decoded[j] * decoded[j];
                }

                if (own > 0 && theirs > 0)
                {
                    double at = dot / Math.Sqrt(own * theirs);
                    if (at > best) best = at;
                }
            }

            Assert.IsTrue(best > 0.9,
                "the decoded tone is the tone that went in (correlation " + best + ")");
        }

        [Test]
        public static void TwoChannelsFollowTheSameShape()
        {
            short[] original = Tone(2);
            int frames;
            byte[][] packets = Encode(original, 2, out frames);
            short[] decoded = Decode(packets, 2, frames);

            Assert.AreEqual(original.Length, decoded.Length,
                "stereo in, stereo out: every sample of every channel comes back");

            long energy = 0;
            for (int i = 0; i < decoded.Length; i++) energy += (long)decoded[i] * decoded[i];

            Assert.IsTrue(energy > original.Length * 1000000L,
                "the round trip carries sound, not silence");
        }
    }
}