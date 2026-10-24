using System;
using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.WABinary;
using WhatsappApp.Protocol.WaProto;
using ProtocolTests.Vectors;

namespace ProtocolTests
{
    /// <summary>
    /// The app state, read against bytes the reference produced.
    ///
    /// Why this matters here and not on the phone: `AppStateSync` verifies three MACs and an
    /// LT-Hash before it names anybody, so a client that gets any of them wrong reads no name
    /// at all - and on the phone that looks exactly like an account whose address book is
    /// empty, with one quiet `app-state: ... was not read` line. The fixture
    /// (`ProtocolTests/Vectors/AppStateFixture.cs`, written by
    /// `tools/emit-appstate-fixture.js` with Node's own crypto) is a snapshot and a patch
    /// built by the *reference's* rules, so these tests disagree loudly with a reader that
    /// only agrees with itself.
    /// </summary>
    public static class AppStateTests
    {
        /// <summary>The collection the fixture's two versions belong to.</summary>
        private const string Collection = "regular";

        [Test]
        public static void TheReferenceSnapshotNamesTheTwoSavedContacts()
        {
            AppStateResult result = AppStateSync.Apply(Snapshot(), Keys());

            Assert.AreEqual(Collection, result.Collection);
            Assert.AreEqual(AppStateFixture.SnapshotVersion, result.Version,
                "the version the snapshot says it is");
            Assert.AreEqual(2, result.Contacts.Count, "the snapshot carries two contact actions");

            SavedContact mamma = result.Contacts[0];
            Assert.AreEqual("393331234567@s.whatsapp.net", mamma.Address,
                "the address is the second element of the record's JSON index");
            Assert.AreEqual("Mamma", mamma.Name);
            Assert.AreEqual("393331234567@s.whatsapp.net", mamma.PnJid);
            Assert.IsTrue(mamma.LidJid == null, "an action with no lid names none");

            SavedContact papa = result.Contacts[1];
            Assert.AreEqual("150375613116620@lid", papa.Address);
            Assert.AreEqual("Pap\u00e0", papa.Name, "the UTF-8 of the action survives the CBC read");
            Assert.AreEqual("150375613116620@lid", papa.LidJid);
            Assert.AreEqual("393892672185@s.whatsapp.net", papa.PnJid,
                "one contact saved under a LID still says which number it is");
        }

        [Test]
        public static void ThePatchAfterTheSnapshotSavesTheThirdContact()
        {
            AppStateCollection collection = Snapshot();
            collection.Patches.Add(Patch());

            AppStateResult result = AppStateSync.Apply(collection, Keys());

            Assert.AreEqual(AppStateFixture.PatchVersion, result.Version,
                "the patch's version is the one the collection reached");
            Assert.AreEqual(3, result.Contacts.Count,
                "the snapshot's two names and the one the patch adds");
            Assert.AreEqual("Nonna", result.Contacts[2].Name);
            Assert.AreEqual("393401112223@s.whatsapp.net", result.Contacts[2].Address);
        }

        [Test]
        public static void TheReferencePatchIsSignedOverItsOwnValueMac()
        {
            SyncdPatch patch = Patch();

            Assert.AreEqual(AppStateFixture.PatchVersion, patch.Version);
            Assert.AreEqual(1, patch.Mutations.Count);
            Assert.AreEqual(SyncdMutation.Set, patch.Mutations[0].Operation);
            Assert.IsTrue(patch.SnapshotMac != null && patch.PatchMac != null,
                "both MACs arrived");
            Assert.AreEqual(AppStateFixture.KeyId, Convert.ToBase64String(patch.KeyId),
                "the patch names the same key id its records do");
        }

        [Test]
        public static void ARecordWithATamperedValueIsRefusedAndNothingIsHalfApplied()
        {
            AppStateCollection collection = Snapshot();
            byte[] value = collection.Snapshot.Records[0].Value;
            value[value.Length - 1] = (byte)(value[value.Length - 1] ^ 0x01);

            Refused(delegate { AppStateSync.Apply(collection, Keys()); }, "the MAC of a regular record",
                "the record's MAC covers its value, and a collection reads whole or not at all");
        }

        [Test]
        public static void ThePatchMacCoversEveryMutationItLists()
        {
            AppStateCollection collection = Snapshot();
            SyncdPatch patch = Patch();
            byte[] value = patch.Mutations[0].Record.Value;
            value[value.Length - 1] = (byte)(value[value.Length - 1] ^ 0x01);
            collection.Patches.Add(patch);

            Refused(delegate { AppStateSync.Apply(collection, Keys()); }, "the MAC of the regular patch",
                "a value MAC that does not match the list is a patch MAC that does not verify");
        }

