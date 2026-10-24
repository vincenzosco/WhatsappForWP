using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// HMAC-SHA256 (RFC 2104 over <see cref="Sha256"/>), for the same reason the hash
    /// is managed: the phone and the desktop runner have to run the same code.
    /// </summary>
    public static class Hmac
    {
        private const int BlockLength = 64;

        // Not `Hmac.Sha256`: inside this class that name would resolve to this method
        // and shadow the type, so `Sha256.Hash` below would not compile. `Compute`
        // says what it does and leaves the type name alone.
        public static byte[] Compute(byte[] key, byte[] data)
        {
            if (key == null) throw new ProtocolException("HMAC needs a key");
            if (data == null) data = new byte[0];

            // A key longer than the block is hashed first; shorter keys are padded.
            byte[] block = new byte[BlockLength];
            if (key.Length > BlockLength)
            {
                byte[] hashed = Sha256.Hash(key);
                Buffer.BlockCopy(hashed, 0, block, 0, hashed.Length);
            }
            else
            {
                Buffer.BlockCopy(key, 0, block, 0, key.Length);
            }

            var inner = new byte[BlockLength + data.Length];
            var outer = new byte[BlockLength + Sha256.DigestLength];

            for (int i = 0; i < BlockLength; i++)
            {
                inner[i] = (byte)(block[i] ^ 0x36);
                outer[i] = (byte)(block[i] ^ 0x5c);
            }

            Buffer.BlockCopy(data, 0, inner, BlockLength, data.Length);
            Buffer.BlockCopy(Sha256.Hash(inner), 0, outer, BlockLength, Sha256.DigestLength);

            return Sha256.Hash(outer);
        }

        /// <summary>
        /// HMAC-SHA512, for the one place that needs a wider digest: the app-state sync's
        /// mutation MAC, which the reference signs with sha512 and keeps the **first 32
        /// bytes** of (`Utils/chat-utils.js`, `generateMac`, and `hmacSign(total, key,
        /// 'sha512')` beside it). A different HMAC and not a different hash of this one: the
        /// block is 128 bytes here, so a key of 64 bytes is padded and not hashed.
        /// </summary>
        public static byte[] ComputeSha512(byte[] key, byte[] data)
        {
            if (key == null) throw new ProtocolException("HMAC needs a key");
            if (data == null) data = new byte[0];

            const int blockLength = 128;

            byte[] block = new byte[blockLength];
            if (key.Length > blockLength)
            {
                byte[] hashed = Sha512.Hash(key);
                Buffer.BlockCopy(hashed, 0, block, 0, hashed.Length);
            }
            else
            {
                Buffer.BlockCopy(key, 0, block, 0, key.Length);
            }

            var inner = new byte[blockLength + data.Length];
            var outer = new byte[blockLength + Sha512.DigestLength];

            for (int i = 0; i < blockLength; i++)
            {
                inner[i] = (byte)(block[i] ^ 0x36);
                outer[i] = (byte)(block[i] ^ 0x5c);
            }

            Buffer.BlockCopy(data, 0, inner, blockLength, data.Length);
            Buffer.BlockCopy(Sha512.Hash(inner), 0, outer, blockLength, Sha512.DigestLength);

            return Sha512.Hash(outer);
        }

        /// <summary>A constant-time comparison, so a tag cannot be found byte by byte.</summary>
        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;

            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
