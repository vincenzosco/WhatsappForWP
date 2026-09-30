using System;
using Windows.Storage;
using WhatsappApp.Controls;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Session state that survives the app being terminated: which section the
    /// user was in. Contacts and messages are not saved, instead: they come from
    /// the adapter, which sends them again on every connection.
    /// </summary>
    public static class SessionService
    {
        private const string KeySection = "Session.Section";

        // In-memory snapshot, as in SettingsService: the section is read at
        // startup and written on suspension, not on every navigation.
        private static bool _loaded;
        private static AppSection _section = AppSection.Chats;

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        /// <summary>Section to show at startup after a termination.</summary>
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
