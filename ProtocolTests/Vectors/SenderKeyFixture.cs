namespace ProtocolTests.Vectors
{
    /// <summary>
    /// One sender key chain, printed by the reference itself and frozen here.
    ///
    /// The command, run in `/tmp/m4ref` (Baileys 7.x, `libsignal`'s group primitives) with
    /// Node 24:
    ///
    /// ```js
    /// import { SenderKeyState } from '@whiskeysockets/baileys/lib/Signal/Group/sender-key-state.js';
    /// import { SenderKeyDistributionMessage } from '@whiskeysockets/baileys/lib/Signal/Group/sender-key-distribution-message.js';
    /// import { SenderKeyMessage } from '@whiskeysockets/baileys/lib/Signal/Group/sender-key-message.js';
    /// import * as keyhelper from '@whiskeysockets/baileys/lib/Signal/Group/keyhelper.js';
    /// import * as ds from 'libsignal/src/curve.js';
    /// import * as crypto from 'libsignal/src/crypto.js';
    ///
    /// const keyId = 1234567, chainKey = Buffer.alloc(32, 0x11), priv = Buffer.alloc(32, 0x21);
    /// const signingKey = keyhelper.generateSenderSigningKey({ pubKey: ds.getPublicFromPrivateKey(priv), privKey: priv });
    /// const state = new SenderKeyState(keyId, 0, chainKey, signingKey);
    /// new SenderKeyDistributionMessage(keyId, 0, chainKey, state.getSigningKeyPublic()).serialize();
    /// const key = state.getSenderChainKey().getSenderMessageKey();
    /// const ciphertext = crypto.encrypt(key.getCipherKey(), Buffer.from('group ciao'), key.getIv());
    /// new SenderKeyMessage(keyId, key.getIteration(), ciphertext, state.getSigningKeyPrivate()).serialize();
    /// ```
    ///
    /// The field numbers of the two protobufs came off the same encoder, one field at a
    /// time: `080110021a20...` is `id` 1, `iteration` 2, `chainKey` 3, `signingKey` 4 for the
    /// distribution, and `id` 1, `iteration` 2, `ciphertext` 3 for the message.
    /// </summary>
    public static class SenderKeyFixture
    {
        public const string GroupId = "120363000000000000@g.us";

        /// <summary>The `keyId` the reference drew.</summary>
        public const uint KeyId = 1234567u;

        /// <summary>The chain key, 32 bytes of 0x11.</summary>
        public const string ChainKey = "1111111111111111111111111111111111111111111111111111111111111111";

        /// <summary>The signing private key the reference signed with, 32 bytes of 0x21.</summary>
        public const string SigningPrivate = "2121212121212121212121212121212121212121212121212121212121212121";

        /// <summary>The signing public key as the distribution carries it: 33 bytes, `0x05` first.</summary>
        public const string SigningPublic = "057d34a4815fa6b982535e60af3bd9b49556816080f1641ff81d2b7c8ae8268a44";

        /// <summary>The whole distribution message, version byte included.</summary>
        public const string Distribution =
            "330887ad4b10001a2011111111111111111111111111111111111111111111111111111111111111112221"
            + "057d34a4815fa6b982535e60af3bd9b49556816080f1641ff81d2b7c8ae8268a44";

        /// <summary>`HMAC-SHA256(chainKey, 0x01)`: the seed of iteration 0's message key.</summary>
        public const string MessageKeySeed = "c4ede064b5f2a51225b2175e5b84fb73f9cec689ac3d912ac4878f7ebeab0149";

        /// <summary>`HMAC-SHA256(chainKey, 0x02)`: the chain key of iteration 1.</summary>
        public const string NextChainKey = "ed50b271f4852277c8218e209858d8bd32a3228a9e4fb6b5a16fb9b4755c53bc";

        /// <summary>The iv out of iteration 0's message key: the HKDF extract's first 16 bytes.</summary>
        public const string Iv = "99f84d6adaf0663bb79e1f541e232e70";

        /// <summary>The AES key: the extract's last 16 bytes, then 16 from the expand.</summary>
        public const string CipherKey = "72000d68346a05ffb143906cf02137458d26f53db8af9aa8f3bfb17c60622a8a";

        /// <summary>`group ciao`, the plaintext of the message below.</summary>
        public const string Plaintext = "67726f7570206369616f";

        /// <summary>`secondo`, off the next chain key.</summary>
        public const string SecondPlaintext = "7365636f6e646f";

        /// <summary>The `skmsg` bytes for <see cref="Plaintext"/>, iteration 0.</summary>
        public const string SenderKeyMessage =
            "330887ad4b10001a1024934325243fbef810634bf4142f5de4"
            + "9ad8adace45bc4bf50f3da33a9a0d4c5a716c6942dcaf87f9e43de475ca3a01fc59e660d0e78700e"
            + "492d95591af1d0c2e15f4c6f6efeccb6504f0de93244b207";

        /// <summary>The same for <see cref="SecondPlaintext"/>, iteration 1.</summary>
        public const string SecondMessage =
            "330887ad4b10011a101cfb515cdab22593eae8feac85e5fd93"
            + "0e346998dc606368ed07d2c88b619509718c3c54465fea11c19203e12be83ee5d62b5d93fc6b25c0"
            + "5bffb603d57cc7adc4e2596985b0f023088db6dde75c4b07";

        /// <summary>The first 96 hex characters are the version byte and the protobuf; the rest is the signature.</summary>
        public const int SignedHexLength = 34;
    }
}
