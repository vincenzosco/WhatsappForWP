using System;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Whether the microphone can be recorded into a stream as PCM - the half of a call that
    /// Media Foundation could not give on this phone.
    ///
    /// Why this exists at all: `waaudio.dll` proved on 6 November 2026 that Media Foundation
    /// starts on this device but has no device enumerator (`MFEnumDeviceSources` is not one of
    /// the seven functions its `MFPlat.DLL` exports), so the capture source a Windows call
    /// would open does not exist here. `AudioRecorder` writes a WAV to a file, which is fine
    /// for a voice note and useless for a call - a call needs the samples while they arrive.
    ///
    /// `MediaCapture.StartRecordToStreamAsync` is the other door, and it is in the WP8.1
    /// projection: ask it for the WAV profile and it writes 16 kHz mono 16-bit PCM into a
    /// stream in real time. Nothing here decodes anything: the bytes that arrive are the
    /// samples an Opus encoder can take.
    ///
    /// What this is not: the call's recording path. It records three seconds, says what it
    /// wrote, and stops. The sentence it writes is what decides whether the call audio path
    /// is worth wiring to this door at all, and a wrong answer here (an M4A where the WAV
    /// profile was refused, or bytes with no `fmt ` chunk) is a fix in this file, not a
    /// mystery in the call.
    /// </summary>
    public static class CallAudioCapture
    {
        /// <summary>How long the probe records. Three seconds is enough to fill several
        /// 20 ms frames and short enough that nobody waits on it.</summary>
        private const int ProbeMilliseconds = 3000;

        /// <summary>How much of the stream is read back: the WAV header is 44 bytes, and
        /// 4 KB leaves room for a chunk this platform writes before `fmt `.</summary>
        private const int HeaderBytes = 4096;

        /// <summary>
        /// Records for a moment and writes one line: how many bytes arrived, and whether they
        /// are PCM and at what rate.
        /// </summary>
        public static async Task RunAsync()
        {
            string sentence = await ProbeAsync();

            Diag.Ok("call audio capture: " + sentence);
        }

        /// <summary>
        /// The measurement itself, as one sentence. Never throws: a probe that failed a start
        /// would be worse than the thing it checks.
        /// </summary>
        public static async Task<string> ProbeAsync()
        {
            MediaCapture capture = null;
            var stream = new InMemoryRandomAccessStream();

            string kind = "wav";
            string sentence;

            try
            {
                // Audio only, like the recorder: the default asks for video too, and that is
                // a different capability and a different consent.
                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.Audio;

                capture = new MediaCapture();
                await capture.InitializeAsync(settings);

                MediaEncodingProfile profile = null;

                // The WAV profile is not in every projection of this platform: a refusal is
                // an answer, not a crash, and the M4A below is the other door.
                try
                {
                    profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio capture/the wav profile", ex);
                }

                if (profile == null)
                {
                    kind = "m4a";
                    profile = MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto);
                }

                await capture.StartRecordToStreamAsync(profile, stream);

                await Task.Delay(ProbeMilliseconds);

                try
                {
                    await capture.StopRecordAsync();
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio capture/stopping", ex);
                }

                sentence = await DescribeAsync(stream, kind);
            }
            catch (Exception ex)
            {
                sentence = "the stream capture failed (" + Diag.Describe(ex) + ")";
            }
            finally
            {
                if (capture != null)
                {
                    try
                    {
                        capture.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Diag.Failed("call audio capture/disposing", ex);
                    }
                }
            }

            return sentence;
        }

        /// <summary>
        /// What the stream holds, as one sentence: the size, and - when the header is a WAV -
        /// the format, the rate, the channels and the bits.
        /// </summary>
        private static async Task<string> DescribeAsync(InMemoryRandomAccessStream stream, string kind)
        {
            ulong size = stream.Size;

            string sentence = "the capture wrote " + size + " byte(s) in "
                + (ProbeMilliseconds / 1000) + " s as " + kind;

            if (size < 12) return sentence + ", which is not a header";

            var reader = new DataReader(stream.GetInputStreamAt(0));

            uint wanted = (uint)Math.Min(size, (ulong)HeaderBytes);
            await reader.LoadAsync(wanted);

            var bytes = new byte[wanted];
            reader.ReadBytes(bytes);
            reader.Dispose();

            if (Text(bytes, 0, 4) != "RIFF" || Text(bytes, 8, 4) != "WAVE")
            {
                return sentence + ", and what it wrote is not a WAV header";
            }

            int at = 12;

            while (at + 8 <= bytes.Length)
            {
                string id = Text(bytes, at, 4);
                int length = (int)Little(bytes, at + 4, 4);
                int body = at + 8;

                if (id == "fmt " && body + 16 <= bytes.Length)
                {
                    int format = (int)Little(bytes, body, 2);
                    int channels = (int)Little(bytes, body + 2, 2);
                    int rate = (int)Little(bytes, body + 4, 4);
                    int bits = (int)Little(bytes, body + 14, 2);

                    return sentence + " (" + (format == 1 ? "pcm" : "format " + format) + ", "
                        + rate + " Hz, " + channels + " channel(s), " + bits + " bit(s))";
                }

                // A length that runs past the read window cannot be followed to the next
                // chunk: the header this platform writes is `fmt ` first, so there is
                // nothing behind it this probe needs.
                if (length <= 0) break;

                at = body + length + (length % 2);
            }

            return sentence + ", with no fmt chunk in the first " + wanted + " byte(s)";
        }

        private static string Text(byte[] bytes, int at, int length)
        {
            if (at < 0 || at + length > bytes.Length) return string.Empty;

            return System.Text.Encoding.UTF8.GetString(bytes, at, length);
        }

        private static long Little(byte[] bytes, int at, int length)
        {
            long value = 0;

            for (int i = length - 1; i >= 0; i--)
            {
                if (at + i < 0 || at + i >= bytes.Length) return 0;

                value = (value << 8) | bytes[at + i];
            }

            return value;
        }
    }
}
