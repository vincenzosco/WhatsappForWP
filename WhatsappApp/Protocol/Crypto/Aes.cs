using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// AES (FIPS-197) with CBC and PKCS#7, managed code.
    ///
    /// The phone has AES in WinRT but the desktop runner does not, and a Noise
    /// handshake needs a block cipher that both can run - so the block cipher lives
    /// here. <see cref="AesGcm"/> is built on <see cref="EncryptBlock"/>, which is why
    /// that one is public: a second block implementation under the GCM path would be a
    /// second thing to get wrong.
    ///
    /// The tables are the FIPS-197 substitution boxes, in their published order. They
    /// are data: a transposed entry encrypts to something that still decrypts here and
    /// nowhere else.
    /// </summary>
    public static class Aes
    {
        private const int BlockSize = 16;

        private static readonly byte[] SBox = new byte[]
        {
            0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
            0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
            0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
            0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
            0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
            0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
            0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
            0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
            0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
            0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
            0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
            0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
            0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
            0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
            0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
            0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16
        };

        private static readonly byte[] InverseSBox = new byte[]
        {
            0x52,0x09,0x6a,0xd5,0x30,0x36,0xa5,0x38,0xbf,0x40,0xa3,0x9e,0x81,0xf3,0xd7,0xfb,
            0x7c,0xe3,0x39,0x82,0x9b,0x2f,0xff,0x87,0x34,0x8e,0x43,0x44,0xc4,0xde,0xe9,0xcb,
            0x54,0x7b,0x94,0x32,0xa6,0xc2,0x23,0x3d,0xee,0x4c,0x95,0x0b,0x42,0xfa,0xc3,0x4e,
            0x08,0x2e,0xa1,0x66,0x28,0xd9,0x24,0xb2,0x76,0x5b,0xa2,0x49,0x6d,0x8b,0xd1,0x25,
            0x72,0xf8,0xf6,0x64,0x86,0x68,0x98,0x16,0xd4,0xa4,0x5c,0xcc,0x5d,0x65,0xb6,0x92,
            0x6c,0x70,0x48,0x50,0xfd,0xed,0xb9,0xda,0x5e,0x15,0x46,0x57,0xa7,0x8d,0x9d,0x84,
            0x90,0xd8,0xab,0x00,0x8c,0xbc,0xd3,0x0a,0xf7,0xe4,0x58,0x05,0xb8,0xb3,0x45,0x06,
            0xd0,0x2c,0x1e,0x8f,0xca,0x3f,0x0f,0x02,0xc1,0xaf,0xbd,0x03,0x01,0x13,0x8a,0x6b,
            0x3a,0x91,0x11,0x41,0x4f,0x67,0xdc,0xea,0x97,0xf2,0xcf,0xce,0xf0,0xb4,0xe6,0x73,
            0x96,0xac,0x74,0x22,0xe7,0xad,0x35,0x85,0xe2,0xf9,0x37,0xe8,0x1c,0x75,0xdf,0x6e,
            0x47,0xf1,0x1a,0x71,0x1d,0x29,0xc5,0x89,0x6f,0xb7,0x62,0x0e,0xaa,0x18,0xbe,0x1b,
            0xfc,0x56,0x3e,0x4b,0xc6,0xd2,0x79,0x20,0x9a,0xdb,0xc0,0xfe,0x78,0xcd,0x5a,0xf4,
            0x1f,0xdd,0xa8,0x33,0x88,0x07,0xc7,0x31,0xb1,0x12,0x10,0x59,0x27,0x80,0xec,0x5f,
            0x60,0x51,0x7f,0xa9,0x19,0xb5,0x4a,0x0d,0x2d,0xe5,0x7a,0x9f,0x93,0xc9,0x9c,0xef,
            0xa0,0xe0,0x3b,0x4d,0xae,0x2a,0xf5,0xb0,0xc8,0xeb,0xbb,0x3c,0x83,0x53,0x99,0x61,
            0x17,0x2b,0x04,0x7e,0xba,0x77,0xd6,0x26,0xe1,0x69,0x14,0x63,0x55,0x21,0x0c,0x7d
        };

        /// <summary>Encrypts one 16-byte block, returning a new array.</summary>
        public static byte[] EncryptBlock(byte[] key, byte[] block)
        {
            CheckBlock(block, "plaintext");

            int rounds = RoundsFor(key);
            uint[] schedule = ExpandKey(key);

            var state = new byte[BlockSize];
            Buffer.BlockCopy(block, 0, state, 0, BlockSize);

            AddRoundKey(state, schedule, 0);
            for (int round = 1; round < rounds; round++)
            {
                SubBytes(state, SBox);
                ShiftRows(state);
                MixColumns(state);
                AddRoundKey(state, schedule, round);
            }

            SubBytes(state, SBox);
            ShiftRows(state);
            AddRoundKey(state, schedule, rounds);

            return state;
        }

        /// <summary>Decrypts one 16-byte block, the inverse cipher of FIPS-197.</summary>
        public static byte[] DecryptBlock(byte[] key, byte[] block)
        {
            CheckBlock(block, "ciphertext");

            int rounds = RoundsFor(key);
            uint[] schedule = ExpandKey(key);

            var state = new byte[BlockSize];
            Buffer.BlockCopy(block, 0, state, 0, BlockSize);

            AddRoundKey(state, schedule, rounds);
            for (int round = rounds - 1; round >= 1; round--)
            {
                InverseShiftRows(state);
                SubBytes(state, InverseSBox);
                AddRoundKey(state, schedule, round);
                InverseMixColumns(state);
            }

            InverseShiftRows(state);
            SubBytes(state, InverseSBox);
            AddRoundKey(state, schedule, 0);

            return state;
        }

        /// <summary>
        /// CBC with PKCS#7. Padding is always added, even to a whole number of blocks:
        /// that is what the reference's `AesCbcPkcs7` does, and a mode that sometimes
        /// pads and sometimes does not cannot be unpadded reliably.
        /// </summary>
        public static byte[] CbcEncrypt(byte[] key, byte[] iv, byte[] plaintext)
        {
            CheckIv(iv);
            if (plaintext == null) plaintext = new byte[0];

            int padding = BlockSize - (plaintext.Length % BlockSize);
            int total = plaintext.Length + padding;

            var padded = new byte[total];
            Buffer.BlockCopy(plaintext, 0, padded, 0, plaintext.Length);
            for (int i = plaintext.Length; i < total; i++) padded[i] = (byte)padding;

            int rounds = RoundsFor(key);
            uint[] schedule = ExpandKey(key);

            var output = new byte[total];
            var previous = new byte[BlockSize];
            Buffer.BlockCopy(iv, 0, previous, 0, BlockSize);

            var input = new byte[BlockSize];
            for (int offset = 0; offset < total; offset += BlockSize)
            {
                for (int i = 0; i < BlockSize; i++) input[i] = (byte)(padded[offset + i] ^ previous[i]);

                byte[] encrypted = Cipher(input, schedule, rounds);
                Buffer.BlockCopy(encrypted, 0, output, offset, BlockSize);
                previous = encrypted;
            }

            return output;
        }

        /// <summary>Reverses <see cref="CbcEncrypt"/> and refuses broken padding.</summary>
        public static byte[] CbcDecrypt(byte[] key, byte[] iv, byte[] ciphertext)
        {
            CheckIv(iv);
            if (ciphertext == null || ciphertext.Length == 0 || ciphertext.Length % BlockSize != 0)
            {
                throw new ProtocolException("CBC needs a whole number of 16-byte blocks");
            }

            int rounds = RoundsFor(key);
            uint[] schedule = ExpandKey(key);

            var padded = new byte[ciphertext.Length];
            var previous = new byte[BlockSize];
            Buffer.BlockCopy(iv, 0, previous, 0, BlockSize);

            for (int offset = 0; offset < ciphertext.Length; offset += BlockSize)
            {
                var block = new byte[BlockSize];
                Buffer.BlockCopy(ciphertext, offset, block, 0, BlockSize);

                byte[] decrypted = InverseCipher(block, schedule, rounds);
                for (int i = 0; i < BlockSize; i++) padded[offset + i] = (byte)(decrypted[i] ^ previous[i]);

                previous = block;
            }

            int padding = padded[padded.Length - 1];
            if (padding < 1 || padding > BlockSize)
            {
                throw new ProtocolException("invalid padding: " + padding);
            }

            for (int i = padded.Length - padding; i < padded.Length; i++)
            {
                if (padded[i] != padding) throw new ProtocolException("invalid padding");
            }

            var plaintext = new byte[padded.Length - padding];
            Buffer.BlockCopy(padded, 0, plaintext, 0, plaintext.Length);
            return plaintext;
        }

        private static byte[] Cipher(byte[] state, uint[] schedule, int rounds)
        {
            var working = new byte[BlockSize];
            Buffer.BlockCopy(state, 0, working, 0, BlockSize);

            AddRoundKey(working, schedule, 0);
            for (int round = 1; round < rounds; round++)
            {
                SubBytes(working, SBox);
                ShiftRows(working);
                MixColumns(working);
                AddRoundKey(working, schedule, round);
            }

            SubBytes(working, SBox);
            ShiftRows(working);
            AddRoundKey(working, schedule, rounds);
            return working;
        }

        private static byte[] InverseCipher(byte[] state, uint[] schedule, int rounds)
        {
            var working = new byte[BlockSize];
            Buffer.BlockCopy(state, 0, working, 0, BlockSize);

            AddRoundKey(working, schedule, rounds);
            for (int round = rounds - 1; round >= 1; round--)
            {
                InverseShiftRows(working);
                SubBytes(working, InverseSBox);
                AddRoundKey(working, schedule, round);
                InverseMixColumns(working);
            }

            InverseShiftRows(working);
            SubBytes(working, InverseSBox);
            AddRoundKey(working, schedule, 0);
            return working;
        }

        /// <summary>
        /// The round count: 10, 12 or 14, which is the key's words plus six. It is a
        /// separate helper rather than an `out` parameter on <see cref="ExpandKey"/>,
        /// because the C# 5 guard reads `out int x` in a signature as C# 6's inline
        /// declaration - and a guard rule is not worth weakening for a signature.
        /// </summary>
        private static int RoundsFor(byte[] key)
        {
            return key.Length / 4 + 6;
        }

        private static uint[] ExpandKey(byte[] key)
        {
            if (key == null) throw new ProtocolException("AES needs a key");

            int keyWords = key.Length / 4;
            if (key.Length != 16 && key.Length != 24 && key.Length != 32)
            {
                throw new ProtocolException("AES needs a 128, 192 or 256-bit key, not " + key.Length + " bytes");
            }

            int rounds = RoundsFor(key);
            int words = 4 * (rounds + 1);
            var schedule = new uint[words];

            for (int i = 0; i < keyWords; i++)
            {
                schedule[i] = ((uint)key[4 * i] << 24) | ((uint)key[4 * i + 1] << 16)
                    | ((uint)key[4 * i + 2] << 8) | key[4 * i + 3];
            }

            var roundConstants = new byte[] { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80, 0x1b, 0x36 };
            for (int i = keyWords; i < words; i++)
            {
                uint temp = schedule[i - 1];
                if (i % keyWords == 0)
                {
                    temp = SubWord(RotateWord(temp)) ^ ((uint)roundConstants[i / keyWords - 1] << 24);
                }
                else if (keyWords > 6 && i % keyWords == 4)
                {
                    temp = SubWord(temp);
                }

                schedule[i] = schedule[i - keyWords] ^ temp;
            }

            return schedule;
        }

        private static void AddRoundKey(byte[] state, uint[] schedule, int round)
        {
            for (int column = 0; column < 4; column++)
            {
                uint word = schedule[4 * round + column];
                state[4 * column] ^= (byte)(word >> 24);
                state[4 * column + 1] ^= (byte)(word >> 16);
                state[4 * column + 2] ^= (byte)(word >> 8);
                state[4 * column + 3] ^= (byte)word;
            }
        }

        private static void SubBytes(byte[] state, byte[] box)
        {
            for (int i = 0; i < BlockSize; i++) state[i] = box[state[i]];
        }

        /// <summary>Row r rotates left by r; index is `row + 4 * column`.</summary>
        private static void ShiftRows(byte[] state)
        {
            var copy = new byte[BlockSize];
            Buffer.BlockCopy(state, 0, copy, 0, BlockSize);

            for (int row = 1; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    state[row + 4 * column] = copy[row + 4 * ((column + row) % 4)];
                }
            }
        }

        private static void InverseShiftRows(byte[] state)
        {
            var copy = new byte[BlockSize];
            Buffer.BlockCopy(state, 0, copy, 0, BlockSize);

            for (int row = 1; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    state[row + 4 * column] = copy[row + 4 * ((column - row + 4) % 4)];
                }
            }
        }

        private static void MixColumns(byte[] state)
        {
            for (int column = 0; column < 4; column++)
            {
                int at = 4 * column;
                byte s0 = state[at], s1 = state[at + 1], s2 = state[at + 2], s3 = state[at + 3];

                state[at] = (byte)(Times(s0) ^ Times(s1) ^ s1 ^ s2 ^ s3);
                state[at + 1] = (byte)(s0 ^ Times(s1) ^ Times(s2) ^ s2 ^ s3);
                state[at + 2] = (byte)(s0 ^ s1 ^ Times(s2) ^ Times(s3) ^ s3);
                state[at + 3] = (byte)(Times(s0) ^ s0 ^ s1 ^ s2 ^ Times(s3));
            }
        }

        private static void InverseMixColumns(byte[] state)
        {
            for (int column = 0; column < 4; column++)
            {
                int at = 4 * column;
                byte s0 = state[at], s1 = state[at + 1], s2 = state[at + 2], s3 = state[at + 3];

                state[at] = (byte)(Multiply(s0, 14) ^ Multiply(s1, 11) ^ Multiply(s2, 13) ^ Multiply(s3, 9));
                state[at + 1] = (byte)(Multiply(s0, 9) ^ Multiply(s1, 14) ^ Multiply(s2, 11) ^ Multiply(s3, 13));
                state[at + 2] = (byte)(Multiply(s0, 13) ^ Multiply(s1, 9) ^ Multiply(s2, 14) ^ Multiply(s3, 11));
                state[at + 3] = (byte)(Multiply(s0, 11) ^ Multiply(s1, 13) ^ Multiply(s2, 9) ^ Multiply(s3, 14));
            }
        }

        /// <summary>x times 2 in GF(2^8) with the AES polynomial 0x11b.</summary>
        private static byte Times(byte value)
        {
            return (byte)((value << 1) ^ ((value & 0x80) != 0 ? 0x1b : 0));
        }

        private static byte Multiply(byte left, byte right)
        {
            byte result = 0;
            byte a = left;
            int b = right;

            while (b != 0)
            {
                if ((b & 1) != 0) result ^= a;
                a = Times(a);
                b >>= 1;
            }

            return result;
        }

        private static uint SubWord(uint word)
        {
            return ((uint)SBox[(word >> 24) & 0xff] << 24)
                | ((uint)SBox[(word >> 16) & 0xff] << 16)
                | ((uint)SBox[(word >> 8) & 0xff] << 8)
                | SBox[word & 0xff];
        }

        private static uint RotateWord(uint word)
        {
            return (word << 8) | (word >> 24);
        }

        private static void CheckBlock(byte[] block, string what)
        {
            if (block == null || block.Length != BlockSize)
            {
                throw new ProtocolException("AES works on 16-byte blocks, not " + what);
            }
        }

        private static void CheckIv(byte[] iv)
        {
            if (iv == null || iv.Length != BlockSize)
            {
                throw new ProtocolException("CBC needs a 16-byte IV");
            }
        }
    }
}
