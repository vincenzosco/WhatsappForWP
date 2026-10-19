using System;
using System.Text;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System.Profile;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The few things this phone remembers about itself.
    ///
    /// It used to be the server settings: an address, a port, a user name, a token,
    /// the switch for the shared service and the URL of the endpoint file. None of
    /// that exists any more - the app speaks WhatsApp's protocol from the phone, and
    /// the account it is linked as lives in the protocol's own credential store
    /// (see `CredentialStore`), not here. What is left is what a person chose (the
    /// notifications) and the id this device reports (see `DeviceId`).
    /// </summary>
    public static class SettingsService
    {
        private const string KeyNotifications = "NotificationsEnabled";
        private const string KeyBackgroundCheck = "BackgroundCheckEnabled";
        private const string KeyDeviceId = "DeviceId";

        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        // In-memory snapshot: without this every read crosses the WinRT boundary
        // of ApplicationData, and that is the common case (several properties read
        // one after another while the page is built).
        private static bool _loaded;
        private static string _deviceId;
        private static bool _notificationsEnabled;
        private static bool _backgroundCheckEnabled = true;

        private static void EnsureLoaded()
        {
            if (_loaded) return;

            _deviceId = ReadString(KeyDeviceId, "");
            _notificationsEnabled = ReadBool(KeyNotifications, true);
            _backgroundCheckEnabled = ReadBool(KeyBackgroundCheck, true);

            _loaded = true;
        }

        /// <summary>
        /// The id this phone presents on the wire. WhatsApp keys a linked device on
        /// the registration it made, not on this, so it no longer decides an account
        /// - it is what the diagnostics page shows and what a log identifies this
        /// handset by.
        ///
        /// It has to outlive the install. LocalSettings does not: WP8.1 deletes them
        /// on uninstall. The package-specific hardware token does, and it is the same
        /// for the same package on the same phone, so it is the id - the stored value
        /// is only a cache of it. A platform that refuses the call (it is not on every
        /// device) falls back to the random id, which is stable for the life of the
        /// install and no further.
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
        /// phone does not answer. It is deliberately not truncated: two phones must
        /// not collide on it.
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

        /// <summary>Whether the app may raise a notification when a message arrives.</summary>
        public static bool NotificationsEnabled
        {
            get { EnsureLoaded(); return _notificationsEnabled; }
            set
            {
                EnsureLoaded();
                _notificationsEnabled = value;
                Settings.Values[KeyNotifications] = value;
            }
        }

        /// <summary>
        /// Whether the app may wake itself every 30 minutes to check the messages
        /// (see `BackgroundSync`). On by default: a phone whose app is closed receives
        /// nothing otherwise, and the user can turn it off from the settings.
        /// </summary>
        public static bool BackgroundCheckEnabled
        {
            get { EnsureLoaded(); return _backgroundCheckEnabled; }
            set
            {
                EnsureLoaded();
                _backgroundCheckEnabled = value;
                Settings.Values[KeyBackgroundCheck] = value;
            }
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
    }
}
