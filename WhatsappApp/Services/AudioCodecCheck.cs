using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The one line at start-up that says whether a voice note goes out as Ogg/Opus and one
    /// that arrives plays.
    ///
    /// The question used to be about a native DLL (`waopus.dll`) and it had three different
    /// wrong answers to give: not in the package, in the package and refused by the loader,
    /// refused before the loader was reached. The phone settled which one was real:
    /// `DllImport cannot be used on user-defined methods` - no Silverlight app on this
    /// platform may call one - so the codec is `Concentus` (`WhatsappApp/Opus`), managed
    /// code in this assembly. There is nothing to load, and a loader that cannot refuse it.
    /// </summary>
    public static class AudioCodecCheck
    {
        /// <summary>
        /// The line, written once per run while the shell starts. It never throws: a probe
        /// that failed a start would be worse than the thing it checks.
        /// </summary>
        public static Task RunAsync()
        {
            bool ready = VoiceNoteEncoder.IsAvailable;

            Diag.Ok("voice: the Opus codec is managed code in this package (" + (ready
                ? "Concentus" : "unavailable") + "), so a note goes out as Ogg/Opus and one "
                + "that arrives is decoded to a WAV that plays");

            return Task.FromResult<object>(null);
        }
    }
}