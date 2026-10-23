using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// A received media, complete. Either the bytes in base64 (image, drawn at
    /// once) or the local file name (video, audio, document: the bytes sit on
    /// disk, because a whole video or document in memory on a 512 MB phone does
    /// not fit, and a player wants a file).
    /// </summary>
    public sealed class IncomingMediaResult
    {
        public string MediaType;
        public string MimeType;
        public string Base64;
        public string LocalFileName;

        /// <summary>The name the file had on the other end, when the message carried one.</summary>
        public string FileName;

        /// <summary>How many bytes were written, 0 when the media stayed in memory.</summary>
        public long SizeBytes;
    }

    /// <summary>
    /// Where the bytes of one received media end up, and the file name they get.
    ///
    /// The one decision this class makes is `ToDisk`: an image is drawn at once and
    /// stays in memory as base64, while a video, a voice note and a document are
    /// written to `LocalFolder`, because a whole video in memory on a 512 MB phone
    /// does not fit. `Protocol/Media/MediaDownload.cs` hands over the whole blob -
    /// this client fetches and decrypts it itself - so there is nothing to
    /// reassemble here, only somewhere to put it.
    /// </summary>
    public static class IncomingMediaStore
    {
        private const string Prefix = "incoming_";

        /// <summary>
        /// One transfer at a time.
        ///
        /// Every transfer writes a file, and its callers do not all await
        /// (`DataService` stores a media from an `async void` path): two of them
        /// started at once would write into the same folder, and a file that was
        /// named for one message could be left half-filled by another.
        /// </summary>
        private static readonly SerialQueue Transfers = new SerialQueue();

        /// <summary>
        /// A media this client fetched and decrypted itself, in one piece. The bytes
        /// enter the queue, so the naming and the write are a single operation.
        /// </summary>
        public static Task<IncomingMediaResult> StoreAsync(string messageId, string mediaType,
            string mimeType, string fileName, byte[] bytes)
        {
            return Transfers.RunAsync(delegate
            {
                return StoreCoreAsync(messageId, mediaType, mimeType, fileName, bytes);
            });
        }

        private static async Task<IncomingMediaResult> StoreCoreAsync(string messageId, string mediaType,
            string mimeType, string fileName, byte[] bytes)
        {
            if (bytes == null || messageId == null) return null;

            string type = string.IsNullOrEmpty(mediaType) ? "image" : mediaType.ToLower();

            var result = new IncomingMediaResult
            {
                MediaType = type,
                MimeType = mimeType,
                FileName = fileName,
                SizeBytes = bytes.Length
            };

            if (!ToDisk(type))
            {
                result.Base64 = Convert.ToBase64String(bytes);
                return result;
            }

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    LocalNameFor(messageId, mimeType, fileName), CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteBytesAsync(file, bytes);

                result.LocalFileName = file.Name;
                return result;
            }
            catch (Exception ex)
            {
                Diag.Failed("IncomingMediaStore.StoreAsync", ex);
                return null;
            }
        }

        /// <summary>
        /// The types that do not fit in memory and go to a file: a video, an
        /// audio, a document. An image is drawn at once from base64; a sticker is
        /// an image.
        /// </summary>
        private static bool ToDisk(string mediaType)
        {
            return string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "audio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "document", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A local file name for a message: cleaned id + extension.</summary>
        private static string LocalNameFor(string messageId, string mimeType, string fileName)
        {
            return Prefix + SafeName(messageId) + ExtensionFor(mimeType, fileName);
        }

        /// <summary>An id cleaned of the characters a file name does not accept.</summary>
        private static string SafeName(string value)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(value))
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                        || (c >= '0' && c <= '9') || c == '-' || c == '_')
                    {
                        builder.Append(c);
                    }
                }
            }
            if (builder.Length == 0) builder.Append("media");
            return builder.ToString();
        }

        /// <summary>
        /// The files of the messages of a chat that was deleted.
        ///
        /// Why it exists: the bytes of a video, a voice note or a document are a file in the
        /// phone's folder, and nothing ever removed one. A conversation deleted from the list
        /// left its megabytes behind; on a 512 MB phone that is the storage the session, the
        /// caches and the tile cards all write into, and when it fills every one of them fails.
        ///
        /// A file that is not there is not a failure: an image never went to disk at all, and a
        /// row whose file failed to write has nothing to drop.
        /// </summary>
        public static async Task DeleteAsync(IList<ChatMessage> messages)
        {
            if (messages == null) return;

            int dropped = 0;

            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage message = messages[i];
                if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) continue;

                try
                {
                    StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(message.MediaFilePath);
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    dropped++;
                }
                catch (FileNotFoundException)
                {
                    // Nothing to drop.
                }
                catch (Exception ex)
                {
                    Diag.Failed("IncomingMediaStore/delete", ex);
                }
            }

            if (dropped > 0) Diag.Ok("media: " + dropped + " file(s) of a deleted chat dropped");
        }

        /// <summary>The file extension, from the name or the MIME type.</summary>
        private static string ExtensionFor(string mimeType, string fileName)
        {
            string name = fileName ?? "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) return name.Substring(dot).ToLower();

            string mime = (mimeType ?? "").ToLower();
            // A video first, and by its own name: `video/mp4` matched no rule at all - the
            // `audio/mp4`/`mp4a` line below asks for that exact word - so a nameless video was
            // saved as `.bin`, a file the gallery and the player refuse.
            if (mime.IndexOf("video/mp4") >= 0) return ".mp4";
            if (mime.IndexOf("3gp") >= 0) return ".3gp";
            if (mime.IndexOf("quicktime") >= 0) return ".mov";
            if (mime.IndexOf("webm") >= 0) return ".webm";
            if (mime.IndexOf("matroska") >= 0) return ".mkv";
            if (mime.IndexOf("msvideo") >= 0) return ".avi";
            // Audio: a voice note is whatever WhatsApp sent (this phone records M4A
            // and plays it back itself), and the type is what decides here.
            if (mime.IndexOf("mpeg") >= 0) return ".mp3";
            if (mime.IndexOf("audio/mp4") >= 0 || mime.IndexOf("mp4a") >= 0) return ".m4a";
            if (mime.IndexOf("amr") >= 0) return ".amr";
            if (mime.IndexOf("wav") >= 0) return ".wav";
            if (mime.IndexOf("ogg") >= 0 || mime.IndexOf("opus") >= 0) return ".ogg";
            if (mime.IndexOf("pdf") >= 0) return ".pdf";
            // A document always carries its name, so only a nameless file arrives
            // here: whoever opens it chooses the extension.
            return ".bin";
        }
    }
}
