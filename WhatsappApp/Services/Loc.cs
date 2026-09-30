using System;
using Windows.ApplicationModel.Resources;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The single access point to the localized strings
    /// (Strings\&lt;language&gt;\Resources.resw). The language is chosen by the
    /// system from the device one; if a resource is missing the fallback text is
    /// used, so an error in the resources never blows the app up.
    /// </summary>
    public static class Loc
    {
        private static ResourceLoader _loader;

        /// <summary>
        /// True after GetForCurrentView has failed once. Without this, every
        /// Loc.Get retried the same call and logged its exception: one failure
        /// became one exception per string.
        /// </summary>
        private static bool _loaderUnavailable;

        private static ResourceLoader Loader
        {
            get
            {
                if (_loader != null || _loaderUnavailable) return _loader;

                try
                {
                    _loader = ResourceLoader.GetForCurrentView();
                }
                catch (Exception ex)
                {
                    _loaderUnavailable = true;
                    Diag.Failed("Loc.Loader", ex);
                }
                return _loader;
            }
        }

        /// <summary>
        /// Creates the loader on the UI thread. GetForCurrentView cannot be called
        /// from a background thread; once created, however, the loader can be
        /// queried from any thread.
        ///
        /// It is also the only place a retry happens after a failure: if the first
        /// attempt started from a background thread, here the block is cleared and
        /// it is tried again, on the right thread.
        /// </summary>
        public static void Prewarm()
        {
            _loaderUnavailable = false;

            var loader = Loader;
            if (loader != null)
            {
                try { loader.GetString("Nav_Chats"); }
                catch (Exception ex) { Diag.Failed("Loc.Prewarm", ex); }
            }
        }

        public static string Get(string key, string fallback)
        {
            var loader = Loader;
            if (loader == null) return fallback;

            try
            {
                string value = loader.GetString(key);
                return string.IsNullOrEmpty(value) ? fallback : value;
            }
            catch (Exception ex)
            {
                // The label is not built with the parenthesis right after the
                // dot: check-resw.js reads every call with a literal string as a
                // key to look for in the .resw files, and built that way it looked
                // like a missing key (even when written in a comment).
                Diag.Failed("Loc.Get key " + key, ex);
                return fallback;
            }
        }
    }
}
