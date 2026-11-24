using System.Threading.Tasks;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// Fetches and opens the external encrypted snapshot carried by an app-state sync.
    /// The reference calls this media type `md-app-state` and derives the HKDF info
    /// `WhatsApp App State Keys` before decrypting the `ciphertext || MAC` body.
    /// </summary>
    public static class AppStateSnapshotBlob
    {
        /// <summary>The decoded SyncdSnapshot bytes behind an external blob reference.</summary>
        public static async Task<byte[]> FetchAsync(IMediaFetcher fetcher, ExternalBlobReference blob)
        {
            if (fetcher == null) throw new ProtocolException("an external app-state snapshot needs something to fetch with");
            if (blob == null) throw new ProtocolException("an external app-state snapshot needs a blob reference");
            Check(blob);
            return await MediaDownload.FetchAsync(fetcher, blob.MediaKey, MediaKeys.AppState,
                blob.DirectPath, null, blob.FileSha256, blob.FileEncSha256, blob.FileSizeBytes);
        }

        /// <summary>Opens a fetched body; exposed for deterministic offline protocol tests.</summary>
        public static byte[] Open(byte[] body, ExternalBlobReference blob)
        {
            Check(blob);
            return MediaDownload.Open(body, blob.MediaKey, MediaKeys.AppState,
                blob.FileSha256, blob.FileEncSha256, blob.FileSizeBytes, blob.DirectPath);
        }

        private static void Check(ExternalBlobReference blob)
        {
            if (blob == null)
            {
                throw new ProtocolException("an external app-state snapshot needs a blob reference");
            }
            if (blob.MediaKey == null || blob.MediaKey.Length != 32)
            {
                throw new ProtocolException("an external app-state snapshot needs a 32-byte mediaKey");
            }
            if (string.IsNullOrEmpty(blob.DirectPath))
            {
                throw new ProtocolException("an external app-state snapshot needs a directPath");
            }
            if (blob.FileSha256 == null || blob.FileSha256.Length != 32)
            {
                throw new ProtocolException("an external app-state snapshot needs a 32-byte fileSha256");
            }
            if (blob.FileEncSha256 == null || blob.FileEncSha256.Length != 32)
            {
                throw new ProtocolException("an external app-state snapshot needs a 32-byte fileEncSha256");
            }
            if (blob.FileSizeBytes <= 0)
            {
                throw new ProtocolException("an external app-state snapshot needs a positive fileSizeBytes");
            }
        }
    }
}
