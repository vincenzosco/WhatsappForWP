using System;
using System.Runtime.InteropServices;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The one line that says what Media Foundation on this phone actually offers the call
    /// path, and which of its pieces the phone refuses.
    ///
    /// Why it exists: `MediaCapture` on this SDK only records to a file, so there is no
    /// managed PCM callback; the WP8.1 reference assembly of `Microsoft.Xna.Framework.dll`
    /// has no `Microphone`; and `audioclient.h` is not in this SDK at all. Media Foundation
    /// is the one surface both present on the phone and callable from a DLL this package
    /// ships, so the question goes to `waaudio.dll`, built by `tools/build-waudio-arm.bat`.
    ///
    /// Why version 2 exists: version 1 enumerated capture devices with
    /// `MFEnumDeviceSources` and opened one with `IMFSourceReader`, and on 6 November 2026
    /// the phone answered `0x8007007F` - procedure not found. That is a different answer
    /// from the `0x8007007E` of the days before: the module loads, one of the functions it
    /// imports does not exist. The phone's own `WhatsAppNative.dll` imports seven functions
    /// from `MFPlat.DLL` and none of them is the enumerator or the source reader, so the
    /// capture-device surface this SDK documents is not the one this device has. Version 2
    /// asks only what the phone can answer: does Media Foundation initialise, and are its
    /// object factories there.
    /// </summary>
    public static class CallAudioProbe
    {
        private const string Dll = "waaudio.dll";

        /// <summary>Where the native side writes its sentence. 256 wide characters is
        /// twice what any answer here needs.</summary>
        private const int Capacity = 256;

        /// <summary>The version this build's managed side knows how to read.</summary>
        private const int ExpectedVersion = 2;

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        private static extern int WaAudioProbe(char[] buffer, int capacity);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
        private static extern int WaAudioProbeVersion();

        /// <summary>
        /// What the probe answered, as one readable sentence. Never throws: a check that
        /// ends a start-up is worse than the thing it checks.
        /// </summary>
        public static string Describe()
        {
            try
            {
                int version = WaAudioProbeVersion();
                if (version != ExpectedVersion)
                {
                    return Dll + " reports version " + version + ", not the "
                        + ExpectedVersion + " this build reads";
                }
            }
            catch (Exception ex)
            {
                // The two failures have two different fixes, and this says which one:
                // 0x8007007E is a module the loader cannot find in the package, and
                // 0x8007007F is a function inside a module the phone does ship.
                return Dll + " did not load (" + Diag.Describe(ex) + ")";
            }

            var buffer = new char[Capacity];

            try
            {
                int code = WaAudioProbe(buffer, Capacity);
                string text = TextOf(buffer);

                switch (code)
                {
                    case 0:
                        return "media foundation starts (" + text + ")";
                    case 1:
                        return "media foundation refused to start (" + text + ")";
                    case 2:
                        return "media foundation started and has no attribute store (" + text + ")";
                    case 3:
                        return "media foundation started and has no media type factory (" + text + ")";
                    case 4:
                        return "media foundation started and has no sample factory (" + text + ")";
                    default:
                        return "media foundation stopped at step " + code + " (" + text + ")";
                }
            }
            catch (Exception ex)
            {
                return Dll + " loaded and the probe threw (" + Diag.Describe(ex) + ")";
            }
        }

        /// <summary>
        /// The probe's answer, written once while the shell starts, next to the Opus line.
        /// </summary>
        public static void Run()
        {
            Diag.Ok("call audio: " + Describe());
        }

        /// <summary>The native buffer is NUL-terminated; the rest of it is whatever was
        /// in the array, and a debugger that shows trailing NULs is a worse log line.</summary>
        private static string TextOf(char[] buffer)
        {
            int end = Array.IndexOf(buffer, '\0');
            if (end < 0) end = buffer.Length;

            return new string(buffer, 0, end);
        }
    }
}
