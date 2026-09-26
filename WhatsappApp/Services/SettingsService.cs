using System;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Persists the server connection settings (address, port, username)
    /// in the app's local settings, so the first-run configuration is
    /// remembered and can be changed later from the settings page.
    /// </summary>
    public static class SettingsService
    {
        private const string KeyServerAddress = "ServerAddress";
        private const string KeyServerPort = "ServerPort";
        private const string KeyUsername = "Username";
        private const string KeyNotifications = "NotificationsEnabled";
        private const string DefaultServerAddress = "192.168.1.100";
        private const int DefaultServerPort = 8585;

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        // Snapshot in memoria: senza questo ogni lettura attraversa il confine
        // WinRT di ApplicationData, ed e' il caso tipico (piu' proprieta' lette
        // una dopo l'altra mentre si costruisce la pagina).
        private static bool _loaded;
        private static string _serverAddress;
        private static int _serverPort;
        private static string _username;
        private static bool _notificationsEnabled;

        private static void EnsureLoaded()
        {
            if (_loaded) return;

            // L'indirizzo resta vuoto finche' l'utente non ne salva uno:
            // HasSavedSettings distingue "primo avvio" da "gia' configurato".
            _serverAddress = ReadString(KeyServerAddress, "");
            _serverPort = ReadInt(KeyServerPort, DefaultServerPort);
            _username = ReadString(KeyUsername, "");
            _notificationsEnabled = ReadBool(KeyNotifications, true);

            _loaded = true;
        }

        /// <summary>Indirizzo proposto quando il campo e' vuoto.</summary>
        public static string DefaultAddress
        {
            get { return DefaultServerAddress; }
        }

        public static string ServerAddress
        {
            get { EnsureLoaded(); return _serverAddress; }
            set { EnsureLoaded(); _serverAddress = value; Settings.Values[KeyServerAddress] = value; }
        }

        public static int ServerPort
        {
            get { EnsureLoaded(); return _serverPort; }
            set { EnsureLoaded(); _serverPort = value; Settings.Values[KeyServerPort] = value; }
        }

        public static string Username
        {
            get { EnsureLoaded(); return _username; }
            set { EnsureLoaded(); _username = value; Settings.Values[KeyUsername] = value; }
        }

        /// <summary>Se l'app puo' alzare un avviso quando arriva un messaggio.</summary>
        public static bool NotificationsEnabled
        {
            get { return _notificationsEnabled; }
            set
            {
                _notificationsEnabled = value;
                Settings.Values[KeyNotifications] = value;
            }
        }

        /// <summary>
        /// True when a server address has been saved (first-run setup done).
        /// </summary>
        public static bool HasSavedSettings
        {
            get { return !string.IsNullOrEmpty(ServerAddress); }
        }

        public static void Save(string address, int port, string username)
        {
            ServerAddress = address;
            ServerPort = port;
            Username = username;
        }

        private static string ReadString(string key, string defaultValue)
        {
            object val;
            if (Settings.Values.TryGetValue(key, out val) && val != null)
            {
                string text = val.ToString();
                if (!string.IsNullOrEmpty(text)) return text;
            }
            return defaultValue;
        }

        private static bool ReadBool(string key, bool fallback)
        {
            object val;
            if (Settings.Values.TryGetValue(key, out val) && val is bool) return (bool)val;
            return fallback;
        }

        private static int ReadInt(string key, int defaultValue)
        {
            object val;
            if (Settings.Values.TryGetValue(key, out val) && val != null)
            {
                int port;
                if (int.TryParse(val.ToString(), out port) && port > 0) return port;
            }
            return defaultValue;
        }
    }
}
