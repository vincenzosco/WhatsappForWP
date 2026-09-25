using System;
using Windows.Storage;
using WhatsappApp.Controls;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Stato della sessione che sopravvive alla terminazione dell'app: in quale
    /// sezione si trovava l'utente. Contatti e messaggi invece non si salvano:
    /// arrivano dall'adapter, che li rimanda ad ogni connessione.
    /// </summary>
    public static class SessionService
    {
        private const string KeySection = "Session.Section";

        // Snapshot in memoria, come SettingsService: la sezione viene letta
        // all'avvio e scritta alla sospensione, non ad ogni navigazione.
        private static bool _loaded;
        private static AppSection _section = AppSection.Chats;

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        /// <summary>Sezione da mostrare all'avvio dopo una terminazione.</summary>
        public static AppSection Section
        {
            get
            {
                EnsureLoaded();
                return _section;
            }
            set
            {
                EnsureLoaded();
                _section = value;
                Settings.Values[KeySection] = (int)value;
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            object stored;
            if (Settings.Values.TryGetValue(KeySection, out stored) && stored != null)
            {
                int value;
                if (int.TryParse(stored.ToString(), out value)
                    && value >= (int)AppSection.Chats
                    && value <= (int)AppSection.Calls)
                {
                    _section = (AppSection)value;
                }
            }
        }
    }
}
