using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// SHA-512 (FIPS 180-4), written here for the same reason as <see cref="Sha256"/>:
    /// the WP8.1 profile has no `System.Security.Cryptography`, and the desktop runner
    /// cannot use WinRT, so the one implementation has to be managed and shared.
    ///
    /// It exists for Ed25519, whose hash it is. Nothing else in the protocol uses it.
    /// </summary>
    public static class Sha512
    {
        public const int DigestLength = 64;
        private const int BlockLength = 128;

        private static readonly ulong[] RoundConstants = new ulong[]
        {
            0x428a2f98d728ae22, 0x7137449123ef65cd, 0xb5c0fbcfec4d3b2f, 0xe9b5dba58189dbbc,
            0x3956c25bf348b538, 0x59f111f1b605d019, 0x923f82a4af194f9b, 0xab1c5ed5da6d8118,
            0xd807aa98a3030242, 0x12835b0145706fbe, 0x243185be4ee4b28c, 0x550c7dc3d5ffb4e2,
            0x72be5d74f27b896f, 0x80deb1fe3b1696b1, 0x9bdc06a725c71235, 0xc19bf174cf692694,
            0xe49b69c19ef14ad2, 0xefbe4786384f25e3, 0x0fc19dc68b8cd5b5, 0x240ca1cc77ac9c65,
            0x2de92c6f592b0275, 0x4a7484aa6ea6e483, 0x5cb0a9dcbd41fbd4, 0x76f988da831153b5,
            0x983e5152ee66dfab, 0xa831c66d2db43210, 0xb00327c898fb213f, 0xbf597fc7beef0ee4,
            0xc6e00bf33da88fc2, 0xd5a79147930aa725, 0x06ca6351e003826f, 0x142929670a0e6e70,
            0x27b70a8546d22ffc, 0x2e1b21385c26c926, 0x4d2c6dfc5ac42aed, 0x53380d139d95b3df,
            0x650a73548baf63de, 0x766a0abb3c77b2a8, 0x81c2c92e47edaee6, 0x92722c851482353b,
            0xa2bfe8a14cf10364, 0xa81a664bbc423001, 0xc24b8b70d0f89791, 0xc76c51a30654be30,
            0xd192e819d6ef5218, 0xd69906245565a910, 0xf40e35855771202a, 0x106aa07032bbd1b8,
            0x19a4c116b8d2d0c8, 0x1e376c085141ab53, 0x2748774cdf8eeb99, 0x34b0bcb5e19b48a8,
            0x391c0cb3c5c95a63, 0x4ed8aa4ae3418acb, 0x5b9cca4f7763e373, 0x682e6ff3d6b2b8a3,
            0x748f82ee5defb2fc, 0x78a5636f43172f60, 0x84c87814a1f0ab72, 0x8cc702081a6439ec,
            0x90befffa23631e28, 0xa4506cebde82bde9, 0xbef9a3f7b2c67915, 0xc67178f2e372532b,
            0xca273eceea26619c, 0xd186b8c721c0c207, 0xeada7dd6cde0eb1e, 0xf57d4f7fee6ed178,
            0x06f067aa72176fba, 0x0a637dc5a2c898a6, 0x113f9804bef90dae, 0x1b710b35131c471b,
            0x28db77f523047d84, 0x32caab7b40c72493, 0x3c9ebe0a15c9bebc, 0x431d67c49c100d4c,
            0x4cc5d4becb3e42b6, 0x597f299cfc657e2a, 0x5fcb6fab3ad6faec, 0x6c44198c4a475817
        };

        public static byte[] Hash(byte[] data)
        {
            if (data == null) throw new ProtocolException("nothing to hash");

            return Hash(data, 0, data.Length);
        }

        /// <summary>Hashes a slice, which is how the signer hashes three pieces in a row.</summary>
        public static byte[] Hash(byte[] data, int offset, int count)
        {
            if (data == null) throw new ProtocolException("nothing to hash");
            if (offset < 0 || count < 0 || offset + count > data.Length)
            {
                throw new ProtocolException("the slice to hash is outside the buffer");
            }

            ulong[] state = new ulong[]
            {
                0x6a09e667f3bcc908, 0xbb67ae8584caa73b, 0x3c6ef372fe94f82b, 0xa54ff53a5f1d36f1,
                0x510e527fade682d1, 0x9b05688c2b3e6c1f, 0x1f83d9abfb41bd6b, 0x5be0cd19137e2179
            };

            byte[] padded = Pad(data, offset, count);
            var schedule = new ulong[80];

            for (int at = 0; at < padded.Length; at += BlockLength)
            {
                Compress(state, padded, at, schedule);
            }

            var digest = new byte[DigestLength];
            for (int i = 0; i < 8; i++)
            {
                for (int b = 0; b < 8; b++)
                {
                    digest[8 * i + b] = (byte)(state[i] >> (56 - 8 * b));
                }
            }

            return digest;
        }

        /// <summary>Hashes several pieces as one message, without joining them first.</summary>
        public static byte[] HashConcat(byte[] first, byte[] second)
        {
            return HashConcat(first, second, null, null);
        }

        public static byte[] HashConcat(byte[] first, byte[] second, byte[] third)
        {
            return HashConcat(first, second, third, null);
        }

        public static byte[] HashConcat(byte[] first, byte[] second, byte[] third, byte[] fourth)
        {
            var joined = new byte[Length(first) + Length(second) + Length(third) + Length(fourth)];
            int at = 0;

            at = Copy(first, joined, at);
            at = Copy(second, joined, at);
            at = Copy(third, joined, at);
            Copy(fourth, joined, at);

            return Hash(joined);
        }

        private static int Length(byte[] data)
        {
            return data == null ? 0 : data.Length;
        }

        private static int Copy(byte[] source, byte[] destination, int at)
        {
            if (source == null) return at;

            Buffer.BlockCopy(source, 0, destination, at, source.Length);
            return at + source.Length;
        }

        private static byte[] Pad(byte[] data, int offset, int count)
        {
            // 0x80, zeros, then the bit length as a 128-bit big-endian number. Nothing
            // hashes more than 2^64 bits here, so the top eight bytes of that length are
            // written as zero.
            int remainder = count % BlockLength;
            int padding = remainder < 112 ? 112 - remainder : 240 - remainder;

            var padded = new byte[count + padding + 16];
            Buffer.BlockCopy(data, offset, padded, 0, count);
            padded[count] = 0x80;

            ulong bitLength = (ulong)count * 8UL;
            for (int i = 0; i < 8; i++)
            {
                padded[padded.Length - 1 - i] = (byte)(bitLength >> (8 * i));
            }

            return padded;
        }

        private static void Compress(ulong[] state, byte[] block, int offset, ulong[] schedule)
        {
            for (int i = 0; i < 16; i++)
            {
                int at = offset + 8 * i;
                schedule[i] = ((ulong)block[at] << 56) | ((ulong)block[at + 1] << 48)
                    | ((ulong)block[at + 2] << 40) | ((ulong)block[at + 3] << 32)
                    | ((ulong)block[at + 4] << 24) | ((ulong)block[at + 5] << 16)
                    | ((ulong)block[at + 6] << 8) | block[at + 7];
            }

            for (int i = 16; i < 80; i++)
            {
                ulong s0 = RotateRight(schedule[i - 15], 1) ^ RotateRight(schedule[i - 15], 8)
                    ^ (schedule[i - 15] >> 7);
                ulong s1 = RotateRight(schedule[i - 2], 19) ^ RotateRight(schedule[i - 2], 61)
                    ^ (schedule[i - 2] >> 6);
                schedule[i] = schedule[i - 16] + s0 + schedule[i - 7] + s1;
            }

            ulong a = state[0], b = state[1], c = state[2], d = state[3];
            ulong e = state[4], f = state[5], g = state[6], h = state[7];

            for (int i = 0; i < 80; i++)
            {
                ulong s1 = RotateRight(e, 14) ^ RotateRight(e, 18) ^ RotateRight(e, 41);
                ulong choice = (e & f) ^ (~e & g);
                ulong temp1 = h + s1 + choice + RoundConstants[i] + schedule[i];
                ulong s0 = RotateRight(a, 28) ^ RotateRight(a, 34) ^ RotateRight(a, 39);
                ulong majority = (a & b) ^ (a & c) ^ (b & c);
                ulong temp2 = s0 + majority;

                h = g; g = f; f = e; e = d + temp1;
                d = c; c = b; b = a; a = temp1 + temp2;
            }

            state[0] += a; state[1] += b; state[2] += c; state[3] += d;
            state[4] += e; state[5] += f; state[6] += g; state[7] += h;
        }

        private static ulong RotateRight(ulong value, int count)
        {
            return (value >> count) | (value << (64 - count));
        }
    }
}
