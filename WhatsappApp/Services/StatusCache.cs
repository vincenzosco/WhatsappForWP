using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using WhatsappApp.Models;
using WhatsappApp.Protocol.Media;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Services
{
    /// <summary>The on-disk file: the statuses of the last day.</summary>
    [DataContract]
    internal class StatusCacheFile
    {
        [DataMember]
        public List<StatusCacheRow> Statuses { get; set; }
    }

    /// <summary>
    /// One status as the phone keeps it.
    ///
    /// No bytes: a photo is a few hundred KB and the copy here is only what the screen
    /// needs to *list* an update and what a tap needs to ask for the file again. The
    /// keys and the address travel (they are the same fields `MediaInfo` carries), so an
    /// update restored from here is a row that opens like any other.
    /// </summary>
    [DataContract]
    internal class StatusCacheRow
    {
        [DataMember]
        public string Id { get; set; }

        /// <summary>The author's JID, without the device: the feed's row is keyed on it.</summary>
        [DataMember]
        public string Author { get; set; }

        [DataMember]
        public string Name { get; set; }

        [DataMember]
        public string Text { get; set; }

        /// <summary>When it was posted, as the app's local time.</summary>
        [DataMember]
        public DateTime PostedAt { get; set; }

        [DataMember]
        public bool Viewed { get; set; }

        // The media, when it had any: the type the row draws and the six things a fetch
        // needs (`MediaInfo.IsFetchable`). A text status writes nothing here.
        [DataMember]
        public string MediaType { get; set; }

        [DataMember]
        public string MimeType { get; set; }

        [DataMember]
        public string FileName { get; set; }

        [DataMember]
        public string Key { get; set; }

        [DataMember]
        public string DirectPath { get; set; }

        [DataMember]
        public string Url { get; set; }

        [DataMember]
        public string FileSha { get; set; }

        [DataMember]
        public string FileEncSha { get; set; }

        [DataMember]
        public long Length { get; set; }

        [DataMember]
        public long KeyTimestamp { get; set; }
    }

    /// <summary>
    /// The status feed of the last session, kept on the phone.
    ///
    /// Why it exists: the statuses arrive with the history sync, and WhatsApp sends that
    /// burst **once**, at the pairing. The feed lived in memory only, so every start of
    /// the app opened the status tab empty (`native: the feed has 0 status(es)`) and it
    /// stayed empty until a contact posted something new: the one list a person checks
    /// in the morning was the one list that could not survive a restart.
    ///
    /// A status lives 24 hours, so the copy does too: older rows are dropped on the way
    /// in and on the way out. The bytes are not kept - the viewer downloads a photo when
    /// it is opened, which is what it already does for a row the history brought.
    ///
    /// It is a photograph, not the truth: the names in it are the ones the app resolved
    /// when it wrote it, and the next connection re-tells the feed anyway.
    /// </summary>
    public static class StatusCache
    {
        private const string FileName = "statuses.json";

        /// <summary>The name the file is built under before it replaces `statuses.json`.</summary>
        private const string TempName = "statuses.json.new";

        /// <summary>How long a status is worth showing: WhatsApp's own day.</summary>
        public const int LifetimeHours = 24;

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(StatusCacheFile));

        /// <summary>
        /// The statuses still worth showing, newest first. Never an exception: on first
        /// run there is no file, and an empty list is the truth.
        /// </summary>
        public static async Task<List<ChatMessage>> LoadAsync()
        {
            var rows = new List<ChatMessage>();

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(json)) return rows;

                StatusCacheFile cache;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    cache = Serializer.ReadObject(stream) as StatusCacheFile;
                }

                if (cache == null || cache.Statuses == null) return rows;

                DateTime cutoff = DateTime.Now.AddHours(-LifetimeHours);

                // One row per status, by its id. This file has one writer, but a copy written
                // by an older build can carry the same status twice, and a feed that draws it
                // twice is an update somebody watches once and the ring fills for twice.
                var seen = new HashSet<string>(StringComparer.Ordinal);

                for (int i = 0; i < cache.Statuses.Count; i++)
                {
                    StatusCacheRow entry = cache.Statuses[i];
                    if (entry == null || string.IsNullOrEmpty(entry.Id)) continue;
                    if (string.IsNullOrEmpty(entry.Author)) continue;
                    // The day has passed: the update is not on anybody's phone any more,
                    // and a row whose date is not a date (a cache written by an older
                    // build) is not something to guess about.
                    if (entry.PostedAt < cutoff) continue;
                    if (!seen.Add(entry.Id)) continue;

                    rows.Add(ToMessage(entry));
                }
            }
            catch (FileNotFoundException)
            {
                // First start: there is no copy on the phone. Normal, not a failure.
            }
            catch (Exception ex)
            {
                // A file written by a different version.
                Diag.Failed("StatusCache.Load", ex);
            }

            return rows;
        }

        /// <summary>
        /// Writes the feed. Called by `DataService.SaveStatusSnapshot`, which is the one
        /// writer: a status that arrives, one that is watched and one whose photo lands
        /// all describe the same list, and a second writer with its own idea of it is how
        /// the file ends up older than the screen. Never an exception.
        /// </summary>
        public static async Task SaveAsync(IList<ChatMessage> statuses)
        {
            if (statuses == null) return;

            try
            {
                DateTime cutoff = DateTime.Now.AddHours(-LifetimeHours);
                var fresh = new List<StatusCacheRow>();

                for (int i = 0; i < statuses.Count; i++)
                {
                    ChatMessage row = statuses[i];
                    if (row == null || string.IsNullOrEmpty(row.Id)) continue;
                    if (string.IsNullOrEmpty(row.ChatId)) continue;
                    if (row.Timestamp < cutoff) continue;

                    fresh.Add(ToRow(row));
                }

                // An empty feed writes nothing: the file of the last day is worth more
                // than an empty one, and a status tab that lost a day to a start where
                // nothing arrived would be the bug this cache exists to fix.
                if (fresh.Count == 0) return;

                string json;
                using (var stream = new MemoryStream())
                {
                    Serializer.WriteObject(stream, new StatusCacheFile { Statuses = fresh });
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                // Beside the file and then moved over it, like the chat list: a process
                // frozen in the middle of a write leaves the previous copy, not a file
                // that does not parse.
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
                // A cache that does not write is not a failure to show: the feed arrives
                // from the connection anyway.
                Diag.Failed("StatusCache.Save", ex);
            }
        }

        /// <summary>
        /// Drops the copy. A link replaces the identity the statuses belong to, so the day
        /// of the previous one is not this account's day.
        /// </summary>
        public static async Task DeleteAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (FileNotFoundException)
            {
                // A link that finds no copy is the first link, not a failure.
            }
            catch (Exception ex)
            {
                Diag.Failed("StatusCache.Delete", ex);
            }
        }

        private static ChatMessage ToMessage(StatusCacheRow entry)
        {
            var row = new ChatMessage();
            row.Id = entry.Id;
            row.ChatId = entry.Author;
            row.IsIncoming = true;
            row.Timestamp = entry.PostedAt;
            row.Text = entry.Text;
            row.SenderName = entry.Name;
            row.IsStatusViewed = entry.Viewed;

            if (!string.IsNullOrEmpty(entry.MediaType))
            {
                row.MediaType = entry.MediaType;

                // The row draws by its type, and the viewer decides by it too: the media
                // type is what tells an empty bubble from a status that is words alone.
                if (string.Equals(entry.MediaType, MediaKeys.Image, StringComparison.OrdinalIgnoreCase))
                    row.Type = MessageType.Image;
                else if (string.Equals(entry.MediaType, MediaKeys.Video, StringComparison.OrdinalIgnoreCase))
                    row.Type = MessageType.Video;
            }

            if (!string.IsNullOrEmpty(entry.MimeType)) row.MediaMimeType = entry.MimeType;
            if (!string.IsNullOrEmpty(entry.FileName)) row.MediaFileName = entry.FileName;

            row.Media = MediaOf(entry);
            return row;
        }

        /// <summary>
        /// The keys and the address of the file, so a tap on a restored status downloads
        /// it the same way it downloads one the history brought. Null when the row carried
        /// no media, or not enough of it to fetch (`MediaInfo.IsFetchable`).
        /// </summary>
        private static MediaInfo MediaOf(StatusCacheRow entry)
        {
            if (string.IsNullOrEmpty(entry.MediaType)) return null;
            if (string.IsNullOrEmpty(entry.Key)) return null;

            byte[] key;
            try
            {
                key = Convert.FromBase64String(entry.Key);
            }
            catch (FormatException)
            {
                return null;
            }

            var info = new MediaInfo();
            info.Type = entry.MediaType;
            info.MediaKey = key;
            info.DirectPath = entry.DirectPath;
            info.Url = entry.Url;
            info.FileSha256 = Decode(entry.FileSha);
            info.FileEncSha256 = Decode(entry.FileEncSha);
            info.FileLength = entry.Length;
            info.MediaKeyTimestamp = entry.KeyTimestamp;
            info.MimeType = entry.MimeType;
            info.FileName = entry.FileName;

            return info.IsFetchable ? info : null;
        }

        private static StatusCacheRow ToRow(ChatMessage row)
        {
            var entry = new StatusCacheRow();
            entry.Id = row.Id;
            entry.Author = row.ChatId;
            entry.Name = row.SenderName;
            entry.Text = row.Text;
            entry.PostedAt = row.Timestamp;
            entry.Viewed = row.IsStatusViewed;
            entry.MediaType = row.MediaType;
            entry.MimeType = row.MediaMimeType;
            entry.FileName = row.MediaFileName;

            MediaInfo info = row.Media;
            if (info != null)
            {
                entry.Key = Encode(info.MediaKey);
                entry.DirectPath = info.DirectPath;
                entry.Url = info.Url;
                entry.FileSha = Encode(info.FileSha256);
                entry.FileEncSha = Encode(info.FileEncSha256);
                entry.Length = info.FileLength;
                entry.KeyTimestamp = info.MediaKeyTimestamp;

                if (string.IsNullOrEmpty(entry.MimeType)) entry.MimeType = info.MimeType;
                if (string.IsNullOrEmpty(entry.FileName)) entry.FileName = info.FileName;
                if (string.IsNullOrEmpty(entry.MediaType)) entry.MediaType = info.Type;
            }

            return entry;
        }

        private static string Encode(byte[] bytes)
        {
            return bytes == null || bytes.Length == 0 ? null : Convert.ToBase64String(bytes);
        }

        private static byte[] Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            try
            {
                return Convert.FromBase64String(text);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}
