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
    /// <summary>
    /// The on-disk file: the list of rows, and whether the sync that wrote it had
    /// finished.
    /// </summary>
    [DataContract]
    internal class ChatCacheFile
    {
        [DataMember]
        public List<ChatMessage> Chats { get; set; }

        /// <summary>
        /// Whether WhatsApp had said the history was all here when this file was
        /// written.
        ///
        /// Why it is on disk: the burst of history is pushed once per link, at the
        /// pairing (`NativeConnection`, the run of 2026-10-22). A session that opens
        /// on a copy written by a finished sync has nothing left to wait for, and
        /// the conversation page is the one that needs to know: it waits twenty
        /// seconds for that burst before binding a chat, which on every start after
        /// the first was twenty seconds of an empty screen for a burst that was
        /// never coming.
        /// </summary>
        [DataMember]
        public bool HistoryComplete { get; set; }
    }

    /// <summary>
    /// The chat list of the last session, kept on the phone.
    ///
    /// Why it exists: at startup the connection is not there yet, and the list
    /// arrives with the sync that follows a login. Without this copy the app opens
    /// on an empty list for the seconds it takes, and it looks like nothing
    /// happened.
    ///
    /// It is not the truth: it is a photograph. As soon as the server answers,
    /// every row is replaced by the real one (see DataService.ApplyChat). Without
    /// the avatar bytes the copy stays small, and the picture arrives with the
    /// first update.
    /// </summary>
    public static class ChatCache
    {
        private const string FileName = "chats.json";

        /// <summary>The name the file is built under before it replaces `chats.json`.</summary>
        private const string TempName = "chats.json.new";

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(ChatCacheFile));

        /// <summary>
        /// The saved rows and the flag they were written with. Never an exception:
        /// on first run there is no file, and an empty answer is the truth.
        /// </summary>
        internal static async Task<ChatCacheFile> LoadAsync()
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
                        if (cache != null && cache.Chats != null) return cache;
                    }
                }
            }
            catch (Exception ex)
            {
                // First run, or a cache written by a different version.
                Diag.Failed("ChatCache.Load", ex);
            }
            return new ChatCacheFile { Chats = new List<ChatMessage>() };
        }

        /// <summary>
        /// Drops the copy. A link replaces the identity the rows belong to, so the
        /// list of the previous one is not this account's list.
        /// </summary>
        public static async Task DeleteAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception ex)
            {
                // A file that is not there is not a failure: the first link deletes
                // a copy that was never written.
                Diag.Failed("ChatCache.Delete", ex);
            }
        }

        /// <summary>
        /// Writes the list. Without the avatar bytes: those are rebuilt on every
        /// connection, and a megabyte file for a list that only serves to fill the
        /// first seconds is not a good trade.
        ///
        /// Called by `DataService.SaveSnapshotAsync`, which is the one writer: the last
        /// sync, a suspension and a background check all describe the same list, and a
        /// second writer with its own idea of the rows is how a file ends up older than
        /// the screen.
        /// </summary>
        public static async Task SaveAsync(List<ChatMessage> chats, bool historyComplete)
        {
            if (chats == null || chats.Count == 0) return;

            try
            {
                var slim = new List<ChatMessage>();
                for (int i = 0; i < chats.Count; i++) slim.Add(Slim(chats[i]));

                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream,
                        new ChatCacheFile { Chats = slim, HistoryComplete = historyComplete });
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                // Written beside the file and then moved over it, not written into it.
                // The file is now rewritten on every suspension and at the end of every
                // background check (see DataService.SaveSnapshotAsync), and a process
                // frozen in the middle of a write leaves a `chats.json` that does not
                // parse - a list with no rows, which is the screen that says nothing has
                // ever happened. A move is one step, so the file is either the old one or
                // the new one.
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile temp = await folder.CreateFileAsync(
                    TempName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(temp, json);

                StorageFile current = null;
                try
                {
                    current = await folder.GetFileAsync(FileName);
                }
                catch (FileNotFoundException)
                {
                    // First write of this phone: there is nothing to replace.
                }

                if (current == null) await temp.RenameAsync(FileName);
                else await temp.MoveAndReplaceAsync(current);
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
        /// The unread count is not here on purpose. It is a live number, cleared by
        /// the receipt the reader's own page sends; a copy of it on disk is a number
        /// nobody has verified since, and re-applying it brought back a count the
        /// reader had already cleared.
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
