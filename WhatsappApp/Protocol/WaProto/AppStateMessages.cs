using System;
using System.Collections.Generic;
using System.Text;

namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// The `protocolMessage` of a message stanza, read down to the two fields the app-state
    /// sync cares about: which kind of protocol message it is, and - for the one that
    /// matters - the keys the phone shares with a device it has just linked.
    ///
    /// Why here: until now this app kept `protocolMessage` as raw bytes and read only the
    /// history notification out of it (`IncomingMessage`), so the app-state key share went
    /// past unread. That share is the only way a companion ever learns the keys the server's
    /// app state is encrypted with, and without them a phone cannot read the address book the
    /// primary keeps in the app state at all.
    ///
    /// `type` is field 2 and `appStateSyncKeyShare` field 7
    /// (`@whiskeysockets/baileys`, `WAProto` - `Message.ProtocolMessage`).
    /// </summary>
    public sealed class MessageProtocol
    {
        /// <summary>`HISTORY_SYNC_NOTIFICATION`: the chat list arriving.</summary>
        public const int HistorySyncNotification = 5;

        /// <summary>`APP_STATE_SYNC_KEY_SHARE`: the app-state keys, from the primary.</summary>
        public const int AppStateSyncKeyShare = 6;

        /// <summary>`APP_STATE_SYNC_KEY_REQUEST`: the primary asking this device for one.</summary>
        public const int AppStateSyncKeyRequest = 7;

        /// <summary>`REVOKE`: the sender takes a message back for everybody.</summary>
        public const int Revoke = 0;

        /// <summary>
        /// `PEER_DATA_OPERATION_REQUEST_MESSAGE`: a request to this account's own devices,
        /// which is what asks for history after the pairing (`Message.ProtocolMessage`,
        /// field 16 in the reference's `WAProto`).
        /// </summary>
        public const int PeerDataOperationRequest = 16;

        /// <summary>`PeerDataOperationRequestType.HISTORY_SYNC_ON_DEMAND`: one chat's history.</summary>
        public const int HistorySyncOnDemandType = 3;

        /// <summary>`PeerDataOperationRequestType.FULL_HISTORY_SYNC_ON_DEMAND`: the whole history again.</summary>
        public const int FullHistorySyncOnDemandType = 6;

        public int Type;

        /// <summary>The encoded `MessageKey`: which message a revoke (or an edit) names.</summary>
        public byte[] Key;

        /// <summary>The encoded `AppStateSyncKeyShare`, or null when the stanza carries none.</summary>
        public byte[] KeyShare;

        public static MessageProtocol Decode(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no protocol message to read");

            var protocol = new MessageProtocol();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        protocol.Key = reader.ReadBytes(field.WireType);
                        break;

                    case 2:
                        if (field.WireType == 0) protocol.Type = (int)reader.ReadVarint();
                        else reader.SkipField(field.WireType);
                        break;

                    case 7:
                        protocol.KeyShare = reader.ReadBytes(field.WireType);
                        break;

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return protocol;
        }

        /// <summary>
        /// The `ProtocolMessage` that takes a message back: the key of the message it names
        /// and the type `REVOKE`.
        ///
        /// The field numbers are the reference's, the same table `type` and the key share
        /// above came from: `Message.ProtocolMessage.key` is 1 and `type` is 2,
        /// `MessageKey.remoteJid` is 1, `fromMe` 2 and `id` 3 (`@whiskeysockets/baileys`,
        /// `WAProto`). `fromMe` is true because only what this phone sent can be taken back.
        /// </summary>
        public static byte[] RevokeMessage(string jid, string messageId)
        {
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a revoke needs the chat it happened in");
            if (string.IsNullOrEmpty(messageId)) throw new ProtocolException("a revoke needs the message it takes back");

            byte[] key = new ProtoWriter()
                .Text(1, jid)
                .Varint(2, 1)
                .Text(3, messageId)
                .ToArray();

            return new ProtoWriter()
                .Bytes(1, key)
                .Varint(2, Revoke)
                .ToArray();
        }

        /// <summary>
        /// The `ProtocolMessage` that asks this account's own devices for one chat's
        /// history: `PEER_DATA_OPERATION_REQUEST_MESSAGE` (type 16) carrying
        /// `HistorySyncOnDemandRequest` (field 4) of type `HISTORY_SYNC_ON_DEMAND` (3).
        /// The answer is the `ON_DEMAND` history chunk the reader already accepts.
        ///
        /// The field numbers are the reference's (`@whiskeysockets/baileys`, `WAProto`,
        /// `Message.PeerDataOperationRequestMessage.HistorySyncOnDemandRequest`): the
        /// chat is 1, the oldest message this phone has is 2 to 3, the number of messages
        /// asked for is 4 and its moment is 5. The oldest is how the answer goes back
        /// further than what is on screen; a request with none of it asks for the newest
        /// messages of the chat.
        /// </summary>
        public static byte[] HistorySyncOnDemandRequest(string chatJid, string oldestMsgId,
            bool oldestMsgFromMe, int count, long oldestTimestampMs)
        {
            if (string.IsNullOrEmpty(chatJid))
            {
                throw new ProtocolException("a history request needs the chat it is for");
            }
            if (count <= 0)
            {
                throw new ProtocolException("a history request needs a number of messages to ask for");
            }

            var request = new ProtoWriter();
            request.Text(1, chatJid);
            if (!string.IsNullOrEmpty(oldestMsgId))
            {
                request.Text(2, oldestMsgId);
                if (oldestMsgFromMe) request.Varint(3, 1);
            }
            request.Varint(4, (ulong)count);
            if (oldestTimestampMs > 0) request.Varint(5, (ulong)oldestTimestampMs);

            byte[] pdo = new ProtoWriter()
                .Varint(1, HistorySyncOnDemandType)
                .Bytes(4, request.ToArray())
                .ToArray();

            return new ProtoWriter()
                .Varint(2, PeerDataOperationRequest)
                .Bytes(16, pdo)
                .ToArray();
        }

        /// <summary>
        /// The `ProtocolMessage` that asks for the whole history again: the same request a
        /// pairing makes, made after it - `FULL_HISTORY_SYNC_ON_DEMAND` (type 6) carrying
        /// `FullHistorySyncOnDemandRequest` (field 6): its `requestMetadata` field 1 and
        /// this client's `HistorySyncConfig` field 2 (`DeviceProps.HistorySyncConfig`, the
        /// very one the pairing sent). It is the answer to a login that brought no history,
        /// whose other answer used to be re-linking the account.
        /// </summary>
        public static byte[] FullHistorySyncOnDemandRequest(string requestId)
        {
            if (string.IsNullOrEmpty(requestId))
            {
                throw new ProtocolException("a full history request needs its id");
            }

            byte[] metadata = new ProtoWriter()
                .Text(1, requestId)
                .ToArray();

            byte[] full = new ProtoWriter()
                .Bytes(1, metadata)
                .Bytes(2, DeviceProps.HistorySyncConfig())
                .ToArray();

            byte[] pdo = new ProtoWriter()
                .Varint(1, FullHistorySyncOnDemandType)
                .Bytes(6, full)
                .ToArray();

            return new ProtoWriter()
                .Varint(2, PeerDataOperationRequest)
                .Bytes(16, pdo)
                .ToArray();
        }

        /// <summary>
        /// The id of the message a revoke names, or null when this is not a revoke.
        ///
        /// Why it is read here and not by whoever handles the stanza: a revoke is a message
        /// like any other on the wire, and the only thing that says it is one is the type
        /// inside this blob.
        /// </summary>
        public static string RevokedId(byte[] protocolMessage)
        {
            if (protocolMessage == null) return null;

            MessageProtocol protocol;
            try
            {
                protocol = Decode(protocolMessage);
            }
            catch (ProtocolException)
            {
                return null;
            }

            if (protocol.Type != Revoke || protocol.Key == null) return null;

            // The second walk over the key bytes answers the same way as the first:
            // the contract is the id or null, and a truncated field is one of the
            // things that ends a walk - it was the one case that threw out of callers
            // that had no reason to expect it.
            try
            {
                var reader = new ProtoReader(protocol.Key);
                while (reader.HasMore)
                {
                    ProtoField field = reader.ReadField();
                    if (field.Number == 3)
                    {
                        byte[] id = reader.ReadBytes(field.WireType);
                        return id == null ? null : Encoding.UTF8.GetString(id, 0, id.Length);
                    }

                    reader.SkipField(field.WireType);
                }
            }
            catch (ProtocolException)
            {
                return null;
            }

            return null;
        }
    }

    /// <summary>
    /// One app-state key: an id the records name, and the 32 bytes every mutation key is
    /// derived from (`SyncAction` and `AppStateSync` do the deriving).
    /// </summary>
    public sealed class AppStateKey
    {
        /// <summary>The id, base64, exactly as the records and patches name it.</summary>
        public string Id;

        /// <summary>The key material: `AppStateSyncKeyData.keyData`, 32 bytes.</summary>
        public byte[] KeyData;
    }

    /// <summary>
    /// The key share a linked device is sent: `AppStateSyncKeyShare` holds a
    /// `AppStateSyncKey` per key, and each names itself with a `keyId` node around the raw id
    /// bytes (`WAProto`: `AppStateSyncKeyShare.keys` 1, `AppStateSyncKey.keyId` 1 and
    /// `.keyData` 2, `AppStateSyncKeyId.keyId` 1, `AppStateSyncKeyData.keyData` 1).
    /// </summary>
    public static class AppStateKeys
    {
        /// <summary>Every key in an encoded `AppStateSyncKeyShare`. Never null.</summary>
        public static IList<AppStateKey> Read(byte[] keyShare)
        {
            var keys = new List<AppStateKey>();
            if (keyShare == null) return keys;

            var reader = new ProtoReader(keyShare);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();
                if (field.Number != 1 || field.WireType != 2)
                {
                    reader.SkipField(field.WireType);
                    continue;
                }

                AppStateKey key = ReadKey(reader.ReadBytes(field.WireType));
                if (key != null) keys.Add(key);
            }

            return keys;
        }

        /// <summary>One `AppStateSyncKey`, or null when it carries no id or no material.</summary>
        private static AppStateKey ReadKey(byte[] bytes)
        {
            string id = null;
            byte[] data = null;

            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1 && field.WireType == 2)
                {
                    id = ReadKeyId(reader.ReadBytes(field.WireType));
                }
                else if (field.Number == 2 && field.WireType == 2)
                {
                    data = ReadKeyData(reader.ReadBytes(field.WireType));
                }
                else
                {
                    reader.SkipField(field.WireType);
                }
            }

            if (string.IsNullOrEmpty(id) || data == null || data.Length == 0) return null;

            var key = new AppStateKey();
            key.Id = id;
            key.KeyData = data;

            return key;
        }

        /// <summary>`AppStateSyncKeyId`: the raw id bytes as base64, which is how it is named.</summary>
        private static string ReadKeyId(byte[] bytes)
        {
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1 && field.WireType == 2) return Convert.ToBase64String(reader.ReadBytes(field.WireType));

                reader.SkipField(field.WireType);
            }

            return null;
        }

        /// <summary>`AppStateSyncKeyData`: field 1 is the key material itself.</summary>
        private static byte[] ReadKeyData(byte[] bytes)
        {
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1 && field.WireType == 2) return reader.ReadBytes(field.WireType);

                reader.SkipField(field.WireType);
            }

            return null;
        }
    }

    /// <summary>
    /// One version of an app state collection: `<snapshot>` in the answer to a sync, with
    /// every record in it and the MAC over the whole set
    /// (`SyncdSnapshot`: `version` 1, `records` 2, `mac` 3, `keyId` 4).
    /// </summary>
    public sealed class SyncdSnapshot
    {
        public int Version;
        public IList<SyncdRecord> Records = new List<SyncdRecord>();
        public byte[] Mac;

        /// <summary>The raw bytes of the key that signed it (`KeyId.id`).</summary>
        public byte[] KeyId;

        public static SyncdSnapshot Decode(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no app state snapshot to read");

            var snapshot = new SyncdSnapshot();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) snapshot.Version = ReadVersion(sub);
                            break;
                        }

                    case 2:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) snapshot.Records.Add(SyncdRecord.Decode(sub));
                            break;
                        }

                    case 3:
                        snapshot.Mac = reader.ReadBytes(field.WireType);
                        break;

                    case 4:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) snapshot.KeyId = ReadKeyId(sub);
                            break;
                        }

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return snapshot;
        }

        /// <summary>`SyncdVersion.version`, field 1.</summary>
        internal static int ReadVersion(byte[] bytes)
        {
            if (bytes == null) return 0;

            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1 && field.WireType == 0) return (int)reader.ReadVarint();

                reader.SkipField(field.WireType);
            }

            return 0;
        }

        /// <summary>`KeyId.id`, field 1: the raw bytes, not the base64 name.</summary>
        internal static byte[] ReadKeyId(byte[] bytes)
        {
            if (bytes == null) return null;

            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1 && field.WireType == 2) return reader.ReadBytes(field.WireType);

                reader.SkipField(field.WireType);
            }

            return null;
        }
    }

    /// <summary>
    /// One record of a collection: its index MAC, its encrypted value and the key that reads
    /// it (`SyncdRecord`: `index` 1, `value` 2, `keyId` 3; `SyncdIndex.blob` 1,
    /// `SyncdValue.blob` 1).
    /// </summary>
    public sealed class SyncdRecord
    {
        /// <summary>HMAC of the JSON index, under the mutation keys' index key.</summary>
        public byte[] Index;

        /// <summary>The AES-256-CBC value with its 32-byte MAC appended.</summary>
        public byte[] Value;

        /// <summary>The raw bytes of the key this record is encrypted with.</summary>
        public byte[] KeyId;

        public static SyncdRecord Decode(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no app state record to read");

            var record = new SyncdRecord();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) record.Index = ReadBlob(sub);
                            break;
                        }

                    case 2:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) record.Value = ReadBlob(sub);
                            break;
                        }

                    case 3:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) record.KeyId = SyncdSnapshot.ReadKeyId(sub);
                            break;
                        }

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return record;
        }

        /// <summary>`SyncdIndex.blob` and `SyncdValue.blob`: both a single field 1.</summary>
        private static byte[] ReadBlob(byte[] bytes)
        {
            if (bytes == null) return null;

            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1 && field.WireType == 2) return reader.ReadBytes(field.WireType);

                reader.SkipField(field.WireType);
            }

            return null;
        }
    }

    /// <summary>
    /// One change to a collection: set a record, or take one out (`SyncdMutation`:
    /// `operation` 1, `record` 2). The operation is the reference's enum, `SET` 0 and
    /// `REMOVE` 1, and a record with no operation is a `SET`.
    /// </summary>
    public sealed class SyncdMutation
    {
        public const int Set = 0;
        public const int Remove = 1;

        public int Operation;
        public SyncdRecord Record;

        public static SyncdMutation Decode(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no app state mutation to read");

            var mutation = new SyncdMutation();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        if (field.WireType == 0) mutation.Operation = (int)reader.ReadVarint();
                        else reader.SkipField(field.WireType);
                        break;

                    case 2:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) mutation.Record = SyncdRecord.Decode(sub);
                            break;
                        }

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return mutation;
        }
    }

    /// <summary>
    /// The changes made to a collection after the version a client has (`SyncdPatch`:
    /// `version` 1, `mutations` 2, `externalMutations` 3, `snapshotMac` 4, `patchMac` 5,
    /// `keyId` 6, `exitCode` 7).
    /// </summary>
    public sealed class SyncdPatch
    {
        public int Version;
        public IList<SyncdMutation> Mutations = new List<SyncdMutation>();
        public byte[] SnapshotMac;
        public byte[] PatchMac;
        public byte[] KeyId;

        /// <summary>
        /// The server's verdict when it is not zero: 2 asks the client to resync from a
        /// snapshot, 3 says the version it sent was not the one asked for. A patch that
        /// carries one is not applied - its mutations are not the whole story.
        /// </summary>
        public int ExitCode;

        public static SyncdPatch Decode(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no app state patch to read");

            var patch = new SyncdPatch();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) patch.Version = SyncdSnapshot.ReadVersion(sub);
                            break;
                        }

                    case 2:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) patch.Mutations.Add(SyncdMutation.Decode(sub));
                            break;
                        }

                    case 4:
                        patch.SnapshotMac = reader.ReadBytes(field.WireType);
                        break;

                    case 5:
                        patch.PatchMac = reader.ReadBytes(field.WireType);
                        break;

                    case 6:
                        {
                            byte[] sub = reader.ReadBytes(field.WireType);
                            if (sub != null) patch.KeyId = SyncdSnapshot.ReadKeyId(sub);
                            break;
                        }

                    case 7:
                        if (field.WireType == 0) patch.ExitCode = (int)reader.ReadVarint();
                        else reader.SkipField(field.WireType);
                        break;

                    // 3 is `externalMutations`, a download of its own: a patch that carries
                    // one is read as a patch with no mutations, which the caller sees as an
                    // exit code it did not get - the honest reading of a change this client
                    // cannot fetch.
                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return patch;
        }
    }

    /// <summary>
    /// What one record of a collection says: the index it is filed under, and the action
    /// itself (`SyncActionData`: `index` 1, `value` 2). The index is the JSON array the
    /// app-state code files by - `["contact"]` for the address book - and the value is a
    /// `SyncActionValue` that only the caller's reader knows how to read.
    /// </summary>
    public sealed class SyncActionData
    {
        public string Index;
        public byte[] Value;

        public static SyncActionData Decode(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no app state action to read");

            var action = new SyncActionData();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        action.Index = Utf8(reader.ReadBytes(field.WireType));
                        break;

                    case 2:
                        action.Value = reader.ReadBytes(field.WireType);
                        break;

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return action;
        }

        private static string Utf8(byte[] bytes)
        {
            return bytes == null ? null : Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// The saved name of a contact, as the primary keeps it in its own address book.
    ///
    /// This is the one action this client reads, and the reason the whole app-state sync is
    /// here: a companion device has no address book of the primary's, so a person saved as
    /// "Mamma" is a phone number until this arrives. The reference applies it as the
    /// contact's name and marks it saved (`processSyncAction`, `contactAction`), and the
    /// fields are `fullName` 1, `firstName` 2, `lidJid` 3, `saveOnPrimaryAddressbook` 4,
    /// `pnJid` 5, `username` 6 (`WAProto`, `SyncActionValue.ContactAction`).
    /// </summary>
    public sealed class ContactAction
    {
        public string FullName;
        public string FirstName;
        public string LidJid;
        public string PnJid;
        public bool SaveOnPrimaryAddressbook;

        /// <summary>The name to show: the full one, then the first one, then null.</summary>
        public string Name
        {
            get
            {
                if (!string.IsNullOrEmpty(FullName)) return FullName;
                return string.IsNullOrEmpty(FirstName) ? null : FirstName;
            }
        }

        /// <summary>
        /// The contact action inside a decoded `SyncActionData.value`, or null when the
        /// action is some other kind (a mute, a pin, a chat state).
        /// </summary>
        public static ContactAction Read(byte[] syncActionValue)
        {
            if (syncActionValue == null) return null;

            var reader = new ProtoReader(syncActionValue);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 3 && field.WireType == 2) return Decode(reader.ReadBytes(field.WireType));

                reader.SkipField(field.WireType);
            }

            return null;
        }

        private static ContactAction Decode(byte[] bytes)
        {
            var action = new ContactAction();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        action.FullName = Utf8(reader.ReadBytes(field.WireType));
                        break;

                    case 2:
                        action.FirstName = Utf8(reader.ReadBytes(field.WireType));
                        break;

                    case 3:
                        action.LidJid = Utf8(reader.ReadBytes(field.WireType));
                        break;

                    case 4:
                        if (field.WireType == 0) action.SaveOnPrimaryAddressbook = reader.ReadVarint() != 0;
                        else reader.SkipField(field.WireType);
                        break;

                    case 5:
                        action.PnJid = Utf8(reader.ReadBytes(field.WireType));
                        break;

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return action;
        }

        private static string Utf8(byte[] bytes)
        {
            return bytes == null ? null : Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
    }
}
