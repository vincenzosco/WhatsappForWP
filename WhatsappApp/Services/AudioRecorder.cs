using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Records a voice note into the app folder.
    ///
    /// The app could play a voice note but not make one. The
    /// platform call is MediaCapture, initialized for audio only (no camera
    /// preview, no CaptureElement: there is nothing to show while recording),
    /// and recording straight to a StorageFile in LocalFolder.
    ///
    /// The format is AAC in an M4A container (MediaEncodingProfile.CreateM4a):
    /// it is what Windows Phone 8.1 records and decodes without help. Ogg/Opus
    /// is what WhatsApp prefers, but the phone has no Opus encoder and nothing here
    /// converts in the outgoing direction: the note is sent as the M4A it is.
    ///
    /// Every failure is answered with false/null and a Diag line: a denied
    /// microphone, a device with none, a capture engine the projection refuses.
    /// The caller shows the sentence and stays usable.
    /// </summary>
    public static class AudioRecorder
    {
        /// <summary>
        /// The recorded file, inside LocalFolder. One at a time, and in PCM.
        ///
        /// PCM WAV and not M4A: the note is turned into the Ogg/Opus WhatsApp plays
        /// (`VoiceNoteEncoder`), and that encoder takes samples, not an AAC stream - the
        /// platform's own decoder is not reachable from this app. `M4aFileName` is the
        /// fallback for a phone whose projection refuses the WAV profile.
        /// </summary>
        public const string WavFileName = "voice_note.wav";

        /// <summary>The recording of a phone that cannot write WAV: it goes out as an audio message.</summary>
        public const string M4aFileName = "voice_note.m4a";

        /// <summary>The file this process recorded, since the two profiles have two names.</summary>
        private static string _fileName = WavFileName;

        private static MediaCapture _capture;

        /// <summary>
        /// A start that has not answered yet. `_capture` is assigned only at the end of a
        /// successful start, so between the first call and that line a second call saw no
        /// capture, created a second MediaCapture over the same microphone and the same file,
        /// and overwrote the first in `_capture` - a capture nothing could release and that
        /// went on recording.
        /// </summary>
        private static bool _starting;

        /// <summary>
        /// How long one MediaCapture call may take before it counts as failed.
        /// Some phones leave InitializeAsync hanging after a failed or doubled
        /// start; with no ceiling the tap does nothing forever, which is the
        /// bug this answers. Ten seconds is far longer than a healthy start and
        /// short enough that nobody waits on it twice.
        /// </summary>
        private const int CaptureTimeoutMs = 10000;

        /// <summary>A capture is running.</summary>
        public static bool IsRecording
        {
            get { return _capture != null; }
        }

        /// <summary>
        /// Starts recording the microphone into LocalFolder. It returns false
        /// instead of throwing: the caller has a sentence to show and no way to
        /// recover from an exception.
        /// </summary>
        public static async Task<bool> StartAsync()
        {
            if (_capture != null) return false;
            if (_starting) return false;

            _starting = true;

            MediaCapture capture = null;
            try
            {
                // Audio only. StreamingCaptureMode.Audio is what keeps the camera
                // out of it: the default would ask for video too, and that is a
                // different capability.
                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.Audio;

                capture = new MediaCapture();
                if (!await InTimeAsync(capture.InitializeAsync(settings)))
                {
                    Fail("AudioRecorder.StartAsync/initialize", "MediaCapture.InitializeAsync");
                    Release(capture);
                    return false;
                }

                string name = WavFileName;
                MediaEncodingProfile profile = ProfileFor(ref name);
                if (profile == null)
                {
                    Fail("AudioRecorder.StartAsync/profile", "MediaEncodingProfile");
                    Release(capture);
                    return false;
                }

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);

                if (!await InTimeAsync(capture.StartRecordToStorageFileAsync(profile, file)))
                {
                    Fail("AudioRecorder.StartAsync/record", "StartRecordToStorageFileAsync");
                    Release(capture);
                    return false;
                }

                _capture = capture;
                _fileName = name;
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.StartAsync", ex);
                Release(capture);
                return false;
            }
            finally
            {
                _starting = false;
            }
        }

        /// <summary>
        /// The recording profile to ask the microphone for, and the name of the file it will
        /// write.
        ///
        /// WAV first because the note has to become Ogg/Opus, and the encoder takes PCM.
        /// The WAV profile is not in every projection of this platform (`CreateWav` is a
        /// Windows 8.1 member), so a refusal is not a lost recording: the M4A is what this
        /// app recorded before, and the note then goes out as an audio message - which
        /// arrives and plays, just not as a voice note.
        /// </summary>
        private static MediaEncodingProfile ProfileFor(ref string name)
        {
            try
            {
                MediaEncodingProfile wav = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
                if (wav != null)
                {
                    name = WavFileName;
                    return wav;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder/ProfileFor", ex);
            }

            Diag.Ok("AudioRecorder: this phone cannot record WAV, so the note is an M4A "
                + "and it will arrive as an audio message");

            name = M4aFileName;
            return MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto);
        }

        /// <summary>
        /// Awaits one capture operation, or gives up. false means it did not
        /// finish in time and has been cancelled. A fault is not swallowed:
        /// awaiting the operation surfaces it to the caller, which logs it like
        /// every other one.
        /// </summary>
        private static async Task<bool> InTimeAsync(IAsyncAction action)
        {
            Task task = action.AsTask();
            Task finished = await Task.WhenAny(task, Task.Delay(CaptureTimeoutMs));
            if (finished != task)
            {
                try
                {
                    action.Cancel();
                }
                catch (Exception ex)
                {
                    Diag.Failed("AudioRecorder/cancel", ex);
                }

                // The abandoned operation can still fault after Cancel(), and
                // nobody awaits it any more: observed here, so it cannot become
                // the unobserved task this whole area is about.
#pragma warning disable 4014
                Guarded.RunGuardedAsync("AudioRecorder.InTimeAsync/abandoned", task);
#pragma warning restore 4014
                return false;
            }

            await task;
            return true;
        }

        /// <summary>One sentence for a call that ran out of time.</summary>
        private static void Fail(string where, string call)
        {
            Diag.Failed(where, new InvalidOperationException(
                call + " did not finish in " + CaptureTimeoutMs + " ms"));
        }

        /// <summary>
        /// Stops the recording and returns the name of the file in LocalFolder,
        /// or null when the capture failed or wrote nothing (a tap that lasted a
        /// moment produces an empty file, and an empty voice note is not one).
        /// </summary>
        public static async Task<string> StopAsync()
        {
            MediaCapture capture = _capture;
            if (capture == null) return null;

            bool stopped = false;
            try
            {
                await capture.StopRecordAsync();
                stopped = true;
            }
            catch (Exception ex)
            {
                // Not rethrown and not awaited inside catch: C# 5 answers CS1985.
                Diag.Failed("AudioRecorder.StopAsync", ex);
            }

            Release(capture);

            if (!stopped) return null;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(_fileName);
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size == 0) return null;
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.StopAsync/size", ex);
                return null;
            }

            return _fileName;
        }

        /// <summary>
        /// Stops and throws the recording away: the user left the page, or
        /// started again. The file stays on disk and the next recording replaces
        /// it (CreationCollisionOption.ReplaceExisting).
        /// </summary>
        public static async Task CancelAsync()
        {
            MediaCapture capture = _capture;
            if (capture == null) return;

            try
            {
                await capture.StopRecordAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.CancelAsync", ex);
            }

            Release(capture);
        }

        /// <summary>Releases the capture exactly once and forgets it.</summary>
        private static void Release(MediaCapture capture)
        {
            _capture = null;
            if (capture == null) return;
            try
            {
                capture.Dispose();
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.Release", ex);
            }
        }
    }
}
