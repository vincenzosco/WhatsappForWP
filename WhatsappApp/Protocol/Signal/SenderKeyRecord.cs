using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// A group's sender key message, as it arrives: a version byte, the protobuf, and a
    /// 64-byte signature over both.
    ///
    /// The layout is `libsignal`'s and it is read from the reference rather than
    /// remembered (`@whiskeysockets/baileys`, `Signal/Group/sender-key-message.js`):
    ///
    ///     serialized = [0x33] || protobuf{ id=1, iteration=2, ciphertext=3 } || signature(64)
    ///     signature  = XEdDSA(signingKeyPrivate, [0x33] || protobuf)
    ///
    /// The version byte is `(3 &lt;&lt; 4) | 3` - `CURRENT_VERSION` in both nibbles, which is
    /// how every WhatsApp client writes it (`ciphertext-message.js`).
    /// </summary>
    public sealed class SenderKeyMessage
    {
        /// <summary>The signature is appended, never length-prefixed.</summary>
        public const int SignatureLength = 64;

        /// <summary>The version both nibbles of the first byte carry.</summary>
        public const int CurrentVersion = 3;

        /// <summary>The first byte: `(version &lt;&lt; 4) | version`.</summary>
        public const byte VersionByte = 0x33;

        public uint KeyId;
        public uint Iteration;
        public byte[] Ciphertext;

        /// <summary>The 64 bytes at the end.</summary>
        public byte[] Signature;

        /// <summary>The version byte and the protobuf: what the signature covers.</summary>
        public byte[] SignedPart;

        /// <summary>The whole thing, as it goes on the wire.</summary>
        public byte[] Serialized;

        /// <summary>
        /// Reads a message off the wire. A body with no signature, or one whose last 64
        /// bytes leave no protobuf, is refused by name: every one of those would otherwise
        /// surface as a decryption failure nobody can explain.
        /// </summary>
        public static SenderKeyMessage Parse(byte[] serialized)
        {
            if (serialized == null || serialized.Length <= SignatureLength + 1)
            {
                throw new ProtocolException("a sender key message is a protobuf and a 64-byte signature, not "
                    + (serialized == null ? "nothing" : serialized.Length + " bytes"));
            }

            int body = serialized.Length - SignatureLength;

            var message = new SenderKeyMessage();
            message.Serialized = serialized;
            message.SignedPart = Slice(serialized, 0, body);
            message.Signature = Slice(serialized, body, SignatureLength);

            if (serialized[0] != VersionByte)
            {
                throw new ProtocolException("this sender key message is version "
                    + (serialized[0] >> 4) + " and this client speaks " + CurrentVersion);
            }

            var reader = new ProtoReader(Slice(serialized, 1, body - 1));

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1: message.KeyId = (uint)reader.ReadVarint(); break;
                    case 2: message.Iteration = (uint)reader.ReadVarint(); break;
                    case 3: message.Ciphertext = reader.ReadBytes(); break;
                    default: reader.SkipField(field.WireType); break;
                }
            }

            if (message.Ciphertext == null) throw new ProtocolException("a sender key message with no ciphertext");

            return message;
        }

        /// <summary>
        /// A message signed here: the protobuf first, because it is the signature's subject
        /// and the version byte is part of it.
        /// </summary>
        public static SenderKeyMessage Create(uint keyId, uint iteration, byte[] ciphertext, byte[] signingPrivate)
        {
            if (ciphertext == null || ciphertext.Length == 0) throw new ProtocolException("there is nothing to encrypt");
            if (signingPrivate == null || signingPrivate.Length != 32)
            {
                throw new ProtocolException("a sender key signing key is 32 bytes");
            }

            byte[] body = new ProtoWriter()
                .Varint(1, keyId)
                .Varint(2, iteration)
                .Bytes(3, ciphertext)
                .ToArray();

            byte[] signed = Concat(new byte[] { VersionByte }, body);

            var message = new SenderKeyMessage();
            message.KeyId = keyId;
            message.Iteration = iteration;
            message.Ciphertext = ciphertext;
            message.SignedPart = signed;
            message.Signature = XEdDsa.Sign(signingPrivate, signed);
            message.Serialized = Concat(signed, message.Signature);

            return message;
        }

        /// <summary>
        /// True when the signature is the signing key's. The check comes **before** the
        /// decrypt, as it does in the reference (`group_cipher.js`, `decrypt`): a body this
        /// client has no business opening never reaches the cipher.
        /// </summary>
        public bool Verifies(byte[] signingPublic)
        {
            byte[] raw = RawKey(signingPublic);

            return raw != null && XEdDsa.Verify(raw, SignedPart, Signature);
        }

        /// <summary>
        /// The 32-byte key inside a signing key. The distribution carries **33** bytes: the
        /// `0x05` type byte libsignal prepends (`sender-key-state.js`, `getSigningKeyPublic`),
        /// and `XEdDsa.Verify` wants the bare Montgomery key.
        /// </summary>
        public static byte[] RawKey(byte[] signingPublic)
        {
            if (signingPublic == null) return null;
            if (signingPublic.Length == 32) return signingPublic;
            if (signingPublic.Length == 33 && signingPublic[0] == 0x05) return Slice(signingPublic, 1, 32);

            return null;
        }

        /// <summary>The 33-byte form WhatsApp carries, for a bare 32-byte public key.</summary>
        public static byte[] WireKey(byte[] montgomeryPublic)
        {
            if (montgomeryPublic == null) return null;
            if (montgomeryPublic.Length == 33) return montgomeryPublic;

            return Concat(new byte[] { 0x05 }, montgomeryPublic);
        }

        private static byte[] Slice(byte[] source, int at, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, at, slice, 0, count);

            return slice;
        }

        private static byte[] Concat(byte[] left, byte[] right)
        {
            var joined = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, joined, 0, left.Length);
            Buffer.BlockCopy(right, 0, joined, left.Length, right.Length);

            return joined;
        }
    }

    /// <summary>
    /// A sender key: the chain one member of a group encrypts with, and everyone else
    /// decrypts with.
    ///
    /// It is a symmetric ratchet and nothing more (`libsignal`'s `SenderKeyState`,
    /// `SenderChainKey`, `SenderMessageKey`, read from the reference):
    ///
    ///     messageKeySeed = HMAC-SHA256(chainKey, 0x01)
    ///     nextChainKey   = HMAC-SHA256(chainKey, 0x02)
    ///     hkdf           = HKDF(messageKeySeed, salt = 0x00 * 32, info = "WhisperGroup", 48 bytes)
    ///     iv             = hkdf[0..16]
    ///     cipherKey      = hkdf[16..48]
    ///     ciphertext     = AES-256-CBC(cipherKey, iv, plaintext)
    ///
    /// The iv is **the first sixteen bytes of the expansion itself** and not of the extract,
    /// the cipher key is the other thirty-two, and a guess in either direction produces a
    /// chain that talks to itself and to nobody else - which is why
    /// `ProtocolTests/SenderKeyTests.cs` opens a ciphertext the reference produced.
    ///
    /// The record holds one state, not the reference's five: WhatsApp rotates a sender key
    /// by starting a new one (`keyId` changes), and a client that keeps only the current
    /// state treats the old one's messages as unreadable - which is what the reference does
    /// too after five rotations.
    /// </summary>
    public sealed class SenderKeyRecord
    {
        /// <summary>Where the group this key is for is remembered, for the guard below.</summary>
        public string GroupId;

        /// <summary>`senderKeyId`: WhatsApp's name for this chain, and what a message names.</summary>
        public uint KeyId;

        /// <summary>How many messages have been sent off this chain.</summary>
        public uint Iteration;

        /// <summary>The chain key itself.</summary>
        public byte[] ChainKey;

        /// <summary>The signing public key as the distribution carries it: 33 bytes, `0x05` first.</summary>
        public byte[] SigningPublic;

        /// <summary>The signing private key. Null for a record that only receives.</summary>
        private byte[] _signingPrivate;

        /// <summary>
        /// The message keys of iterations that were skipped: a message can arrive out of
        /// order, and without these it would be undecryptable rather than late.
        /// </summary>
        private readonly List<SkippedKey> _skipped = new List<SkippedKey>();

        private struct SkippedKey
        {
            public uint Iteration;
            public byte[] Seed;
        }

        /// <summary>How many sender-key records this client keeps, and how many skipped keys each.</summary>
        public const int MaxSkippedKeys = 2000;

        /// <summary>True when this record has a chain at all.</summary>
        public bool HasKey
        {
            get { return ChainKey != null && SigningPublic != null; }
        }

        /// <summary>
        /// A brand new chain for a group this device is about to write in: a 32-byte chain
        /// key, a random `keyId` (the reference draws it below 2^31, `keyhelper.js`) and a
        /// signing key pair. The first message off it is iteration 0.
        /// </summary>
        public static SenderKeyRecord Create(string groupId, IRandomSource random)
        {
            if (string.IsNullOrEmpty(groupId)) throw new ProtocolException("a sender key belongs to a group");
            if (random == null) throw new ProtocolException("a sender key needs randomness");

            var record = new SenderKeyRecord();
            record.GroupId = groupId;
            // `generateSenderKeyId`: below 2^31 (`keyhelper.js`, `randomInt(2147483647)`),
            // which is what the field is signed for on the other side.
            byte[] id = random.Bytes(4);
            record.KeyId = (((uint)id[0] << 24) | ((uint)id[1] << 16) | ((uint)id[2] << 8) | id[3]) & 0x7fffffffu;
            record.Iteration = 0;
            record.ChainKey = random.Bytes(32);

            // Clamped, like every other X25519 private key this client draws: XEdDSA signs
            // with the bytes as stored, so an unclamped scalar produces a signature that
            // verifies nowhere (and `Create` would otherwise be the only place doing it).
            byte[] signingPrivate = Credentials.Clamp(random.Bytes(32));
            record._signingPrivate = signingPrivate;
            record.SigningPublic = SenderKeyMessage.WireKey(Curve25519.PublicKey(signingPrivate));

            return record;
        }

        /// <summary>
        /// The chain in a `senderKeyDistributionMessage` from another member: the `keyId`,
        /// the iteration the chain is at, the chain key, and the signing public key they
        /// will sign with.
        ///
        /// The group in the argument is remembered, and every later call that uses this
        /// record has to name the same one. The scheme itself does not carry the group id -
        /// nothing in the key schedule includes it - so a record used with the wrong group
        /// would *work*, and encrypt to the wrong people. That is why the check is here.
        /// </summary>
        public static SenderKeyRecord StartAsReceiver(string groupId, byte[] distribution)
        {
            if (string.IsNullOrEmpty(groupId)) throw new ProtocolException("a sender key belongs to a group");

            SenderKeyDistribution parsed = SenderKeyDistribution.Parse(distribution);

            var record = new SenderKeyRecord();
            record.GroupId = groupId;
            record.KeyId = parsed.KeyId;
            record.Iteration = parsed.Iteration;
            record.ChainKey = parsed.ChainKey;
            record.SigningPublic = parsed.SigningPublic;

            return record;
        }

        /// <summary>
        /// The bytes another member needs to read what this device writes: the version byte,
        /// the `keyId`, the iteration **the chain is at** and the signing public key.
        /// </summary>
        public byte[] Distribution(string groupId)
        {
            CheckGroup(groupId);

            if (!HasKey) throw new ProtocolException("there is no sender key to hand out for " + groupId);

            return SenderKeyDistribution.Build(KeyId, Iteration, ChainKey, SigningPublic);
        }

        /// <summary>
        /// The message as it goes on the wire: the key for this iteration, then the same
        /// chain advanced. The iteration written in the message is the one the key decrypts
        /// at, which is why the chain is stepped *after* the key is drawn.
        /// </summary>
        public byte[] Encrypt(string groupId, byte[] plaintext)
        {
            CheckGroup(groupId);

            if (!HasKey) throw new ProtocolException("there is no sender key for " + groupId);
            if (_signingPrivate == null)
            {
                throw new ProtocolException("this sender key can only read: it arrived from another member "
                    + "of " + groupId);
            }
            if (plaintext == null || plaintext.Length == 0) throw new ProtocolException("there is nothing to encrypt");

            // The key of *this* iteration, which is the message key seed and not the chain
            // key itself: `SenderMessageKey` takes `getDerivative(MESSAGE_KEY_SEED, chainKey)`
            // (`sender-chain-key.js`), and using the chain key directly produced a chain that
            // read its own writes and nobody else's.
            byte[] iv;
            byte[] cipherKey;
            MessageKey(ChainKeyFor(ChainKey, MessageSeed), out iv, out cipherKey);

            byte[] ciphertext = Aes.CbcEncrypt(cipherKey, iv, plaintext);
            SenderKeyMessage message = SenderKeyMessage.Create(KeyId, Iteration, ciphertext, _signingPrivate);

            ChainKey = ChainKeyFor(ChainKey, ChainKeySeed);
            Iteration = Iteration + 1;

            return message.Serialized;
        }

        /// <summary>
        /// One message from the member this record belongs to.
        ///
        /// The order is the reference's and it is the whole of the security here: the
        /// signature first, then the key for that iteration, then the decrypt. A message
        /// whose iteration is behind the chain needs the key kept when it was skipped; one
        /// too far ahead is refused rather than stepping the chain thousands of times on a
        /// phone (`group_cipher.js`, `getSenderKey`: 2000 is the reference's ceiling too).
        /// </summary>
        public byte[] Decrypt(string groupId, SenderKeyMessage message)
        {
            CheckGroup(groupId);

            if (!HasKey) throw new ProtocolException("there is no sender key for " + groupId);
            if (message == null) throw new ProtocolException("there is no message to read");
            if (message.KeyId != KeyId)
            {
                throw new ProtocolException("that message is for sender key " + message.KeyId
                    + " and this record holds " + KeyId);
            }

            if (!message.Verifies(SigningPublic))
            {
                throw new ProtocolException("the group message does not carry the signature of the key it names: "
                    + groupId + ", iteration " + message.Iteration);
            }

            byte[] seed = SeedFor(message.Iteration);

            byte[] iv;
            byte[] cipherKey;
            MessageKey(seed, out iv, out cipherKey);

            return Aes.CbcDecrypt(cipherKey, iv, message.Ciphertext);
        }

        /// <summary>
        /// Whether this member's message carries an iteration this record has already read -
        /// a duplicate, and not a failure.
        ///
        /// Why it exists: `SeedFor` refuses a message whose key was spent and dropped with a
        /// `ProtocolException` ("iteration N was already read and its key is gone"), the
        /// connection reports it as a failure and asks the group to send the message again,
        /// and the group does - for ever, because there is no key left to open it with. The
        /// run of 24 October 2026 asked for the same three messages on every connection.
        ///
        /// The condition is the one `SeedFor` throws on, asked before it: the iteration is
        /// behind the chain and its key was not kept for a skipped message.
        /// </summary>
        public bool AlreadyRead(SenderKeyMessage message)
        {
            if (message == null || !HasKey) return false;
            if (message.Iteration >= Iteration) return false;

            for (int i = 0; i < _skipped.Count; i++)
            {
                if (_skipped[i].Iteration == message.Iteration) return false;
            }

            return true;
        }

        /// <summary>
        /// The chain key a given iteration's message key comes off, stepping the chain as
        /// far as is needed and keeping the keys of the iterations it steps over.
        /// </summary>
        private byte[] SeedFor(uint iteration)
        {
            if (iteration + MaxSkippedKeys < Iteration)
            {
                throw new ProtocolException("iteration " + iteration + " is more than "
                    + MaxSkippedKeys + " messages behind this chain (" + Iteration + ")");
            }

            if (iteration < Iteration)
            {
                for (int i = 0; i < _skipped.Count; i++)
                {
                    if (_skipped[i].Iteration != iteration) continue;

                    byte[] kept = _skipped[i].Seed;
                    _skipped.RemoveAt(i);

                    return kept;
                }

                throw new ProtocolException("iteration " + iteration + " was already read and its key is gone");
            }

            if (iteration - Iteration > MaxSkippedKeys)
            {
                throw new ProtocolException("iteration " + iteration + " is more than "
                    + MaxSkippedKeys + " messages ahead of this chain (" + Iteration + ")");
            }

            while (Iteration < iteration)
            {
                Keep(Iteration, ChainKeyFor(ChainKey, MessageSeed));
                ChainKey = ChainKeyFor(ChainKey, ChainKeySeed);
                Iteration = Iteration + 1;
            }

            byte[] seed = ChainKeyFor(ChainKey, MessageSeed);
            ChainKey = ChainKeyFor(ChainKey, ChainKeySeed);
            Iteration = Iteration + 1;

            return seed;
        }

        private void Keep(uint iteration, byte[] seed)
        {
            for (int i = 0; i < _skipped.Count; i++)
            {
                if (_skipped[i].Iteration == iteration) return;
            }

            var key = new SkippedKey();
            key.Iteration = iteration;
            key.Seed = seed;
            _skipped.Add(key);

            if (_skipped.Count > MaxSkippedKeys) _skipped.RemoveAt(0);
        }

        /// <summary>
        /// The iv and the AES key of a message key seed.
        ///
        /// `HKDF(seed, salt = 32 zero bytes, info = "WhisperGroup")` and then the first
        /// **48** bytes of it: the iv is the first 16, the cipher key the remaining 32
        /// (`sender-message-key.js`). One `Expand` of 48 bytes rather than the reference's
        /// two chunks, because the second chunk of an RFC 5869 expansion is the continuation
        /// of the first (`T(2) = HMAC(PRK, T(1) || info || 0x02)`), so the first 48 bytes are
        /// the same bytes either way. `TheIvAndCipherKeyAreTheReferenceOnes` is what says so.
        /// </summary>
        public static void MessageKey(byte[] seed, out byte[] iv, out byte[] cipherKey)
        {
            if (seed == null) throw new ProtocolException("a message key comes from a chain key");

            byte[] prk = Hmac.Compute(new byte[32], seed);
            byte[] expanded = Hkdf.Expand(prk, System.Text.Encoding.UTF8.GetBytes("WhisperGroup"), 48);

            iv = Slice(expanded, 0, 16);
            cipherKey = Slice(expanded, 16, 32);
        }

        /// <summary>One step of a chain: `HMAC(chainKey, seed)`, with the reference's two seeds.</summary>
        public static byte[] ChainKeyFor(byte[] chainKey, byte[] seed)
        {
            return Hmac.Compute(chainKey, seed);
        }

        /// <summary>The seed the message key of an iteration is drawn with.</summary>
        private static readonly byte[] MessageSeed = new byte[] { 0x01 };

        /// <summary>The seed the next chain key is drawn with.</summary>
        private static readonly byte[] ChainKeySeed = new byte[] { 0x02 };

        /// <summary>
        /// THIS GROUP is not that group.
        ///
        /// A sender key record knows which group it was made for, and refuses to be used
        /// with any other. Nothing in the scheme would catch it - the key schedule does not
        /// mention the group - so a record that drifted would encrypt happily, to whoever
        /// this group's members are, and the mistake would show up as "my message never
        /// arrived" on one side and nothing at all on the other.
        /// </summary>
        private void CheckGroup(string groupId)
        {
            if (string.IsNullOrEmpty(groupId))
            {
                throw new ProtocolException("a sender key is used with the group it belongs to, and none was named");
            }

            if (!string.IsNullOrEmpty(GroupId) && GroupId != groupId)
            {
                throw new ProtocolException("this sender key is for group " + GroupId
                    + " and it was asked for " + groupId);
            }
        }

        /// <summary>
        /// The record as bytes, for the store that keeps it between runs. The framing is
        /// ours (`SessionRecord.Encode` explains why): a version byte, the length, the body,
        /// and the SHA-256 of the body.
        /// </summary>
        public byte[] Encode()
        {
            var body = new ProtoWriter()
                .Text(1, GroupId)
                .Varint(2, KeyId)
                .Varint(3, Iteration)
                .Bytes(4, ChainKey)
                .Bytes(5, SigningPublic)
                .Bytes(6, _signingPrivate);

            for (int i = 0; i < _skipped.Count; i++)
            {
                body.Bytes(7, new ProtoWriter()
                    .Varint(1, _skipped[i].Iteration)
                    .Bytes(2, _skipped[i].Seed)
                    .ToArray());
            }

            byte[] written = body.ToArray();
            byte[] digest = Sha256.Hash(written);

            var framed = new byte[1 + 4 + written.Length + digest.Length];
            framed[0] = FileVersion;
            framed[1] = (byte)(written.Length >> 24);
            framed[2] = (byte)(written.Length >> 16);
            framed[3] = (byte)(written.Length >> 8);
            framed[4] = (byte)written.Length;

            Buffer.BlockCopy(written, 0, framed, 5, written.Length);
            Buffer.BlockCopy(digest, 0, framed, 5 + written.Length, digest.Length);

            return framed;
        }

        /// <summary>The framing's version, and what `Decode` refuses anything else with.</summary>
        public const byte FileVersion = 1;

        /// <summary>
        /// The record <see cref="Encode"/> wrote, or null.
        ///
        /// Null for anything that is not whole, as `SessionRecord.Decode` is: what reaches
        /// this is a file, and a damaged record is not a key - the caller starts a new chain
        /// rather than decrypting with half of one.
        /// </summary>
        public static SenderKeyRecord Decode(byte[] bytes)
        {
            try
            {
                return Read(bytes);
            }
            catch (ProtocolException)
            {
                return null;
            }
        }

        private static SenderKeyRecord Read(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 6) throw new ProtocolException("there is no record to read");
            if (bytes[0] != FileVersion) throw new ProtocolException("this record is version " + bytes[0]);

            int length = (bytes[1] << 24) | (bytes[2] << 16) | (bytes[3] << 8) | bytes[4];
            if (length <= 0 || bytes.Length != 5 + length + 32) throw new ProtocolException("the record is not whole");

            byte[] body = Slice(bytes, 5, length);
            if (!Hmac.FixedTimeEquals(Slice(bytes, 5 + length, 32), Sha256.Hash(body)))
            {
                throw new ProtocolException("the record does not match its own digest");
            }

            var record = new SenderKeyRecord();
            var reader = new ProtoReader(body);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1: record.GroupId = Utf8(reader.ReadBytes()); break;
                    case 2: record.KeyId = (uint)reader.ReadVarint(); break;
                    case 3: record.Iteration = (uint)reader.ReadVarint(); break;
                    case 4: record.ChainKey = reader.ReadBytes(); break;
                    case 5: record.SigningPublic = reader.ReadBytes(); break;
                    case 6: record._signingPrivate = reader.ReadBytes(); break;
                    case 7: record.ReadSkipped(reader.ReadBytes()); break;
                    default: reader.SkipField(field.WireType); break;
                }
            }

            if (!record.HasKey) throw new ProtocolException("the record carries no chain key");

            return record;
        }

        private void ReadSkipped(byte[] encoded)
        {
            var key = new SkippedKey();
            var reader = new ProtoReader(encoded);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1) key.Iteration = (uint)reader.ReadVarint();
                else if (field.Number == 2) key.Seed = reader.ReadBytes();
                else reader.SkipField(field.WireType);
            }

            if (key.Seed != null) _skipped.Add(key);
        }

        private static string Utf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        private static byte[] Slice(byte[] source, int at, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, at, slice, 0, count);

            return slice;
        }
    }

    /// <summary>
    /// The `SenderKeyDistributionMessage` protobuf, which is what one member of a group
    /// hands the others so they can read what they write:
    ///
    ///     serialized = [0x33] || protobuf{ id=1, iteration=2, chainKey=3, signingKey=4 }
    ///
    /// The field numbers were printed from the reference's own encoder
    /// (`ProtocolTests/Vectors/SenderKeyFixture.cs` holds the bytes and the command), and
    /// the `signingKey` is the **33-byte** form: libsignal stores 32 and prepends the
    /// `0x05` type byte on the way out (`sender-key-state.js`, `getSigningKeyPublic`).
    /// </summary>
    public sealed class SenderKeyDistribution
    {
        public uint KeyId;
        public uint Iteration;
        public byte[] ChainKey;
        public byte[] SigningPublic;

        public static SenderKeyDistribution Parse(byte[] distribution)
        {
            if (distribution == null || distribution.Length < 2)
            {
                throw new ProtocolException("a sender key distribution message is a version byte and a protobuf");
            }

            if (distribution[0] != SenderKeyMessage.VersionByte)
            {
                throw new ProtocolException("this sender key distribution message is version "
                    + (distribution[0] >> 4) + " and this client speaks " + SenderKeyMessage.CurrentVersion);
            }

            var parsed = new SenderKeyDistribution();
            var reader = new ProtoReader(Slice(distribution, 1, distribution.Length - 1));

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1: parsed.KeyId = (uint)reader.ReadVarint(); break;
                    case 2: parsed.Iteration = (uint)reader.ReadVarint(); break;
                    case 3: parsed.ChainKey = reader.ReadBytes(); break;
                    case 4: parsed.SigningPublic = reader.ReadBytes(); break;
                    default: reader.SkipField(field.WireType); break;
                }
            }

            if (parsed.ChainKey == null || parsed.ChainKey.Length != 32)
            {
                throw new ProtocolException("a sender key chain key is 32 bytes");
            }

            if (SenderKeyMessage.RawKey(parsed.SigningPublic) == null)
            {
                throw new ProtocolException("a sender key distribution message carries a 32 or 33 byte signing key, not "
                    + (parsed.SigningPublic == null ? "nothing" : parsed.SigningPublic.Length.ToString()));
            }

            return parsed;
        }

        public static byte[] Build(uint keyId, uint iteration, byte[] chainKey, byte[] signingPublic)
        {
            if (chainKey == null || chainKey.Length != 32) throw new ProtocolException("a sender key chain key is 32 bytes");

            if (SenderKeyMessage.RawKey(signingPublic) == null)
            {
                throw new ProtocolException("a sender key signing key is 32 bytes");
            }

            byte[] publicKey = SenderKeyMessage.WireKey(signingPublic);

            byte[] body = new ProtoWriter()
                .Varint(1, keyId)
                .Varint(2, iteration)
                .Bytes(3, chainKey)
                .Bytes(4, publicKey)
                .ToArray();

            var framed = new byte[body.Length + 1];
            framed[0] = SenderKeyMessage.VersionByte;
            Buffer.BlockCopy(body, 0, framed, 1, body.Length);

            return framed;
        }

        private static byte[] Slice(byte[] source, int at, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, at, slice, 0, count);

            return slice;
        }
    }
}
