using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
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

        /// <summary>The name the file had on the other end, when the frame carried one.</summary>
        public string FileName;

        /// <summary>How many bytes were written, 0 when the media stayed in memory.</summary>
        public long SizeBytes;
    }

    /// <summary>
    /// The bytes of an incoming media, piece by piece.
    ///
    /// Why it exists: a frame has an 8 MiB ceiling and the content travels in
    /// base64, which adds a third; a video does not fit in a single frame. The
    /// adapter splits it (see server.js, sendMediaChunks) and it is reassembled
    /// here.
    ///
    /// A video, an audio or a document is written to disk as it arrives, one piece
    /// at a time: holding a base64 of tens of MB and then decoding it all at once
    /// is the fastest way to get the app closed by a 512 MB phone. The pieces are
    /// multiples of 4 base64 characters, so they decode on their own.
    /// </summary>
    public static class IncomingMediaStore
    {
        private const string Prefix = "incoming_";

        private sealed class Pending
        {
            public int Total;
            public int Received;
            public string MediaType;
            public string MimeType;
            public bool ToDisk;
            public StorageFile File;
            public IRandomAccessStream Stream;
            public DataWriter Writer;
            public StringBuilder Base64;
            public string FileName;
            public long Bytes;
        }

        private static readonly Dictionary<string, Pending> Transfers =
            new Dictionary<string, Pending>();

        /// <summary>
        /// The pieces of all incoming media, one at a time.
        ///
        /// Why it is needed: the order check below compares the piece index with
        /// Received, and Received is incremented after the write to disk. The path
        /// that leads here does not await (DispatchOnUiThread is async void,
        /// ApplyMediaFrame is async void), so the next piece could arrive while the
        /// first was still writing: the comparison failed, the media was dropped,
        /// and every following piece opened another one that failed the same way. A
        /// video or a voice note never arrived.
        /// </summary>
        private static readonly SerialQueue Chunks = new SerialQueue();

        /// <summary>
        /// Adds a piece. It returns null until the media is complete. The piece
        /// enters the queue: the order check and the write must be a single
        /// operation.
        /// </summary>
        public static Task<IncomingMediaResult> AddChunkAsync(ChatMessage frame)
        {
            return Chunks.RunAsync(delegate { return AddChunkCoreAsync(frame); });
        }

        private static async Task<IncomingMediaResult> AddChunkCoreAsync(ChatMessage frame)
        {
            if (frame == null || string.IsNullOrEmpty(frame.RelatedMessageId)) return null;
            if (string.IsNullOrEmpty(frame.MediaData)) return null;

            int total = frame.MediaChunkTotal > 0 ? frame.MediaChunkTotal : 1;

            Pending pending;
            if (!Transfers.TryGetValue(frame.RelatedMessageId, out pending) || pending.Total != total)
            {
                if (pending != null) Abandon(pending);
                pending = await StartAsync(frame, total);
                if (pending == null) return null;
                Transfers[frame.RelatedMessageId] = pending;
            }

            // The pieces arrive in order on the same socket: a jump is a fault,
            // and composing wrong bytes is worse than showing nothing.
            if (frame.MediaChunkIndex != pending.Received)
            {
                Transfers.Remove(frame.RelatedMessageId);
                Abandon(pending);
                return null;
            }

            try
            {
                if (pending.ToDisk)
                {
                    byte[] part = Convert.FromBase64String(frame.MediaData);
                    pending.Writer.WriteBytes(part);
                    await pending.Writer.StoreAsync();
                    pending.Bytes += part.Length;
                }
                else
                {
                    pending.Base64.Append(frame.MediaData);
                }
                pending.Received++;
            }
            catch (Exception ex)
            {
                Transfers.Remove(frame.RelatedMessageId);
                Abandon(pending);
                Diag.Failed("IncomingMediaStore.AddChunkAsync", ex);
                return null;
            }

            if (pending.Received < pending.Total) return null;

            Transfers.Remove(frame.RelatedMessageId);

            var result = new IncomingMediaResult
            {
                MediaType = pending.MediaType,
                MimeType = pending.MimeType,
                FileName = pending.FileName,
                SizeBytes = pending.Bytes
            };

            if (pending.ToDisk)
            {
                await pending.Writer.FlushAsync();
                pending.Writer.DetachStream();
                pending.Stream.Dispose();
                pending.Stream = null;
                pending.Writer = null;
                result.LocalFileName = pending.File.Name;
            }
            else
            {
                result.Base64 = pending.Base64.ToString();
            }

            return result;
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

        private static async Task<Pending> StartAsync(ChatMessage frame, int total)
        {
            bool toDisk = ToDisk(frame.MediaType);

            var pending = new Pending
            {
                Total = total,
                // The type declared by the message, when there is one: an old
                // single-frame media does not declare it, and was an image.
                MediaType = string.IsNullOrEmpty(frame.MediaType)
                    ? "image"
                    : frame.MediaType.ToLower(),
                MimeType = frame.MediaMimeType,
                FileName = frame.MediaFileName,
                ToDisk = toDisk
            };

            if (!toDisk)
            {
                pending.Base64 = new StringBuilder();
                return pending;
            }

            try
            {
                string name = LocalNameFor(frame.RelatedMessageId, frame.MediaMimeType, frame.MediaFileName);
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    name, CreationCollisionOption.ReplaceExisting);
                IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
                pending.File = file;
                pending.Stream = stream;
                pending.Writer = new DataWriter(stream);
                return pending;
            }
            catch (Exception ex)
            {
                Diag.Failed("IncomingMediaStore.StartAsync", ex);
                return null;
            }
        }

        /// <summary>Drops a received media: it is never assembled halfway.</summary>
        private static void Abandon(Pending pending)
        {
            if (pending == null) return;
            try
            {
                if (pending.Writer != null) pending.Writer.DetachStream();
                if (pending.Stream != null) pending.Stream.Dispose();
            }
            catch (Exception ex)
            {
                Diag.Failed("IncomingMediaStore.Abandon", ex);
            }
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

        /// <summary>The file extension, from the name or the MIME type.</summary>
        private static string ExtensionFor(string mimeType, string fileName)
        {
            string name = fileName ?? "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) return name.Substring(dot).ToLower();

            string mime = (mimeType ?? "").ToLower();
            if (mime.IndexOf("3gp") >= 0) return ".3gp";
            if (mime.IndexOf("quicktime") >= 0) return ".mov";
            if (mime.IndexOf("webm") >= 0) return ".webm";
            if (mime.IndexOf("matroska") >= 0) return ".mkv";
            if (mime.IndexOf("msvideo") >= 0) return ".avi";
            // Audio: a voice note is already an MP3 when it arrives here (the
            // adapter converts it), but the type is checked anyway for the others.
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
