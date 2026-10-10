using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// Arithmetic in the field of integers modulo `p = 2^255 - 19`, which is what
    /// Ed25519 - and so XEdDSA, and so WhatsApp's certificate signatures - is built on.
    ///
    /// The representation is eight 32-bit little-endian limbs holding a value that is
    /// always **fully reduced** (below `p`). Every operation therefore ends in a
    /// reduction, every value is canonical, and two field elements are equal exactly
    /// when their limbs are: no "carries may be loose" invariant for a reader to hold in
    /// their head.
    ///
    /// This is deliberately **not** a transcription of the reference's `fe_*` code.
    /// That code keeps values in 25.5-bit limbs with bounded but unreduced carries and
    /// is checked by its authors' reasoning, not by reading; a line-for-line port that
    /// is subtly wrong produces keys and signatures that verify nowhere and passes every
    /// round-trip test. What is here is slower by microseconds on the handful of
    /// signatures and verifications a handshake and a pre-key upload do, and it can be
    /// checked by reading it next to the textbook formulas:
    ///
    ///     a + b   : add, then subtract p while the sum is at least p
    ///     a - b   : subtract, and add p back when the result went negative
    ///     a * b   : schoolbook into 16 limbs, then fold `2^256 = 38 (mod p)`
    ///               until nothing above the eighth limb is left
    ///     a^-1    : a^(p - 2), the Fermat inverse
    ///
    /// The whole file is exercised against RFC 8032's vectors through
    /// <see cref="Ed25519"/>; that is the test that matters, because it is a check on
    /// every line here at once.
    /// </summary>
    public sealed class Field25519
    {
        /// <summary>Every field element and every coordinate is 32 bytes.</summary>
        public const int ByteLength = 32;

        private const int Limbs = 8;

        /// <summary>One limb of room for a carry that a sum or a fold produces.</summary>
        private const int WorkLimbs = Limbs + 1;

        /// <summary>`p` itself, and the reference for every reduction.</summary>
        private static readonly uint[] Prime = new uint[]
        {
            0xffffffed, 0xffffffff, 0xffffffff, 0xffffffff,
            0xffffffff, 0xffffffff, 0xffffffff, 0x7fffffff
        };

        /// <summary>`p - 2`, the exponent of the Fermat inverse, little-endian.</summary>
        private static readonly byte[] InverseExponent = HexBytes("eb" + Repeat("ff", 30) + "7f");

        /// <summary>`(p + 3) / 8`, the exponent whose square is the square root test.</summary>
        private static readonly byte[] SqrtExponent = HexBytes("fe" + Repeat("ff", 30) + "0f");

        /// <summary>
        /// A square root of -1 modulo p. Its existence is what lets a square root be
        /// recoverable at all in this field; the test suite checks that it squares to -1
        /// rather than trusting the constant.
        /// </summary>
        private static readonly Field25519 SqrtMinusOne = FromBytes(HexBytes(
            "b0a00e4a271beec478e42fad0618432fa7d7fb3d99004d2b0bdfc14f8024832b"));

        private readonly uint[] _limbs;

        private Field25519(uint[] limbs)
        {
            _limbs = limbs;
        }

        public static Field25519 Zero()
        {
            return new Field25519(new uint[Limbs]);
        }

        public static Field25519 One()
        {
            var limbs = new uint[Limbs];
            limbs[0] = 1;
            return new Field25519(limbs);
        }

        public static Field25519 FromSmall(uint value)
        {
            if (value >= 0xffffffed) throw new ProtocolException("this small value is not below p: " + value);

            var limbs = new uint[Limbs];
            limbs[0] = value;
            return new Field25519(limbs);
        }

        /// <summary>
        /// Reads 32 little-endian bytes, **ignoring the top bit**: in Ed25519's encoding
        /// that bit carries the sign of x and is not part of the number. Callers that
        /// need the sign read it themselves.
        /// </summary>
        public static Field25519 FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length != ByteLength)
            {
                throw new ProtocolException("a field element is 32 bytes, not "
                    + (bytes == null ? "null" : bytes.Length.ToString()));
            }

            var limbs = new uint[Limbs];
            for (int i = 0; i < Limbs; i++)
            {
                limbs[i] = (uint)(bytes[4 * i] | (bytes[4 * i + 1] << 8)
                    | (bytes[4 * i + 2] << 16) | (bytes[4 * i + 3] << 24));
            }

            limbs[Limbs - 1] &= 0x7fffffff;
            return new Field25519(limbs);
        }

        public byte[] ToBytes()
        {
            var bytes = new byte[ByteLength];
            for (int i = 0; i < Limbs; i++)
            {
                bytes[4 * i] = (byte)_limbs[i];
                bytes[4 * i + 1] = (byte)(_limbs[i] >> 8);
                bytes[4 * i + 2] = (byte)(_limbs[i] >> 16);
                bytes[4 * i + 3] = (byte)(_limbs[i] >> 24);
            }

            return bytes;
        }

        public Field25519 Add(Field25519 other)
        {
            var work = new uint[WorkLimbs];
            ulong carry = 0;

            for (int i = 0; i < Limbs; i++)
            {
                ulong sum = (ulong)_limbs[i] + other._limbs[i] + carry;
                work[i] = (uint)sum;
                carry = sum >> 32;
            }

            work[Limbs] = (uint)carry;
            return Reduce(work);
        }

        public Field25519 Sub(Field25519 other)
        {
            var limbs = new uint[Limbs];
            long borrow = 0;

            for (int i = 0; i < Limbs; i++)
            {
                long difference = (long)_limbs[i] - other._limbs[i] - borrow;

                if (difference < 0)
                {
                    difference += 0x100000000L;
                    borrow = 1;
                }
                else
                {
                    borrow = 0;
                }

                limbs[i] = (uint)difference;
            }

            if (borrow != 0)
            {
                // The difference is negative, so what is in `limbs` is `a - b + 2^256`.
                // Adding p turns it into `a - b + p`, and since a < b that is below p.
                ulong carry = 0;
                for (int i = 0; i < Limbs; i++)
                {
                    ulong sum = (ulong)limbs[i] + Prime[i] + carry;
                    limbs[i] = (uint)sum;
                    carry = sum >> 32;
                }
            }

            return new Field25519(limbs);
        }

        public Field25519 Negate()
        {
            return Zero().Sub(this);
        }

        public Field25519 Mul(Field25519 other)
        {
            var wide = new uint[2 * Limbs];

            for (int i = 0; i < Limbs; i++)
            {
                ulong carry = 0;

                for (int j = 0; j < Limbs; j++)
                {
                    ulong product = (ulong)_limbs[i] * other._limbs[j] + wide[i + j] + carry;
                    wide[i + j] = (uint)product;
                    carry = product >> 32;
                }

                for (int k = i + Limbs; carry != 0 && k < wide.Length; k++)
                {
                    ulong sum = (ulong)wide[k] + carry;
                    wide[k] = (uint)sum;
                    carry = sum >> 32;
                }

                if (carry != 0) throw new ProtocolException("a field multiplication overflowed its limbs");
            }

            return Fold(wide);
        }

        public Field25519 Square()
        {
            return Mul(this);
        }

        public Field25519 Invert()
        {
            return Power(InverseExponent);
        }

        public Field25519 Pow2523()
        {
            return Power(SqrtExponent);
        }

        /// <summary>
        /// A square root, or null when there is none. The test is `x^2 == a`, with
        /// `x * sqrt(-1)` tried second - the two cases Ed25519's decompression needs.
        /// </summary>
        public static Field25519 TrySqrt(Field25519 value)
        {
            Field25519 candidate = value.Pow2523();

            if (candidate.Square().IsEqual(value)) return candidate;
            if (candidate.Square().IsEqual(value.Negate())) return candidate.Mul(SqrtMinusOne);

            return null;
        }

        public static Field25519 SqrtMinusOneValue()
        {
            return SqrtMinusOne;
        }

        /// <summary>The low bit, which Ed25519 uses as the sign of x.</summary>
        public bool IsNegative()
        {
            return (_limbs[0] & 1) != 0;
        }

        public bool IsZero()
        {
            for (int i = 0; i < Limbs; i++)
            {
                if (_limbs[i] != 0) return false;
            }

            return true;
        }

        public bool IsEqual(Field25519 other)
        {
            for (int i = 0; i < Limbs; i++)
            {
                if (_limbs[i] != other._limbs[i]) return false;
            }

            return true;
        }

        /// <summary>Copies `other` into this one when `take` is true, without branching.</summary>
        public Field25519 Cmov(Field25519 other, bool take)
        {
            uint mask = take ? 0xffffffffu : 0u;
            var limbs = new uint[Limbs];

            for (int i = 0; i < Limbs; i++)
            {
                limbs[i] = (_limbs[i] & ~mask) | (other._limbs[i] & mask);
            }

            return new Field25519(limbs);
        }

        private Field25519 Power(byte[] exponentLittleEndian)
        {
            Field25519 result = One();

            for (int bit = exponentLittleEndian.Length * 8 - 1; bit >= 0; bit--)
            {
                result = result.Square();
                if (((exponentLittleEndian[bit >> 3] >> (bit & 7)) & 1) != 0) result = result.Mul(this);
            }

            return result;
        }

        /// <summary>
        /// Folds a 512-bit product down: `2^256 = 2p + 38`, so the value above the eighth
        /// limb can be multiplied by 38 and added back. Two or three passes are enough
        /// because each one shrinks the part that is left above the fold enormously.
        /// </summary>
        private static Field25519 Fold(uint[] wide)
        {
            var current = new uint[2 * Limbs];
            Buffer.BlockCopy(wide, 0, current, 0, wide.Length * 4);

            for (int pass = 0; pass < 4; pass++)
            {
                var next = new uint[current.Length];
                ulong carry = 0;

                for (int i = 0; i < Limbs; i++)
                {
                    ulong term = (ulong)current[i + Limbs] * 38UL + current[i] + carry;
                    next[i] = (uint)term;
                    carry = term >> 32;
                }

                next[Limbs] = (uint)carry;
                next[Limbs + 1] = (uint)(carry >> 32);

                bool folded = true;
                for (int i = Limbs; i < next.Length; i++)
                {
                    if (next[i] != 0)
                    {
                        folded = false;
                        break;
                    }
                }

                current = next;
                if (folded) break;
            }

            var work = new uint[WorkLimbs];
            Buffer.BlockCopy(current, 0, work, 0, WorkLimbs * 4);
            return Reduce(work);
        }

        /// <summary>Brings a value that is at most a few multiples of p below p.</summary>
        private static Field25519 Reduce(uint[] work)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (!AtLeastPrime(work)) break;
                SubtractPrime(work);
            }

            var limbs = new uint[Limbs];
            Buffer.BlockCopy(work, 0, limbs, 0, Limbs * 4);
            return new Field25519(limbs);
        }

        private static bool AtLeastPrime(uint[] work)
        {
            for (int i = work.Length - 1; i >= Limbs; i--)
            {
                if (work[i] != 0) return true;
            }

            for (int i = Limbs - 1; i >= 0; i--)
            {
                if (work[i] > Prime[i]) return true;
                if (work[i] < Prime[i]) return false;
            }

            return true;
        }

        private static void SubtractPrime(uint[] work)
        {
            long borrow = 0;

            for (int i = 0; i < work.Length; i++)
            {
                // The cast to long has to be on the first operand: `work[i] - Prime[i]`
                // is a subtraction of two uints, and it wraps to a huge *positive*
                // number before `borrow` widens it, so the borrow would never be seen.
                long value = (long)work[i] - (i < Limbs ? (long)Prime[i] : 0L) - borrow;

                if (value < 0)
                {
                    value += 0x100000000L;
                    borrow = 1;
                }
                else
                {
                    borrow = 0;
                }

                work[i] = (uint)value;
            }
        }

        private static string Repeat(string text, int times)
        {
            var builder = new System.Text.StringBuilder();

            for (int i = 0; i < times; i++) builder.Append(text);
            return builder.ToString();
        }

        private static byte[] HexBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];

            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            }

            return bytes;
        }
    }
}
