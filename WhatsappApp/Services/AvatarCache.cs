using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>The on-disk file: one picture per chat.</summary>
    [DataContract]
    internal class AvatarCacheEntry
    {
        [DataMember]
        public string ChatId { get; set; }

        [DataMember]
        public string Data { get; set; }
    }

    /// <summary>The whole file: the list of pictures.</summary>
    [DataContract]
    internal class AvatarCacheFile
    {
        [DataMember]
        public List<AvatarCacheEntry> Chats { get; set; }
    }

    /// <summary>
    /// The profile pictures the adapter sent, kept on the phone.
    ///
    /// Why it exists: the chat list arrives with the picture of every
    /// conversation, and the app decodes and draws it. But the bytes did not
    /// survive anything - the list copy (ChatCache) leaves them out on purpose,
    /// because it is the file read before the connection exists and must stay
    /// small, and the decoded copy is dropped by MemoryWatcher under pressure.
    /// Result: after a restart, or after a memory spike, the list shows the
    /// initials until the adapter sends every row again, that is two HTTP requests
    /// per chat (the picture address, then the bytes from the CDN).
    ///
    /// Here the bytes are kept. It is not the truth: it is a cache, and the row
    /// that arrives from the server replaces it as soon as it arrives (see
    /// ApplyChat).
    ///
    /// Ceilings, because what has no ceiling grows: MaxChats chats (one more than
    /// CHATS_LIMIT would never be drawn), MaxEntryChars for a single picture and
    /// MaxTotalChars for the file. What does not fit is not kept, and the row goes
    /// back to the initials - which is what it did before.
    /// </summary>
    public static class AvatarCache
    {
        private const string FileName = "avatar-cache.json";

        /// <summary>How many chats are kept.</summary>
        public const int MaxChats = 40;

        /// <summary>The ceiling of a single picture, in base64 characters (~110 KB).</summary>
        public const int MaxEntryChars = 150000;

        /// <summary>The ceiling of the whole file, in base64 characters (~1.1 MB).</summary>
        public const int MaxTotalChars = 1500000;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(AvatarCacheFile));

        /// <summary>ChatId -> picture in base64.</summary>
        private static readonly Dictionary<string, string> Known =
            new Dictionary<string, string>();

        /// <summary>The order the chats entered in: the excess goes out from here.</summary>
        private static readonly List<string> Order = new List<string>();

        /// <summary>The chats whose picture is too large: said once only.</summary>
        private static readonly HashSet<string> TooBig = new HashSet<string>();

        /// <summary>The characters Known occupies now: adding them on every check would cost.</summary>
        private static long _chars;

        /// <summary>
        /// The lock of Known, Order, TooBig and _chars. It is needed because
        /// Remember is called by the UI thread (ApplyChat) and the write is run by
        /// the queue.
        /// </summary>
        private static readonly object Gate = new object();

        /// <summary>The file writes, one at a time and in order.</summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        /// <summary>Grows on every change: it says whether there is something to write.</summary>
        private static int _version;

        /// <summary>The last version that reached the disk.</summary>
        private static int _written;

        private static bool _loaded;

        /// <summary>How many pictures are kept right now. For diagnosis only.</summary>
        public static int Count
        {
            get { lock (Gate) { return Known.Count; } }
        }

        /// <summary>
        /// Reads the file once, and must be awaited before applying the saved
        /// rows: without that, those rows find no picture.
        /// Never an exception: on first run the file is not there.
        /// </summary>
        public static async Task LoadAsync()
        {
            if (_loaded) return;
            _loaded = true;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(json)) return;

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var cache = Serializer.ReadObject(stream) as AvatarCacheFile;
                    if (cache == null || cache.Chats == null) return;

                    lock (Gate)
                    {
                        for (int i = 0; i < cache.Chats.Count; i++)
                        {
                            var entry = cache.Chats[i];
                            if (entry == null) continue;
                            if (string.IsNullOrEmpty(entry.ChatId)) continue;
                            if (string.IsNullOrEmpty(entry.Data)) continue;
                            if (Known.ContainsKey(entry.ChatId)) continue;

                            Known[entry.ChatId] = entry.Data;
                            Order.Add(entry.ChatId);
                            _chars += entry.Data.Length;
                        }

                        Evict();

                        // What was read is already on disk: there is nothing to
                        // rewrite.
                        _written = _version;
                    }
                }
            }
            catch (Exception ex)
            {
                // First run, or a file written by a different version.
                Diag.Failed("AvatarCache.Load", ex);
            }
        }

        /// <summary>The picture kept for this chat, or null. After LoadAsync.</summary>
        public static string Get(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            lock (Gate)
            {
                string data;
                return Known.TryGetValue(chatId, out data) ? data : null;
            }
        }

        /// <summary>
        /// Keeps the picture of one chat. ApplyChat calls it for every row that
        /// carries one: if they are the same bytes as before nothing is done, and
        /// the server sends them back identical on every list, so an update does
        /// not rewrite the file.
        /// </summary>
        public static void Remember(string chatId, string base64)
        {
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(base64)) return;

            bool changed = false;
            lock (Gate)
            {
                if (base64.Length > MaxEntryChars)
                {
                    if (TooBig.Add(chatId))
                    {
                        Diag.Ok("avatar-cache: image of " + chatId + " is " + base64.Length +
                            " chars, over the " + MaxEntryChars + " limit, not kept");
                    }
                    return;
                }

                string current;
                if (Known.TryGetValue(chatId, out current) && current == base64) return;

                if (current != null)
                {
                    _chars -= current.Length;
                    Order.Remove(chatId);
                }

                Known[chatId] = base64;
                Order.Add(chatId);
                _chars += base64.Length;
                Evict();

                _version++;
                changed = true;
            }

            if (changed) Save();
        }

        /// <summary>
        /// Keeps the file within the ceilings: the chat that entered first goes
        /// out. It must be called under Gate, because it touches Known, Order and
        /// _chars together.
        /// </summary>
        private static void Evict()
        {
            while (Order.Count > MaxChats || _chars > MaxTotalChars)
            {
                if (Order.Count == 0) return;

                string oldest = Order[0];
                Order.RemoveAt(0);

                string data;
                if (Known.TryGetValue(oldest, out data)) _chars -= data.Length;
                Known.Remove(oldest);
            }
        }

        /// <summary>
        /// Queues a write. The snapshot is taken inside the queue, not here:
        /// twenty rows arriving together - one chat list - write the file once in
        /// this shape, and serializing stays outside the lock, because the caller
        /// of Remember is the UI thread and a megabyte of JSON is not something to
        /// hold it with.
        /// </summary>
        private static void Save()
        {
#pragma warning disable 4014
            Writes.RunAsync(delegate { return WriteIfChangedAsync(); });
#pragma warning restore 4014
        }

        /// <summary>Writes if something changed since it was last written.</summary>
        private static async Task WriteIfChangedAsync()
        {
            int version;
            List<AvatarCacheEntry> snapshot;

            lock (Gate)
            {
                if (_version == _written) return;

                version = _version;
                snapshot = new List<AvatarCacheEntry>();
                for (int i = 0; i < Order.Count; i++)
                {
                    snapshot.Add(new AvatarCacheEntry
                    {
                        ChatId = Order[i],
                        Data = Known[Order[i]]
                    });
                }
            }

            if (!await WriteFileAsync(Serialize(snapshot))) return;

            lock (Gate)
            {
                if (_written < version) _written = version;
            }
        }

        /// <summary>The entries in JSON.</summary>
        private static string Serialize(List<AvatarCacheEntry> entries)
        {
            using (var stream = new MemoryStream())
            {
                Serializer.WriteObject(stream, new AvatarCacheFile { Chats = entries });
                return Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
            }
        }

        /// <summary>
        /// Writes the file. Nobody awaits it - whoever looks at the chat list has
        /// nothing to do with the outcome - so it catches on its own: an unowned
        /// deposit must not be able to bring the page down. It says whether it went
        /// well, because only then is the version on disk.
        /// </summary>
        private static async Task<bool> WriteFileAsync(string json)
        {
            try
            {
                StorageFile storage = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(storage, json);
                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("AvatarCache.Save", ex);
                return false;
            }
        }
    }
}
