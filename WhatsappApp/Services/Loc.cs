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

        /// <summary>
        /// Vero dopo che GetForCurrentView ha fallito una volta. Senza questo,
        /// ogni Loc.Get riprovava la stessa chiamata e ne registrava
        /// l'eccezione: un guasto solo diventava un'eccezione per stringa.
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
        /// Crea il loader sul thread UI. GetForCurrentView non si puo' chiamare
        /// da un thread di background; una volta creato, invece, il loader si
        /// puo' interrogare da qualsiasi thread.
        ///
        /// E' anche l'unico punto in cui si ritenta dopo un fallimento: se il
        /// primo tentativo e' partito da un thread di background, qui si azzera
        /// il blocco e si prova di nuovo, sul thread giusto.
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
                // L'etichetta non si costruisce con la parentesi dopo il punto:
                // check-resw.js legge ogni chiamata con una stringa letterale
                // come una chiave da cercare nei .resw, e cosi' costruita
                // sembrava una chiave mancante (anche scritta in un commento).
                Diag.Failed("Loc.Get key " + key, ex);
                return fallback;
            }
        }
    }
}
