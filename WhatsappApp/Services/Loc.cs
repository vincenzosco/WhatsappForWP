using System;
using Windows.ApplicationModel.Resources;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Unico punto di accesso alle stringhe localizzate
    /// (Strings\&lt;lingua&gt;\Resources.resw). La lingua la sceglie il sistema
    /// in base a quella del dispositivo; se manca una risorsa si usa il testo
    /// di fallback, quindi un errore nelle risorse non fa mai esplodere l'app.
    /// </summary>
    public static class Loc
    {
        private static ResourceLoader _loader;

        private static ResourceLoader Loader
        {
            get
            {
                if (_loader == null)
                {
                    try { _loader = ResourceLoader.GetForCurrentView(); }
                    catch { }
                }
                return _loader;
            }
        }

        /// <summary>
        /// Crea il loader sul thread UI. GetForCurrentView non si puo' chiamare
        /// da un thread di background; una volta creato, invece, il loader si
        /// puo' interrogare da qualsiasi thread.
        /// </summary>
        public static void Prewarm()
        {
            var loader = Loader;
            if (loader != null)
            {
                try { loader.GetString("Nav_Chats"); }
                catch { }
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
            catch
            {
                return fallback;
            }
        }
    }
}