        [Test]
        public static void AKeyThisDeviceWasNeverGivenIsRefused()
        {
            var keys = new AppStateKeyset();
            var other = new AppStateKey();
            other.Id = "AAAAAAAAAAA=";
            other.KeyData = new byte[32];
            Assert.IsTrue(keys.Add(other), "the wrong key is still a key this device has");

            Refused(delegate { AppStateSync.Apply(Snapshot(), keys); }, "names a key this device was never given",
                "the record names a key id the phone was never sent");
        }

        [Test]
        public static void AnEmptyKeySetReadsNothing()
        {
            Refused(delegate { AppStateSync.Apply(Snapshot(), new AppStateKeyset()); }, "needs a key",
                "a device with no shared key cannot read the address book");
        }

        [Test]
        public static void TheLtHashIsASumAndNotASet()
        {
            byte[] mac = Filled(32, 0x37);

            byte[] sum = LtHash.Add(LtHash.Empty(), mac);
            Assert.IsFalse(Same(sum, LtHash.Empty()), "adding a record changes the sum");

            Assert.ByteArraysEqual(sum, LtHash.Add(LtHash.Empty(), mac),
                "the same record twice is the same sum: it is arithmetic");

            Assert.ByteArraysEqual(LtHash.Empty(), LtHash.Subtract(sum, mac),
                "adding a record and taking it out leaves the empty sum");

            byte[] both = LtHash.Add(sum, Filled(32, 0x11));
            Assert.IsFalse(Same(both, sum), "a second record moves the sum again");
            Assert.ByteArraysEqual(sum, LtHash.Subtract(both, Filled(32, 0x11)));
        }

        [Test]
        public static void TheFiveMutationKeysComeFromTheOneKeyData()
        {
            MutationKeys keys = MutationKeys.Derive(Convert.FromBase64String(AppStateFixture.KeyData));

            Assert.AreEqual(32, keys.IndexKey.Length);
            Assert.AreEqual(32, keys.ValueEncryptionKey.Length);
            Assert.AreEqual(32, keys.ValueMacKey.Length);
            Assert.AreEqual(32, keys.SnapshotMacKey.Length);
            Assert.AreEqual(32, keys.PatchMacKey.Length);

            Assert.IsFalse(Same(keys.IndexKey, keys.ValueEncryptionKey),
                "the five slices are five different keys");
            Assert.IsFalse(Same(keys.SnapshotMacKey, keys.PatchMacKey),
                "a snapshot MAC key that also signs patches would let one stand for the other");

            Assert.Throws<ProtocolException>(
                delegate { MutationKeys.Derive(new byte[16]); },
                "the key material is 32 bytes");
        }

        [Test]
        public static void TheQueryAsksForEveryCollectionFromZeroWithItsSnapshot()
        {
            BinaryNode iq = AppStateSync.Query("42");

            Assert.AreEqual("iq", iq.Tag);
            Assert.AreEqual("42", iq.Attrs["id"]);
            Assert.AreEqual("w:sync:app:state", iq.Attrs["xmlns"]);

            BinaryNode sync = iq.Child("sync");
            Assert.IsTrue(sync != null, "the collections hang off a <sync>");

            IList<BinaryNode> collections = sync.Children();
            Assert.AreEqual(AppStateSync.Collections.Length, collections.Count);

            for (int i = 0; i < collections.Count; i++)
            {
                Assert.AreEqual("collection", collections[i].Tag);
                Assert.AreEqual(AppStateSync.Collections[i], collections[i].Attrs["name"],
                    "the reference's collections, in its order");
                Assert.AreEqual("0", collections[i].Attrs["version"],
                    "a snapshot is self-contained, so the read is from the start");
                Assert.AreEqual("true", collections[i].Attrs["return_snapshot"]);
            }
        }

        [Test]
        public static void TheKeyShareIsReadOffTheProtocolMessage()
        {
            byte[] id = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            byte[] data = Filled(32, 0x5a);

            // AppStateSyncKeyShare.keys[0] = AppStateSyncKey { keyId: { keyId }, keyData: { keyData } }
            byte[] share = Message(1, Concat(
                Message(1, Message(1, id)),
                Message(2, Message(1, data))));

            byte[] protocol = Concat(
                VarintField(2, MessageProtocol.AppStateSyncKeyShare),
                Message(7, share));

            MessageProtocol decoded = MessageProtocol.Decode(protocol);

            Assert.AreEqual(MessageProtocol.AppStateSyncKeyShare, decoded.Type,
                "type 6 is the share, which is the only way a companion learns the keys");
            Assert.ByteArraysEqual(share, decoded.KeyShare);

            IList<AppStateKey> keys = AppStateKeys.Read(decoded.KeyShare);
            Assert.AreEqual(1, keys.Count);
            Assert.AreEqual(Convert.ToBase64String(id), keys[0].Id,
                "the id is named base64, which is how every record and patch names it");
            Assert.ByteArraysEqual(data, keys[0].KeyData);
        }

