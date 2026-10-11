using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The three keys a media blob is encrypted, decrypted and signed with, derived from the
    /// one key the server hands out with the message.
    ///
    /// The scheme is the reference's (`@whiskeysockets/baileys`, `Utils/messages-media.js`,
    /// `getMediaKeys`): HKDF-SHA256 over the 32-byte media key, expanded to **112** bytes with
    /// the info string `WhatsApp &lt;Type&gt; Keys` - the mapping is `Defaults/index.js`,
    /// where a photo is `Image`, a voice note `Audio`, a video `Video` and a document
    /// `Document` - and then split `iv` 0..16, `cipherKey` 16..48, `macKey` 48..80. The last
    /// 32 bytes are not used by anything this client does, and they are part of the expansion
    /// because the length is what HKDF's output depends on: expanding to 80 bytes would give
    /// three different keys.
    /// </summary>
    public static class MediaKeys
    {
        /// <summary>The media types the info string is built from, as the reference names them.</summary>
        public const string Image = "image";
        public const string Video = "video";
        public const string Audio = "audio";
        public const string Document = "document";

        private const int IvLength = 16;
        private const int CipherKeyLength = 32;
        private const int MacKeyLength = 32;
        private const int ExpandedLength = 112;

        /// <summary>The type word inside the info string. An unknown type is refused rather than guessed.</summary>
        public static string NameOf(string mediaType)
        {
            if (mediaType == Image) return "Image";
            if (mediaType == Video) return "Video";
            if (mediaType == Audio) return "Audio";
            if (mediaType == Document) return "Document";

            throw new ProtocolException("not a media type this client knows: " + mediaType);
        }

        /// <summary>The info string HKDF is expanded with.</summary>
        public static string Info(string mediaType)
        {
            return "WhatsApp " + NameOf(mediaType) + " Keys";
        }

        /// <summary>The whole expansion, 112 bytes, in the order the reference splits it.</summary>
        public static byte[] Expand(byte[] mediaKey, string mediaType)
        {
            if (mediaKey == null || mediaKey.Length != 32)
            {
                throw new ProtocolException("a media key is 32 bytes, not "
                    + (mediaKey == null ? "nothing" : mediaKey.Length.ToString()));
            }

            byte[] info = System.Text.Encoding.UTF8.GetBytes(Info(mediaType));

            // The salt is empty and not null: HKDF's extract step with no salt is HMAC keyed
            // with a block of zeros, which is what the reference's own hkdf does.
            return Hkdf.Derive(mediaKey, new byte[0], info, ExpandedLength);
        }

        public static byte[] Iv(byte[] mediaKey, string mediaType)
        {
            return Slice(Expand(mediaKey, mediaType), 0, IvLength);
        }

        public static byte[] CipherKey(byte[] mediaKey, string mediaType)
        {
            return Slice(Expand(mediaKey, mediaType), IvLength, CipherKeyLength);
        }

        public static byte[] MacKey(byte[] mediaKey, string mediaType)
        {
            return Slice(Expand(mediaKey, mediaType), IvLength + CipherKeyLength, MacKeyLength);
        }

        private static byte[] Slice(byte[] source, int at, int length)
        {
            var slice = new byte[length];
            System.Buffer.BlockCopy(source, at, slice, 0, length);

            return slice;
        }
    }
}
