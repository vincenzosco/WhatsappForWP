using System;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>The two halves a ratchet step produces: the new root and the chain that hangs off it.</summary>
    public struct RootAndChain
    {
        /// <summary>The root key the next step will be derived from.</summary>
        public byte[] RootKey;

        /// <summary>The chain key of the sending chain this step opened.</summary>
        public byte[] ChainKey;
    }

    /// <summary>What a message is encrypted with, all three derived from one message key and deleted after use.</summary>
    public struct MessageKeys
    {
        /// <summary>AES-256-CBC's key.</summary>
        public byte[] CipherKey;

        /// <summary>HMAC-SHA256's key, for the 8 bytes appended to the message.</summary>
        public byte[] MacKey;

        /// <summary>The 16-byte IV.</summary>
        public byte[] Iv;
    }

    /// <summary>
    /// The key schedule: three derivations and nothing else.
    ///
    /// It is a file of its own because it is the part of Signal that is pure arithmetic,
    /// and because two of the three are one line each and the third is the one that has
    /// to be read against the reference:
    ///
    /// - **The chain advances by HMAC.** The message key is `HMAC-SHA256(chainKey, 0x01)`
    ///   and the next chain key is `HMAC-SHA256(chainKey, 0x02)`. Two one-byte inputs and
    ///   the order they are used in is the whole protocol; a chain that used the same
    ///   byte twice would produce a session that works and leaks.
    /// - **The root advances by HKDF**, with the current root as the salt and the DH
    ///   output as the input key material, `"WhisperRatchet"` as the info, and 64 bytes
    ///   out: the first 32 are the new root and the second 32 the chain.
    /// - **A message key becomes three keys** through HKDF with 32 zero bytes as the
    ///   salt and `"WhisperMessageKeys"` as the info: 80 bytes out, of which the first 32
    ///   are the cipher key, the next 32 the MAC key and the last 16 the IV.
    ///
    /// Every constant these three use was printed from the reference; see
    /// <see cref="SignalConstants"/>.
    /// </summary>
    public static class Keys
    {
        /// <summary>One ratchet step: the DH of our new key with theirs, salted with the root we hold.</summary>
        public static RootAndChain Root(byte[] dhOutput, byte[] currentRootKey)
        {
            if (dhOutput == null || dhOutput.Length != 32) throw new ProtocolException("a ratchet step needs a 32-byte agreement");
            if (currentRootKey == null || currentRootKey.Length != 32) throw new ProtocolException("a ratchet step needs the root it is stepping from");

            byte[] derived = Hkdf.Derive(dhOutput, currentRootKey, X3dh.Text(SignalConstants.InfoRatchet), 64);

            var step = new RootAndChain();
            step.RootKey = new byte[32];
            step.ChainKey = new byte[32];

            Buffer.BlockCopy(derived, 0, step.RootKey, 0, 32);
            Buffer.BlockCopy(derived, 32, step.ChainKey, 0, 32);

            return step;
        }

        /// <summary>The message key of the current link in the chain.</summary>
        public static byte[] MessageKey(byte[] chainKey)
        {
            return Hmac.Compute(Check(chainKey), new byte[] { SignalConstants.MessageKeySeed });
        }

        /// <summary>The chain key of the next link, which is what the current one becomes after a message.</summary>
        public static byte[] NextChainKey(byte[] chainKey)
        {
            return Hmac.Compute(Check(chainKey), new byte[] { SignalConstants.ChainKeySeed });
        }

        /// <summary>The cipher key, the MAC key and the IV of one message.</summary>
        public static MessageKeys MessageKeys(byte[] messageKey)
        {
            if (messageKey == null || messageKey.Length != 32) throw new ProtocolException("a message key is 32 bytes");

            byte[] derived = Hkdf.Derive(
                messageKey,
                new byte[32],
                X3dh.Text(SignalConstants.InfoMessageKeys),
                SignalConstants.CipherKeyLength + SignalConstants.MacKeyLength + SignalConstants.IvLength);

            var keys = new MessageKeys();
            keys.CipherKey = new byte[SignalConstants.CipherKeyLength];
            keys.MacKey = new byte[SignalConstants.MacKeyLength];
            keys.Iv = new byte[SignalConstants.IvLength];

            Buffer.BlockCopy(derived, 0, keys.CipherKey, 0, SignalConstants.CipherKeyLength);
            Buffer.BlockCopy(derived, SignalConstants.CipherKeyLength, keys.MacKey, 0, SignalConstants.MacKeyLength);
            Buffer.BlockCopy(derived,
                SignalConstants.CipherKeyLength + SignalConstants.MacKeyLength,
                keys.Iv, 0, SignalConstants.IvLength);

            return keys;
        }

        private static byte[] Check(byte[] chainKey)
        {
            if (chainKey == null || chainKey.Length != 32) throw new ProtocolException("a chain key is 32 bytes");

            return chainKey;
        }
    }
}
