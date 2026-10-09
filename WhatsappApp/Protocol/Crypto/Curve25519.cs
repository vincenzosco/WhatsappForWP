using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// X25519 (RFC 7748) over the field 2^255 - 19, in managed code.
    ///
    /// .NET Framework 4.5 has no Curve25519 at all and WP8.1 has no usable elliptic
    /// curve the Noise handshake could substitute, so the ladder is written here. The
    /// representation is the portable one - 16 limbs of 16 bits held in `long` - which
    /// keeps every intermediate product far inside 64 bits: a 16x16 schoolbook
    /// multiply peaks around 2^36, so there is no 128-bit arithmetic to emulate and no
    /// per-limb carry to get subtly wrong.
    ///
    /// It is a transcription of the reference field arithmetic (the `gf`/`car25519`/
    /// `sel25519` form of TweetNaCl), not an optimization: a handshake does two
    /// scalar multiplications, and a phone on a 512 MB budget is not where a faster,
    /// harder-to-check ladder earns its keep.
    /// </summary>
    public static class Curve25519
    {
        public const int KeyLength = 32;
        private const int Limbs = 16;

        /// <summary>121665 in this representation, the `a24` of the ladder.</summary>
        private static readonly long[] A24 = new long[Limbs] { 0xDB41, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        private static readonly byte[] BasePoint = new byte[KeyLength] { 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>The public key of a private key: the scalar times the base point.</summary>
        public static byte[] PublicKey(byte[] privateKey)
        {
            return ScalarMult(privateKey, BasePoint);
        }

        /// <summary>The shared secret of a private key and the other side's public key.</summary>
        public static byte[] SharedSecret(byte[] privateKey, byte[] publicKey)
        {
            return ScalarMult(privateKey, publicKey);
        }

        // There is deliberately no NewPrivateKey() here: random numbers are not this
        // core's business. `System.Security.Cryptography` does not exist in the
        // WP8.1 projection, and reaching for it would silently make this file
        // unbuildable for the phone - the one toolchain the whole port exists for.
        // The caller passes the bytes, and the app gets them from WinRT.

        /// <summary>The Montgomery ladder, 255 iterations over the clamped scalar.</summary>
        public static byte[] ScalarMult(byte[] scalar, byte[] point)
        {
            if (scalar == null || scalar.Length != KeyLength)
            {
                throw new ProtocolException("X25519 needs a " + KeyLength + "-byte scalar");
            }

            if (point == null || point.Length != KeyLength)
            {
                throw new ProtocolException("X25519 needs a " + KeyLength + "-byte point");
            }

            // Clamping: the low three bits off, the top bit off, the second-highest on.
            var z = new byte[KeyLength];
            Buffer.BlockCopy(scalar, 0, z, 0, KeyLength);
            z[31] = (byte)((z[31] & 127) | 64);
            z[0] &= 248;

            var x = new long[5 * Limbs];
            Unpack(x, 0, point);

            var a = new long[Limbs];
            var b = new long[Limbs];
            var c = new long[Limbs];
            var d = new long[Limbs];
            var e = new long[Limbs];
            var f = new long[Limbs];

            for (int i = 0; i < Limbs; i++) b[i] = x[i];
            a[0] = 1;
            d[0] = 1;

            for (int i = 254; i >= 0; i--)
            {
                int bit = (z[i >> 3] >> (i & 7)) & 1;

                SelectSwap(a, b, bit);
                SelectSwap(c, d, bit);

                Add(e, a, c);
                Sub(a, a, c);
                Add(c, b, d);
                Sub(b, b, d);

                Square(d, e);
                Square(f, a);
                Mul(a, c, a);
                Mul(c, b, e);

                Add(e, a, c);
                Sub(a, a, c);
                Square(b, a);
                Sub(c, d, f);

                Mul(a, c, A24);
                Add(a, a, d);
                Mul(c, c, a);
                Mul(a, d, f);

                Mul(d, b, x);
                Square(b, e);

                SelectSwap(a, b, bit);
                SelectSwap(c, d, bit);
            }

            for (int i = 0; i < Limbs; i++)
            {
                x[i + 16] = a[i];
                x[i + 32] = c[i];
                x[i + 48] = b[i];
                x[i + 64] = d[i];
            }

            Invert(x, 32, x, 32);
            Mul(x, 16, x, 16, x, 32);

            return Pack(x, 16);
        }

        /// <summary>
        /// The carry chain. Every limb is normalized to 16 bits, and the carry out of
        /// the top one folds back into the bottom multiplied by 38 - the field's
        /// reduction, since 2^256 = 38 mod (2^255 - 19).
        /// </summary>
        private static void Carry(long[] o, int offset)
        {
            for (int i = 0; i < Limbs; i++)
            {
                o[offset + i] += 1L << 16;
                long carry = o[offset + i] >> 16;
                o[offset + i] -= carry << 16;

                if (i < Limbs - 1) o[offset + i + 1] += carry - 1;
                else o[offset] += 38 * (carry - 1);
            }
        }

        /// <summary>A constant-time conditional swap: `bit` is only ever 0 or 1.</summary>
        private static void SelectSwap(long[] p, long[] q, int bit)
        {
            long mask = -(long)bit;
            for (int i = 0; i < Limbs; i++)
            {
                long difference = mask & (p[i] ^ q[i]);
                p[i] ^= difference;
                q[i] ^= difference;
            }
        }

        private static void Add(long[] o, long[] a, long[] b)
        {
            for (int i = 0; i < Limbs; i++) o[i] = a[i] + b[i];
        }

        private static void Sub(long[] o, long[] a, long[] b)
        {
            for (int i = 0; i < Limbs; i++) o[i] = a[i] - b[i];
        }

        private static void Mul(long[] o, long[] a, long[] b)
        {
            Mul(o, 0, a, 0, b, 0);
        }

        private static void Square(long[] o, long[] a)
        {
            Mul(o, 0, a, 0, a, 0);
        }

        private static void Mul(long[] o, int oi, long[] a, int ai, long[] b, int bi)
        {
            var product = new long[2 * Limbs - 1];

            for (int i = 0; i < Limbs; i++)
            {
                for (int j = 0; j < Limbs; j++)
                {
                    product[i + j] += a[ai + i] * b[bi + j];
                }
            }

            // Everything from 2^256 up reduces to a multiple of 38 in the low limbs.
            for (int i = 0; i < Limbs - 1; i++) product[i] += 38 * product[i + Limbs];

            for (int i = 0; i < Limbs; i++) o[oi + i] = product[i];
            Carry(o, oi);
            Carry(o, oi);
        }

        /// <summary>The inverse, as a^(p-2), by the standard square-and-multiply chain.</summary>
        private static void Invert(long[] o, int oi, long[] input, int ii)
        {
            var c = new long[Limbs];
            for (int i = 0; i < Limbs; i++) c[i] = input[ii + i];

            for (int exponent = 253; exponent >= 0; exponent--)
            {
                Square(c, c);
                if (exponent != 2 && exponent != 4) Mul(c, 0, c, 0, input, ii);
            }

            for (int i = 0; i < Limbs; i++) o[oi + i] = c[i];
        }

        private static void Unpack(long[] o, int oi, byte[] bytes)
        {
            for (int i = 0; i < Limbs; i++)
            {
                o[oi + i] = bytes[2 * i] + ((long)bytes[2 * i + 1] << 8);
            }

            o[oi + 15] &= 0x7fff;
        }

        /// <summary>
        /// Reduce modulo 2^255 - 19 and write the 32 bytes little-endian. The two
        /// subtractions are what turns a value that is "almost reduced" into the
        /// canonical representative.
        /// </summary>
        private static byte[] Pack(long[] n, int ni)
        {
            var t = new long[Limbs];
            for (int i = 0; i < Limbs; i++) t[i] = n[ni + i];

            Carry(t, 0);
            Carry(t, 0);
            Carry(t, 0);

            var m = new long[Limbs];
            for (int round = 0; round < 2; round++)
            {
                m[0] = t[0] - 0xffed;
                for (int i = 1; i < Limbs - 1; i++)
                {
                    m[i] = t[i] - 0xffff - ((m[i - 1] >> 16) & 1);
                    m[i - 1] &= 0xffff;
                }

                m[Limbs - 1] = t[Limbs - 1] - 0x7fff - ((m[Limbs - 2] >> 16) & 1);
                long borrow = (m[Limbs - 1] >> 16) & 1;
                m[Limbs - 2] &= 0xffff;

                SelectSwap(t, m, (int)(1 - borrow));
            }

            var output = new byte[KeyLength];
            for (int i = 0; i < Limbs; i++)
            {
                output[2 * i] = (byte)(t[i] & 0xff);
                output[2 * i + 1] = (byte)((t[i] >> 8) & 0xff);
            }

            return output;
        }
    }
}
