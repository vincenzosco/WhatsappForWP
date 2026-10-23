using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using WhatsappApp.Protocol.Client;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>One name in the file: the address and what to call it.</summary>
    [DataContract]
    internal class NameCacheEntry
    {
        [DataMember]
        public string Jid { get; set; }

        [DataMember]
        public string Name { get; set; }
    }

    /// <summary>The whole file: the saved names, the push names and the LID pairs.</summary>
    [DataContract]
    internal class NameCacheFile
    {
        [DataMember]
        public List<NameCacheEntry> Saved { get; set; }

        [DataMember]
        public List<NameCacheEntry> Push { get; set; }

        /// <summary>Jid is the `@lid`, Name is the phone-number JID.</summary>
        [DataMember]
        public List<NameCacheEntry> Lids { get; set; }
    }

    /// <summary>
    /// The names the chat list learned, kept on the phone.
    ///
    /// Why a file of its own and not a field of the chat cache: `chats.json` is read to draw
    /// the list, but it only carries the name of each row - not the LID/phone-number pair that
    /// makes a row keyed on a LID draw its real number, and not the name of somebody whose
    /// history row never had one. Both are exactly the things a restart before a sync would
    /// lose, and both are what `NameBook` exists to hold.
    ///
    /// Precedence is not decided here: the book keeps a saved name over a push name
    /// (`NameBook.NameFor`), and this file only puts back what it was given.
    /// </summary>
    public static class NameCache
    {
        private const string FileName = "name-cache.json";

        /// <summary>
        /// The book every caller shares: the connection, the chat list and the pages. One
        /// instance and not a copy per caller, because a name learned on the socket has to be
        /// the name the list draws.
        /// </summary>
        public static readonly NameBook Book = new NameBook();

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(NameCacheFile));

        private static readonly object Gate = new object();

        /// <summary>The file writes, one at a time and in order.</summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        private static int _version;
        private static int _written;
        private static bool _loaded;

        /// <summary>
        /// Reads the file once, and must be awaited before the cached rows are applied: those
        /// rows have no name of their own when they were written by a version that did not
        /// know one, and this is where it comes from.
        ///
        /// Never an exception: on a first run the file is not there.
        /// </summary>
        public static async Task LoadAsync()
        {
            if (_loaded) return;
            _loaded = true;

            // What the file holds is what is already on disk. A name learned while this read
            // is in flight bumps the version past it, and that write has to reach the file:
            // stamping the *current* version as written swallowed it.
            int diskVersion = _version;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(json)) return;

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var cache = Serializer.ReadObject(stream) as NameCacheFile;
                    if (cache == null) return;

                    lock (Gate)
                    {
                        if (cache.Lids != null)
                        {
                            for (int i = 0; i < cache.Lids.Count; i++)
                            {
                                NameCacheEntry entry = cache.Lids[i];
                                if (entry == null) continue;
                                Book.RememberMapping(entry.Jid, entry.Name);
                            }
                        }

                        if (cache.Saved != null)
                        {
                            for (int i = 0; i < cache.Saved.Count; i++)
                            {
                                NameCacheEntry entry = cache.Saved[i];
                                if (entry == null) continue;
                                Book.RememberSavedName(entry.Jid, entry.Name);
                            }
                        }

                        if (cache.Push != null)
                        {
                            for (int i = 0; i < cache.Push.Count; i++)
                            {
                                NameCacheEntry entry = cache.Push[i];
                                if (entry == null) continue;
                                Book.RememberPushName(entry.Jid, entry.Name);
                            }
                        }

                        // What was read is already on disk.
                        _written = diskVersion;
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("NameCache.Load", ex);
            }
        }

        /// <summary>Keeps the address-book name of a contact.</summary>
        public static void RememberSavedName(string jid, string name)
        {
            if (!Book.RememberSavedName(jid, name)) return;

            Changed();
        }

        /// <summary>Keeps the name a contact goes by on WhatsApp.</summary>
        public static void RememberPushName(string jid, string name)
        {
            if (!Book.RememberPushName(jid, name)) return;

            Changed();
        }

        /// <summary>Keeps the fact that a `@lid` and a phone number are one account.</summary>
        public static void RememberMapping(string lid, string phone)
        {
            if (!Book.RememberMapping(lid, phone)) return;

            Changed();
        }

        /// <summary>Forgets the file on logout, so another account does not inherit the names.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                Book.Clear();
                _version++;
                // `_written` is deliberately not stamped here: the file on disk still holds
                // the previous account's names, and stamping the new version said it was
                // already empty, so the queued write returned without writing anything.
            }

            Save();
        }

        private static void Changed()
        {
            lock (Gate)
            {
                _version++;
            }

            Save();
        }

        /// <summary>
        /// Queues a write. The snapshot is taken inside the queue, so a sync that teaches
        /// hundreds of names writes the file once rather than once per name.
        /// </summary>
        private static void Save()
        {
#pragma warning disable 4014
            Guarded.RunGuardedAsync("NameCache/Save",
                delegate { return Writes.RunAsync(delegate { return WriteIfChangedAsync(); }); });
#pragma warning restore 4014
        }

        private static async Task WriteIfChangedAsync()
        {
            int version;
            string json;

            lock (Gate)
            {
                if (_version == _written) return;

                version = _version;
                json = Serialize(new NameCacheFile
                {
                    Saved = Entries(Book.SavedEntries()),
                    Push = Entries(Book.PushEntries()),
                    Lids = Entries(Book.MappingEntries())
                });
            }

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                Diag.Failed("NameCache.Save", ex);
                return;
            }

            lock (Gate)
            {
                if (_written < version) _written = version;
            }
        }

        private static string Serialize(NameCacheFile file)
        {
            using (var stream = new MemoryStream())
            {
                Serializer.WriteObject(stream, file);
                return Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
            }
        }

        private static List<NameCacheEntry> Entries(IList<KeyValuePair<string, string>> pairs)
        {
            var entries = new List<NameCacheEntry>();

            for (int i = 0; i < pairs.Count; i++)
            {
                entries.Add(new NameCacheEntry { Jid = pairs[i].Key, Name = pairs[i].Value });
            }

            return entries;
        }
    }
}
