using System;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// The constants Signal's wire format is made of, read out of the reference
    /// implementation rather than from memory.
    ///
    /// Every value here was printed by the commands in this file's header. That matters
    /// more than it looks: a constant that is wrong by one byte produces a session that
    /// decrypts perfectly against our own tests and is refused by WhatsApp, and the
    /// failure surfaces on a phone, hours later, as silence. The reference is
    /// `libsignal-protocol` (npm, 1.3.15), installed in a directory outside this
    /// repository:
    ///
    ///   mkdir -p /tmp/signalref &amp;&amp; cd /tmp/signalref &amp;&amp; npm init -y
    ///   npm install libsignal-protocol
    ///   grep -rn "deriveSecrets" node_modules/libsignal-protocol/src
    ///   grep -rn "sharedSecret\[i\] = 0xff" node_modules/libsignal-protocol/src/SessionBuilder.js
    ///   awk '/fillMessageKeys: function/,/^  },/' node_modules/libsignal-protocol/src/SessionCipher.js
    ///
    /// The wire format itself is pinned by `ProtocolTests/Vectors/SignalFixture.cs`,
    /// which holds the reference's own frozen conversation.
    /// </summary>
    public static class SignalConstants
    {
        /// <summary>
        /// The 32 bytes of `0xFF` in front of the X3DH key material, for domain
        /// separation from XEdDSA (the same prefix `XEdDsa` uses when it signs).
        /// Reference: `SessionBuilder.js:141`, `sharedSecret[i] = 0xff;` filling a
        /// buffer of `32 + 32 * 4` bytes, before the four DH outputs are written into
        /// it at offsets 32, 64, 96 and 128.
        /// </summary>
        public static readonly byte[] AgreementPrefix = Filled(0xFF, 32);

        /// <summary>The agreement's HKDF info. Reference: `SessionBuilder.js:166`.</summary>
        public const string InfoText = "WhisperText";

        /// <summary>The ratchet's HKDF info, with the current root key as the salt. Reference: `SessionBuilder.js:209` and `SessionCipher.js:342`.</summary>
        public const string InfoRatchet = "WhisperRatchet";

        /// <summary>The message keys' HKDF info, with 32 zero bytes as the salt. Reference: `SessionCipher.js:57` and `:255`.</summary>
        public const string InfoMessageKeys = "WhisperMessageKeys";

        /// <summary>
        /// The version byte that starts every ciphertext: `(3 &lt;&lt; 4) | 3`, which is
        /// `0x33`. Written both at the front of the message and inside the MAC's input,
        /// which is why it is one constant and not two. Reference: `SessionCipher.js`,
        /// the `result[0]` and `macInput[33*2]` that both hold that expression.
        /// </summary>
        public const byte MessageVersionByte = (3 << 4) | 3;

        /// <summary>
        /// How much of the 32-byte HMAC is kept: the first 8 bytes. Reference:
        /// `SessionCipher.js`, `result.set(new Uint8Array(mac, 0, 8), ...)`. A verifier
        /// that compares 32 bytes refuses every real message.
        /// </summary>
        public const int MacLength = 8;

        /// <summary>
        /// The type byte Signal puts in front of a 32-byte public key: identity keys in
        /// the MAC's input are 33 bytes, and the fixture's `BobIdentityPublic` starts
        /// with it. Reference: the `33*2` in the MAC's input and the keys themselves.
        /// </summary>
        public const byte KeyTypeByte = 0x05;

        /// <summary>
        /// The chain key's two advances. The message key is `HMAC-SHA256(chainKey,
        /// 0x01)` and the next chain key is `HMAC-SHA256(chainKey, 0x02)`. Reference:
        /// `fillMessageKeys` in `SessionCipher.js`, which sets `byteArray[0]` to 1 and
        /// then to 2 between the two `crypto.sign` calls.
        /// </summary>
        public const byte MessageKeySeed = 0x01;

        /// <inheritdoc cref="MessageKeySeed"/>
        public const byte ChainKeySeed = 0x02;

        /// <summary>The cipher key and the MAC key, both 32 bytes, and the 16-byte IV, in that order: `keys[0]`, `keys[1]`, `keys[2]`. Reference: `SessionCipher.js`, `encrypt(keys[0], buffer, keys[2].slice(0, 16))`.</summary>
        public const int CipherKeyLength = 32;

        /// <inheritdoc cref="CipherKeyLength"/>
        public const int MacKeyLength = 32;

        /// <inheritdoc cref="CipherKeyLength"/>
        public const int IvLength = 16;

        /// <summary>A 32-byte key with the type byte in front, which is the form the MAC sees.</summary>
        public const int TypedKeyLength = 33;

        private static byte[] Filled(byte value, int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = value;

            return bytes;
        }
    }
}
