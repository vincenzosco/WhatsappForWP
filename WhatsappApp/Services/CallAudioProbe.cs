using System;
using System.Runtime.InteropServices;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The one line that says whether this phone exposes a microphone through Media
    /// Foundation, and at what rate the device hands samples over.
    ///
    /// Why it exists: the call path had no answer to that question. `MediaCapture` on this
    /// SDK only records to a file, so there is no managed PCM callback; the reference
    /// assembly of `Microsoft.Xna.Framework.dll` for WP8.1 has no `Microphone`; and
    /// `audioclient.h` is not in this SDK at all. Media Foundation is the one surface that
    /// is both on the phone (WhatsAppNative.dll imports it) and callable from a DLL this
    /// package ships - so the question goes to `waaudio.dll`, built by
    /// `tools/build-waudio-arm.bat`, and this class asks it once per run.
    ///
    /// Why a log line and not a call implementation: whether the device is there at all,
    /// and in which format, decides how the encoder is fed. Assuming 16 kHz mono and
    /// building an encoder on top of the assumption would produce a silent call that looks
    /// exactly like a signalling bug. The probe reports; the wiring comes after a device
    /// answers with a format.
    /// </summary>
    public static class CallAudioProbe
    {
        private const string Dll = "waaudio.dll";

        /// <summary>Where the native side writes its sentence. 256 wide characters is
        /// twice what any answer here needs.</summary>
        private const int Capacity = 256;

        /// <summary>The version this build's managed side knows how to read.</summary>
        private const int ExpectedVersion = 1;

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
                // a DLL missing from the package or built for another machine, against
                // a DLL that loads and disagrees about its own shape.
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
                        return "media foundation captured a device (" + text + ")";
                    case 4:
                        return "media foundation started but enumerated no capture device ("
                            + text + ")";
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
