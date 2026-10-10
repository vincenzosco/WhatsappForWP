using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// Arithmetic modulo `l = 2^252 + 27742317777372353535851937790883648493`, the order
    /// of the Ed25519 base point. A signature's `S` is a number in this range, and a hash
    /// that is being turned into a scalar is reduced into it.
    ///
    /// The reduction is a bitwise division - shift the remainder left one bit at a time
    /// and subtract `l` whenever it fits - rather than the reference's 21-bit limb
    /// arithmetic. It runs five hundred times over nine limbs, which is nothing next to
    /// the scalar multiplication beside it, and it can be read and believed.
    /// </summary>
    public static class Scalar25519
    {
        public const int ByteLength = 32;

        /// <summary>`l`, little-endian, eight 32-bit limbs.</summary>
        private static readonly uint[] Order = new uint[]
        {
            0x5cf5d3ed, 0x5812631a, 0xa2f79cd6, 0x14def9de,
            0x00000000, 0x00000000, 0x00000000, 0x10000000
        };

        public static uint[] ToLimbs(byte[] bytes)
        {
            if (bytes == null || bytes.Length != ByteLength)
            {
                throw new ProtocolException("a scalar is 32 bytes");
            }

            var limbs = new uint[Order.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                limbs[i >> 2] |= (uint)bytes[i] << (8 * (i & 3));
            }

            return limbs;
        }

        public static byte[] ToBytes(uint[] limbs)
        {
            var bytes = new byte[ByteLength];
            for (int i = 0; i < ByteLength; i++)
            {
                bytes[i] = (byte)(limbs[i >> 2] >> (8 * (i & 3)));
            }

            return bytes;
        }

        /// <summary>Reduces a little-endian number of any length below 2^512 modulo `l`.</summary>
        public static byte[] Reduce(byte[] wide)
        {
            if (wide == null || wide.Length == 0 || wide.Length > 64)
            {
                throw new ProtocolException("a value to reduce is 1 to 64 bytes, not "
                    + (wide == null ? "null" : wide.Length.ToString()));
            }

            int limbCount = (wide.Length + 3) / 4;
            var limbs = new uint[limbCount + 1];

            for (int i = 0; i < wide.Length; i++)
            {
                limbs[i >> 2] |= (uint)wide[i] << (8 * (i & 3));
            }

            ReduceInPlace(limbs);
            return ToBytes(limbs);
        }

        /// <summary>`(a * b + c) mod l`, which is the last step of every signature.</summary>
        public static byte[] MultiplyAdd(byte[] a, byte[] b, byte[] c)
        {
            uint[] left = ToLimbs(a);
            uint[] right = ToLimbs(b);
            uint[] addend = ToLimbs(c);

            var product = new uint[2 * Order.Length + 1];

            for (int i = 0; i < Order.Length; i++)
            {
                ulong carry = 0;

                for (int j = 0; j < Order.Length; j++)
                {
                    ulong term = (ulong)left[i] * right[j] + product[i + j] + carry;
                    product[i + j] = (uint)term;
                    carry = term >> 32;
                }

                for (int k = i + Order.Length; carry != 0 && k < product.Length; k++)
                {
                    ulong sum = (ulong)product[k] + carry;
                    product[k] = (uint)sum;
                    carry = sum >> 32;
                }

                if (carry != 0) throw new ProtocolException("a scalar multiplication overflowed its limbs");
            }

            ulong addCarry = 0;
            for (int i = 0; i < Order.Length; i++)
            {
                ulong sum = (ulong)product[i] + addend[i] + addCarry;
                product[i] = (uint)sum;
                addCarry = sum >> 32;
            }

            for (int k = Order.Length; addCarry != 0 && k < product.Length; k++)
            {
                ulong sum = (ulong)product[k] + addCarry;
                product[k] = (uint)sum;
                addCarry = sum >> 32;
            }

            ReduceInPlace(product);
            return ToBytes(product);
        }

        /// <summary>Whether a scalar is already below `l`, which a signature's `S` must be.</summary>
        public static bool IsCanonical(byte[] scalar)
        {
            uint[] limbs = ToLimbs(scalar);
            return !AtLeastOrder(limbs);
        }

        /// <summary>
        /// Replaces the value in `limbs` with its remainder modulo `l`. Every limb above
        /// the eighth is left at zero, so the caller can read the answer from the front.
        /// </summary>
        private static void ReduceInPlace(uint[] limbs)
        {
            var remainder = new uint[Order.Length + 1];

            for (int bit = limbs.Length * 32 - 1; bit >= 0; bit--)
            {
                uint carry = (limbs[bit >> 5] >> (bit & 31)) & 1u;

                for (int i = 0; i < remainder.Length; i++)
                {
                    uint next = remainder[i] >> 31;
                    remainder[i] = (remainder[i] << 1) | carry;
                    carry = next;
                }

                if (AtLeastOrder(remainder)) SubtractOrder(remainder);
            }

            for (int i = 0; i < limbs.Length; i++)
            {
                limbs[i] = i < Order.Length ? remainder[i] : 0u;
            }
        }

        private static bool AtLeastOrder(uint[] value)
        {
            for (int i = value.Length - 1; i >= Order.Length; i--)
            {
                if (value[i] != 0) return true;
            }

            for (int i = Order.Length - 1; i >= 0; i--)
            {
                if (value[i] > Order[i]) return true;
                if (value[i] < Order[i]) return false;
            }

            return true;
        }

        private static void SubtractOrder(uint[] value)
        {
            long borrow = 0;

            for (int i = 0; i < value.Length; i++)
            {
                // Widen before subtracting: a uint subtraction wraps to a huge positive
                // number, which would hide the borrow entirely (see Field25519).
                long difference = (long)value[i] - (i < Order.Length ? (long)Order[i] : 0L) - borrow;

                if (difference < 0)
                {
                    difference += 0x100000000L;
                    borrow = 1;
                }
                else
                {
                    borrow = 0;
                }

                value[i] = (uint)difference;
            }
        }
    }
}
