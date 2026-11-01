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

        /// <summary>
        /// When the user last had a section on screen, in the clock the rows carry.
        /// `default(DateTime)` means never, which is before every row.
        ///
        /// Why it is written down and not kept in memory: a start that decides where to
        /// go is a new process, and "what arrived since you last looked" is a question
        /// about the last run.
        ///
        /// Why local time and not UTC: the rows it is compared with (`ChatMessage.Timestamp`,
        /// `CallLogEntry.Timestamp`) are built by `DataService.Moment`, which converts the
        /// wire's seconds to local. Comparing a local moment with a UTC mark is two hours of
        /// error in Italy and more elsewhere, which is exactly the window where a badge
        /// would either stay lit or never light.
        /// </summary>
        public static DateTime OpenedAt(AppSection section)
        {
            object stored;
            long ticks;
            if (!Settings.Values.TryGetValue(KeyOpened(section), out stored) || stored == null) return default(DateTime);
            if (!long.TryParse(stored.ToString(), out ticks) || ticks <= 0) return default(DateTime);

            return new DateTime(ticks, DateTimeKind.Local);
        }

        /// <summary>The section came to the front: what is in it is no longer news.</summary>
        public static void MarkOpened(AppSection section)
        {
            try
            {
                Settings.Values[KeyOpened(section)] = DateTime.Now.Ticks;
            }
            catch (Exception ex)
            {
                Diag.Failed("SessionService/MarkOpened", ex);
            }
        }

        /// <summary>
        /// The destination the notification layer recorded for the next start, and when it
        /// was recorded. Written when the badge is drawn (`DataService.RefreshLaunchRoute`),
        /// taken once by `App.OnLaunched`.
        /// </summary>
        public static void RememberRoute(string target)
        {
            try
            {
                if (string.IsNullOrEmpty(target))
                {
                    Settings.Values[KeyRoute] = "";
                    Settings.Values[KeyRouteAt] = (long)0;
                    return;
                }

                // Written only when it changes, so the stamp is the moment the phone
                // started advertising this and not the last message of any kind.
                object stored;
                if (Settings.Values.TryGetValue(KeyRoute, out stored) && stored != null
                    && string.Equals(stored.ToString(), target, StringComparison.Ordinal)) return;

                Settings.Values[KeyRoute] = target;
                Settings.Values[KeyRouteAt] = DateTime.UtcNow.Ticks;
            }
            catch (Exception ex)
            {
                Diag.Failed("SessionService/RememberRoute", ex);
            }
        }

        /// <summary>
        /// The route a start may act on, taken away as it is read. Null when there is none,
        /// or when the record is too old to be what the user just tapped (`LaunchRoute.IsFresh`).
        /// </summary>
        public static string TakeRoute(DateTime nowUtc)
        {
            try
            {
                object stored;
                if (!Settings.Values.TryGetValue(KeyRoute, out stored) || stored == null) return null;

                string target = stored.ToString();
                if (string.IsNullOrEmpty(target)) return null;

                object at;
                long ticks = 0;
                if (Settings.Values.TryGetValue(KeyRouteAt, out at) && at != null) long.TryParse(at.ToString(), out ticks);

                // Taken either way: a route that is too old is a route that is done.
                Settings.Values[KeyRoute] = "";
                Settings.Values[KeyRouteAt] = (long)0;

                if (!LaunchRoute.IsFresh(ticks > 0 ? new DateTime(ticks, DateTimeKind.Utc) : default(DateTime), nowUtc))
                {
                    Diag.Ok("launch: the recorded route was too old to be the tap, dropped");
                    return null;
                }

                return target;
            }
            catch (Exception ex)
            {
                Diag.Failed("SessionService/TakeRoute", ex);
                return null;
            }
        }

        private static string KeyOpened(AppSection section)
        {
            return "Session.Opened." + (int)section;
        }

        private const string KeyRoute = "Session.Route";
        private const string KeyRouteAt = "Session.RouteAt";

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
