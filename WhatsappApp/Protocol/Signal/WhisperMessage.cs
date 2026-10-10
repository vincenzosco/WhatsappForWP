using System;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// The inner message: which ratchet key it was encrypted for, where it sits in the
    /// chain, and the ciphertext.
    ///
    /// Field numbers are the reference's (`protos/WhisperTextProtocol.proto`:
    /// `ephemeralKey` 1, `counter` 2, `previousCounter` 3, `ciphertext` 4) and the fields
    /// are written in that order, which is what makes the bytes comparable with the
    /// fixture's own.
    /// </summary>
    public sealed class SignalMessage
    {
        /// <summary>The sender's ratchet public key, 33 bytes with its type byte.</summary>
        public byte[] EphemeralKey;

        /// <summary>The message's index in its chain.</summary>
        public uint Counter;

        /// <summary>The length of the previous sending chain, which is how the receiver knows how many keys it may have to skip.</summary>
        public uint PreviousCounter;

        public byte[] Ciphertext;

        public static byte[] Encode(byte[] ephemeralKey, uint counter, uint previousCounter, byte[] ciphertext)
        {
            var writer = new ProtoWriter();
            writer.Bytes(1, ephemeralKey);
            writer.Varint(2, counter);
            writer.Varint(3, previousCounter);
            writer.Bytes(4, ciphertext);

            return writer.ToArray();
        }

        /// <summary>
        /// The message as the wire carries it: `version || protobuf || mac`.
        ///
        /// It is a method on the message rather than on the session because a pre-key
        /// message embeds exactly this - a whole message, its own eight bytes of MAC
        /// included - inside a wrapper that carries no MAC of its own. The sealed message
        /// travels unchanged, and it is the only thing on the wire that is authenticated.
        /// </summary>
        public static byte[] Seal(byte[] ephemeralKey, uint counter, uint previousCounter,
            byte[] ciphertext, byte[] macKey, byte[] senderIdentity, byte[] receiverIdentity)
        {
            return SignalEnvelope.Seal(
                Encode(ephemeralKey, counter, previousCounter, ciphertext),
                macKey, senderIdentity, receiverIdentity);
        }

        public static SignalMessage Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) throw new ProtocolException("an empty message is not a message");

            var message = new SignalMessage();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1 && field.WireType == 2) message.EphemeralKey = reader.ReadBytes();
                else if (field.Number == 2 && field.WireType == 0) message.Counter = (uint)reader.ReadVarint();
                else if (field.Number == 3 && field.WireType == 0) message.PreviousCounter = (uint)reader.ReadVarint();
                else if (field.Number == 4 && field.WireType == 2) message.Ciphertext = reader.ReadBytes();
                else reader.SkipField(field.WireType);
            }

            if (message.EphemeralKey == null) throw new ProtocolException("the message names no ratchet key");
            if (message.Ciphertext == null) throw new ProtocolException("the message carries no ciphertext");

            return message;
        }
    }

    /// <summary>
    /// The message that starts a session: the inner one, wrapped in everything the
    /// receiver needs to run the agreement - whose pre-keys were used, and the ephemeral
    /// key of the sender's half of X3DH.
    ///
    /// The field numbers are the reference's and they are not in declaration order:
    /// `preKeyId` is 1, `baseKey` is 2, `identityKey` is 3, `message` is 4,
    /// `registrationId` is 5 and `signedPreKeyId` is 6. The wire order is numeric, which
    /// is why the registration id - the field declared first in the reference's `.proto` -
    /// comes second to last.
    /// </summary>
    public sealed class PreKeySignalMessage
    {
        /// <summary>The pre-key the sender used; absent when the sender had none left to use.</summary>
        public uint PreKeyId;

        /// <summary>False when the message was built without a one-time pre-key, which the receiver must then not look for.</summary>
        public bool HasPreKey;

        /// <summary>The sender's half of the agreement: the ephemeral key, 33 bytes with its type byte.</summary>
        public byte[] BaseKey;

        /// <summary>The sender's identity key, 33 bytes.</summary>
        public byte[] IdentityKey;

        /// <summary>The inner message's protobuf bytes.</summary>
        public byte[] Message;

        /// <summary>The sender's registration id, which is how the reference names the device a session is with.</summary>
        public uint RegistrationId;

        /// <summary>The signed pre-key the sender used.</summary>
        public uint SignedPreKeyId;

        /// <summary>
        /// The wrapper as it goes on the wire: the fields in their numeric order, with the
        /// two ids written only when they are not zero.
        ///
        /// A zero id is written as *absent*, not as a zero: the reference's protobuf
        /// library is proto3, where a scalar at its default is not serialized at all, and
        /// a wrapper that wrote `0` would be a byte longer than the same wrapper from
        /// WhatsApp. Pre-keys are numbered from one, so the only id that can be zero is a
        /// pair of ids the sender does not have.
        /// </summary>
        public static byte[] Encode(
            uint preKeyId, bool hasPreKey, byte[] baseKey, byte[] identityKey,
            byte[] message, uint registrationId, uint signedPreKeyId)
        {
            var writer = new ProtoWriter();
            if (hasPreKey) writer.Varint(1, preKeyId);
            writer.Bytes(2, baseKey);
            writer.Bytes(3, identityKey);
            writer.Bytes(4, message);
            if (registrationId != 0) writer.Varint(5, registrationId);
            if (signedPreKeyId != 0) writer.Varint(6, signedPreKeyId);

            return writer.ToArray();
        }

        public static PreKeySignalMessage Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) throw new ProtocolException("an empty message is not a message");

            var message = new PreKeySignalMessage();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1 && field.WireType == 0)
                {
                    message.PreKeyId = (uint)reader.ReadVarint();
                    message.HasPreKey = true;
                }
                else if (field.Number == 2 && field.WireType == 2) message.BaseKey = reader.ReadBytes();
                else if (field.Number == 3 && field.WireType == 2) message.IdentityKey = reader.ReadBytes();
                else if (field.Number == 4 && field.WireType == 2) message.Message = reader.ReadBytes();
                else if (field.Number == 5 && field.WireType == 0) message.RegistrationId = (uint)reader.ReadVarint();
                else if (field.Number == 6 && field.WireType == 0) message.SignedPreKeyId = (uint)reader.ReadVarint();
                else reader.SkipField(field.WireType);
            }

            if (message.BaseKey == null) throw new ProtocolException("the message names no base key");
            if (message.IdentityKey == null) throw new ProtocolException("the message carries no identity key");
            if (message.Message == null) throw new ProtocolException("the message carries no inner message");

            return message;
        }
    }

    /// <summary>
    /// The framing both shapes share: a version byte in front of the protobuf, and - for
    /// the message itself - eight bytes of MAC behind it.
    ///
    /// It is one class because the version byte is one rule, and because it is written
    /// *and* authenticated: a message whose first byte did not match the one inside the
    /// MAC would be a message with a mutable version.
    ///
    /// The MAC is the difference between the two shapes, and it is not symmetric. The
    /// message that carries the ciphertext is sealed; the wrapper a session's first
    /// messages travel in is not, because what a MAC would protect in it - which keys and
    /// which identity the message claims - is what the inner MAC is computed *from*: a
    /// wrapper tampered with produces different message keys, and the inner MAC then fails
    /// on its own.
    /// </summary>
    public static class SignalEnvelope
    {
        /// <summary>
        /// Frames the message a pre-key message carries: `version || protobuf`, and no MAC.
        ///
        /// The reference writes a bare version byte in front of an encoded
        /// `PreKeyWhisperMessage` and stops (`SessionCipher.js`, the `String.fromCharCode`
        /// the pre-key branch returns), and a wrapper that appended eight bytes would be
        /// eight bytes longer than every real one.
        /// </summary>
        public static byte[] Wrap(byte[] message)
        {
            var wire = new byte[message.Length + 1];
            wire[0] = SignalConstants.MessageVersionByte;
            Buffer.BlockCopy(message, 0, wire, 1, message.Length);

            return wire;
        }

        /// <summary>The protobuf behind a wrapper's version byte, which is the check <see cref="Open"/> makes minus the MAC.</summary>
        public static byte[] Unwrap(byte[] wire)
        {
            if (wire == null || wire.Length < 2) throw new ProtocolException("a pre-key message is a version byte and a protobuf");

            if (wire[0] != SignalConstants.MessageVersionByte)
            {
                throw new ProtocolException("a message of version 0x" + wire[0].ToString("x2")
                    + " is not the version this client speaks (0x" + SignalConstants.MessageVersionByte.ToString("x2") + ")");
            }

            var message = new byte[wire.Length - 1];
            Buffer.BlockCopy(wire, 1, message, 0, message.Length);

            return message;
        }
        /// <summary>Wraps an encoded message. Returns `version || message || mac`.</summary>
        public static byte[] Seal(byte[] message, byte[] macKey, byte[] senderIdentity, byte[] receiverIdentity)
        {
            byte[] mac = Cipher.Mac(macKey, senderIdentity, receiverIdentity, message);

            var wire = new byte[message.Length + 1 + SignalConstants.MacLength];
            wire[0] = SignalConstants.MessageVersionByte;
            Buffer.BlockCopy(message, 0, wire, 1, message.Length);
            Buffer.BlockCopy(mac, 0, wire, message.Length + 1, SignalConstants.MacLength);

            return wire;
        }

        /// <summary>
        /// Checks the version and the MAC and returns the protobuf bytes. Throws rather
        /// than returning null: every caller that gets this wrong is decrypting, and a
        /// null would be a decryption of nothing.
        /// </summary>
        public static byte[] Open(byte[] wire, byte[] macKey, byte[] senderIdentity, byte[] receiverIdentity)
        {
            if (wire == null || wire.Length < 1 + SignalConstants.MacLength + 1)
            {
                throw new ProtocolException("a message is at least the version byte, one field and a MAC");
            }

            if (wire[0] != SignalConstants.MessageVersionByte)
            {
                throw new ProtocolException("a message of version 0x" + wire[0].ToString("x2")
                    + " is not the version this client speaks (0x" + SignalConstants.MessageVersionByte.ToString("x2") + ")");
            }

            int messageLength = wire.Length - 1 - SignalConstants.MacLength;

            var message = new byte[messageLength];
            Buffer.BlockCopy(wire, 1, message, 0, messageLength);

            var mac = new byte[SignalConstants.MacLength];
            Buffer.BlockCopy(wire, 1 + messageLength, mac, 0, SignalConstants.MacLength);

            if (!Cipher.VerifyMac(macKey, senderIdentity, receiverIdentity, message, mac))
            {
                throw new ProtocolException("the message's MAC does not verify");
            }

            return message;
        }
    }
}
