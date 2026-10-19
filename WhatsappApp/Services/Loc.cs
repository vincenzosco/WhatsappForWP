using System;
using Windows.ApplicationModel.Resources;
using Windows.Storage;

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

            // The strings a background task draws on the tile are not reachable from
            // there: a background task has no view and no ResourceLoader. They are
            // written down here, on the UI thread, and `Get` reads the copy when the
            // loader is unavailable.
            RememberForBackground("TileUnreadOne", "1 unread message");
            RememberForBackground("TileUnreadMany", "{0} unread messages");
        }

        /// <summary>
        /// Reads a string now and keeps it in LocalSettings, so a process without a
        /// view (the background check) can still draw it in the user's language.
        /// </summary>
        private static void RememberForBackground(string key, string fallback)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values["Loc." + key] = Get(key, fallback);
            }
            catch (Exception ex)
            {
                Diag.Failed("Loc.RememberForBackground " + key, ex);
            }
        }

        /// <summary>The copy written by `RememberForBackground`, or null.</summary>
        private static string RememberedForBackground(string key)
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue("Loc." + key, out value) && value != null)
                {
                    string text = value.ToString();
                    if (!string.IsNullOrEmpty(text)) return text;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("Loc.RememberedForBackground " + key, ex);
            }

            return null;
        }

        public static string Get(string key, string fallback)
        {
            var loader = Loader;
            if (loader == null)
            {
                // A background task: no view, so no ResourceLoader - the copy the
                // UI thread wrote is the only localized source left.
                string remembered = RememberedForBackground(key);
                return remembered ?? fallback;
            }

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
