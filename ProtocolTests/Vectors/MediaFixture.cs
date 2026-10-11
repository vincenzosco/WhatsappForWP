namespace ProtocolTests.Vectors
{
    /// <summary>
    /// One encrypted media blob, built with the reference's own scheme.
    ///
    /// Regenerated with `node /tmp/gen-media-fixture.js` - Node's `crypto` with the parameters read
    /// out of the reference's source and not remembered:
    /// `@whiskeysockets/baileys/lib/Utils/messages-media.js` derives the three keys with
    /// `hkdf(mediaKey, 112, { info: hkdfInfoKey(type) })` and splits them `iv` 0..16,
    /// `cipherKey` 16..48, `macKey` 48..80; `Defaults/index.js` maps `image` to `Image`,
    /// so the info is `WhatsApp Image Keys`; the body is `AES-256-CBC(cipherKey, iv)` with
    /// the MAC `HMAC-SHA256(macKey, iv || ciphertext)[0..10]` appended (line 324 to 354);
    /// and `fileSha256` is over the plaintext while `fileEncSha256` is over the body.
    ///
    /// The generator is Node's crypto and not Baileys' `encryptedStream`, which needs an
    /// image pipeline this machine does not have - so this is the *same scheme* and not the
    /// same code path, and that is what the fixture is worth.
    /// </summary>
    public static class MediaFixture
    {
        /// <summary>The 32-byte key the server hands out: every byte is 0x11.</summary>
        public const string MediaKey =
            "1111111111111111111111111111111111111111111111111111111111111111";

        /// <summary>The HKDF info string for a photo.</summary>
        public const string Info = "WhatsApp Image Keys";

        public const string Iv =
            "c3994d20d318005274c80ebc6e20b29b";

        public const string CipherKey =
            "85948aeaa63e60cdf785c365ac057f150d47cb99ac8ed9c6b6ea6fe1e13f5a4c";

        public const string MacKey =
            "93ee1e1836fe3c24a01193b97e8001f2f1acad6d2e667cf9e9f2017b4a601dd5";

        /// <summary>What is inside: 51 bytes.</summary>
        public const string Plaintext =
            "54686520717569636b2062726f776e20666f78206a756d7073206f76657220746865206c617a7920646f672c20747769" +
            "636521";

        /// <summary>Ciphertext || the ten-byte MAC: what a download fetches.</summary>
        public const string Body =
            "98231dc99dbbacf9f0f4628ee0f365a9e9af12d9788ee5ab3554e349f87fb6b4f6b6a393f2d5ca711f97e306a259b785" +
            "3a2327543a3c5f3e2a6281589c5d001c41495b745ab84a44a311";

        /// <summary>sha256 of the plaintext.</summary>
        public const string FileSha256 =
            "87a66f486458d3c99c22355b486c30f4be4f72cb15f70024b3706566238cb659";

        /// <summary>sha256 of the body.</summary>
        public const string FileEncSha256 =
            "0b927ed92b202e47511674462fa12da24ff428c91cf53213872e513bf7fe34cd";

        /// <summary>The same blob with one ciphertext byte moved.</summary>
        public const string MovedByte =
            "98231dc89dbbacf9f0f4628ee0f365a9e9af12d9788ee5ab3554e349f87fb6b4f6b6a393f2d5ca711f97e306a259b785" +
            "3a2327543a3c5f3e2a6281589c5d001c41495b745ab84a44a311";
    }
}
