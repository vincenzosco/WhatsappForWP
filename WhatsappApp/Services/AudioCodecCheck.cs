using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

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
            IStorageItem item = null;

            try
            {
                item = await Package.Current.InstalledLocation.TryGetItemAsync(Dll);
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
            // says which of the two halves is missing and, in the same line, which image the
            // package is carrying: that is the other half of the question, and reading it out
            // of the pulled log is the difference between ruling a build out and guessing.
            StorageFile file = item as StorageFile;
            string header = file == null ? "no file" : await DescribeAsync(file);

            Diag.Ok("voice: " + Dll + " is in the package and Windows will not load it ("
                + header + "), so a note goes out as M4A and one that arrives cannot play");
        }

        /// <summary>
        /// The header of the DLL the package carries: how many bytes, the machine it is built
        /// for and the loader flags it declares, which is what the loader reads before it
        /// refuses a file.
        ///
        /// Why the phone has to say this itself: on 5 November 2026 the log said the file was
        /// in the package and that Windows would not load it, and the app container bit that
        /// had just been added to the C build was the first suspect. Ruling it out took reading
        /// the DLL here, hashing it against the build output and knowing which of the two the
        /// phone had installed - a comparison one line of this log could have answered. The
        /// machine decides whether the image is ARM at all (0x01c4 is ARMNT, 0x014c is the x86
        /// the emulator would need) and bit 0x1000 of the characteristics is
        /// `IMAGE_DLLCHARACTERISTICS_APPCONTAINER`, which this app's process requires.
        /// </summary>
        private static async Task<string> DescribeAsync(StorageFile file)
        {
            try
            {
                BasicProperties properties = await file.GetBasicPropertiesAsync();
                IBuffer buffer = await FileIO.ReadBufferAsync(file);
                byte[] bytes = buffer.ToArray();

                ulong size = properties.Size;
                if (bytes.Length < 0x40) return size + " byte(s), the header is cut";

                int pe = (int)ReadUInt32(bytes, 0x3c);
                if (pe <= 0 || pe + 0x60 > bytes.Length) return size + " byte(s), no PE header";

                int machine = ReadUInt16(bytes, pe + 4);
                int flags = ReadUInt16(bytes, pe + 0x5e);

                return size + " byte(s), machine 0x" + machine.ToString("x4")
                    + ", dll characteristics 0x" + flags.ToString("x4")
                    + ((flags & 0x1000) != 0 ? ", app container" : ", no app container");
            }
            catch (Exception ex)
            {
                Diag.Failed("AudioCodecCheck/header", ex);
                return "a header this platform would not read";
            }
        }

        private static int ReadUInt16(byte[] bytes, int at)
        {
            return bytes[at] | (bytes[at + 1] << 8);
        }

        private static uint ReadUInt32(byte[] bytes, int at)
        {
            return (uint)(bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16)
                | (bytes[at + 3] << 24));
        }
    }
}
