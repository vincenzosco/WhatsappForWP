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
        private const string KeyToken = "ServiceToken";
        private const string KeyUsePublicServer = "UsePublicServer";
        private const string KeyEndpointUrl = "EndpointUrl";
        private const string DefaultServerAddress = "192.168.1.100";
        private const int DefaultServerPort = 8585;

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        // In-memory snapshot: without this every read crosses the WinRT boundary
        // of ApplicationData, and that is the common case (several properties read
        // one after another while the page is built).
        private static bool _loaded;
        private static string _serverAddress;
        private static int _serverPort;
        private static string _username;
        private static string _token;
        private static bool _usePublicServer;
        private static string _endpointUrl;
        private static bool _notificationsEnabled;

        private static void EnsureLoaded()
        {
            if (_loaded) return;

            // The address stays empty until the user saves one, or until the public
            // service is connected to at least once: HasSavedSettings tells "first
            // run" from "already configured".
            _serverAddress = ReadString(KeyServerAddress, "");
            _serverPort = ReadInt(KeyServerPort, DefaultServerPort);
            _username = ReadString(KeyUsername, "");
            _token = ReadString(KeyToken, "");
            // The service this app is built around is the shared one: it needs no
            // address, so a phone that has never been configured can go straight to
            // the chats and find it. A private server is one switch away, in the
            // settings page, which the gear of the chats screen opens.
            _usePublicServer = ReadBool(KeyUsePublicServer, true);
            _endpointUrl = ReadString(KeyEndpointUrl, "");
            _notificationsEnabled = ReadBool(KeyNotifications, true);

            _loaded = true;
        }

        /// <summary>Address proposed when the field is empty.</summary>
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

        /// <summary>
        /// The token of the shared service, if it is one. On a private server it
        /// stays empty and the server does not ask for it; on the public service
        /// it is the only thing that tells one phone from another.
        /// </summary>
        public static string Token
        {
            get { EnsureLoaded(); return _token; }
            set { EnsureLoaded(); _token = value; Settings.Values[KeyToken] = value; }
        }

        /// <summary>
        /// True when the app must use the public service instead of a typed
        /// address: it reads the address by itself, and changes by itself when the
        /// tunnel changes port.
        /// </summary>
        public static bool UsePublicServer
        {
            get { EnsureLoaded(); return _usePublicServer; }
            set { EnsureLoaded(); _usePublicServer = value; Settings.Values[KeyUsePublicServer] = value; }
        }

        /// <summary>The address file. Empty means the default one.</summary>
        public static string EndpointUrl
        {
            get { EnsureLoaded(); return _endpointUrl; }
            set { EnsureLoaded(); _endpointUrl = value; Settings.Values[KeyEndpointUrl] = value; }
        }

        /// <summary>Whether the app may raise a notification when a message arrives.</summary>
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
        /// True when the app has enough to connect on its own: a typed address, or
        /// the public service, which needs no address at all because it reads one
        /// from GitHub. Without the second half, a phone set up for the public
        /// server counted as a first run, so every launch opened the settings page
        /// and the chats screen never asked for its list.
        /// </summary>
        public static bool HasSavedSettings
        {
            get { return UsePublicServer || !string.IsNullOrEmpty(ServerAddress); }
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
