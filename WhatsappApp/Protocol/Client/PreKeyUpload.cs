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

            return NeedsUpload(store.Remaining, floor);
        }

        /// <summary>
        /// The same question over a count rather than a store, so a caller that owns a loop
        /// (`Services/NativeConnection`, through `ConnectionPolicy.NeedsPreKeys`) asks this
        /// floor instead of restating the comparison and drifting from it.
        /// </summary>
        public static bool NeedsUpload(int remaining, int floor)
        {
            return remaining < floor;
        }

        /// <summary>
        /// The upload: `count` fresh one-time pre-keys published into the store, plus the
        /// device's own registration, identity and signed pre-key.
        /// </summary>
        public static BinaryNode Node(Credentials credentials, PreKeyStore store, IRandomSource random, int count)
        {
            return Node(credentials, store, random, count, null);
        }

        /// <summary>
        /// The same upload, naming the ids it published.
        ///
        /// Why the caller is told: the keys go into the store before the node can be sent,
        /// and a send that fails leaves them counted as keys the server has. The caller is
        /// the one that learns the upload failed and is therefore the one that can take them
        /// back (`PreKeyStore.Unpublish`).
        /// </summary>
        public static BinaryNode Node(Credentials credentials, PreKeyStore store, IRandomSource random, int count,
            IList<uint> published)
        {
            if (credentials == null) throw new ProtocolException("a pre-key upload needs this device's credentials");
            if (store == null) throw new ProtocolException("a pre-key upload needs the store its private keys go in");
            if (random == null) throw new ProtocolException("a pre-key upload needs randomness");
            if (count < 1) throw new ProtocolException("a pre-key upload publishes at least one key");

            AdoptSignedPreKey(credentials, store);

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
                if (published != null) published.Add(id);

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
        /// Puts this account's signed pre-key in the store the responder path reads, and says
        /// whether it had to.
        ///
        /// "Is there one" is not the question, and neither is the id. Signed pre-keys are numbered
        /// from **one**, and a pairing numbers its fresh one one again - so a store left from the
        /// previous identity holds the same id over a **different private key**. The responder
        /// path can only compare the id a message names (`IncomingMessage.StartResponder`), and a
        /// pre-key wrapper carries nothing else, so the two agree on paper and the agreement runs
        /// on a private key whose public half was never published: the root the sender derived is
        /// not the root this phone derives, and **every message of that session fails `the
        /// message's MAC does not verify`**.
        ///
        /// That is the phone of 2026-10-18. The upload published the new identity's signed
        /// pre-key while the store kept the previous pairing's, and a hundred stanzas from four
        /// senders - the account's own phone, a contact, the history sync - were unreadable,
        /// every one of them failing on the MAC and none of them saying why.
        ///
        /// The comparison is by public key, derived from the private half the store holds, because
        /// the public key is what a sender was given. The signature is replaced with it: it is a
        /// function of the same pair.
        /// </summary>
        public static bool AdoptSignedPreKey(Credentials credentials, PreKeyStore store)
        {
            if (credentials == null) throw new ProtocolException("a signed pre-key belongs to an account");
            if (store == null) throw new ProtocolException("a signed pre-key lives in the pre-key store");

            if (!SignedPreKeyIsStale(credentials, store)) return false;

            if (credentials.SignedPreKeyId == 0 || credentials.SignedPreKeyPrivate == null
                || credentials.SignedPreKeyPublic == null)
            {
                throw new ProtocolException("this device has no signed pre-key to publish");
            }

            store.SetSignedPreKey(credentials.SignedPreKeyId,
                credentials.SignedPreKeyPrivate, credentials.SignedPreKeySignature);

            return true;
        }

        /// <summary>
        /// Whether the store's signed pre-key is not the one this account publishes - absent, of
        /// another id, or the same id over another key.
        /// </summary>
        public static bool SignedPreKeyIsStale(Credentials credentials, PreKeyStore store)
        {
            if (credentials == null || store == null) return true;
            if (!store.HasSignedPreKey) return true;
            if (store.SignedPreKeyId != credentials.SignedPreKeyId) return true;
            if (credentials.SignedPreKeyPublic == null) return true;

            return !Hmac.FixedTimeEquals(Curve25519.PublicKey(store.SignedPreKeyPrivate),
                credentials.SignedPreKeyPublic);
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
