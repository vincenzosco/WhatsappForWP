using System.Threading.Tasks;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The history a notification hides behind a media download: fetch it, open it like any
    /// other media blob, and inflate what comes out.
    ///
    /// Why this is its own file rather than a branch inside <see cref="MediaDownload"/>: a
    /// history sync is not a message's media even though it travels the same way. The
    /// reference fetches it, decrypts it and only then inflates it (`Utils/history.ts`,
    /// `downloadHistory`), and those two steps - the inflate, and the fact that the
    /// notification's own fields are what the checks are given - are the whole of what this
    /// adds. What is *not* here matters as much: the MAC is still checked before the decrypt,
    /// `fileSha256` is still checked over the decrypted bytes, and a notification that names
    /// no key is refused with a sentence instead of leaving the chat list empty with nothing
    /// in the log.
    ///
    /// One trap is worth naming, because the first draft of this file fell into it: the media
    /// path's own `FetchAsync` fetches **and** opens, so its answer is the decrypted payload
    /// and not the body on the wire. Opening that answer again checks a MAC over plaintext
    /// and refuses a blob WhatsApp really sent. Both entry points below therefore end in
    /// <see cref="Inflate"/> over bytes that have been opened exactly once.
    ///
    /// It compiles into the phone and into the desktop test host, and it reaches HTTP only
    /// through <see cref="IMediaFetcher"/>.
    /// </summary>
    public static class HistoryBlob
    {
        /// <summary>
        /// The bytes a download carries, decrypted and inflated: what
        /// <see cref="HistorySync.Decode"/> takes.
        ///
        /// The three values the media path checks are the notification's own, and they
        /// describe the **compressed** payload - WhatsApp deflates the history before it
        /// encrypts it, so the inflate is the step after the checks, never before them.
        /// </summary>
        public static byte[] Open(byte[] body, HistorySyncNotification notification)
        {
            Check(notification);

            return Inflate(MediaDownload.Open(body, notification.MediaKey, MediaKeys.History,
                notification.FileSha256, notification.FileEncSha256, notification.FileLength,
                notification.DirectPath));
        }

        /// <summary>
        /// The same bytes, from the `directPath` the notification names. The address, the
        /// signature and the hashes are the media path's doing; this only says which blob to
        /// ask for and what to do with what comes back.
        /// </summary>
        public static async Task<byte[]> FetchAsync(IMediaFetcher fetcher,
            HistorySyncNotification notification)
        {
            if (fetcher == null)
            {
                throw new ProtocolException("a download needs something to fetch with");
            }

            Check(notification);

            byte[] compressed = await MediaDownload.FetchAsync(fetcher, notification.MediaKey,
                MediaKeys.History, notification.DirectPath, null, notification.FileSha256,
                notification.FileEncSha256, notification.FileLength);

            return Inflate(compressed);
        }

        /// <summary>
        /// What a notification has to name before anything is fetched or opened. A missing
        /// field is a sentence naming it, because the alternative - an empty chat list and a
        /// silent log - is the failure this whole path exists to end.
        /// </summary>
        private static void Check(HistorySyncNotification notification)
        {
            if (notification == null)
            {
                throw new ProtocolException("there is no history notification to open");
            }

            if (notification.MediaKey == null || notification.MediaKey.Length != 32)
            {
                throw new ProtocolException("the history blob names no 32-byte mediaKey");
            }

            if (string.IsNullOrEmpty(notification.DirectPath))
            {
                throw new ProtocolException("the history blob names no directPath to fetch");
            }
        }

        /// <summary>
        /// The one step a message's media does not have: WhatsApp deflates the history before
        /// it encrypts it, so the checks above proved the compressed bytes - which is exactly
        /// what the notification's `fileSha256` and `fileLength` describe.
        /// </summary>
        private static byte[] Inflate(byte[] compressed)
        {
            return Zlib.Inflate(compressed);
        }
    }
}
