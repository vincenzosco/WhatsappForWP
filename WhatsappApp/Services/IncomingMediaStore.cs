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
    /// Un media ricevuto, completo. O i byte in base64 (immagine, si disegna
    /// subito) o il nome del file locale (video: i byte stanno su disco, perche'
    /// un video intero in memoria su un telefono da 512 MB non ci sta).
    /// </summary>
    public sealed class IncomingMediaResult
    {
        public string MediaType;
        public string MimeType;
        public string Base64;
        public string LocalFileName;
    }

    /// <summary>
    /// I byte di un media in arrivo, pezzo per pezzo.
    ///
    /// Perche' esiste: un frame ha un tetto di 8 MiB e il contenuto viaggia in
    /// base64, che aggiunge un terzo; un video non ci sta in un frame solo.
    /// L'adapter lo spezza (vedi server.js, sendMediaChunks) e qui si ricompone.
    ///
    /// Un video si scrive su disco mentre arriva, un pezzo alla volta: tenere
    /// una base64 da decine di MB per poi decodificarla tutta insieme e' il modo
    /// piu' veloce per farsi chiudere l'app da un telefono da 512 MB. I pezzi
    /// sono multipli di 4 caratteri base64, quindi si decodificano da soli.
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
            public bool IsVideo;
            public StorageFile File;
            public IRandomAccessStream Stream;
            public DataWriter Writer;
            public StringBuilder Base64;
        }

        private static readonly Dictionary<string, Pending> Transfers =
            new Dictionary<string, Pending>();

        /// <summary>
        /// Aggiunge un pezzo. Restituisce null finche' il media non e' completo.
        /// </summary>
        public static async Task<IncomingMediaResult> AddChunkAsync(ChatMessage frame)
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

            // I pezzi arrivano in ordine sullo stesso socket: un salto e' un
            // guasto, e comporre bytes sbagliati e' peggio che non mostrare
            // niente.
            if (frame.MediaChunkIndex != pending.Received)
            {
                Transfers.Remove(frame.RelatedMessageId);
                Abandon(pending);
                return null;
            }

            try
            {
                if (pending.IsVideo)
                {
                    pending.Writer.WriteBytes(Convert.FromBase64String(frame.MediaData));
                    await pending.Writer.StoreAsync();
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
                MimeType = pending.MimeType
            };

            if (pending.IsVideo)
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

        private static async Task<Pending> StartAsync(ChatMessage frame, int total)
        {
            bool video = string.Equals(frame.MediaType, "video", StringComparison.OrdinalIgnoreCase);

            var pending = new Pending
            {
                Total = total,
                // Il tipo dichiarato dal messaggio, quando c'e': un vecchio
                // media di un frame solo non lo dichiara, ed era un'immagine.
                MediaType = string.IsNullOrEmpty(frame.MediaType)
                    ? "image"
                    : frame.MediaType.ToLower(),
                MimeType = frame.MediaMimeType,
                IsVideo = video
            };

            if (!video)
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

        /// <summary>Butta via un mezzo ricevuto: non si monta mai a meta'.</summary>
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

        /// <summary>Un nome di file locale per un messaggio: id ripulito + estensione.</summary>
        private static string LocalNameFor(string messageId, string mimeType, string fileName)
        {
            return Prefix + SafeName(messageId) + ExtensionFor(mimeType, fileName);
        }

        /// <summary>Un id ripulito dai caratteri che un nome di file non accetta.</summary>
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

        /// <summary>L'estensione del file, dal nome o dal tipo MIME.</summary>
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
            return ".mp4";
        }
    }
}
