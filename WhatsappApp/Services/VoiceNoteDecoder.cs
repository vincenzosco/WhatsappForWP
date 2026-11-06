using System;
using System.IO;
using System.Runtime.InteropServices;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// A received voice note, turned into a file this phone can play.
    ///
    /// A WhatsApp voice note is Ogg/Opus, and WP8.1 has no Opus decoder -
    /// `MediaElement` opens the file and answers `MediaFailed`, which is the note that
    /// "arrives and does not play". The decoder was already in `waopus.dll` (libopus'
    /// `opus_decoder.c`, compiled for the encoder's arithmetic), so the missing half was
    /// these three functions and a WAV writer: PCM is the one thing this platform plays
    /// without help.
    ///
    /// The output is **16 kHz** whatever the note was: libopus resamples while it decodes,
    /// and a thirty-second note is 960 KB of WAV instead of 2.9 MB - on a phone with a
    /// 512 MB budget that is the difference between a bubble and a crash.
    ///
    /// Everything that fails answers null and a line, never an exception: the caller keeps
    /// the Ogg file exactly as it arrived, and the bubble shows the failure it showed
    /// before this existed.
    /// </summary>
    public static class VoiceNoteDecoder
    {
        /// <summary>The rate the WAV is written at. Opus decodes from 48 kHz down to this.</summary>
        private const int PlaybackRate = 16000;

        /// <summary>The format's own ceiling for one packet (RFC 6716, section 3.2.5).</summary>
        private const int MaxFrameMilliseconds = 120;

        /// <summary>How many samples one packet can hold, per channel.</summary>
        private const int MaxSamplesPerChannel = PlaybackRate * MaxFrameMilliseconds / 1000;

        private const int WavHeaderBytes = 44;

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr wa_opus_decode_create(int sampleRate, int channels);

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int wa_opus_decode(IntPtr decoder, byte[] packet, int length,
            short[] pcm, int capacityPerChannel);

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void wa_opus_decode_destroy(IntPtr decoder);

        /// <summary>
        /// The file as a PCM WAV, or null when this is not Ogg/Opus, when the package has no
        /// decoder, or when a packet came back refused.
        /// </summary>
        public static byte[] ToWav(byte[] ogg)
        {
            if (ogg == null || ogg.Length == 0) return null;

            OggOpus.Content content = OggOpus.Read(ogg);
            if (content == null || content.Packets.Count == 0) return null;

            if (!VoiceNoteEncoder.IsAvailable)
            {
                Diag.Ok("voice: an Ogg/Opus note arrived and this package has no decoder for it");
                return null;
            }

            byte[] pcm = Decode(content);
            if (pcm == null) return null;

            byte[] wav = Wav(pcm, content.Channels);
            if (wav == null) return null;

            Diag.Ok("voice: " + content.Packets.Count + " packet(s) of Ogg/Opus decoded to "
                + (pcm.Length / (2 * content.Channels)) + " sample(s) of WAV from " + ogg.Length
                + " byte(s), " + content.Channels + " channel(s)");

            return wav;
        }

        /// <summary>
        /// The packets decoded one at a time, the encoder's start-up delay dropped, and the
        /// samples written as the little-endian bytes a WAV carries.
        /// </summary>
        private static byte[] Decode(OggOpus.Content content)
        {
            IntPtr decoder = IntPtr.Zero;

            try
            {
                decoder = wa_opus_decode_create(PlaybackRate, content.Channels);
            }
            catch (Exception ex)
            {
                Diag.Failed("voice: the Opus decoder is not loadable", ex);
                return null;
            }

            if (decoder == IntPtr.Zero)
            {
                Diag.Ok("voice: the decoder refused " + PlaybackRate + " Hz, "
                    + content.Channels + " channel(s)");
                return null;
            }

            try
            {
                // The pre-skip is counted at 48 kHz and the samples are not: dropping it
                // unscaled trims a third of a second in front of a note recorded at 16 kHz.
                long toSkip = (long)content.PreSkip * PlaybackRate / 48000;
                var pcm = new MemoryStream();
                var samples = new short[MaxSamplesPerChannel * content.Channels];
                var bytes = new byte[samples.Length * 2];

                for (int i = 0; i < content.Packets.Count; i++)
                {
                    byte[] packet = content.Packets[i];

                    int got = wa_opus_decode(decoder, packet, packet.Length, samples,
                        MaxSamplesPerChannel);

                    if (got <= 0)
                    {
                        // A packet the decoder refuses is a note that cannot be trusted from
                        // here on: what was decoded plays, the rest is silence.
                        Diag.Ok("voice: the decoder refused packet " + i + " (" + got + ")");
                        break;
                    }

                    int perChannel = got;
                    int offset = 0;

                    if (toSkip > 0)
                    {
                        long dropped = Math.Min(toSkip, perChannel);
                        offset = (int)dropped * content.Channels;
                        perChannel -= (int)dropped;
                        toSkip -= dropped;
                        if (perChannel <= 0) continue;
                    }

                    Buffer.BlockCopy(samples, offset * 2, bytes, 0, perChannel * content.Channels * 2);
                    pcm.Write(bytes, 0, perChannel * content.Channels * 2);
                }

                if (pcm.Length == 0) return null;

                return pcm.ToArray();
            }
            catch (Exception ex)
            {
                Diag.Failed("voice: decoding an Ogg/Opus note", ex);
                return null;
            }
            finally
            {
                wa_opus_decode_destroy(decoder);
            }
        }

        /// <summary>
        /// PCM in a RIFF header: 16-bit, `PlaybackRate`, the note's own channels. The header
        /// is the whole container this phone needs, and it is written here rather than read
        /// by a library.
        /// </summary>
        private static byte[] Wav(byte[] pcm, int channels)
        {
            if (pcm.Length == 0 || pcm.Length > int.MaxValue - WavHeaderBytes) return null;

            var wav = new byte[WavHeaderBytes + pcm.Length];

            Text(wav, 0, "RIFF");
            Put(wav, 4, (ulong)(36 + pcm.Length), 4);
            Text(wav, 8, "WAVE");

            Text(wav, 12, "fmt ");
            Put(wav, 16, 16, 4);                              // the fmt chunk's own size
            Put(wav, 20, 1, 2);                               // PCM
            Put(wav, 22, (ulong)channels, 2);
            Put(wav, 24, PlaybackRate, 4);
            Put(wav, 28, (ulong)(PlaybackRate * channels * 2), 4);   // bytes per second
            Put(wav, 32, (ulong)(channels * 2), 2);           // bytes per frame
            Put(wav, 34, 16, 2);                              // bits per sample

            Text(wav, 36, "data");
            Put(wav, 40, (ulong)pcm.Length, 4);

            Buffer.BlockCopy(pcm, 0, wav, WavHeaderBytes, pcm.Length);

            return wav;
        }

        private static void Text(byte[] into, int at, string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Buffer.BlockCopy(bytes, 0, into, at, bytes.Length);
        }

        private static void Put(byte[] into, int at, ulong value, int bytes)
        {
            for (int i = 0; i < bytes; i++) into[at + i] = (byte)((value >> (8 * i)) & 0xff);
        }
    }
}
