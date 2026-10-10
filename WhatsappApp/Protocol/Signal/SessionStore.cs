using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// The bytes a store writes and reads, one small blob per key.
    ///
    /// It is deliberately this narrow. A session record is a file on a phone; a one-time
    /// pre-key is a file that must be *deleted* the moment it is used; and the two have
    /// nothing else in common, so the interface is three methods over a key and a byte
    /// array, and both stores are written on top of it. The phone's implementation is
    /// `Services/FileSessionStorage`; a test's is a dictionary, and neither the record nor
    /// the pre-key store can tell which one it got.
    /// </summary>
    public interface ISessionStorage
    {
        /// <summary>The blob stored under a key, false when there is none.</summary>
        bool TryRead(string key, out byte[] value);

        /// <summary>Stores a blob, replacing whatever was under that key.</summary>
        void Write(string key, byte[] value);

        /// <summary>Removes a key. Removing one that is not there is not an error.</summary>
        void Remove(string key);
    }

    /// <summary>
    /// Where a session per peer lives, and what a caller gets back from it.
    ///
    /// One record per peer, keyed by whatever the caller names the conversation with - a
    /// JID today, a JID and a device id when the client has more than one device to talk
    /// to. The store holds nothing itself: it reads through to the bytes on every load, so
    /// handing out two of them is not two copies of a session that then drift.
    /// </summary>
    public sealed class SessionStore
    {
        private readonly ISessionStorage _storage;
        private readonly IRandomSource _random;

        public SessionStore(ISessionStorage storage, IRandomSource random)
        {
            if (storage == null) throw new ProtocolException("a session store needs somewhere to store");
            if (random == null) throw new ProtocolException("a session store needs a source for the keys its sessions draw");

            _storage = storage;
            _random = random;
        }

        /// <summary>
        /// The session with a peer, or null: null when there is none, and null for a record
        /// that is not whole. That is the same answer on purpose - a damaged record is not a
        /// session and the caller starts a new one - and it is what keeps a torn file from
        /// becoming an exception in the middle of a decryption (review focus 2).
        /// </summary>
        public SessionRecord Load(string peer)
        {
            if (string.IsNullOrEmpty(peer)) throw new ProtocolException("a session is stored under a peer's name");

            byte[] stored;
            if (!_storage.TryRead(Key(peer), out stored)) return null;

            return SessionRecord.Decode(stored, _random);
        }

        /// <summary>Writes a session, replacing the one that was there.</summary>
        public void Save(string peer, SessionRecord session)
        {
            if (string.IsNullOrEmpty(peer)) throw new ProtocolException("a session is stored under a peer's name");
            if (session == null) throw new ProtocolException("there is no session to save");

            _storage.Write(Key(peer), session.Encode());
        }

        /// <summary>Forgets a peer, which is what a blocked contact or a re-pair does.</summary>
        public void Remove(string peer)
        {
            if (string.IsNullOrEmpty(peer)) throw new ProtocolException("a session is stored under a peer's name");

            _storage.Remove(Key(peer));
        }

        private static string Key(string peer)
        {
            return "session/" + peer;
        }
    }

    /// <summary>
    /// The device's own pre-keys: the signed one it advertises, and the one-time ones it
    /// publishes and then spends.
    ///
    /// The two are one object because they are one job - the keys a peer's first message
    /// names - and because their lifetimes are the opposite of each other: the signed
    /// pre-key keeps its id, its private half and its signature until it is rotated, and a
    /// one-time pre-key exists to be handed out exactly once. `Take` is the whole of the
    /// second rule: it reads the key and deletes it in one call, so there is no way to ask
    /// for one twice, and a message that arrives twice cannot make the device reuse a key.
    ///
    /// A restarted app finds both where it left them, which is the point of the store: a
    /// one-time pre-key that was published on the server but forgotten by the phone is a
    /// first message nobody can open.
    /// </summary>
    public sealed class PreKeyStore
    {
        private const string Index = "prekeys/index";
        private const string Signed = "prekeys/signed";

        private readonly ISessionStorage _storage;

        public PreKeyStore(ISessionStorage storage)
        {
            if (storage == null) throw new ProtocolException("a pre-key store needs somewhere to store");

            _storage = storage;
        }

        /// <summary>How many one-time pre-keys are published and not yet spent.</summary>
        public int Remaining
        {
            get { return Published().Count; }
        }

        /// <summary>
        /// Publishes a one-time pre-key's private half under its id, which is what the
        /// server hands out and what an incoming message names.
        /// </summary>
        public void Publish(uint id, byte[] preKeyPrivate)
        {
            if (id == 0) throw new ProtocolException("pre-key ids start at one; zero is what an absent id looks like");
            if (preKeyPrivate == null || preKeyPrivate.Length != 32)
            {
                throw new ProtocolException("a one-time pre-key is 32 bytes");
            }

            List<uint> ids = Published();
            if (!ids.Contains(id))
            {
                ids.Add(id);
                _storage.Write(Index, Body(ids));
            }

            _storage.Write(Private(id), preKeyPrivate);
        }

        /// <summary>
        /// The private half of a one-time pre-key the peer's message names, and the key is
        /// spent by reading it: null when it was already used, and null when it was never
        /// published, which the caller answers the same way.
        /// </summary>
        public byte[] Take(uint id)
        {
            byte[] key;
            if (!_storage.TryRead(Private(id), out key)) return null;

            _storage.Remove(Private(id));

            List<uint> ids = Published();
            if (ids.Remove(id)) _storage.Write(Index, Body(ids));

            return key;
        }

        /// <summary>Whether a signed pre-key has been set, which is what a bundle needs before there is one.</summary>
        public bool HasSignedPreKey
        {
            get { return SignedPreKeyPrivate != null; }
        }

        /// <summary>The signed pre-key the device advertises, its private half and the signature over it.</summary>
        public byte[] SignedPreKeyPrivate
        {
            get { return Field(2); }
        }

        public byte[] SignedPreKeySignature
        {
            get { return Field(3); }
        }

        public uint SignedPreKeyId
        {
            get { return (uint)Number(1); }
        }

        /// <summary>
        /// Sets the signed pre-key, its id and its signature. Rotating it is the same call
        /// with a new id: the old one is replaced, and a message that used it is answered
        /// with a new session rather than with this key (the retire plan's M4 decides how
        /// long an old one is kept for messages already in flight).
        /// </summary>
        public void SetSignedPreKey(uint id, byte[] privateKey, byte[] signature)
        {
            if (id == 0) throw new ProtocolException("a signed pre-key id starts at one");
            if (privateKey == null || privateKey.Length != 32) throw new ProtocolException("a signed pre-key is 32 bytes");
            if (signature == null || signature.Length != 64) throw new ProtocolException("the signature over it is 64 bytes");

            var writer = new ProtoWriter();
            writer.Varint(1, id);
            writer.Bytes(2, privateKey);
            writer.Bytes(3, signature);

            _storage.Write(Signed, writer.ToArray());
        }

        /// <summary>The published ids, in the order they were published: the index a restart needs.</summary>
        private List<uint> Published()
        {
            var ids = new List<uint>();

            byte[] stored;
            if (!_storage.TryRead(Index, out stored) || stored == null) return ids;

            var reader = new ProtoReader(stored);
            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                // One repeated field, one id each: the order of the file is the order of
                // publication, which is what makes the server's hand-outs predictable.
                if (field.Number == 1 && field.WireType == 0) ids.Add((uint)reader.ReadVarint());
                else reader.SkipField(field.WireType);
            }

            return ids;
        }

        private static byte[] Body(List<uint> ids)
        {
            var writer = new ProtoWriter();
            for (int i = 0; i < ids.Count; i++) writer.Varint(1, ids[i]);

            return writer.ToArray();
        }

        private static string Private(uint id)
        {
            return "prekeys/one-time/" + id;
        }

        private byte[] Field(int number)
        {
            ulong unused;
            return Read(number, out unused);
        }

        private ulong Number(int number)
        {
            ulong value;
            Read(number, out value);

            return value;
        }

        /// <summary>The signed pre-key blob's field `number`: the number, or the bytes, or nothing.</summary>
        private byte[] Read(int number, out ulong value)
        {
            value = 0;

            byte[] stored;
            if (!_storage.TryRead(Signed, out stored) || stored == null) return null;

            var reader = new ProtoReader(stored);
            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == number && field.WireType == 2) return reader.ReadBytes();
                if (field.Number == number && field.WireType == 0) value = reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            return null;
        }
    }
}
