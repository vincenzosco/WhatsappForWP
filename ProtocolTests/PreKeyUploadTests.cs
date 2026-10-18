using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The pre-key upload: the node the server stores this device's keys from, and the
    /// store the same keys go into so an incoming pre-key message can be answered.
    ///
    /// The two halves are asserted together on purpose. A node that publishes a public key
    /// whose private half was never kept, or kept under a different id, looks perfect on
    /// the wire and answers nothing.
    /// </summary>
    public static class PreKeyUploadTests
    {
        [Test]
        public static void TheNodeIsTheOneTheServerExpects()
        {
            Credentials credentials = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new PreKeyStore(new Memory());

            BinaryNode iq = PreKeyUpload.Node(credentials, store, new SignalSessions.CounterRandom(), 20);

            Assert.AreEqual("iq", iq.Tag);
            Assert.AreEqual(PairingSession.ServerJid, iq.Attrs["to"]);
            Assert.AreEqual("encrypt", iq.Attrs["xmlns"]);
            Assert.AreEqual("set", iq.Attrs["type"]);
            Assert.IsTrue(!string.IsNullOrEmpty(iq.Attrs["id"]), "an iq without an id is an iq nobody can answer");

            // The reference's order: registration, type, identity, list, skey.
            IList<BinaryNode> children = iq.Children();
            Assert.AreEqual(5, children.Count);
            Assert.AreEqual("registration", children[0].Tag);
            Assert.AreEqual("type", children[1].Tag);
            Assert.AreEqual("identity", children[2].Tag);
            Assert.AreEqual("list", children[3].Tag);
            Assert.AreEqual("skey", children[4].Tag);

            Assert.AreEqual(4, children[0].Bytes().Length);
            Assert.AreEqual(credentials.RegistrationId, Number(children[0].Bytes()));
            Assert.AreEqual((byte)PreKeyUpload.KeyBundleType, children[1].Bytes()[0]);
            Assert.ByteArraysEqual(credentials.IdentityPublicKey, children[2].Bytes());

            IList<BinaryNode> list = children[3].Children();
            Assert.AreEqual(20, list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                Assert.AreEqual("key", list[i].Tag);
                Assert.AreEqual(3, list[i].Child("id").Bytes().Length);
                Assert.AreEqual(32, list[i].Child("value").Bytes().Length);
            }

            IList<BinaryNode> skey = children[4].Children();
            Assert.AreEqual(credentials.SignedPreKeyId, Number(skey[0].Bytes()));
            Assert.ByteArraysEqual(credentials.SignedPreKeyPublic, skey[1].Bytes());
            Assert.ByteArraysEqual(credentials.SignedPreKeySignature, skey[2].Bytes());

            // The signed pre-key reached the store the responder path reads: a pre-key
            // message that names its id is answerable now.
            Assert.IsTrue(store.HasSignedPreKey, "the signed pre-key has to be in the store, not only in the node");
            Assert.AreEqual(credentials.SignedPreKeyId, store.SignedPreKeyId);
            Assert.ByteArraysEqual(credentials.SignedPreKeyPrivate, store.SignedPreKeyPrivate);
            Assert.ByteArraysEqual(credentials.SignedPreKeySignature, store.SignedPreKeySignature);
        }

        [Test]
        public static void ItPublishesExactlyTheCountItWasAskedAndKeepsThePrivateHalves()
        {
            Credentials credentials = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new PreKeyStore(new Memory());

            BinaryNode iq = PreKeyUpload.Node(credentials, store, new SignalSessions.CounterRandom(), 20);

            Assert.AreEqual(20, store.Remaining);

            var ids = new List<uint>();
            IList<BinaryNode> list = iq.Child("list").Children();

            for (int i = 0; i < list.Count; i++)
            {
                uint id = Number(list[i].Child("id").Bytes());
                Assert.IsTrue(id != 0, "a pre-key id of zero is what an absent id looks like");
                Assert.IsTrue(!ids.Contains(id), "two keys in one upload cannot share an id");
                ids.Add(id);

                // The id the server hands out is the id that finds the private half, and
                // the private half is the one the published public key belongs to.
                byte[] stored = store.Consume(id);
                Assert.IsTrue(stored != null, "every published key has to be stored under its own id");
                Assert.ByteArraysEqual(list[i].Child("value").Bytes(), Curve25519.PublicKey(stored));
            }

            Assert.IsTrue(store.Remaining == 0,
                "every key that was published was taken back out in this test");
        }

        [Test]
        public static void TheFloorIsTheLastCountThatIsStillEnough()
        {
            var store = new PreKeyStore(new Memory());

            Assert.AreEqual(5, PreKeyUpload.LowWaterMark);
            Assert.AreEqual(20, PreKeyUpload.RefillCount);

            for (uint id = 1; id <= 20; id++)
            {
                store.Publish(id, new byte[32]);
            }

            Assert.IsFalse(PreKeyUpload.NeedsUpload(store, PreKeyUpload.LowWaterMark),
                "twenty keys left is not a reason to publish twenty more");
            Assert.IsFalse(PreKeyUpload.NeedsUpload(store, 5), "exactly five left is still enough");
            Assert.IsTrue(PreKeyUpload.NeedsUpload(store, 21));

            for (uint id = 1; id <= 18; id++)
            {
                store.Consume(id);
            }

            Assert.AreEqual(2, store.Remaining);
            Assert.IsTrue(PreKeyUpload.NeedsUpload(store, PreKeyUpload.LowWaterMark));

            // And the app's own question, without a floor named, is the constant.
            Assert.IsTrue(PreKeyUpload.NeedsUpload(store));
        }

        /// <summary>
        /// A store left by another identity is recognised by the key, not by the id - and the
        /// second call changes nothing.
        /// </summary>
        [Test]
        public static void TheAccountsSignedPreKeyReplacesOneWithTheSameIdAndAnotherKey()
        {
            var random = new SignalSessions.CounterRandom();
            Credentials previous = Credentials.Create(random);
            Credentials account = Credentials.Create(random);

            var store = new PreKeyStore(new Memory());
            store.SetSignedPreKey(previous.SignedPreKeyId, previous.SignedPreKeyPrivate,
                previous.SignedPreKeySignature);

            Assert.AreEqual(previous.SignedPreKeyId, account.SignedPreKeyId,
                "a fresh pairing numbers its signed pre-key one again, which is why the id cannot decide this");

            Assert.IsTrue(PreKeyUpload.SignedPreKeyIsStale(account, store),
                "the stored key is not the account's, and the public half of it was never published");
            Assert.IsTrue(PreKeyUpload.AdoptSignedPreKey(account, store),
                "so it is replaced before anything is published against it");

            Assert.ByteArraysEqual(account.SignedPreKeyPrivate, store.SignedPreKeyPrivate);
            Assert.ByteArraysEqual(account.SignedPreKeySignature, store.SignedPreKeySignature);
            Assert.IsFalse(PreKeyUpload.SignedPreKeyIsStale(account, store));
            Assert.IsFalse(PreKeyUpload.AdoptSignedPreKey(account, store),
                "and asking again writes nothing");
        }

        private static uint Number(byte[] bigEndian)
        {
            uint value = 0;
            for (int i = 0; i < bigEndian.Length; i++)
            {
                value = (value << 8) | bigEndian[i];
            }

            return value;
        }

        /// <summary>Session storage in memory, the way the store tests do it.</summary>
        private sealed class Memory : ISessionStorage
        {
            private readonly Dictionary<string, byte[]> _blobs = new Dictionary<string, byte[]>();

            public bool TryRead(string key, out byte[] value)
            {
                return _blobs.TryGetValue(key, out value);
            }

            public void Write(string key, byte[] value)
            {
                _blobs[key] = value;
            }

            public void Remove(string key)
            {
                _blobs.Remove(key);
            }
        }
    }
}
