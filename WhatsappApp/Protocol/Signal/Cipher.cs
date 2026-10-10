using System;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// What a Signal message is encrypted and authenticated with: AES-256-CBC, and an
    /// HMAC-SHA256 over the *framework* of the message rather than over its ciphertext.
    ///
    /// The MAC's input is the reference's, and it is the part of the wire format that is
    /// impossible to guess:
    ///
    ///   HMAC(macKey, senderIdentity || receiverIdentity || versionByte || protobuf)[0..8]
    ///
    /// with both identity keys 33 bytes - the type byte is part of them - and the whole
    /// thing truncated to eight bytes. Eight is short by the standards of a modern AEAD
    /// and it is not a mistake to fix: it is the protocol, and a verifier that compares
    /// thirty-two bytes refuses every message WhatsApp ever sends.
    ///
    /// The identities are in the MAC and not only in the keys because that is what binds
    /// a message to its sender: a message re-signed by somebody else fails here even
    /// though the ciphertext is untouched.
    /// </summary>
    public static class Cipher
    {
        /// <summary>The ciphertext: AES-256-CBC with PKCS#7, which is what the reference's WebCrypto call produces.</summary>
        public static byte[] Encrypt(byte[] cipherKey, byte[] iv, byte[] plaintext)
        {
            if (plaintext == null) plaintext = new byte[0];

            return Aes.CbcEncrypt(cipherKey, iv, plaintext);
        }

        /// <summary>Reverses <see cref="Encrypt"/>; a wrong key fails on the padding as often as not, and the MAC is what settles it.</summary>
        public static byte[] Decrypt(byte[] cipherKey, byte[] iv, byte[] ciphertext)
        {
            return Aes.CbcDecrypt(cipherKey, iv, ciphertext);
        }

        /// <summary>The eight bytes appended to every message.</summary>
        public static byte[] Mac(byte[] macKey, byte[] senderIdentity, byte[] receiverIdentity, byte[] message)
        {
            byte[] full = Hmac.Compute(macKey, Input(senderIdentity, receiverIdentity, message));

            var truncated = new byte[SignalConstants.MacLength];
            Buffer.BlockCopy(full, 0, truncated, 0, SignalConstants.MacLength);
            return truncated;
        }

        /// <summary>
        /// Compares the MAC in constant time, which is the same comparison the transport
        /// uses: a MAC found byte by byte is not a MAC.
        /// </summary>
        public static bool VerifyMac(byte[] macKey, byte[] senderIdentity, byte[] receiverIdentity, byte[] message, byte[] mac)
        {
            if (mac == null || mac.Length != SignalConstants.MacLength) return false;

            return Hmac.FixedTimeEquals(Mac(macKey, senderIdentity, receiverIdentity, message), mac);
        }

        /// <summary>
        /// The MAC's input: the two identities, the version byte and the message's
        /// protobuf bytes - without the version byte, which is only written once, at the
        /// front of the wire.
        /// </summary>
        private static byte[] Input(byte[] senderIdentity, byte[] receiverIdentity, byte[] message)
        {
            if (senderIdentity == null || senderIdentity.Length != SignalConstants.TypedKeyLength)
            {
                throw new ProtocolException("the MAC needs the sender's identity key, 33 bytes");
            }

            if (receiverIdentity == null || receiverIdentity.Length != SignalConstants.TypedKeyLength)
            {
                throw new ProtocolException("the MAC needs the receiver's identity key, 33 bytes");
            }

            var input = new byte[SignalConstants.TypedKeyLength * 2 + 1 + message.Length];
            Buffer.BlockCopy(senderIdentity, 0, input, 0, SignalConstants.TypedKeyLength);
            Buffer.BlockCopy(receiverIdentity, 0, input, SignalConstants.TypedKeyLength, SignalConstants.TypedKeyLength);
            input[SignalConstants.TypedKeyLength * 2] = SignalConstants.MessageVersionByte;
            Buffer.BlockCopy(message, 0, input, SignalConstants.TypedKeyLength * 2 + 1, message.Length);

            return input;
        }
    }
}
