namespace WhatsappApp.Services
{
    /// <summary>Describes native interop support in the current application model.</summary>
    public static class NativeInterop
    {
        private static bool _unsupportedReported;

        /// <summary>Silverlight applications cannot call native DLL methods with DllImport.</summary>
        public static bool IsSupported
        {
            get
            {
#if SILVERLIGHT
                return false;
#else
                return true;
#endif
            }
        }

        /// <summary>The reason native codec and call-audio paths are unavailable.</summary>
        public static string UnavailableReason
        {
            get { return "the Silverlight application model does not support managed P/Invoke"; }
        }

        /// <summary>Records the unsupported native bridge once without attempting to call it.</summary>
        public static void ReportUnavailable(string feature)
        {
            if (_unsupportedReported) return;
            _unsupportedReported = true;
            Diag.Ok(feature + " unavailable: " + UnavailableReason);
        }
    }
}
