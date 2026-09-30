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
    /// <summary>What this phone has decided about a chat.</summary>
    [DataContract]
    internal class ChatPreference
    {
        [DataMember]
        public string ChatId { get; set; }

        [DataMember]
        public bool Pinned { get; set; }

        [DataMember]
        public bool Muted { get; set; }

        [DataMember]
        public bool Hidden { get; set; }
    }

    /// <summary>The on-disk file: a single field, the list of decisions.</summary>
    [DataContract]
    internal class ChatPreferenceFile
    {
        [DataMember]
        public List<ChatPreference> Chats { get; set; }
    }

    /// <summary>
    /// Pinning, muting and deleting a chat are decisions of this phone, not of
    /// WhatsApp: GOWA has no endpoint for any of the three, and the conversation
    /// list the adapter sends is read-only.
    ///
    /// Why not in the list cache (ChatCache): that is a photograph the server
    /// replaces row by row, and a chat that no longer appears in that list (old,
    /// or cut off by CHATS_LIMIT) would lose its pin. Here it survives the server.
    /// </summary>
    public static class ChatPreferences
    {
        private const string FileName = "chat-preferences.json";

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(ChatPreferenceFile));

        // In-memory snapshot: the chat list asks for these answers for every row,
        // and reading them from disk each time would be one file per message.
        private static readonly Dictionary<string, ChatPreference> Known =
            new Dictionary<string, ChatPreference>();

        /// <summary>The file writes, one at a time and in order.</summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        private static bool _loaded;

        /// <summary>True when the file has been read. For diagnosis only.</summary>
        public static bool IsLoaded
        {
            get { return _loaded; }
        }

        /// <summary>Reads the file once. Never an exception: on first run there is none.</summary>
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
                    var known = Serializer.ReadObject(stream) as ChatPreferenceFile;
                    if (known == null || known.Chats == null) return;

                    for (int i = 0; i < known.Chats.Count; i++)
                    {
                        var entry = known.Chats[i];
                        if (entry == null || string.IsNullOrEmpty(entry.ChatId)) continue;
                        Known[entry.ChatId] = entry;
                    }
                }
            }
            catch (Exception ex)
            {
                // First run, or a file written by a different version.
                Diag.Failed("ChatPreferences.Load", ex);
            }
        }

        public static bool IsPinned(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Pinned;
        }

        public static bool IsMuted(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Muted;
        }

        public static bool IsHidden(string chatId)
        {
            var entry = Find(chatId);
            return entry != null && entry.Hidden;
        }

        public static void SetPinned(string chatId, bool pinned)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Pinned = pinned;
            Forget(chatId, entry);
            Save();
        }

        public static void SetMuted(string chatId, bool muted)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Muted = muted;
            Forget(chatId, entry);
            Save();
        }

        /// <summary>
        /// The chat was deleted from this phone: it stays out of the list even
        /// when the server sends it again. It is not the end of the conversation -
        /// Reveal brings it back, and a new message calls it, as WhatsApp does.
        /// </summary>
        public static void Hide(string chatId)
        {
            var entry = EntryFor(chatId);
            if (entry == null) return;
            entry.Hidden = true;
            Save();
        }

        /// <summary>The chat returns to the list. Pin and mute stay as they were.</summary>
        public static void Reveal(string chatId)
        {
            var entry = Find(chatId);
            if (entry == null || !entry.Hidden) return;
            entry.Hidden = false;
            Forget(chatId, entry);
            Save();
        }

        private static ChatPreference Find(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            ChatPreference entry;
            return Known.TryGetValue(chatId, out entry) ? entry : null;
        }

        private static ChatPreference EntryFor(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return null;

            var entry = Find(chatId);
            if (entry != null) return entry;

            entry = new ChatPreference { ChatId = chatId };
            Known[chatId] = entry;
            return entry;
        }

        /// <summary>
        /// An entry with nothing to say is not kept: a file of all-false rows
        /// grows with the conversation list, which changes on its own.
        /// </summary>
        private static void Forget(string chatId, ChatPreference entry)
        {
            if (entry.Pinned || entry.Muted || entry.Hidden) return;
            Known.Remove(chatId);
        }

        /// <summary>
        /// Snaps now and writes in the queue. The snap happens on the thread of
        /// whoever changed the preference, where the list is still: inside the
        /// queue the work touches only a string, not Known.
        ///
        /// Writing at once without waiting lost the last change: two close
        /// edits (Unpin all does one per chat) launched two writes on the same
        /// file, and the older one could reach the disk. Now the second waits for
        /// the first, and the last written is the last decided.
        /// </summary>
        private static void Save()
        {
            string json = Serialize();
#pragma warning disable 4014
            Writes.RunAsync(delegate { return WriteFileAsync(json); });
#pragma warning restore 4014
        }

        /// <summary>The list as it is now, in JSON. Call it on the thread that changed the preference.</summary>
        private static string Serialize()
        {
            var file = new ChatPreferenceFile { Chats = new List<ChatPreference>() };
            foreach (var entry in Known.Values) file.Chats.Add(entry);

            using (var stream = new MemoryStream())
            {
                Serializer.WriteObject(stream, file);
                return Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
            }
        }

        /// <summary>
        /// Writes the file. Nobody awaits it - whoever changes a pin has nothing
        /// to do with the outcome - so it catches on its own: an unowned deposit
        /// must not be able to bring the page down. Never an exception.
        /// </summary>
        private static async Task WriteFileAsync(string json)
        {
            try
            {
                StorageFile storage = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(storage, json);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPreferences.Save", ex);
            }
        }
    }
}
