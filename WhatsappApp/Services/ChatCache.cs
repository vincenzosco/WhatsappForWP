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
    /// <summary>The on-disk file: a single field, the list of rows.</summary>
    [DataContract]
    internal class ChatCacheFile
    {
        [DataMember]
        public List<ChatMessage> Chats { get; set; }
    }

    /// <summary>
    /// The chat list of the last session, kept on the phone.
    ///
    /// Why it exists: at startup the connection is not there yet, and the adapter
    /// answers the list request only when WhatsApp is linked. Without this copy
    /// the app opens on an empty list for the seconds it takes, and it looks like
    /// nothing happened.
    ///
    /// It is not the truth: it is a photograph. As soon as the server answers,
    /// every row is replaced by the real one (see DataService.ApplyChat). Without
    /// the avatar bytes the copy stays small, and the picture arrives with the
    /// first update.
    /// </summary>
    public static class ChatCache
    {
        private const string FileName = "chats.json";

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(ChatCacheFile));

        /// <summary>The saved rows. Never an exception: on first run there is no file.</summary>
        public static async Task<List<ChatMessage>> LoadAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string json = await FileIO.ReadTextAsync(file);
                if (!string.IsNullOrEmpty(json))
                {
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        var cache = Serializer.ReadObject(stream) as ChatCacheFile;
                        if (cache != null && cache.Chats != null) return cache.Chats;
                    }
                }
            }
            catch (Exception ex)
            {
                // First run, or a cache written by a different version.
                Diag.Failed("ChatCache.Load", ex);
            }
            return new List<ChatMessage>();
        }

        /// <summary>
        /// Writes what the server just sent. Without the avatar bytes: those are
        /// rebuilt on every connection, and a megabyte file for a list that only
        /// serves to fill the first seconds is not a good trade.
        /// </summary>
        public static async Task SaveAsync(List<ChatMessage> chats)
        {
            if (chats == null || chats.Count == 0) return;

            try
            {
                var slim = new List<ChatMessage>();
                for (int i = 0; i < chats.Count; i++) slim.Add(Slim(chats[i]));

                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, new ChatCacheFile { Chats = slim });
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                // A cache that does not write is not a failure to show: the list
                // arrives from the server anyway.
                Diag.Failed("ChatCache.Save", ex);
            }
        }

        /// <summary>
        /// The copy of a row with only the fields the list draws.
        ///
        /// The unread count is not here on purpose. It is a live number the
        /// adapter owns and clears with the `read` frame; a copy of it on disk is
        /// a number nobody has verified since, and re-applying it brought back a
        /// count the reader had already cleared.
        /// </summary>
        private static ChatMessage Slim(ChatMessage row)
        {
            return new ChatMessage
            {
                ChatId = row.ChatId,
                SenderName = row.SenderName,
                Text = row.Text,
                IsGroup = row.IsGroup,
                Timestamp = row.Timestamp
            };
        }
    }
}
