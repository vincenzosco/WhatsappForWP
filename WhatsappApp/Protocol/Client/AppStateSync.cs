using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.WABinary;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>The keys this device was given for the app state, by the id a record names.</summary>
    public sealed class AppStateKeyset
    {
        /// <summary>How many keys are kept: a rotation leaves the old one in the records for a while.</summary>
        public const int MaxKeys = 8;

        private readonly Dictionary<string, byte[]> _keys = new Dictionary<string, byte[]>();
        private readonly List<AppStateKey> _order = new List<AppStateKey>();

        /// <summary>Whether this device can read anything at all.</summary>
        public bool IsEmpty
        {
            get { return _keys.Count == 0; }
        }

        /// <summary>How many keys are held. For the log and the tests.</summary>
        public int Count
        {
            get { return _keys.Count; }
        }

        /// <summary>
        /// Adds one key. False when it was already known - a phone that re-sends its share
        /// is not news, and a write per re-send is a file written for nothing.
        /// </summary>
        public bool Add(AppStateKey key)
        {
            if (key == null || string.IsNullOrEmpty(key.Id) || key.KeyData == null) return false;
            if (key.KeyData.Length != 32) return false;

            if (_keys.ContainsKey(key.Id)) return false;
            if (_keys.Count >= MaxKeys) return false;

            _keys[key.Id] = key.KeyData;

            var stored = new AppStateKey();
            stored.Id = key.Id;
            stored.KeyData = key.KeyData;
            _order.Add(stored);

            return true;
        }

        /// <summary>Every key it was given, for the store that keeps them between runs.</summary>
        public IList<AppStateKey> Entries()
        {
            return new List<AppStateKey>(_order);
        }

        /// <summary>The key material a record names, or null when this device was never given it.</summary>
        public byte[] KeyFor(byte[] keyId)
        {
            if (keyId == null) return null;

            byte[] data;
            return _keys.TryGetValue(Convert.ToBase64String(keyId), out data) ? data : null;
        }
    }

    /// <summary>
    /// One contact's name as the primary's address book has it, from the app state.
    ///
    /// `Address` is the JID the action is filed under (the index of the record), and the
    /// other two are the pair the action itself names: the same person is known as a phone
    /// number and as a `@lid`, and a name that arrived for one of them is the name for both.
    /// </summary>
    public sealed class SavedContact
    {
        public string Address;
        public string Name;
        public string PnJid;
        public string LidJid;
    }

    /// <summary>One collection of the app state: a snapshot of it, and the patches after it.</summary>
    public sealed class AppStateCollection
    {
        public string Name;
        public SyncdSnapshot Snapshot;
        public IList<SyncdPatch> Patches = new List<SyncdPatch>();
    }

    /// <summary>What applying a collection taught, and the version it reached.</summary>
    public sealed class AppStateResult
    {
        public string Collection;
        public int Version;
        public IList<SavedContact> Contacts = new List<SavedContact>();
    }

    /// <summary>
    /// The app state: the settings a WhatsApp account carries from device to device -
    /// pinned and muted chats, the archive setting, and **the names saved in the primary
    /// phone's address book**.
    ///
    /// Why this exists: a linked device has no address book of its own. The history sync
    /// names the people who write (their push names) and says nothing about how the account's
    /// owner saved them, and the phone's own contact store is a *different* book. The names
    /// the official app shows - "Mamma", "Papà" - travel in the app state, as `contact`
    /// actions, and this is the reader for them.
    ///
    /// How the bytes are laid out, and why each check is here: every record is an AES-256-CBC
    /// `SyncActionData` whose index is an HMAC and whose value carries a 32-byte MAC; a
    /// snapshot is all of the records with a MAC over their LT-Hash; a patch is a version's
    /// worth of changes with a MAC over its own value MACs. All of it is the reference's
    /// (`@whiskeysockets/baileys`, `Utils/chat-utils.js` and `Utils/lt-hash.js`), and the
    /// keys are the ones the phone shares with a newly linked device
    /// (`Message.ProtocolMessage.APP_STATE_SYNC_KEY_SHARE`).
    ///
    /// The names are applied and nothing else is: pins, mutes and the archive setting are
    /// already this phone's own (`ChatPreferences`), and letting a snapshot set them would
    /// change what a person did on this device.
    /// </summary>
    public static class AppStateSync
    {
        /// <summary>The collections the reference syncs, in its order.</summary>
        public static readonly string[] Collections = new string[]
        {
            "critical_block", "critical_unblock_low", "regular_high", "regular_low", "regular"
        };

        /// <summary>The index the saved contact names are filed under.</summary>
        public const string ContactIndex = "contact";

        /// <summary>
        /// The sync request: every collection from version zero, with its snapshot.
        ///
        /// From zero every time, and not from the version of the last run: a snapshot is
        /// self-contained and the names it carries are idempotent, so the state to keep on the
        /// phone is one number less to be wrong about. What it costs is the whole snapshot at
        /// every login, which is the same read the first one did.
        /// </summary>
        public static BinaryNode Query(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("an app state query needs an id");

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("id", id);
            iq.Attrs.Add("type", "set");
            iq.Attrs.Add("xmlns", "w:sync:app:state");
            iq.Attrs.Add("to", JidUtils.WhatsappServer);

            var sync = new BinaryNode("sync");
            var collections = new List<BinaryNode>();

            for (int i = 0; i < Collections.Length; i++)
            {
                var collection = new BinaryNode("collection");
                collection.Attrs.Add("name", Collections[i]);
                collection.Attrs.Add("version", "0");
                collection.Attrs.Add("return_snapshot", "true");
                collections.Add(collection);
            }

            sync.Content = collections;
            iq.Content = new List<BinaryNode> { sync };

            return iq;
        }

        /// <summary>
        /// The answer as collections. One a caller does not know - or cannot read - is
        /// skipped, which is what keeps a server that adds a collection sending an iq this
        /// client can still answer.
        /// </summary>
        public static IList<AppStateCollection> Read(BinaryNode answer)
        {
            var collections = new List<AppStateCollection>();
            if (answer == null) return collections;

            BinaryNode sync = answer.Child("sync");
            if (sync == null) return collections;

            IList<BinaryNode> nodes = sync.Children();
            if (nodes == null) return collections;

            for (int i = 0; i < nodes.Count; i++)
            {
                BinaryNode node = nodes[i];
                if (node == null || node.Tag != "collection") continue;

                var collection = new AppStateCollection();
                collection.Name = node.Attrs["name"];

                BinaryNode snapshot = node.Child("snapshot");
                if (snapshot != null && snapshot.Bytes() != null)
                {
                    collection.Snapshot = SyncdSnapshot.Decode(snapshot.Bytes());
                }

                // A patch is either a child of the collection or of a `<patches>` wrapper,
                // and the reference reads both (`extractSyncdPatches`).
                BinaryNode patches = node.Child("patches");
                IList<BinaryNode> sources = patches != null ? patches.Children() : node.Children();
                if (sources != null)
                {
                    for (int p = 0; p < sources.Count; p++)
                    {
                        BinaryNode patch = sources[p];
                        if (patch == null || patch.Tag != "patch" || patch.Bytes() == null) continue;

                        collection.Patches.Add(SyncdPatch.Decode(patch.Bytes()));
                    }
                }

                collections.Add(collection);
            }

            return collections;
        }

        /// <summary>
        /// One collection, verified and read.
        ///
        /// Throws a <see cref="ProtocolException"/> for anything that does not verify - a
        /// record whose MAC does not hold, a key this device was never given, a snapshot whose
        /// LT-Hash is not what the MAC covers. Nothing is half-applied: a collection either
        /// reads whole or it does not read, which is the only honest answer for a set of names
        /// that could otherwise be a mix of this account's and a tampered one's.
        /// </summary>
        public static AppStateResult Apply(AppStateCollection collection, AppStateKeyset keys)
        {
            if (collection == null) throw new ProtocolException("there is no app state collection to read");
            if (keys == null || keys.IsEmpty) throw new ProtocolException("the app state needs a key this device does not have");

            var result = new AppStateResult();
            result.Collection = collection.Name;
            result.Version = 0;

            byte[] sum = LtHash.Empty();

            if (collection.Snapshot != null)
            {
                SyncdSnapshot snapshot = collection.Snapshot;

                if (snapshot.KeyId == null)
                {
                    // An external snapshot: a `ExternalBlobReference` and a download of its
                    // own, which this client does not fetch. The answer is to say so, not to
                    // read the reference's fields as a snapshot's.
                    throw new ProtocolException("the " + collection.Name
                        + " snapshot is an external blob, which this client does not download");
                }

                for (int i = 0; i < snapshot.Records.Count; i++)
                {
                    sum = ReadRecord(snapshot.Records[i], SyncdMutation.Set, keys, result, sum);
                }

                MutationKeys snapshotKeys = MutationKeys.Derive(KeyFor(keys, snapshot.KeyId, collection.Name));
                byte[] expected = SnapshotMac(snapshotKeys, sum, snapshot.Version, collection.Name);
                if (!Hmac.FixedTimeEquals(expected, snapshot.Mac))
                {
                    throw new ProtocolException("the LT-Hash of the " + collection.Name + " snapshot at version "
                        + snapshot.Version + " is not what its MAC covers");
                }

                result.Version = snapshot.Version;
            }

            for (int i = 0; i < collection.Patches.Count; i++)
            {
                SyncdPatch patch = collection.Patches[i];

                if (patch.ExitCode != 0)
                {
                    // 2 asks for a resync from a snapshot, 3 says the version asked for was
                    // not the one sent. Either way the mutations are not the whole story.
                    throw new ProtocolException("the server asked for a full resync of " + collection.Name
                        + " (exit " + patch.ExitCode + ", version " + patch.Version + ")");
                }

                MutationKeys patchKeys = MutationKeys.Derive(KeyFor(keys, patch.KeyId, collection.Name));

                if (patch.PatchMac != null)
                {
                    var valueMacs = new List<byte[]>();
                    for (int m = 0; m < patch.Mutations.Count; m++)
                    {
                        SyncdRecord record = patch.Mutations[m].Record;
                        if (record == null || record.Value == null || record.Value.Length < 32)
                        {
                            throw new ProtocolException("a " + collection.Name + " patch carries a record with no MAC");
                        }

                        valueMacs.Add(Slice(record.Value, record.Value.Length - 32, 32));
                    }

                    byte[] expectedPatchMac = PatchMac(patchKeys, patch.SnapshotMac, valueMacs, patch.Version, collection.Name);
                    if (!Hmac.FixedTimeEquals(expectedPatchMac, patch.PatchMac))
                    {
                        throw new ProtocolException("the MAC of the " + collection.Name + " patch at version "
                            + patch.Version + " does not verify");
                    }
                }

                for (int m = 0; m < patch.Mutations.Count; m++)
                {
                    SyncdMutation mutation = patch.Mutations[m];
                    if (mutation == null || mutation.Record == null) continue;

                    sum = ReadRecord(mutation.Record, mutation.Operation, keys, result, sum);
                }

                // A patch's MAC covers the LT-Hash it produced: the same check a snapshot
                // gets, one version at a time.
                byte[] computed = SnapshotMac(patchKeys, sum, patch.Version, collection.Name);
                if (patch.SnapshotMac != null && !Hmac.FixedTimeEquals(computed, patch.SnapshotMac))
                {
                    throw new ProtocolException("the LT-Hash of the " + collection.Name + " at version "
                        + patch.Version + " is not what the patch's snapshot MAC covers");
                }

                result.Version = patch.Version;
            }

            return result;
        }

        /// <summary>
        /// One record: its MAC, its index MAC, its value, and - for a contact action - the
        /// name. Returns the LT-Hash with this record added or taken out.
        /// </summary>
        private static byte[] ReadRecord(SyncdRecord record, int operation, AppStateKeyset keys,
            AppStateResult result, byte[] sum)
        {
            if (record == null || record.Value == null || record.Index == null)
            {
                throw new ProtocolException("a " + result.Collection + " record is missing its value or its index");
            }

            if (record.Value.Length < 16 + 16 + 32)
            {
                throw new ProtocolException("a " + result.Collection + " record is too short to carry a value");
            }

            byte[] keyData = KeyFor(keys, record.KeyId, result.Collection);
            MutationKeys recordKeys = MutationKeys.Derive(keyData);

            int encryptedLength = record.Value.Length - 32;
            byte[] encrypted = Slice(record.Value, 0, encryptedLength);
            byte[] mac = Slice(record.Value, encryptedLength, 32);

            byte[] expected = MutationMac(recordKeys, operation, encrypted, record.KeyId);
            if (!Hmac.FixedTimeEquals(expected, mac))
            {
                throw new ProtocolException("the MAC of a " + result.Collection + " record does not verify");
            }

            byte[] iv = Slice(encrypted, 0, 16);
            byte[] ciphertext = Slice(encrypted, 16, encryptedLength - 16);
            byte[] plaintext = Aes.CbcDecrypt(recordKeys.ValueEncryptionKey, iv, ciphertext);

            SyncActionData action = SyncActionData.Decode(plaintext);
            if (string.IsNullOrEmpty(action.Index))
            {
                throw new ProtocolException("a " + result.Collection + " record carries no index");
            }

            byte[] indexMac = Hmac.Compute(recordKeys.IndexKey, Encoding.UTF8.GetBytes(action.Index));
            if (!Hmac.FixedTimeEquals(indexMac, record.Index))
            {
                throw new ProtocolException("the index MAC of a " + result.Collection + " record does not verify");
            }

            if (operation == SyncdMutation.Remove)
            {
                sum = LtHash.Subtract(sum, mac);
            }
            else
            {
                sum = LtHash.Add(sum, mac);

                if (IndexType(action.Index) == ContactIndex) ReadContact(action, result);
            }

            return sum;
        }

        /// <summary>The name of one contact, filed under the index's address.</summary>
        private static void ReadContact(SyncActionData action, AppStateResult result)
        {
            ContactAction contact = ContactAction.Read(action.Value);
            if (contact == null) return;

            string name = NameBook.Clean(contact.Name);
            if (name == null) return;

            var saved = new SavedContact();
            saved.Address = IndexElement(action.Index, 1);
            saved.Name = name;
            saved.PnJid = contact.PnJid;
            saved.LidJid = contact.LidJid;

            if (saved.Address == null && saved.PnJid == null && saved.LidJid == null) return;

            result.Contacts.Add(saved);
        }

        /// <summary>The first element of a JSON index: `["contact", "…@s.whatsapp.net"]`.</summary>
        private static string IndexType(string index)
        {
            return IndexElement(index, 0);
        }

        /// <summary>
        /// One element of the JSON array the index is, read by walking the quoted runs: the
        /// index holds JIDs and a chat state, so it never needs an escape.
        /// </summary>
        private static string IndexElement(string index, int position)
        {
            if (string.IsNullOrEmpty(index)) return null;

            int seen = 0;
            int i = 0;

            while (i < index.Length)
            {
                if (index[i] != '"')
                {
                    i++;
                    continue;
                }

                int start = i + 1;
                int end = start;
                while (end < index.Length && index[end] != '"') end++;

                if (end >= index.Length) return null;
                if (seen == position) return index.Substring(start, end - start);

                seen++;
                i = end + 1;
            }

            return null;
        }

        private static byte[] KeyFor(AppStateKeyset keys, byte[] keyId, string collection)
        {
            byte[] data = keys.KeyFor(keyId);
            if (data == null)
            {
                throw new ProtocolException("the " + collection + " names a key this device was never given ("
                    + (keyId == null ? "none" : Convert.ToBase64String(keyId)) + ")");
            }

            return data;
        }

        /// <summary>
        /// The MAC of one encrypted value: the operation, the key's id, the bytes and the
        /// length of that key id, signed with the value MAC key under sha512 and cut to 32
        /// bytes (`generateMac`).
        /// </summary>
        private static byte[] MutationMac(MutationKeys keys, int operation, byte[] data, byte[] keyId)
        {
            if (keyId == null) throw new ProtocolException("an app state record names no key");

            var prefix = new byte[1 + keyId.Length];
            prefix[0] = operation == SyncdMutation.Remove ? (byte)2 : (byte)1;
            Buffer.BlockCopy(keyId, 0, prefix, 1, keyId.Length);

            var total = new byte[prefix.Length + data.Length + 8];
            Buffer.BlockCopy(prefix, 0, total, 0, prefix.Length);
            Buffer.BlockCopy(data, 0, total, prefix.Length, data.Length);

            // The length of the key id, as eight big-endian bytes at the end.
            byte[] length = BigEndian((ulong)prefix.Length);
            Buffer.BlockCopy(length, 0, total, prefix.Length + data.Length, 8);

            return Slice(Hmac.ComputeSha512(keys.ValueMacKey, total), 0, 32);
        }

        /// <summary>The MAC over an LT-Hash: the sum, the version and the collection's name.</summary>
        private static byte[] SnapshotMac(MutationKeys keys, byte[] sum, int version, string collection)
        {
            var total = new byte[sum.Length + 8 + Encoding.UTF8.GetByteCount(collection)];
            Buffer.BlockCopy(sum, 0, total, 0, sum.Length);

            byte[] versionBytes = BigEndian((ulong)version);
            Buffer.BlockCopy(versionBytes, 0, total, sum.Length, 8);

            byte[] name = Encoding.UTF8.GetBytes(collection);
            Buffer.BlockCopy(name, 0, total, sum.Length + 8, name.Length);

            return Hmac.Compute(keys.SnapshotMacKey, total);
        }

        /// <summary>The MAC over a patch: its snapshot's MAC, its value MACs, the version and the name.</summary>
        private static byte[] PatchMac(MutationKeys keys, byte[] snapshotMac, IList<byte[]> valueMacs,
            int version, string collection)
        {
            int size = (snapshotMac == null ? 0 : snapshotMac.Length) + 8 + Encoding.UTF8.GetByteCount(collection);
            for (int i = 0; i < valueMacs.Count; i++) size += valueMacs[i].Length;

            var total = new byte[size];
            int at = 0;

            if (snapshotMac != null)
            {
                Buffer.BlockCopy(snapshotMac, 0, total, at, snapshotMac.Length);
                at += snapshotMac.Length;
            }

            for (int i = 0; i < valueMacs.Count; i++)
            {
                Buffer.BlockCopy(valueMacs[i], 0, total, at, valueMacs[i].Length);
                at += valueMacs[i].Length;
            }

            byte[] versionBytes = BigEndian((ulong)version);
            Buffer.BlockCopy(versionBytes, 0, total, at, 8);
            at += 8;

            byte[] name = Encoding.UTF8.GetBytes(collection);
            Buffer.BlockCopy(name, 0, total, at, name.Length);

            return Hmac.Compute(keys.PatchMacKey, total);
        }

        private static byte[] BigEndian(ulong value)
        {
            var bytes = new byte[8];
            for (int i = 7; i >= 0; i--)
            {
                bytes[i] = (byte)(value & 0xff);
                value >>= 8;
            }

            return bytes;
        }

        private static byte[] Slice(byte[] bytes, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(bytes, offset, slice, 0, count);
            return slice;
        }
    }

    /// <summary>
    /// The five keys an app-state value is read with, all derived from the key material the
    /// phone shared: 160 bytes of HKDF, then five 32-byte slices
    /// (`Utils/chat-utils.js`, `mutationKeys`).
    /// </summary>
    public sealed class MutationKeys
    {
        public byte[] IndexKey;
        public byte[] ValueEncryptionKey;
        public byte[] ValueMacKey;
        public byte[] SnapshotMacKey;
        public byte[] PatchMacKey;

        public static MutationKeys Derive(byte[] keyData)
        {
            if (keyData == null || keyData.Length != 32)
            {
                throw new ProtocolException("an app state key is 32 bytes");
            }

            byte[] expanded = Hkdf.Derive(keyData, null,
                Encoding.UTF8.GetBytes("WhatsApp Mutation Keys"), 160);

            var keys = new MutationKeys();
            keys.IndexKey = Slice(expanded, 0);
            keys.ValueEncryptionKey = Slice(expanded, 32);
            keys.ValueMacKey = Slice(expanded, 64);
            keys.SnapshotMacKey = Slice(expanded, 96);
            keys.PatchMacKey = Slice(expanded, 128);

            return keys;
        }

        private static byte[] Slice(byte[] bytes, int offset)
        {
            var slice = new byte[32];
            Buffer.BlockCopy(bytes, offset, slice, 0, 32);
            return slice;
        }
    }
}
