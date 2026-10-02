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
    /// Why it exists: the app could play a voice note but not make one. The
    /// platform call is MediaCapture, initialized for audio only (no camera
    /// preview, no CaptureElement: there is nothing to show while recording),
    /// and recording straight to a StorageFile in LocalFolder.
    ///
    /// The format is AAC in an M4A container (MediaEncodingProfile.CreateM4a):
    /// it is what Windows Phone 8.1 records and decodes without help. Ogg/Opus
    /// is what WhatsApp prefers, but the phone has no Opus encoder, and the
    /// adapter cannot convert in the outgoing direction.
    ///
    /// Every failure is answered with false/null and a Diag line: a denied
    /// microphone, a device with none, a capture engine the projection refuses.
    /// The caller shows the sentence and stays usable.
    /// </summary>
    public static class AudioRecorder
    {
        /// <summary>The recorded file, inside LocalFolder. One at a time.</summary>
        public const string FileName = "voice_note.m4a";

        private static MediaCapture _capture;

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

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);

                if (!await InTimeAsync(capture.StartRecordToStorageFileAsync(
                        MediaEncodingProfile.CreateM4a(AudioEncodingQuality.Auto), file)))
                {
                    Fail("AudioRecorder.StartAsync/record", "StartRecordToStorageFileAsync");
                    Release(capture);
                    return false;
                }

                _capture = capture;
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.StartAsync", ex);
                Release(capture);
                return false;
            }
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
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size == 0) return null;
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioRecorder.StopAsync/size", ex);
                return null;
            }

            return FileName;
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
