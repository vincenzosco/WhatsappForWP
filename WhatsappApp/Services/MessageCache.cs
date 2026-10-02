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
            var empty = new List<ChatMessage>();
            if (string.IsNullOrEmpty(chatId)) return empty;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileNameFor(chatId));
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(json)) return empty;

                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var cache = Serializer.ReadObject(stream) as MessageCacheFile;
                    if (cache != null && cache.Messages != null) return cache.Messages;
                }
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
            return empty;
        }

        /// <summary>Writes the tail of the conversation. Never an exception.</summary>
        public static async Task SaveAsync(string chatId, IList<ChatMessage> messages)
        {
            if (string.IsNullOrEmpty(chatId) || messages == null || messages.Count == 0) return;

            try
            {
                var slim = new List<ChatMessage>();
                int from = messages.Count > Keep ? messages.Count - Keep : 0;
                for (int i = from; i < messages.Count; i++) slim.Add(Slim(messages[i]));

                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, new MessageCacheFile { Messages = slim });
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileNameFor(chatId), CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                // A cache that does not write is not a failure to show: the
                // history arrives from the server anyway.
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
            catch (Exception ex)
            {
                Diag.Failed("MessageCache.Delete", ex);
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
