using System;
using Concentus.Enums;
using Concentus.Structs;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The Opus of a call: one encoder for the microphone and one decoder for the speaker,
    /// alive for as long as the call is.
    ///
    /// A voice note is encoded in one pass over a finished file (`VoiceNoteEncoder`), which is
    /// the wrong shape for a call: the audio of a call is one frame at a time, in both
    /// directions, for as long as the call lasts. This is the streaming half of the codec,
    /// `Concentus` (`WhatsappApp/Opus`), the pure C# port of libopus 1.1.2 in fixed point.
    /// It is C# because it has to be: the phone answered
    /// `DllImport cannot be used on user-defined methods` to the native codec, the model
    /// every Silverlight app on this platform runs under.
    ///
    /// Every failure answers null: a frame the encoder refuses is one frame in a call that is
    /// already speaking, and an exception on this path is a call that stops for a packet.
    /// </summary>
    public static class CallOpus
    {
        /// <summary>One frame is 20 ms, the length a call's packet carries.</summary>
        public const int FrameMilliseconds = 20;

        /// <summary>The rate the microphone records and WhatsApp's calls carry.</summary>
        public const int SampleRate = 16000;

        /// <summary>Samples in one frame, mono.</summary>
        public const int SamplesPerFrame = SampleRate / 1000 * FrameMilliseconds;

        /// <summary>The format's own ceiling for one packet (RFC 6716, section 3.4).</summary>
        private const int MaxPacketBytes = 1275;

        /// <summary>Twenty-four kilobits is what a voice call spends on its own audio.</summary>
        private const int Bitrate = 24000;

        /// <summary>The encoder of one call, or null when this package cannot make one.</summary>
        public static CallEncoder CreateEncoder()
        {
            try
            {
                OpusEncoder encoder = OpusEncoder.Create(SampleRate, 1,
                    OpusApplication.OPUS_APPLICATION_VOIP);
                encoder.Bitrate = Bitrate;

                return new CallEncoder(encoder);
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/the encoder", ex);
                return null;
            }
        }

        /// <summary>The decoder of one call, or null when this package cannot make one.</summary>
        public static CallDecoder CreateDecoder()
        {
            try
            {
                return new CallDecoder(OpusDecoder.Create(SampleRate, 1));
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/the decoder", ex);
                return null;
            }
        }

        /// <summary>One encoder, for the length of one call.</summary>
        public sealed class CallEncoder : IDisposable
        {
            private OpusEncoder _encoder;
            private readonly byte[] _packet = new byte[MaxPacketBytes];

            public CallEncoder(OpusEncoder encoder)
            {
                _encoder = encoder;
            }

            /// <summary>
            /// One frame of sixteen-bit mono samples into one Opus packet, or null when the
            /// encoder refused it. The packet is a copy: the buffer it was written into is
            /// reused by the next frame.
            /// </summary>
            public byte[] Encode(short[] samples, int count)
            {
                OpusEncoder encoder = _encoder;
                if (encoder == null || samples == null) return null;

                try
                {
                    int size = encoder.Encode(samples, 0, count, _packet, 0, _packet.Length);
                    if (size <= 0) return null;

                    var one = new byte[size];
                    Buffer.BlockCopy(_packet, 0, one, 0, size);

                    return one;
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/an encode", ex);
                    return null;
                }
            }

            public void Dispose()
            {
                // Nothing native to give back: the codec is managed memory, and dropping the
                // reference is the whole release. The method stays because the pipe closes its
                // halves the same way whatever the codec is made of.
                _encoder = null;
            }
        }

        /// <summary>One decoder, for the length of one call.</summary>
        public sealed class CallDecoder : IDisposable
        {
            /// <summary>What one packet can hold: 120 ms is the format's own ceiling.</summary>
            private const int MaxSamples = SampleRate * 120 / 1000;

            private OpusDecoder _decoder;
            private readonly short[] _samples = new short[MaxSamples];

            public CallDecoder(OpusDecoder decoder)
            {
                _decoder = decoder;
            }

            /// <summary>
            /// One Opus packet into the PCM of one frame, or null when the decoder refused it.
            /// A packet that decoded to less than a frame is returned as it is and the playout
            /// pads it; one that decoded to more is returned whole for the same reason.
            /// </summary>
            public byte[] Decode(byte[] packet)
            {
                OpusDecoder decoder = _decoder;
                if (decoder == null || packet == null || packet.Length == 0) return null;

                try
                {
                    int got = decoder.Decode(packet, 0, packet.Length, _samples, 0, MaxSamples);
                    if (got <= 0) return null;

                    var pcm = new byte[got * 2];

                    for (int i = 0; i < got; i++)
                    {
                        pcm[i * 2] = (byte)(_samples[i] & 0xff);
                        pcm[i * 2 + 1] = (byte)((_samples[i] >> 8) & 0xff);
                    }

                    return pcm;
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/a decode", ex);
                    return null;
                }
            }

            public void Dispose()
            {
                _decoder = null;
            }
        }
    }
}