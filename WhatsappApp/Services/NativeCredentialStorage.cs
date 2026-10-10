using Windows.Storage;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Where the native session lives on the phone: `ICredentialStorage` over the app's
    /// own settings map.
    ///
    /// `ApplicationData.Current.LocalSettings` is a flat string map and nothing more,
    /// which is exactly the shape the interface asks for - and it is the same store the
    /// server build used, under different keys: `CredentialStore.KeyPrefix` is `Session.`,
    /// and the old address, port and public-server switch sit next to it untouched (the
    /// session build ignores them; Task 6 removes them).
    ///
    /// The map survives an app update, so an upgrade keeps its account and does not ask
    /// the user to scan again. That is the whole reason the credentials are here and not
    /// in a file the packaging can take away.
    /// </summary>
    public sealed class NativeCredentialStorage : ICredentialStorage
    {
        /// <summary>
        /// The storage this app reads its account from. One object, because the settings
        /// map is one: two of these would be two views of the same keys, and nothing in
        /// either would know the other had written.
        /// </summary>
        public static readonly NativeCredentialStorage Device = new NativeCredentialStorage();

        /// <summary>
        /// The account, over the storage above. A store is cheap and holds nothing itself -
        /// it reads through to the settings on every call - so handing out a fresh one is
        /// not a second copy of the session.
        /// </summary>
        public static CredentialStore Session()
        {
            return new CredentialStore(Device);
        }

        /// <summary>The settings map.</summary>
        private static ApplicationDataContainer Settings
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        public string Read(string key)
        {
            object stored;
            if (!Settings.Values.TryGetValue(key, out stored) || stored == null) return null;

            return stored.ToString();
        }

        /// <summary>
        /// Stores a value; an empty one removes the key, so "no session" reads as absent
        /// rather than as a set of empty strings that happen to be there.
        /// </summary>
        public void Write(string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                Settings.Values.Remove(key);
                return;
            }

            Settings.Values[key] = value;
        }
    }
}
