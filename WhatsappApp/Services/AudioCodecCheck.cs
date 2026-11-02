using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The one line at start-up that says whether the Opus codec is in this package, and
    /// whether Windows will load it.
    ///
    /// Why it exists: `waopus.dll` is a native file no compiler can miss - the app builds,
    /// starts and runs exactly as before without it, and the first sign of trouble is a
    /// voice note that goes out as M4A or one that arrives and does not play. That happened:
    /// a package was installed without the DLL and the failure surfaced as
    /// `DllNotFoundException` from a P/Invoke in the middle of a recording.
    ///
    /// The two questions are asked separately on purpose, because they have two different
    /// fixes and a log that only said "not loadable" sends the reader to the wrong one:
    ///
    /// - the file is not in the installed package -> the build dropped it (`Content` in
    ///   `WhatsApp.csproj`, and `unzip -l` the `.appx` to see);
    /// - the file is there and `LoadLibrary` still answers `0x8007007E` -> the DLL's own
    ///   imports are ones this phone does not have, and the fix is in the C, not the package.
    /// </summary>
    public static class AudioCodecCheck
    {
        /// <summary>The DLL `VoiceNoteEncoder` and `VoiceNoteDecoder` both come from.</summary>
        private const string Dll = "waopus.dll";

        /// <summary>
        /// The lines, written once per run while the shell starts. It never throws: a probe
        /// that failed a start would be worse than the thing it checks.
        /// </summary>
        public static async Task RunAsync()
        {
            bool inPackage = false;

            try
            {
                var item = await Package.Current.InstalledLocation.TryGetItemAsync(Dll);
                inPackage = item != null;
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioCodecCheck/lookup", ex);
            }

            if (VoiceNoteEncoder.IsAvailable)
            {
                Diag.Ok("voice: " + Dll + " is in the package and loads, so a note goes out "
                    + "as Ogg/Opus and one that arrives is decoded to a WAV that plays");
                return;
            }

            if (!inPackage)
            {
                Diag.Ok("voice: " + Dll + " is NOT in this package, so a note goes out as M4A "
                    + "and one that arrives cannot play - this build dropped it");
                return;
            }

            // The file arrived and the loader still refused it. Nothing here can say why -
            // `VoiceNoteEncoder.Available` already logged the exception and its code - so this
            // only says which of the two halves is missing.
            Diag.Ok("voice: " + Dll + " is in the package and Windows will not load it, so a "
                + "note goes out as M4A and one that arrives cannot play");
        }
    }
}
