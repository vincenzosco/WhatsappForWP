using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// One session with one device: the state a message is encrypted and decrypted with,
    /// and the state the ratchet moves.
    ///
    /// It is written as the reference's own session is, which is not the shape the
    /// specification describes, and the differences are the ones that bite:
    ///
    /// - **A chain belongs to a ratchet key, not to a direction.** The session keeps the
    ///   chains it has opened, keyed by the peer's ratchet key they hang off, because a
    ///   message can arrive from an older key - a retry, a reordered pair - and its chain
    ///   has to still be there. The reference does the same thing with a dictionary keyed
    ///   by the key's text form; here it is a list, because a session has a handful of
    ///   chains and a list of five beats a dictionary on a phone.
    /// - **The first message of a chain carries the length of the previous one**
    ///   (`previousCounter`), which is how the receiver knows how many keys it may have to
    ///   keep for messages that have not arrived yet.
    /// - **A step generates a *new* key pair before it opens the sending chain**, so the
    ///   receiving chain of the peer and ours cannot be the same chain: the DH that opens
    ///   the sending chain is with a key the peer has never seen.
    ///
    /// What is deliberately *not* here: the pre-key store, the identity trust decisions
    /// and the persistence. A record is the session's arithmetic; where it is written and
    /// who it belongs to is the caller's problem.
    /// </summary>
    public sealed class SessionRecord
    {
        /// <summary>A chain of message keys: the key it hangs off, and where it has got to.</summary>
        private sealed class Chain
        {
            /// <summary>The peer's ratchet key this chain's messages arrive with, 33 bytes. Null for a sending chain.</summary>
            public byte[] RatchetKey;

            public byte[] ChainKey;

            /// <summary>-1 before the first message, then the index of the last key produced.</summary>
            public int Counter;

            /// <summary>Keys passed over and kept, by index, for messages that may still arrive.</summary>
            public readonly Dictionary<int, byte[]> Skipped = new Dictionary<int, byte[]>();
        }

        private readonly IRandomSource _random;

        private byte[] _ourIdentityPrivate;
        private byte[] _ourIdentityPublic;
        private byte[] _theirIdentityPublic;

        private byte[] _rootKey;
        private byte[] _ourRatchetPrivate;
        private byte[] _ourRatchetPublic;
        private byte[] _theirRatchetPublic;
        private uint _previousCounter;

        private Chain _sending;
        private readonly List<Chain> _chains = new List<Chain>();

        /// <summary>The pre-key the first messages carry until the peer answers; cleared once they do.</summary>
        private uint _theirPreKeyId;
        private bool _hasTheirPreKey;
        private uint _theirSignedPreKeyId;
        private bool _pendingPreKey;
        private byte[] _ourBaseKeyPublic;

        private uint _ourRegistrationId;
        private uint _theirRegistrationId;

        private SessionRecord(IRandomSource random)
        {
            // Required, not defaulted. A session draws ratchet keys, and a fallback here
            // would be a key nobody chose: on the phone the source is WinRT's generator
            // and in a test it is the fixture's fixed bytes, and both are the caller's
            // decision rather than this class's.
            if (random == null) throw new ProtocolException("a session needs a source for its ratchet keys");

            _random = random;
        }

        /// <summary>True once the session has keys: before that it is a placeholder and cannot encrypt.</summary>
        public bool HasSession
        {
            get { return _rootKey != null; }
        }

        /// <summary>The peer's identity key as this session knows it, 33 bytes.</summary>
        public byte[] TheirIdentityPublic
        {
            get { return _theirIdentityPublic; }
        }

        /// <summary>
        /// Whether the messages this session sends are pre-key messages yet.
        ///
        /// It is what the stanza's `enc` element has to declare: `type="pkmsg"` while the
        /// peer has not answered, `type="msg"` afterwards. The caller reads it and writes
        /// the attribute, and nobody has to guess the shape from the bytes - which is the
        /// reference's own state (`session.pendingPreKey !== undefined`) under the name a
        /// caller can act on.
        /// </summary>
        public bool PreKeyPending
        {
            get { return _pendingPreKey; }
        }

        /// <summary>Our identity key, the form the wire carries, 33 bytes.</summary>
        public byte[] OurIdentityPublic
        {
            get { return _ourIdentityPublic; }
        }

        /// <summary>
        /// Starts a session from a bundle, as the side that speaks first.
        ///
        /// The three private keys are passed in rather than drawn here because on a phone
        /// the two that matter are the device's own (they come out of the credential
        /// store) and only the ratchet key is fresh; the desktop fixture passes all three
        /// so that the reference's own bytes can be reproduced.
        ///
        /// Our own registration id is passed in because the first messages *tell* the peer
        /// what it is - the wrapper names the device the messages come from - and a session
        /// that drew it from the bundle would send the peer its own id back.
        /// </summary>
        public static SessionRecord StartAsInitiator(
            byte[] ourIdentityPrivate,
            byte[] ourEphemeralPrivate,
            byte[] ourRatchetPrivate,
            uint ourRegistrationId,
            PreKeyBundle bundle,
            IRandomSource random)
        {
            if (ourIdentityPrivate == null || ourIdentityPrivate.Length != 32)
            {
                throw new ProtocolException("a session needs our identity private key");
            }

            if (ourEphemeralPrivate == null || ourEphemeralPrivate.Length != 32)
            {
                throw new ProtocolException("a session needs an ephemeral private key");
            }

            if (ourRatchetPrivate == null || ourRatchetPrivate.Length != 32)
            {
                throw new ProtocolException("a session needs a ratchet private key");
            }

            if (bundle == null) throw new ProtocolException("a session needs a bundle to start from");
            bundle.Check();

            var session = new SessionRecord(random);

            session._ourIdentityPrivate = ourIdentityPrivate;
            session._ourIdentityPublic = X3dh.TypedPublicKey(ourIdentityPrivate);
            session._theirIdentityPublic = X3dh.Typed(bundle.IdentityKey);
            session._ourRegistrationId = ourRegistrationId;
            session._theirRegistrationId = bundle.RegistrationId;
            session._ourBaseKeyPublic = X3dh.TypedPublicKey(ourEphemeralPrivate);
            session._ourRatchetPrivate = ourRatchetPrivate;
            session._ourRatchetPublic = X3dh.TypedPublicKey(ourRatchetPrivate);

            session._rootKey = X3dh.AliceSecret(
                ourIdentityPrivate,
                ourEphemeralPrivate,
                bundle.IdentityKey,
                bundle.SignedPreKeyPublic,
                bundle.HasPreKey ? bundle.PreKeyPublic : null);

            // The reference starts the remote ratchet at the signed pre-key, because that
            // is the key the bundle published and the only one the sender knows of.
            session._theirRatchetPublic = X3dh.Typed(bundle.SignedPreKeyPublic);
            session.StepSending();

            session._theirPreKeyId = bundle.PreKeyId;
            session._hasTheirPreKey = bundle.HasPreKey;
            session._theirSignedPreKeyId = bundle.SignedPreKeyId;
            session._pendingPreKey = true;

            return session;
        }

        /// <summary>
        /// The receiver's half of a session that an incoming pre-key message opens.
        ///
        /// The ratchet key the receiver starts with is his *signed pre-key* pair, which is
        /// the key the sender's agreement already used - and the new pair his first step
        /// generates is what his replies are sent under. `ourOneTimePreKeyPrivate` is the
        /// pair the server handed out, when the message names one.
        /// </summary>
        public static SessionRecord StartAsResponder(
            byte[] ourIdentityPrivate,
            byte[] ourSignedPreKeyPrivate,
            byte[] ourOneTimePreKeyPrivate,
            uint ourRegistrationId,
            PreKeySignalMessage message,
            IRandomSource random)
        {
            if (ourIdentityPrivate == null || ourIdentityPrivate.Length != 32)
            {
                throw new ProtocolException("a session needs our identity private key");
            }

            if (ourSignedPreKeyPrivate == null || ourSignedPreKeyPrivate.Length != 32)
            {
                throw new ProtocolException("a session opened by a pre-key message needs the signed pre-key it names");
            }

            if (message == null) throw new ProtocolException("a responder session starts from the message that opened it");

            var session = new SessionRecord(random);

            session._ourIdentityPrivate = ourIdentityPrivate;
            session._ourIdentityPublic = X3dh.TypedPublicKey(ourIdentityPrivate);
            session._theirIdentityPublic = X3dh.Typed(message.IdentityKey);
            session._ourRegistrationId = ourRegistrationId;
            session._theirRegistrationId = message.RegistrationId;
            session._ourRatchetPrivate = ourSignedPreKeyPrivate;
            session._ourRatchetPublic = X3dh.TypedPublicKey(ourSignedPreKeyPrivate);

            session._rootKey = X3dh.BobSecret(
                ourIdentityPrivate,
                ourSignedPreKeyPrivate,
                message.HasPreKey ? ourOneTimePreKeyPrivate : null,
                message.IdentityKey,
                message.BaseKey);

            // His ratchet starts on the sender's ephemeral key: the reference makes
            // `lastRemoteEphemeralKey` the base key the message carries.
            session._theirRatchetPublic = X3dh.Typed(message.BaseKey);

            return session;
        }

        /// <summary>
        /// Encrypts one message.
        ///
        /// While the peer has not answered, the message is a pre-key message: it carries
        /// the ephemeral key of our half of the agreement, our identity key and which
        /// pre-keys we used, because that is all the receiver needs to build the session
        /// it does not have yet. After the first reply, one plain message is enough.
        /// </summary>
        public byte[] Encrypt(byte[] plaintext)
        {
            if (!HasSession) throw new ProtocolException("there is no session to encrypt with");
            if (_sending == null) StepSending();

            _sending.Counter++;

            byte[] messageKey = Keys.MessageKey(_sending.ChainKey);
            _sending.ChainKey = Keys.NextChainKey(_sending.ChainKey);

            MessageKeys keys = Keys.MessageKeys(messageKey);
            byte[] ciphertext = Cipher.Encrypt(keys.CipherKey, keys.Iv, plaintext);

            // The message itself, sealed: `version || protobuf || mac`. It is a whole
            // message at this point - the MAC included - and a pre-key message embeds it
            // exactly like this, which is why the bytes the sender writes and the bytes
            // the receiver verifies are the same bytes and not two framings of the same
            // fields.
            byte[] sealedMessage = SignalEnvelope.Seal(
                SignalMessage.Encode(_ourRatchetPublic, (uint)_sending.Counter, _previousCounter, ciphertext),
                keys.MacKey, _ourIdentityPublic, _theirIdentityPublic);

            if (!_pendingPreKey) return sealedMessage;

            // The wrapper carries the sealed message whole and is not sealed itself: it
            // names the pre-keys it used and the identity it comes from, and the message
            // inside was authenticated with keys those very fields produced. There is
            // nothing left for a second MAC to protect - see `SignalEnvelope.Wrap`.
            byte[] outer = PreKeySignalMessage.Encode(
                _theirPreKeyId,
                _hasTheirPreKey,
                _ourBaseKeyPublic,
                _ourIdentityPublic,
                sealedMessage,
                _ourRegistrationId,
                _theirSignedPreKeyId);

            return SignalEnvelope.Wrap(outer);
        }

        /// <summary>
        /// Opens a message that may or may not be the one that starts the session. The
        /// caller knows which from the stanza (`enc type="pkmsg"` says pre-key), and the
        /// two paths differ in the first thing they do: a pre-key message runs the
        /// agreement, a plain one steps the ratchet it names.
        /// </summary>
        public byte[] Decrypt(byte[] wire, bool preKey)
        {
            if (!HasSession) throw new ProtocolException("there is no session to decrypt with");

            byte[] sealedMessage = wire;
            SignalMessage message;

            if (preKey)
            {
                PreKeySignalMessage envelope = PreKeySignalMessage.Decode(SignalEnvelope.Unwrap(wire));
                if (!Same(envelope.IdentityKey, _theirIdentityPublic))
                {
                    throw new ProtocolException("the message's identity key is not the session's peer");
                }

                // The message inside is the sealed one: the wrapper's fields are what the
                // agreement that produced the key for this message was run over, and the
                // MAC to check is its own.
                sealedMessage = envelope.Message;

                // The message inside is *sealed*: version byte in front, MAC behind, and
                // both are peeled off here the way they are for a plain message.
                message = SignalMessage.Decode(Shift(sealedMessage));
            }
            else
            {
                message = SignalMessage.Decode(Shift(wire));
            }

            if (!Same(message.EphemeralKey, _theirRatchetPublic)) StepReceiving(message.EphemeralKey, message.PreviousCounter);

            byte[] messageKey = TakeMessageKey(message.Counter);
            MessageKeys keys = Keys.MessageKeys(messageKey);

            // The MAC is checked before anything is decrypted. It covers the two
            // identities, the version byte and the protobuf, and for a pre-key message it
            // also covers, indirectly, everything the wrapper claimed: the key it is
            // checked with came out of the agreement those claims drove. `Open` throws
            // when it does not verify, so reaching the next line means the message is the
            // one the sender signed, field for field.
            SignalEnvelope.Open(sealedMessage, keys.MacKey, _theirIdentityPublic, _ourIdentityPublic);

            byte[] plaintext = Cipher.Decrypt(keys.CipherKey, keys.Iv, message.Ciphertext);

            // The peer has answered with a message it accepted: the session is established
            // on both sides, and the next message needs no pre-key wrapper.
            _pendingPreKey = false;

            return plaintext;
        }

        /// <summary>
        /// A ratchet step: everything the peer's new key changes.
        ///
        /// The order is the reference's and each half depends on the previous one. The old
        /// receiving chain is closed (its leftover keys kept, in case a message from it is
        /// still in flight), the receiving chain for the new key is derived, the old
        /// sending chain is archived with its length recorded - that number is what the
        /// peer is told, so it knows how many keys to keep - and only then is a fresh key
        /// pair generated and the sending chain derived from *it*.
        /// </summary>
        private void StepReceiving(byte[] theirNewKey, uint previousCounter)
        {
            Chain old = Find(theirNewKey);
            if (old != null) return;

            Chain previous = Find(_theirRatchetPublic);
            if (previous != null) SkipTo(previous, (int)previousCounter);

            RootAndChain opening = Keys.Root(
                Curve25519.SharedSecret(_ourRatchetPrivate, X3dh.Bare(theirNewKey)), _rootKey);
            _rootKey = opening.RootKey;

            var receiving = new Chain();
            receiving.RatchetKey = theirNewKey;
            receiving.ChainKey = opening.ChainKey;
            receiving.Counter = -1;
            _chains.Add(receiving);

            if (_sending != null)
            {
                _previousCounter = (uint)_sending.Counter;
                _sending = null;
            }

            byte[] newPrivate = Key(_random);
            _ourRatchetPrivate = newPrivate;
            _ourRatchetPublic = X3dh.TypedPublicKey(newPrivate);
            _theirRatchetPublic = theirNewKey;

            StepSending();
        }

        /// <summary>Opens our sending chain off the ratchet's current root and key pair.</summary>
        private void StepSending()
        {
            RootAndChain opening = Keys.Root(
                Curve25519.SharedSecret(_ourRatchetPrivate, X3dh.Bare(_theirRatchetPublic)), _rootKey);
            _rootKey = opening.RootKey;

            var sending = new Chain();
            sending.ChainKey = opening.ChainKey;
            sending.Counter = -1;

            _sending = sending;
        }

        /// <summary>
        /// The message key at an index, advancing the chain to reach it and keeping every
        /// key it passes.
        ///
        /// Two cases that look similar and are not:
        ///
        /// - **A message that arrives before a gap it can fill** is answered from a kept
        ///   key, and the gap stays fillable: WhatsApp retries, and a phone that receives
        ///   3 before 2 must be able to show both.
        /// - **A message that arrives twice** is refused, with an error that says so. The
        ///   key it would need was used and deleted, which is the reference's behaviour and
        ///   the reason a duplicate is not a decryption: the same message twice is the
        ///   *app's* to drop, by its id, and it is dropped there (see `DataService`).
        /// </summary>
        private byte[] TakeMessageKey(uint counter)
        {
            Chain chain = Find(_theirRatchetPublic);
            if (chain == null) throw new ProtocolException("no chain for the message's ratchet key");

            int index = (int)counter;
            if (index < 0 || index > chain.Counter + 2000)
            {
                throw new ProtocolException("message " + index + " is more than a chain ahead");
            }

            byte[] kept;
            if (chain.Skipped.TryGetValue(index, out kept))
            {
                chain.Skipped.Remove(index);
                return kept;
            }

            if (index <= chain.Counter)
            {
                throw new ProtocolException("the message key for " + index + " was already used");
            }

            while (chain.Counter < index)
            {
                chain.Counter++;
                byte[] key = Keys.MessageKey(chain.ChainKey);
                chain.ChainKey = Keys.NextChainKey(chain.ChainKey);

                if (chain.Counter == index) return key;
                chain.Skipped[chain.Counter] = key;
            }

            throw new ProtocolException("the chain did not reach " + index);
        }

        /// <summary>Advances a closed chain to its end and keeps every key it still might need.</summary>
        private static void SkipTo(Chain chain, int counter)
        {
            while (chain.Counter < counter)
            {
                chain.Counter++;
                chain.Skipped[chain.Counter] = Keys.MessageKey(chain.ChainKey);
                chain.ChainKey = Keys.NextChainKey(chain.ChainKey);
            }
        }

        private Chain Find(byte[] ratchetKey)
        {
            if (ratchetKey == null) return null;

            for (int i = 0; i < _chains.Count; i++)
            {
                if (Same(_chains[i].RatchetKey, ratchetKey)) return _chains[i];
            }

            return null;
        }

        /// <summary>The protobuf behind the framing: `version || message || mac`.</summary>
        private static byte[] Shift(byte[] wire)
        {
            if (wire == null || wire.Length < 1 + SignalConstants.MacLength + 1)
            {
                throw new ProtocolException("a message is at least the version byte, one field and a MAC");
            }

            int length = wire.Length - 1 - SignalConstants.MacLength;
            var message = new byte[length];
            Buffer.BlockCopy(wire, 1, message, 0, length);
            return message;
        }

        private static byte[] Key(IRandomSource random)
        {
            byte[] key = random.Bytes(32);
            if (key == null || key.Length != 32) throw new ProtocolException("a ratchet key is 32 bytes");

            return key;
        }

        private static bool Same(byte[] left, byte[] right)
        {
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;

            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

    }
}
