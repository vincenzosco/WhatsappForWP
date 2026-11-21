using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Concentus.Enums;
using Concentus.Structs;
using Windows.Storage;
using Windows.Storage.Streams;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// A recording, turned into the Ogg/Opus a WhatsApp voice note is made of.
    ///
    /// Where the encoder comes from: WhatsApp's own clients play a `ptt` only as Ogg/Opus,
    /// and this platform has no Opus encoder of its own. A native one was tried first and
    /// the phone settled the question - `DllImport cannot be used on user-defined methods`,
    /// the answer every Silverlight app gets - so the codec here is `Concentus`
    /// (`WhatsappApp/Opus`), the pure C# port of libopus 1.1.2 in fixed point. It is part
    /// of this assembly: there is nothing to load and nothing to refuse.
    ///
    /// The input is the **PCM WAV** the recorder writes (`AudioRecorder`), and not the M4A:
    /// an M4A would have to be AAC-decoded first, and the platform's decoder is not
    /// reachable from here. PCM in, packets out, no decoder anywhere.
    ///
    /// Everything that can go wrong here is answered with null and a line, never with an
    /// exception: the caller's fallback is the M4A it already knows how to send, and a
    /// voice note that cannot be encoded must still be a message that arrives.
    /// </summary>
    public static class VoiceNoteEncoder
    {
        /// <summary>One frame is 20 ms, which is what WhatsApp's own recorder writes.</summary>
        private const int FrameMilliseconds = 20;

        /// <summary>The format's own ceiling for one packet (RFC 6716, section 3.4).</summary>
        private const int MaxPacketBytes = 1275;

        /// <summary>A note that is now Ogg/Opus, and how long it plays.</summary>
        public sealed class Encoded
        {
            /// <summary>The file inside LocalFolder.</summary>
            public string FileName;

            /// <summary>Whole seconds, as the frames add up.</summary>
            public uint Seconds;

            public int PacketCount;
            public int Bytes;
        }

        /// <summary>
        /// The WAV turned into an Ogg/Opus file in the app folder, or null when this phone
        /// cannot (no encoder in the package, a file that is not 16-bit PCM, a frame the
        /// encoder refused) - and the caller sends the file it has.
        /// </summary>
        public static async Task<Encoded> FromWavAsync(string wavFileName)
        {
            if (string.IsNullOrEmpty(wavFileName)) return null;

            byte[] bytes;
            try
            {
                IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(wavFileName);
                StorageFile file = item as StorageFile;
                if (file == null) return null;
                Windows.Storage.Streams.IBuffer buffer = await FileIO.ReadBufferAsync(file);

                bytes = new byte[buffer.Length];
                using (var reader = DataReader.FromBuffer(buffer))
                {
                    reader.ReadBytes(bytes);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("voice: reading " + wavFileName, ex);
                return null;
            }

            Wave wave = Wave.Read(bytes);
            if (wave == null)
            {
                Diag.Ok("voice: " + wavFileName + " is not 16-bit PCM at a rate Opus takes, "
                    + "so it goes as the file it is");
                return null;
            }

            List<byte[]> packets = Encode(wave);
            if (packets == null || packets.Count == 0) return null;

            byte[] ogg;
            try
            {
                ogg = OggOpus.Build(packets, wave.SampleRate, wave.Channels,
                    (int)(DateTime.UtcNow.Ticks & 0x7fffffff));
            }
            catch (Exception ex)
            {
                Diag.Failed("voice: building the Ogg", ex);
                return null;
            }

            // A name of its own, like every outgoing attachment: two bubbles must never
            // point at one file, or the second recording turns the first one into itself.
            string name = "voice_note_" + DateTime.UtcNow.Ticks + ".ogg";

            try
            {
                StorageFile target = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(target, ogg);
            }
            catch (Exception ex)
            {
                Diag.Failed("voice: writing " + name, ex);
                return null;
            }

            var encoded = new Encoded();
            encoded.FileName = name;
            encoded.PacketCount = packets.Count;
            encoded.Seconds = (uint)Math.Round(packets.Count * (double)FrameMilliseconds / 1000.0);
            encoded.Bytes = ogg.Length;

            Diag.Ok("voice: " + packets.Count + " packet(s), " + encoded.Seconds + " s, "
                + ogg.Length + " bytes of Ogg/Opus from " + wavFileName
                + " (" + wave.SampleRate + " Hz, " + wave.Channels + " channel(s))");

            return encoded;
        }

        /// <summary>
        /// One frame at a time, as the encoder is asked: the returned packets are copies,
        /// because the buffer they were written into is reused.
        /// </summary>
        private static List<byte[]> Encode(Wave wave)
        {
            OpusEncoder encoder;
            try
            {
                encoder = OpusEncoder.Create(wave.SampleRate, wave.Channels,
                    OpusApplication.OPUS_APPLICATION_VOIP);
            }
            catch (Exception ex)
            {
                Diag.Ok("voice: the encoder refused " + wave.SampleRate + " Hz, "
                    + wave.Channels + " channel(s) (" + ex.Message + ")");
                return null;
            }

            int perFrame = wave.SampleRate / (1000 / FrameMilliseconds);
            int sampleCount = perFrame * wave.Channels;
            var samples = new short[sampleCount];
            var packet = new byte[MaxPacketBytes];
            var packets = new List<byte[]>();

            int at = wave.DataOffset;
            int end = wave.DataOffset + wave.DataLength;

            while (end - at >= sampleCount * 2)
            {
                for (int i = 0; i < sampleCount; i++)
                {
                    samples[i] = (short)(wave.Bytes[at] | (wave.Bytes[at + 1] << 8));
                    at += 2;
                }

                int size;
                try
                {
                    size = encoder.Encode(samples, 0, perFrame, packet, 0, packet.Length);
                }
                catch (Exception ex)
                {
                    Diag.Ok("voice: the encoder refused a frame (" + ex.Message + ")");
                    return null;
                }

                if (size <= 0)
                {
                    Diag.Ok("voice: the encoder refused a frame (" + size + ")");
                    return null;
                }

                var one = new byte[size];
                System.Buffer.BlockCopy(packet, 0, one, 0, size);
                packets.Add(one);
            }

            if (packets.Count == 0)
            {
                Diag.Ok("voice: the recording is shorter than one 20 ms frame");
                return null;
            }

            return packets;
        }

        /// <summary>
        /// Whether the codec is part of this package. It is managed code in this assembly
        /// (`WhatsappApp/Opus`), so the answer cannot change between phones: a note goes out
        /// as Ogg/Opus and one that arrives is decoded to a WAV that plays.
        /// </summary>
        public static bool IsAvailable
        {
            get { return true; }
        }

        /// <summary>
        /// A WAV that Opus can encode: 16-bit PCM, one or two channels, a rate it takes.
        ///
        /// The chunk walk is the whole parser: a WAV is a RIFF header and then `fmt ` and
        /// `data`, whose sizes are what tell the two apart.
        /// </summary>
        private sealed class Wave
        {
            public byte[] Bytes;
            public int SampleRate;
            public int Channels;
            public int DataOffset;
            public int DataLength;

            public static Wave Read(byte[] bytes)
            {
                if (bytes == null || bytes.Length < 44) return null;
                if (Text(bytes, 0) != "RIFF" || Text(bytes, 8) != "WAVE") return null;

                var wave = new Wave();
                wave.Bytes = bytes;

                int at = 12;
                bool hasFormat = false;

                while (at + 8 <= bytes.Length)
                {
                    string id = Text(bytes, at);
                    int size = (int)Little(bytes, at + 4, 4);
                    int body = at + 8;

                    if (size < 0 || body + size > bytes.Length)
                    {
                        // A length that runs past the file: the header is what is wrong, and
                        // guessing the rest would encode noise.
                        size = bytes.Length - body;
                    }

                    if (id == "fmt " && size >= 16)
                    {
                        int format = (int)Little(bytes, body, 2);
                        wave.Channels = (int)Little(bytes, body + 2, 2);
                        wave.SampleRate = (int)Little(bytes, body + 4, 4);
                        int bits = (int)Little(bytes, body + 14, 2);

                        if (format != 1 || bits != 16) return null;
                        if (wave.Channels != 1 && wave.Channels != 2) return null;
                        if (wave.SampleRate != 8000 && wave.SampleRate != 12000
                            && wave.SampleRate != 16000 && wave.SampleRate != 24000
                            && wave.SampleRate != 48000)
                        {
                            return null;
                        }

                        hasFormat = true;
                    }
                    else if (id == "data")
                    {
                        wave.DataOffset = body;
                        wave.DataLength = size;
                    }

                    // Chunks are word-aligned: an odd size is followed by a filler byte.
                    at = body + size + (size % 2);
                }

                if (!hasFormat || wave.DataLength <= 0) return null;

                return wave;
            }

            private static string Text(byte[] bytes, int at)
            {
                return System.Text.Encoding.UTF8.GetString(bytes, at, 4);
            }

            private static long Little(byte[] bytes, int at, int length)
            {
                long value = 0;
                for (int i = length - 1; i >= 0; i--) value = (value << 8) | bytes[at + i];

                return value;
            }
        }
    }
}
