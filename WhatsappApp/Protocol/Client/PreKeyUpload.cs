using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The `iq` that publishes this device's pre-keys, so a peer that has never talked to
    /// this phone can start a session with it.
    ///
    /// The shape is the reference's (`@whiskeysockets/baileys`, `Utils/signal.js`):
    /// `iq` with `xmlns` `encrypt`, `type` `set` and `to` `s.whatsapp.net`, whose children
    /// come in this order - `registration` (the id, four bytes big-endian), `type` (the
    /// key-bundle type byte, `0x05`), `identity` (the 32-byte identity public key), `list`
    /// (one `key` per one-time pre-key, each `id` three bytes big-endian and `value`), and
    /// `skey` (the signed pre-key's `id`, `value` and 64-byte `signature`).
    ///
    /// The private halves never leave the phone: each is handed to the `PreKeyStore` as it
    /// is drawn, which is where an incoming pre-key message finds it.
    /// </summary>
    public static class PreKeyUpload
    {
        /// <summary>
        /// The count below which more pre-keys are published. A server that has run out
        /// stops handing this device out to peers, so the refill has to happen before the
        /// last one is spent, not after.
        /// </summary>
        public const int LowWaterMark = 5;

        /// <summary>How many one-time pre-keys are published at a time.</summary>
        public const int RefillCount = 20;

        /// <summary>The key-bundle type byte, the second child of the upload.</summary>
        public const byte KeyBundleType = 5;

        /// <summary>Whether the server is running low on this device's one-time pre-keys.</summary>
        public static bool NeedsUpload(PreKeyStore store)
        {
            return NeedsUpload(store, LowWaterMark);
        }

        /// <summary>
        /// The same question with the floor named: `floor` is the last count that is still
        /// enough, so a store with exactly that many left has nothing to publish yet.
        /// </summary>
        public static bool NeedsUpload(PreKeyStore store, int floor)
        {
            if (store == null) throw new ProtocolException("the pre-key store is where the keys live");

            return store.Remaining < floor;
        }

        /// <summary>
        /// The upload: `count` fresh one-time pre-keys published into the store, plus the
        /// device's own registration, identity and signed pre-key.
        /// </summary>
        public static BinaryNode Node(Credentials credentials, PreKeyStore store, IRandomSource random, int count)
        {
            if (credentials == null) throw new ProtocolException("a pre-key upload needs this device's credentials");
            if (store == null) throw new ProtocolException("a pre-key upload needs the store its private keys go in");
            if (random == null) throw new ProtocolException("a pre-key upload needs randomness");
            if (count < 1) throw new ProtocolException("a pre-key upload publishes at least one key");

            EnsureSignedPreKey(credentials, store);

            var list = new BinaryNode("list");
            var keys = new List<BinaryNode>();
            var ids = new List<uint>();

            for (int i = 0; i < count; i++)
            {
                uint id = NextId(random, ids);
                byte[] privateKey = random.Bytes(Curve25519.KeyLength);

                // Stored before it is published: a key the server has handed out and this
                // phone cannot find is a message nobody can read.
                store.Publish(id, privateKey);

                keys.Add(Pair("key", BigEndian(id, 3), Curve25519.PublicKey(privateKey)));
            }

            list.Content = keys;

            var signedPreKey = new BinaryNode("skey");
            signedPreKey.Content = new List<BinaryNode>
            {
                Child("id", BigEndian(credentials.SignedPreKeyId, 3)),
                Child("value", credentials.SignedPreKeyPublic),
                Child("signature", credentials.SignedPreKeySignature)
            };

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", PairingSession.ServerJid);
            iq.Attrs.Add("xmlns", "encrypt");
            iq.Attrs.Add("type", "set");
            iq.Attrs.Add("id", Hex(random.Bytes(8)));
            iq.Content = new List<BinaryNode>
            {
                Child("registration", BigEndian(credentials.RegistrationId, 4)),
                Child("type", new byte[] { KeyBundleType }),
                Child("identity", credentials.IdentityPublicKey),
                list,
                signedPreKey
            };

            return iq;
        }

        /// <summary>
        /// The signed pre-key this device already has goes into the store the responder
        /// path reads, once: a message that opened a session with it names its id, and a
        /// store that does not know that id cannot answer.
        /// </summary>
        private static void EnsureSignedPreKey(Credentials credentials, PreKeyStore store)
        {
            if (store.HasSignedPreKey) return;

            if (credentials.SignedPreKeyId == 0 || credentials.SignedPreKeyPublic == null)
            {
                throw new ProtocolException("this device has no signed pre-key to publish");
            }

            store.SetSignedPreKey(credentials.SignedPreKeyId,
                credentials.SignedPreKeyPrivate, credentials.SignedPreKeySignature);
        }

        /// <summary>An id no other key in this upload has, and never zero.</summary>
        private static uint NextId(IRandomSource random, IList<uint> taken)
        {
            for (int attempt = 0; attempt < 16; attempt++)
            {
                byte[] bytes = random.Bytes(3);
                uint id = (uint)((bytes[0] << 16) | (bytes[1] << 8) | bytes[2]);

                if (id == 0 || taken.Contains(id)) continue;

                taken.Add(id);
                return id;
            }

            throw new ProtocolException("could not draw an unused pre-key id");
        }

        private static BinaryNode Child(string tag, byte[] content)
        {
            var node = new BinaryNode(tag);
            node.Content = content;
            return node;
        }

        private static BinaryNode Pair(string tag, byte[] id, byte[] value)
        {
            var node = new BinaryNode(tag);
            node.Content = new List<BinaryNode> { Child("id", id), Child("value", value) };
            return node;
        }

        private static byte[] BigEndian(uint value, int bytes)
        {
            var result = new byte[bytes];
            for (int i = 0; i < bytes; i++)
            {
                result[bytes - 1 - i] = (byte)(value >> (8 * i));
            }

            return result;
        }

        private static string Hex(byte[] bytes)
        {
            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));

            return text.ToString();
        }
    }
}
