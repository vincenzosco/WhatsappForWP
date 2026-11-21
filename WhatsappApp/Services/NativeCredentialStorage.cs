using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;
using Windows.Storage;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Where the native session lives on the phone: `ICredentialStorage` over the app's
    /// own settings map, with a file beside it that carries the same words.
    ///
    /// `ApplicationData.Current.LocalSettings` is a flat string map and nothing more,
    /// which is exactly the shape the interface asks for - and it is the same store the
    /// server build used, under different keys: `CredentialStore.KeyPrefix` is `Session.`,
    /// and the old address, port and public-server switch sit next to it untouched (the
    /// session build ignores them; Task 6 removes them).
    ///
    /// The map is where a person can read the account off the phone's settings, and it
    /// was where the account was kept - until the device taught otherwise. A Windows
    /// Phone 8.1 Silverlight app is not suspended when it is left by the back button or
    /// taken down by a deploy: the process simply ends, and settings written on the way
    /// never reach the disk. The account looked saved - `pair: the account is saved` -
    /// and the next start had nothing. The chat files and the pre-keys are written
    /// through to disk and never had this problem, so the session now is too: every
    /// word is mirrored to one file as it is written, and the mirror answers a read the
    /// settings cannot. The file is written as a temporary and then a move, the same
    /// rule the caches follow, because a half-written session is the same as none.
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

        /// <summary>The file that mirrors the map. One line per key, tab between the two.</summary>
        private const string MirrorName = "session-settings.txt";

        /// <summary>The suffix a half-written mirror carries until it is complete.</summary>
        private const string MirrorTemp = MirrorName + ".writing";

        /// <summary>The mirror, read once and kept level with the writes.</summary>
        private static Dictionary<string, string> _mirror;

        /// <summary>The gate of the mirror: the pairing writes it from one thread, a read may come from another.</summary>
        private static readonly object MirrorGate = new object();

        public string Read(string key)
        {
            object stored;
            if (Settings.Values.TryGetValue(key, out stored) && stored != null)
            {
                string value = stored.ToString();

                // A key that is in the settings but not in the mirror is an account from
                // before this file existed: it becomes durable now rather than at the
                // next pairing. The write is the ordinary one and not a second path.
                bool missing;
                lock (MirrorGate)
                {
                    missing = !Mirror().ContainsKey(key);
                }

                if (missing) Write(key, value);
                return value;
            }

            // The process may have ended between the write and the settings reaching
            // disk. The file answers, and the settings are repaired from it so the two
            // say the same thing from here on.
            string remembered;
            lock (MirrorGate)
            {
                if (!Mirror().TryGetValue(key, out remembered)) remembered = null;
            }

            if (string.IsNullOrEmpty(remembered)) return null;

            try
            {
                Settings.Values[key] = remembered;
            }
            catch (Exception ex)
            {
                // The file already answered; a settings map that cannot be repaired is
                // the reason the file is here.
                Diag.Failed("credentials: the settings", ex);
            }

            return remembered;
        }

        /// <summary>
        /// Stores a value; an empty one removes the key, so "no session" reads as absent
        /// rather than as a set of empty strings that happen to be there.
        /// </summary>
        public void Write(string key, string value)
        {
            try
            {
                if (string.IsNullOrEmpty(value))
                {
                    Settings.Values.Remove(key);
                }
                else
                {
                    Settings.Values[key] = value;
                }
            }
            catch (Exception ex)
            {
                // The map is the mirror a person reads; the file below is the one the
                // next run needs. A settings map that refuses a word must not keep the
                // word from being written where it survives.
                Diag.Failed("credentials: the settings", ex);
            }

            lock (MirrorGate)
            {
                Dictionary<string, string> map = Mirror();

                if (string.IsNullOrEmpty(value)) map.Remove(key);
                else map[key] = value;

                SaveMirror(map);
            }
        }

        /// <summary>
        /// The mirror as it stands on disk, read once per run. A file that cannot be read
        /// is no file: the map comes back empty and the settings answer as they always
        /// did.
        /// </summary>
        private static Dictionary<string, string> Mirror()
        {
            if (_mirror != null) return _mirror;

            var map = new Dictionary<string, string>();
            try
            {
                using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    if (store.FileExists(MirrorName))
                    {
                        using (var stream = new IsolatedStorageFileStream(
                            MirrorName, FileMode.Open, FileAccess.Read, store))
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                int split = line.IndexOf('\t');
                                if (split <= 0) continue;
                                map[line.Substring(0, split)] = line.Substring(split + 1);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("credentials: the file mirror", ex);
            }

            _mirror = map;
            return _mirror;
        }

        /// <summary>
        /// Writes the mirror whole: a temporary file and then a move, so the file on disk
        /// is either the map before the write or the map after it, never a line of one
        /// and a line of the other. A session file that tore would read as a session with
        /// half a key - which `CredentialStore.HasSession` calls no session at all, and
        /// rightly, but it would take the pairing with it.
        /// </summary>
        private static void SaveMirror(Dictionary<string, string> map)
        {
            try
            {
                using (IsolatedStorageFile store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    using (var stream = new IsolatedStorageFileStream(
                        MirrorTemp, FileMode.Create, FileAccess.Write, store))
                    using (var writer = new StreamWriter(stream, Encoding.UTF8))
                    {
                        foreach (KeyValuePair<string, string> pair in map)
                        {
                            writer.Write(pair.Key);
                            writer.Write('\t');
                            writer.Write(pair.Value);
                            writer.Write('\n');
                        }
                    }

                    if (store.FileExists(MirrorName)) store.DeleteFile(MirrorName);
                    store.MoveFile(MirrorTemp, MirrorName);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("credentials: the file mirror", ex);
            }
        }
    }
}