        [Test]
        public static void TheSharedKeysSurviveTheStore()
        {
            var storage = new MemoryStorage();
            var store = new CredentialStore(storage);

            Credentials credentials = Credentials.Create(new CountingRandom());
            credentials.Jid = "393892672185:5@s.whatsapp.net";

            var shared = new AppStateKey();
            shared.Id = AppStateFixture.KeyId;
            shared.KeyData = Convert.FromBase64String(AppStateFixture.KeyData);
            credentials.AppStateKeys.Add(shared);

            store.Save(credentials);

            Credentials restored = store.Load();
            Assert.AreEqual(1, restored.AppStateKeys.Count,
                "a key the phone shares is remembered across runs, or the address book is unreadable after a restart");
            Assert.AreEqual(AppStateFixture.KeyId, restored.AppStateKeys[0].Id);
            Assert.ByteArraysEqual(shared.KeyData, restored.AppStateKeys[0].KeyData,
                "the key material itself comes back, byte for byte");
        }

        // ------------------------------------------------------------------ the helpers

        /// <summary>
        /// The refusal, and the sentence that says which one it is.
        ///
        /// Why the message and not only the type: a fixture and a reader that both disagree
        /// with the reference throw the same `ProtocolException`, and a test that only asks
        /// "did it throw" passes on the bug it was written for. The message is what names
        /// the check that answered.
        /// </summary>
        private static void Refused(Action action, string expected, string because)
        {
            try
            {
                action();
            }
            catch (ProtocolException error)
            {
                Assert.IsTrue(error.Message.IndexOf(expected, StringComparison.Ordinal) >= 0,
                    "expected a refusal about \"" + expected + "\" but got: " + error.Message);
                return;
            }

            throw new AssertionException("expected a ProtocolException but nothing was thrown: " + because);
        }

        private static AppStateKeyset Keys()
        {
            var keys = new AppStateKeyset();
            var key = new AppStateKey();
            key.Id = AppStateFixture.KeyId;
            key.KeyData = Convert.FromBase64String(AppStateFixture.KeyData);

            Assert.IsTrue(keys.Add(key), "the fixture's key is 32 bytes and carries an id");
            return keys;
        }

        private static AppStateCollection Snapshot()
        {
            var collection = new AppStateCollection();
            collection.Name = Collection;
            collection.Snapshot = SyncdSnapshot.Decode(Convert.FromBase64String(AppStateFixture.Snapshot));
            return collection;
        }

        private static SyncdPatch Patch()
        {
            return SyncdPatch.Decode(Convert.FromBase64String(AppStateFixture.Patch));
        }

        /// <summary>A protobuf length-delimited field, which is all this test needs to write.</summary>
        private static byte[] Message(int number, byte[] payload)
        {
            var bytes = new List<byte>();
            Varint(bytes, (uint)(number << 3 | 2));
            Varint(bytes, (uint)payload.Length);
            bytes.AddRange(payload);
            return bytes.ToArray();
        }

        /// <summary>A protobuf varint field.</summary>
        private static byte[] VarintField(int number, int value)
        {
            var bytes = new List<byte>();
            Varint(bytes, (uint)(number << 3));
            Varint(bytes, (uint)value);
            return bytes.ToArray();
        }

        private static void Varint(List<byte> bytes, uint value)
        {
            while (value >= 0x80)
            {
                bytes.Add((byte)((value & 0x7f) | 0x80));
                value >>= 7;
            }

            bytes.Add((byte)value);
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var joined = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, joined, 0, first.Length);
            Buffer.BlockCopy(second, 0, joined, first.Length, second.Length);
            return joined;
        }

        private static byte[] Filled(int length, byte value)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++) bytes[i] = value;
            return bytes;
        }

        private static bool Same(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }

        private sealed class CountingRandom : IRandomSource
        {
            private byte _next = 0x80;

            public byte[] Bytes(int count)
            {
                var bytes = new byte[count];
                for (int i = 0; i < count; i++) bytes[i] = _next;
                _next--;
                return bytes;
            }
        }

        private sealed class MemoryStorage : ICredentialStorage
        {
            private readonly Dictionary<string, string> _values =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public string Read(string key)
            {
                string value;
                return _values.TryGetValue(key, out value) ? value : null;
            }

            public void Write(string key, string value)
            {
                _values[key] = value;
            }
        }
    }
}
