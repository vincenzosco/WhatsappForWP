using System;
using Windows.Storage;
using WhatsappApp.Controls;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Session state that survives the app being terminated: which section the
    /// user was in. Contacts and messages are not saved here: the chat list has its
    /// own cache (`ChatCache`) and the messages arrive again with the sync that
    /// follows a login.
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

        /// <summary>
        /// The chat a tile on the Start screen asked for, until the home page has opened it.
        ///
        /// It is in memory only, and it does not need more: the arguments arrive with the
        /// launch and the page that acts on them is built in the same run. A start with no
        /// session opens the QR instead, and the request is dropped with it - there is no
        /// account to open a chat on.
        /// </summary>
        public static string PendingChatId;

        /// <summary>The name the tile carried, for a cold start that has no list yet.</summary>
        public static string PendingChatName;

        /// <summary>Takes the request away, so a return to the page does not reopen it.</summary>
        public static void ClearPendingChat()
        {
            PendingChatId = null;
            PendingChatName = null;
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
