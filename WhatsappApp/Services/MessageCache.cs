using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>The on-disk file: a single field, the messages of one chat.</summary>
    [DataContract]
    internal class MessageCacheFile
    {
        [DataMember]
        public List<ChatMessage> Messages { get; set; }
    }

    /// <summary>
    /// The last messages of a conversation, kept on the phone.
    ///
    /// Why it exists: opening a chat used to start from an empty list and wait
    /// for the history from the server, which is the one thing the phone cannot
    /// hurry. With this copy the conversation is seen at once, and the real
    /// messages replace it when they arrive.
    ///
    /// It is a photograph, not the truth: only the tail of the conversation is
    /// kept, and it is rewritten on leaving the chat (not on every message, which
    /// would write a file in bursts).
    /// </summary>
    public static class MessageCache
    {
        /// <summary>How many messages are kept per chat: enough to fill the
        /// screen, not enough to weigh.</summary>
        private const int Keep = 60;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(MessageCacheFile));

        /// <summary>The saved messages. Never an exception: the first time there is no file.</summary>
        public static async Task<List<ChatMessage>> LoadAsync(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return new List<ChatMessage>();

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileNameFor(chatId));
                return await ReadAsync(file);
            }
            catch (FileNotFoundException)
            {
                // First open: there is no copy of this chat on the phone. That is
                // the normal state and not a failure, so it is not logged: a
                // FileNotFoundException line in the log hid the real fault.
            }
            catch (Exception ex)
            {
                // A cache written by a different version.
                Diag.Failed("MessageCache.Load", ex);
            }
            return new List<ChatMessage>();
        }

        /// <summary>
        /// The rows in one tail file. It throws on a file it cannot parse, on purpose: the
        /// caller is the one that can name the file the failure belongs to.
        /// </summary>
        private static async Task<List<ChatMessage>> ReadAsync(StorageFile file)
        {
            string json = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrEmpty(json)) return new List<ChatMessage>();

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var cache = Serializer.ReadObject(stream) as MessageCacheFile;
                if (cache != null && cache.Messages != null) return cache.Messages;
            }

            return new List<ChatMessage>();
        }

        /// <summary>
        /// How many conversations this phone holds. A count and not a read: the start asks
        /// this before reading anything (see DataService.RestoreRowsFromTheirTailsAsync).
        /// </summary>
        public static async Task<int> CountAsync()
        {
            try
            {
                IReadOnlyList<StorageFile> files =
                    await ApplicationData.Current.LocalFolder.GetFilesAsync();

                int count = 0;
                for (int i = 0; i < files.Count; i++)
                {
                    if (IsTailFile(files[i])) count++;
                }

                return count;
            }
            catch (Exception ex)
            {
                Diag.Failed("MessageCache.Count", ex);
                return 0;
            }
        }

        /// <summary>
        /// Every conversation on the phone, with its rows.
        ///
        /// Why it exists: the chat list on disk (`chats.json`) is what the next start draws and
        /// WhatsApp names the list again only at a link, so a tail whose chat is not in that
        /// file is a conversation with no way back on screen - 56 tails against 2 rows on the
        /// phone of 24 October 2026 (`DataService.RestoreRowsFromTheirTailsAsync` puts them
        /// back).
        ///
        /// The id is the one the rows carry, not the one in the file name: `FileNameFor` drops
        /// `@` and `.`, so a name can only say roughly whose file it is.
        /// </summary>
        public static async Task<List<KeyValuePair<string, List<ChatMessage>>>> TailsAsync()
        {
            var all = new List<KeyValuePair<string, List<ChatMessage>>>();

            try
            {
                IReadOnlyList<StorageFile> files =
                    await ApplicationData.Current.LocalFolder.GetFilesAsync();

                for (int i = 0; i < files.Count; i++)
                {
                    StorageFile file = files[i];
                    if (!IsTailFile(file)) continue;

                    try
                    {
                        List<ChatMessage> messages = await ReadAsync(file);
                        string chatId = ChatIdOf(messages);
                        if (string.IsNullOrEmpty(chatId)) continue;

                        all.Add(new KeyValuePair<string, List<ChatMessage>>(chatId, messages));
                    }
                    catch (Exception ex)
                    {
                        // One file this build cannot read is one conversation missing from the
                        // list, not a start that stops at it.
                        Diag.Failed("MessageCache.Read " + file.Name, ex);
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("MessageCache.Tails", ex);
            }

            return all;
        }

        /// <summary>Where the rows say they belong: the last of them that names a chat.</summary>
        private static string ChatIdOf(List<ChatMessage> messages)
        {
            if (messages == null) return null;

            for (int i = messages.Count - 1; i >= 0; i--)
            {
                ChatMessage message = messages[i];
                if (message == null) continue;
                if (!string.IsNullOrEmpty(message.ChatId)) return message.ChatId;
            }

            return null;
        }

        /// <summary>One conversation's file: `messages_*.json`.</summary>
        private static bool IsTailFile(StorageFile file)
        {
            if (file == null || string.IsNullOrEmpty(file.Name)) return false;
            if (!file.Name.StartsWith("messages_", StringComparison.OrdinalIgnoreCase)) return false;
            return file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The writes, one at a time and in order.</summary>
        ///
        /// Why: `AppendAsync` is a read, a merge and a write, and two of them for one chat - a
        /// live message and the backlog filling in - both read the file before either wrote, so
        /// the second dropped the rows of the first. The queue makes the three steps one.
        /// </summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        /// <summary>Writes the tail of the conversation. Never an exception.</summary>
        public static async Task SaveAsync(string chatId, IList<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatId) || messages == null || messages.Count == 0) return;

            // Snapped here, on the thread that owns the collection, and written in the queue:
            // what is written is one instant of the conversation and not the list as it was
            // before the first write and after the last.
            string json = JsonOf(messages);
            if (json == null) return;

            await Writes.RunAsync(delegate { return WriteJsonAsync(chatId, json); });
        }

        /// <summary>The rows as JSON, or null when they cannot be serialised (named, never thrown).</summary>
        private static string JsonOf(IList<ChatMessage> messages)
        {
            try
            {
                var slim = new List<ChatMessage>();
                int from = messages.Count > Keep ? messages.Count - Keep : 0;
                for (int i = from; i < messages.Count; i++) slim.Add(Slim(messages[i]));

                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, new MessageCacheFile { Messages = slim });
                    return Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }
            }
            catch (Exception ex)
            {
                // A cache that does not write is not a failure to show: the
                // history arrives from the server anyway.
                Diag.Failed("MessageCache.Save", ex);
                return null;
            }
        }

        /// <summary>
        /// The file, written beside itself and moved over: a conversation written into the file
        /// it replaces loses everything to one suspension in the middle - and WhatsApp sends the
        /// history once, so the conversation comes back empty. `ChatCache` and `StatusCache`
        /// already write this way.
        /// </summary>
        private static async Task WriteJsonAsync(string chatId, string json)
        {
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                string name = FileNameFor(chatId);

                StorageFile temp = await folder.CreateFileAsync(
                    name + ".new", CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(temp, json);

                StorageFile current = null;
                try
                {
                    current = await folder.GetFileAsync(name);
                }
                catch (FileNotFoundException)
                {
                    // The first message of this chat: there is nothing to replace.
                }

                if (current == null) await temp.RenameAsync(name);
                else await temp.MoveAndReplaceAsync(current);
            }
            catch (Exception ex)
            {
                Diag.Failed("MessageCache.Save", ex);
            }
        }

        /// <summary>
        /// Drops the copy of one chat. Deletion calls it: without this, the
        /// deleted conversation would come back on screen at the first
        /// open-and-close, because DataService reads the cache when the in-memory
        /// list is empty. Never an exception: a file that is not there is not a
        /// failure.
        /// </summary>
        public static async Task DeleteAsync(string chatId)
        {
            if (string.IsNullOrEmpty(chatId)) return;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileNameFor(chatId));
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (FileNotFoundException)
            {
                // A chat that was never saved has no file to drop: the state of every chat
                // deleted before it was opened, not a failure.
            }
            catch (Exception ex)
            {
                Diag.Failed("MessageCache.Delete", ex);
            }
        }

        /// <summary>
        /// Adds these messages to the copy already on the phone, keeping the ones that
        /// are there.
        ///
        /// Why not `SaveAsync`: the caller may hold only part of a conversation. The
        /// background check reads the backlog in a process of its own, where no page
        /// was opened and so nothing was loaded from disk (see BackgroundSync): it
        /// holds the two or three messages that just arrived, and writing them over
        /// the file would replace sixty saved ones with those three.
        /// </summary>
        public static async Task AppendAsync(string chatId, IList<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatId) || messages == null || messages.Count == 0) return;

            await Writes.RunAsync(delegate { return AppendCoreAsync(chatId, messages); });
        }

        /// <summary>The merge itself, inside the queue: load, merge and write are one step.</summary>
        private static async Task AppendCoreAsync(string chatId, IList<ChatMessage> messages)
        {
            List<ChatMessage> existing = await LoadAsync(chatId);
            var merged = new List<ChatMessage>(existing);

            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage message = messages[i];
                if (message == null) continue;
                if (HasId(merged, message.Id)) continue;

                merged.Add(message);
            }

            // In the order they happened, whatever the order they came in: a backlog is
            // not promised to be sorted, and a file whose rows straddle the saved ones
            // would be a conversation read out of order on the next start.
            merged.Sort(delegate (ChatMessage left, ChatMessage right)
            {
                return left.Timestamp.CompareTo(right.Timestamp);
            });

            // The last `Keep` rows, slimmed, so the file stays the same size whatever this
            // adds. Written directly and not through `SaveAsync`: that would queue behind the
            // very turn it is running in and wait for itself.
            string json = JsonOf(merged);
            if (json != null) await WriteJsonAsync(chatId, json);
        }

        /// <summary>Whether this conversation already holds a message with this id.</summary>
        private static bool HasId(List<ChatMessage> messages, string id)
        {
            // A message with no id is kept: only an id can tell a re-delivery from news,
            // and a client that dropped everything unnamed would lose the messages whose
            // shape it does not recognise yet (`NativeChats.AddMessage` decides the same).
            if (string.IsNullOrEmpty(id)) return false;

            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] != null && messages[i].Id == id) return true;
            }

            return false;
        }

        /// <summary>
        /// Drops every conversation on the phone. A link is a new identity, and the
        /// messages of the previous one are not this account's messages: the sync the
        /// link brings writes its own copy (see DataService, the end of the history).
        /// </summary>
        public static async Task DeleteAllAsync()
        {
            try
            {
                IReadOnlyList<StorageFile> files =
                    await ApplicationData.Current.LocalFolder.GetFilesAsync();

                for (int i = 0; i < files.Count; i++)
                {
                    StorageFile file = files[i];
                    if (!IsTailFile(file)) continue;

                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("MessageCache.DeleteAll", ex);
            }
        }

        /// <summary>
        /// The copy of a message without the media bytes: those would fill the
        /// file, and for a video they would not even fit. It marks IsHistory, so a
        /// message taken from the cache does not count as unread and raises no
        /// notification, exactly like the real history.
        /// </summary>
        private static ChatMessage Slim(ChatMessage message)
        {
            return new ChatMessage
            {
                Id = message.Id,
                Text = message.Text,
                SenderId = message.SenderId,
                SenderName = message.SenderName,
                ChatId = message.ChatId,
                Timestamp = message.Timestamp,
                Status = message.Status,
                Type = message.Type,
                IsIncoming = message.IsIncoming,
                MediaMimeType = message.MediaMimeType,
                MediaFileName = message.MediaFileName,
                MediaType = message.MediaType,
                IsHistory = true
            };
        }

        /// <summary>A file name per chat: the id cleaned of the characters a
        /// file name does not accept.</summary>
        private static string FileNameFor(string chatId)
        {
            var builder = new StringBuilder("messages_");
            for (int i = 0; i < chatId.Length; i++)
            {
                char c = chatId[i];
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9') || c == '-' || c == '_')
                {
                    builder.Append(c);
                }
            }
            builder.Append(".json");
            return builder.ToString();
        }
    }
}
