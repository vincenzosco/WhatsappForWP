using System;
using System.Runtime.InteropServices;

namespace WhatsappApp.Services
{
    /// <summary>
    /// libopus for a call: one encoder for the microphone and one decoder for the speaker,
    /// alive for as long as the call is.
    ///
    /// A voice note is encoded in one pass over a finished file (`VoiceNoteEncoder`), which is
    /// the wrong shape for a call: the audio of a call is one frame at a time, in both
    /// directions, for as long as the call lasts. The encoder and the decoder are the same
    /// native file, so this is the streaming half of the calls `waopus.dll` already exports -
    /// `wa_opus_create`/`wa_opus_frame` and `wa_opus_decode_create`/`wa_opus_decode`.
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

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wa_opus_create(int sampleRate, int channels, int bitrate);

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int wa_opus_frame(IntPtr encoder, short[] pcm,
            int samplesPerChannel, byte[] output, int capacity);

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void wa_opus_destroy(IntPtr encoder);

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wa_opus_decode_create(int sampleRate, int channels);

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int wa_opus_decode(IntPtr decoder, byte[] packet, int length,
            short[] pcm, int capacityPerChannel);

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void wa_opus_decode_destroy(IntPtr decoder);

        /// <summary>The encoder of one call, or null when this package cannot make one.</summary>
        public static CallEncoder CreateEncoder()
        {
            if (!VoiceNoteEncoder.IsAvailable) return null;

            try
            {
                IntPtr handle = wa_opus_create(SampleRate, 1, Bitrate);
                if (handle == IntPtr.Zero)
                {
                    Diag.Ok("call audio: the encoder refused " + SampleRate + " Hz mono");
                    return null;
                }

                return new CallEncoder(handle);
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
            if (!VoiceNoteEncoder.IsAvailable) return null;

            try
            {
                IntPtr handle = wa_opus_decode_create(SampleRate, 1);
                if (handle == IntPtr.Zero)
                {
                    Diag.Ok("call audio: the decoder refused " + SampleRate + " Hz mono");
                    return null;
                }

                return new CallDecoder(handle);
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
            private IntPtr _handle;
            private readonly byte[] _packet = new byte[MaxPacketBytes];

            public CallEncoder(IntPtr handle)
            {
                _handle = handle;
            }

            /// <summary>
            /// One frame of sixteen-bit mono samples into one Opus packet, or null when the
            /// encoder refused it. The packet is a copy: the buffer it was written into is
            /// reused by the next frame.
            /// </summary>
            public byte[] Encode(short[] samples, int count)
            {
                if (_handle == IntPtr.Zero || samples == null) return null;

                try
                {
                    int size = wa_opus_frame(_handle, samples, count, _packet, _packet.Length);
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
                IntPtr handle = _handle;
                _handle = IntPtr.Zero;

                if (handle != IntPtr.Zero) wa_opus_destroy(handle);
            }
        }

        /// <summary>One decoder, for the length of one call.</summary>
        public sealed class CallDecoder : IDisposable
        {
            /// <summary>What one packet can hold: 120 ms is the format's own ceiling.</summary>
            private const int MaxSamples = SampleRate * 120 / 1000;

            private IntPtr _handle;
            private readonly short[] _samples = new short[MaxSamples];

            public CallDecoder(IntPtr handle)
            {
                _handle = handle;
            }

            /// <summary>
            /// One Opus packet into the PCM of one frame, or null when the decoder refused it.
            /// A packet that decoded to less than a frame is returned as it is and the playout
            /// pads it; one that decoded to more is returned whole for the same reason.
            /// </summary>
            public byte[] Decode(byte[] packet)
            {
                if (_handle == IntPtr.Zero || packet == null || packet.Length == 0) return null;

                try
                {
                    int got = wa_opus_decode(_handle, packet, packet.Length, _samples, MaxSamples);
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
                IntPtr handle = _handle;
                _handle = IntPtr.Zero;

                if (handle != IntPtr.Zero) wa_opus_decode_destroy(handle);
            }
        }
    }
}
