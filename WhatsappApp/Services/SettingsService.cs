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

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        public static string ServerAddress
        {
            get { return GetString(KeyServerAddress, ""); }
            set { Settings.Values[KeyServerAddress] = value; }
        }

        public static int ServerPort
        {
            get
            {
                object val;
                if (Settings.Values.TryGetValue(KeyServerPort, out val) && val != null)
                {
                    int port;
                    if (int.TryParse(val.ToString(), out port)) return port;
                }
                return 8585;
            }
            set { Settings.Values[KeyServerPort] = value; }
        }

        public static string Username
        {
            get { return GetString(KeyUsername, ""); }
            set { Settings.Values[KeyUsername] = value; }
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

        private static string GetString(string key, string defaultValue)
        {
            object val;
            if (Settings.Values.TryGetValue(key, out val) && val != null)
            {
                return val.ToString();
            }
            return defaultValue;
        }
    }
}
