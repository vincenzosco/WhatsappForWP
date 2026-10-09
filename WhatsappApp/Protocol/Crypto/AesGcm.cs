using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// AES-GCM (NIST SP 800-38D) over <see cref="Aes"/>.
    ///
    /// This is the cipher the Noise handshake encrypts with, and the one thing the
    /// phone cannot do itself: its WinRT projection answers
    /// `NotImplementedException 0x80004001` for AES-GCM, which is why this project's
    /// frame transport had to fall back to CBC+HMAC. The handshake has no such
    /// alternative - the Noise suite WhatsApp speaks is
    /// `Noise_XX_25519_AESGCM_SHA256` - so GCM is written here, on top of the block
    /// cipher, with GHASH in GF(2^128).
    ///
    /// Only the 96-bit nonce form is supported: that is the only one the handshake
    /// uses, and the general form is a different (longer) derivation that would
    /// otherwise look implemented while being untested.
    /// </summary>
    public static class AesGcm
    {
        public const int TagLength = 16;
        private const int BlockSize = 16;
        private const int NonceLength = 12;

        /// <summary>
        /// Encrypts and authenticates, returning the ciphertext with the 16-byte tag
        /// appended. The additional data is authenticated and not encrypted.
        /// </summary>
        public static byte[] Seal(byte[] key, byte[] nonce, byte[] plaintext, byte[] additionalData)
        {
            if (plaintext == null) plaintext = new byte[0];
            if (additionalData == null) additionalData = new byte[0];

            byte[] h = Aes.EncryptBlock(key, new byte[BlockSize]);
            byte[] initialCounter = InitialCounter(nonce);

            byte[] ciphertext = CounterMode(key, initialCounter, plaintext);
            byte[] tag = Xor(Aes.EncryptBlock(key, initialCounter), GHash(h, additionalData, ciphertext));

            var output = new byte[ciphertext.Length + TagLength];
            Buffer.BlockCopy(ciphertext, 0, output, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, output, ciphertext.Length, TagLength);
            return output;
        }

        /// <summary>
        /// Verifies the tag and only then decrypts. A tag that does not match is a
        /// refusal, never a best-effort plaintext: a caller that reads the bytes of a
        /// frame it could not authenticate has no way to learn it was tampered with.
        /// </summary>
        public static byte[] Open(byte[] key, byte[] nonce, byte[] sealedBytes, byte[] additionalData)
        {
            if (sealedBytes == null || sealedBytes.Length < TagLength)
            {
                throw new ProtocolException("a sealed message carries a " + TagLength + "-byte tag");
            }

            if (additionalData == null) additionalData = new byte[0];

            int cipherLength = sealedBytes.Length - TagLength;
            var ciphertext = new byte[cipherLength];
            Buffer.BlockCopy(sealedBytes, 0, ciphertext, 0, cipherLength);

            var tag = new byte[TagLength];
            Buffer.BlockCopy(sealedBytes, cipherLength, tag, 0, TagLength);

            byte[] h = Aes.EncryptBlock(key, new byte[BlockSize]);
            byte[] initialCounter = InitialCounter(nonce);
            byte[] expected = Xor(Aes.EncryptBlock(key, initialCounter), GHash(h, additionalData, ciphertext));

            if (!Hmac.FixedTimeEquals(expected, tag))
            {
                throw new ProtocolException("the authentication tag does not match");
            }

            return CounterMode(key, initialCounter, ciphertext);
        }

        /// <summary>J0 for a 96-bit nonce: the nonce, then the counter at one.</summary>
        private static byte[] InitialCounter(byte[] nonce)
        {
            if (nonce == null || nonce.Length != NonceLength)
            {
                throw new ProtocolException("AES-GCM here needs a " + NonceLength + "-byte nonce, not "
                    + (nonce == null ? "none" : nonce.Length.ToString()));
            }

            var counter = new byte[BlockSize];
            Buffer.BlockCopy(nonce, 0, counter, 0, NonceLength);
            counter[BlockSize - 1] = 1;
            return counter;
        }

        /// <summary>
        /// GCTR: the first keystream block is `inc32(J0)`, not `J0` - J0 is the tag's
        /// block. Getting this one increment wrong encrypts and decrypts consistently
        /// with itself and is refused by WhatsApp.
        /// </summary>
        private static byte[] CounterMode(byte[] key, byte[] initialCounter, byte[] data)
        {
            var output = new byte[data.Length];
            if (data.Length == 0) return output;

            var counter = new byte[BlockSize];
            Buffer.BlockCopy(initialCounter, 0, counter, 0, BlockSize);

            for (int offset = 0; offset < data.Length; offset += BlockSize)
            {
                IncrementCounter(counter);

                byte[] keystream = Aes.EncryptBlock(key, counter);
                int take = data.Length - offset < BlockSize ? data.Length - offset : BlockSize;
                for (int i = 0; i < take; i++) output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            }

            return output;
        }

        /// <summary>Only the last 32 bits are a counter, as the mode specifies.</summary>
        private static void IncrementCounter(byte[] counter)
        {
            for (int i = BlockSize - 1; i >= BlockSize - 4; i--)
            {
                counter[i]++;
                if (counter[i] != 0) return;
            }
        }

        /// <summary>GHASH over the additional data, the ciphertext, then both lengths.</summary>
        private static byte[] GHash(byte[] h, byte[] additionalData, byte[] ciphertext)
        {
            var y = new byte[BlockSize];
            var block = new byte[BlockSize];

            AbsorbBlocks(y, block, h, additionalData);
            AbsorbBlocks(y, block, h, ciphertext);

            for (int i = 0; i < BlockSize; i++) block[i] = 0;
            WriteLength(block, 0, (ulong)additionalData.Length * 8UL);
            WriteLength(block, 8, (ulong)ciphertext.Length * 8UL);
            XorInPlace(y, block);
            Multiply(y, h, y);

            return y;
        }

        private static void AbsorbBlocks(byte[] y, byte[] block, byte[] h, byte[] data)
        {
            for (int offset = 0; offset < data.Length; offset += BlockSize)
            {
                for (int i = 0; i < BlockSize; i++) block[i] = 0;

                int take = data.Length - offset < BlockSize ? data.Length - offset : BlockSize;
                Buffer.BlockCopy(data, offset, block, 0, take);

                XorInPlace(y, block);
                Multiply(y, h, y);
            }
        }

        private static void WriteLength(byte[] block, int offset, ulong bits)
        {
            for (int i = 0; i < 8; i++)
            {
                block[offset + i] = (byte)(bits >> (8 * (7 - i)));
            }
        }

        /// <summary>
        /// The GCM field multiplication: bit i of X against the shifted-down V, with
        /// the reduction polynomial's low byte xored in when V's low bit was set.
        /// </summary>
        private static void Multiply(byte[] x, byte[] y, byte[] result)
        {
            var v = new byte[BlockSize];
            Buffer.BlockCopy(y, 0, v, 0, BlockSize);

            var z = new byte[BlockSize];

            for (int i = 0; i < 128; i++)
            {
                if (((x[i >> 3] >> (7 - (i & 7))) & 1) != 0) XorInPlace(z, v);

                bool lowBit = (v[BlockSize - 1] & 1) != 0;
                ShiftRightOne(v);
                if (lowBit) v[0] ^= 0xe1;
            }

            Buffer.BlockCopy(z, 0, result, 0, BlockSize);
        }

        private static void ShiftRightOne(byte[] value)
        {
            for (int i = BlockSize - 1; i > 0; i--)
            {
                value[i] = (byte)((value[i] >> 1) | (value[i - 1] << 7));
            }

            value[0] >>= 1;
        }

        private static void XorInPlace(byte[] target, byte[] source)
        {
            for (int i = 0; i < target.Length; i++) target[i] ^= source[i];
        }

        private static byte[] Xor(byte[] left, byte[] right)
        {
            var result = new byte[left.Length];
            for (int i = 0; i < left.Length; i++) result[i] = (byte)(left[i] ^ right[i]);
            return result;
        }
    }
}
