using System;
using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The state machine of a voice note: idle, starting, recording.
    ///
    /// Why it exists: the tap that did nothing on a real phone was a state bug
    /// spread across two modules. The page held `_recording` and
    /// `_startingRecording`, the recorder held `_capture`, and no module owned
    /// the transitions between them - so a second tap started a second
    /// MediaCapture, and a failure thrown at the call site was invisible. Here
    /// the transitions have one home and one interface, and the caller renders
    /// the result.
    ///
    /// The module is an instance, not a static: a ChatPage holds one, so two
    /// pages cached by the frame cannot share a session.
    /// </summary>
    public sealed class RecordingSession
    {
        private bool _starting;
        private bool _recording;

        /// <summary>Whether the microphone is capturing.</summary>
        public bool IsRecording
        {
            get { return _recording; }
        }

        /// <summary>
        /// When the running recording began. Only meaningful while
        /// <see cref="IsRecording"/> is true; the caller reads it to draw how
        /// long the note has been going.
        /// </summary>
        public DateTime StartedAt { get; private set; }

        /// <summary>
        /// Starts a recording. It returns false instead of throwing - the caller
        /// has a sentence to show and no way to recover from an exception - and a
        /// start that is already in flight is ignored, because a second
        /// MediaCapture at once wedges the engine on this platform.
        /// </summary>
        public async Task<bool> StartAsync()
        {
            if (_recording || _starting) return false;

            _starting = true;
            bool started;
            try
            {
                started = await AudioRecorder.StartAsync();
            }
            catch (Exception ex)
            {
                // An exception thrown before AudioRecorder's own try - a type the
                // phone refuses to load, a method it does not have - is thrown at
                // this call site. It is caught here so it becomes a sentence
                // instead of an unobserved Task, which is what the tap that did
                // nothing used to be.
                Diag.Failed("RecordingSession.StartAsync", ex);
                started = false;
            }
            finally
            {
                _starting = false;
            }

            if (!started) return false;

            _recording = true;
            StartedAt = DateTime.Now;
            return true;
        }

        /// <summary>
        /// Ends the recording and returns the name of the file in LocalFolder, or
        /// null when the capture failed or wrote nothing (a tap that lasted a
        /// moment produces an empty file, and an empty voice note is not one).
        /// </summary>
        public async Task<string> StopAsync()
        {
            string fileName = null;
            try
            {
                fileName = await AudioRecorder.StopAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("RecordingSession.StopAsync", ex);
            }

            _recording = false;
            return fileName;
        }

        /// <summary>
        /// Throws the recording away: the user left the page, or started again.
        /// No file is deposited, so nothing can be sent by mistake from another
        /// chat.
        ///
        /// The recorder is asked to cancel even when nothing is captured here: a
        /// start that has not answered yet may still create a capture after this
        /// returns, and this is the only call that reaches it. It is harmless
        /// when there is nothing to cancel.
        /// </summary>
        public async Task CancelAsync()
        {
            _starting = false;
            _recording = false;

            try
            {
                await AudioRecorder.CancelAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("RecordingSession.CancelAsync", ex);
            }
        }
    }
}
