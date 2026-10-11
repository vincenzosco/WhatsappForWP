using System.Threading.Tasks;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Transport;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// Media that arrives: fetch, check the signature, decrypt, check the file.
    ///
    /// The body the server serves is `ciphertext || MAC`, where the MAC is the first ten bytes
    /// of `HMAC-SHA256(macKey, iv || ciphertext)` (the reference computes it that way in
    /// `Utils/messages-media.js`, lines 324 to 354). Ten bytes is short for a signature and
    /// that is the point: it is checked *before* the ciphertext is decrypted, so a blob that
    /// was tampered with in transit never reaches the cipher, and the caller's failure names
    /// which of the two checks refused it.
    ///
    /// The order is the whole design and it is worth stating: MAC first (cheap, and it is what
    /// keeps a wrong key from being a decryption oracle), then the decrypt, then `fileSha256`
    /// over the plaintext, which is the check that catches a *correctly signed* body that is
    /// not the file the message described.
    /// </summary>
    public static class MediaDownload
    {
        /// <summary>The MAC the server appends: ten bytes, no more.</summary>
        public const int MacLength = 10;

        /// <summary>The host every `directPath` in a message is relative to.</summary>
        public const string DefaultHost = "https://mmg.whatsapp.net";

        /// <summary>
        /// The file behind a media message, or a <see cref="ProtocolException"/> naming the
        /// check that failed. `directPath` is preferred over `url` when both are present: the
        /// reference does the same, because the path is the one the server keeps alive
        /// longest.
        /// </summary>
        public static async Task<byte[]> FetchAsync(IMediaFetcher fetcher, byte[] mediaKey,
            string mediaType, string directPath, string url, byte[] fileSha256, byte[] fileEncSha256,
            long fileLength)
        {
            if (fetcher == null) throw new ProtocolException("a download needs something to fetch with");

            string address = Address(directPath, url);
            byte[] body = await fetcher.GetAsync(address);

            return Open(body, mediaKey, mediaType, fileSha256, fileEncSha256, fileLength, address);
        }

        /// <summary>
        /// The same without the fetch: the checks and the decryption, for a body that is
        /// already in hand - which is what the desktop tests exercise and what a resumed
        /// download will use.
        /// </summary>
        public static byte[] Open(byte[] body, byte[] mediaKey, string mediaType,
            byte[] fileSha256, byte[] fileEncSha256, long fileLength, string address)
        {
            if (body == null) throw new ProtocolException("there is no media body to open");

            byte[] iv = MediaKeys.Iv(mediaKey, mediaType);
            byte[] cipherKey = MediaKeys.CipherKey(mediaKey, mediaType);
            byte[] macKey = MediaKeys.MacKey(mediaKey, mediaType);

            if (body.Length <= MacLength)
            {
                // A body this short cannot even carry a MAC, and it is checked before the
                // decrypt so the caller is told the download was truncated rather than being
                // handed a padding error from the cipher.
                throw new ProtocolException("the media at " + address + " is " + body.Length
                    + " bytes: shorter than its own signature");
            }

            int cipherLength = body.Length - MacLength;
            var ciphertext = new byte[cipherLength];
            System.Buffer.BlockCopy(body, 0, ciphertext, 0, cipherLength);

            byte[] expected = Hmac.Compute(macKey, Concat(iv, ciphertext));
            if (!MacMatches(expected, body, cipherLength))
            {
                throw new ProtocolException("the media at " + address + " does not carry the signature its key expects");
            }

            byte[] plaintext = Aes.CbcDecrypt(cipherKey, iv, ciphertext);

            if (fileSha256 != null && !Hmac.FixedTimeEquals(fileSha256, Sha256.Hash(plaintext)))
            {
                throw new ProtocolException("the media at " + address + " decrypted to something else than fileSha256 says");
            }

            if (fileEncSha256 != null && !Hmac.FixedTimeEquals(fileEncSha256, Sha256.Hash(body)))
            {
                throw new ProtocolException("the media at " + address + " is not the body fileEncSha256 was taken over");
            }

            if (fileLength > 0 && plaintext.Length != fileLength)
            {
                throw new ProtocolException("the media at " + address + " is " + plaintext.Length
                    + " bytes and the message said " + fileLength);
            }

            return plaintext;
        }

        /// <summary>
        /// Which URL to ask for. `directPath` wins when it is there, and a URL that is already
        /// absolute is used as it is - the server sends both, and they are not always the
        /// same host.
        /// </summary>
        public static string Address(string directPath, string url)
        {
            if (!string.IsNullOrEmpty(directPath))
            {
                if (directPath.StartsWith("http", System.StringComparison.OrdinalIgnoreCase)) return directPath;

                return DefaultHost + directPath;
            }

            if (!string.IsNullOrEmpty(url)) return url;

            throw new ProtocolException("this media says neither where it is nor how to get there");
        }

        private static bool MacMatches(byte[] expected, byte[] body, int at)
        {
            var found = new byte[MacLength];
            System.Buffer.BlockCopy(body, at, found, 0, MacLength);

            var head = new byte[MacLength];
            System.Buffer.BlockCopy(expected, 0, head, 0, MacLength);

            return Hmac.FixedTimeEquals(head, found);
        }

        private static byte[] Concat(byte[] left, byte[] right)
        {
            var joined = new byte[left.Length + right.Length];
            System.Buffer.BlockCopy(left, 0, joined, 0, left.Length);
            System.Buffer.BlockCopy(right, 0, joined, left.Length, right.Length);

            return joined;
        }
    }
}
