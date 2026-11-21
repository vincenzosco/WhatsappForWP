using System;
using System.Runtime.InteropServices;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Whether this build can call into the native DLLs it carries, answered once by asking
    /// the phone instead of reading the application model.
    ///
    /// The answer used to be `#if SILVERLIGHT return false` - an assumption that a Silverlight
    /// app cannot P/Invoke, and it switched every codec and call-audio path off before the
    /// loader was ever reached. The phone's own answers say the marshaler runs and the loader
    /// decides: a probe came back `DllNotFoundException 0x80131524` (the image was in the
    /// package, its imports were not on the phone - `KERNEL32.dll`, replaced by
    /// `tools/opus/waheap.def` and `waerr.def` with the API sets `WhatsAppNative.dll` names),
    /// and `CallAudioProbe`'s `waaudio.dll` came back `0x8007007F` (the DLL loaded and one
    /// export was missing). So the question is one real call into `waopus.dll`, and whatever
    /// the loader answers is what every caller gets.
    /// </summary>
    public static class NativeInterop
    {
        /// <summary>What one Opus frame is, so the managed side does not restate the constant.</summary>
        private const int FrameMilliseconds = 20;

        private static bool _asked;
        private static bool _supported;
        private static string _reason = "no native DLL has been asked yet";
        private static bool _unsupportedReported;

        [DllImport("waopus.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int wa_opus_frame_ms();

        /// <summary>
        /// True once the loader has accepted one call into `waopus.dll`. The first caller pays
        /// for the question; the answer is kept for the rest of the run.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                Ask();
                return _supported;
            }
        }

        /// <summary>The reason native codec and call-audio paths are unavailable.</summary>
        public static string UnavailableReason
        {
            get
            {
                Ask();
                return _reason;
            }
        }

        /// <summary>Records the unsupported native bridge once without attempting to call it.</summary>
        public static void ReportUnavailable(string feature)
        {
            if (_unsupportedReported) return;
            _unsupportedReported = true;
            Diag.Ok(feature + " unavailable: " + UnavailableReason);
        }

        /// <summary>
        /// Asks the loader once and keeps whatever it said - a refusal is a fact about this
        /// phone and this package, not an exception for the caller to handle.
        /// </summary>
        private static void Ask()
        {
            if (_asked) return;
            _asked = true;

            try
            {
                int answered = wa_opus_frame_ms();
                _supported = true;
                _reason = answered == FrameMilliseconds
                    ? "the phone loads waopus.dll"
                    : "waopus.dll loads but answers " + answered + " milliseconds for one frame";
            }
            catch (Exception ex)
            {
                _supported = false;
                _reason = "the loader refuses waopus.dll ("
                    + ex.GetType().Name + ": " + ex.Message + ")";
            }
        }
    }
}