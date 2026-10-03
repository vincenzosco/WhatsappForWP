using System;
using System.Text;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System.Profile;

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
        private const string KeyDeviceId = "DeviceId";
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
        private static string _deviceId;
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
            _deviceId = ReadString(KeyDeviceId, "");
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
        /// The id this phone presents to the shared service, in the `SenderId` of
        /// every handshake. The service derives the token from it, so a new id is
        /// a new user: a fresh one on every connection was a different token each
        /// time, and a fresh one on every reinstall was a new device, which is why
        /// the WhatsApp login had to be done again.
        ///
        /// The id therefore has to outlive the install. LocalSettings does not:
        /// WP8.1 deletes them on uninstall. The package-specific hardware token
        /// does, and it is the same for the same package on the same phone, so it
        /// is the id - the stored value is only a cache of it. A platform that
        /// refuses the call (it is not on every device) falls back to the random
        /// id, which is stable for the life of the install and no further.
        /// </summary>
        public static string DeviceId
        {
            get
            {
                EnsureLoaded();
                if (string.IsNullOrEmpty(_deviceId))
                {
                    _deviceId = HardwareDeviceId();
                    if (string.IsNullOrEmpty(_deviceId))
                    {
                        _deviceId = Guid.NewGuid().ToString("N").Substring(0, 8);
                        Diag.Ok("device id: random (the hardware token is not available)");
                    }
                    else
                    {
                        Diag.Ok("device id: hardware (survives a reinstall)");
                    }
                    Settings.Values[KeyDeviceId] = _deviceId;
                }
                return _deviceId;
            }
        }

        /// <summary>
        /// The package-specific hardware token as hexadecimal, or null when the
        /// phone does not answer. It is deliberately not truncated: it is what the
        /// service keys the user on, and two phones must not collide on it.
        /// </summary>
        private static string HardwareDeviceId()
        {
            try
            {
                var token = HardwareIdentification.GetPackageSpecificToken(null);
                if (token == null || token.Id == null) return null;

                var buffer = token.Id;
                var bytes = new byte[(int)buffer.Length];
                DataReader.FromBuffer(buffer).ReadBytes(bytes);

                var text = new StringBuilder(bytes.Length * 2);
                for (int i = 0; i < bytes.Length; i++)
                {
                    text.Append(bytes[i].ToString("x2"));
                }
                return text.ToString();
            }
            catch (Exception ex)
            {
                Diag.Failed("SettingsService/DeviceId", ex);
                return null;
            }
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
