using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// P-256 and ECDSA-SHA256, for verifying signatures only.
    ///
    /// Hand-written because WP8.1 has no `System.Numerics` we can rely on: eight 32-bit limbs,
    /// reduced by binary long division. Not constant time and not a signer - it only checks a
    /// peer's signature, and trust in the certificate stays the caller's decision.
    /// </summary>
    public static class EcP256
    {
        public const int CoordinateLength = 32;
        public const int PointLength = 65;

        private const int Limbs = 8;

        /// <summary>p = 2^256 - 2^224 + 2^192 + 2^96 - 1.</summary>
        private static readonly uint[] P = FromHex("ffffffff00000001000000000000000000000000ffffffffffffffffffffffff");

        /// <summary>The order of the base point.</summary>
        private static readonly uint[] N = FromHex("ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551");

        private static readonly uint[] Gx = FromHex("6b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c296");
        private static readonly uint[] Gy = FromHex("4fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");

        /// <summary>The curve's b, for the on-curve test.</summary>
        private static readonly uint[] CurveB = FromHex("5ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b");

        /// <summary>
        /// Verifies `signature` over `message` with the key in `publicPoint` (65 bytes,
        /// 0x04 || X || Y). A bad point, a bad encoding or r/s out of range answer false.
        /// </summary>
        public static bool Verify(byte[] message, byte[] signature, byte[] publicPoint)
        {
            if (message == null || signature == null || publicPoint == null) return false;
            if (!IsOnCurve(publicPoint)) return false;

            byte[] r;
            byte[] s;
            if (!ParseSignature(signature, out r, out s)) return false;

            uint[] rn = FromBytes(r, 0);
            uint[] sn = FromBytes(s, 0);

            if (IsZero(rn) || Compare(rn, N) >= 0) return false;
            if (IsZero(sn) || Compare(sn, N) >= 0) return false;

            uint[] e = FromBytes(Sha256.Hash(message), 0);

            uint[] w = Inverse(sn, N);
            uint[] u1 = ModMul(e, w, N);
            uint[] u2 = ModMul(rn, w, N);

            var generator = new Point(Gx, Gy, One());
            var publicKey = new Point(FromBytes(publicPoint, 1), FromBytes(publicPoint, 33), One());

            Point sum = Add(Multiply(u1, generator), Multiply(u2, publicKey));
            if (IsInfinity(sum)) return false;

            uint[] x = AffineX(sum);

            // x mod n: x is below p, and n is below p, so one subtraction is enough.
            uint[] xr = Compare(x, N) >= 0 ? Sub(x, N) : x;

            return Compare(xr, rn) == 0;
        }

        /// <summary>Whether the 65 bytes are an uncompressed point on the curve.</summary>
        public static bool IsOnCurve(byte[] point)
        {
            if (point == null || point.Length != PointLength || point[0] != 0x04) return false;

            uint[] x = FromBytes(point, 1);
            uint[] y = FromBytes(point, 33);

            if (Compare(x, P) >= 0 || Compare(y, P) >= 0) return false;

            // y^2 == x^3 - 3x + b
            uint[] left = ModMul(y, y, P);
            uint[] x2 = ModMul(x, x, P);
            uint[] right = ModMul(x2, x, P);
            right = ModSub(right, ModMul(Three(), x, P), P);
            right = ModAdd(right, CurveB, P);

            return Compare(left, right) == 0;
        }

        // ---- the point ------------------------------------------------------------------

        private sealed class Point
        {
            public readonly uint[] X;
            public readonly uint[] Y;
            public readonly uint[] Z;

            public Point(uint[] x, uint[] y, uint[] z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        private static bool IsInfinity(Point point)
        {
            return IsZero(point.Z);
        }

        /// <summary>Jacobian doubling, with a = -3 folded into 3(X-Z^2)(X+Z^2).</summary>
        private static Point Double(Point point)
        {
            if (IsInfinity(point) || IsZero(point.Y)) return Infinity();

            uint[] yy = ModMul(point.Y, point.Y, P);
            uint[] s = ModMul(Four(), ModMul(point.X, yy, P), P);
            uint[] zz = ModMul(point.Z, point.Z, P);

            uint[] m = ModMul(Three(), ModSub(ModMul(point.X, point.X, P), ModMul(zz, zz, P), P), P);
            // X3 = M^2 - 2S: one doubling of S, and only one.
            uint[] x3 = ModSub(ModMul(m, m, P), ModAdd(s, s, P), P);
            uint[] y3 = ModSub(ModMul(m, ModSub(s, x3, P), P), ModMul(Eight(), ModMul(yy, yy, P), P), P);
            uint[] yz = ModMul(point.Y, point.Z, P);
            uint[] z3 = ModAdd(yz, yz, P);

            return new Point(x3, y3, z3);
        }

        private static Point Add(Point left, Point right)
        {
            if (IsInfinity(left)) return right;
            if (IsInfinity(right)) return left;

            uint[] z1z1 = ModMul(left.Z, left.Z, P);
            uint[] z2z2 = ModMul(right.Z, right.Z, P);

            uint[] u1 = ModMul(left.X, z2z2, P);
            uint[] u2 = ModMul(right.X, z1z1, P);

            uint[] s1 = ModMul(left.Y, ModMul(right.Z, z2z2, P), P);
            uint[] s2 = ModMul(right.Y, ModMul(left.Z, z1z1, P), P);

            if (Compare(u1, u2) == 0)
            {
                return Compare(s1, s2) == 0 ? Double(left) : Infinity();
            }

            uint[] h = ModSub(u2, u1, P);
            uint[] r = ModSub(s2, s1, P);
            uint[] hh = ModMul(h, h, P);
            uint[] hhh = ModMul(hh, h, P);
            uint[] v = ModMul(u1, hh, P);

            // X3 = R^2 - H^3 - 2*U1*H^2, Y3 = R*(U1*H^2 - X3) - S1*H^3, Z3 = H*Z1*Z2.
            uint[] x3 = ModSub(ModSub(ModMul(r, r, P), hhh, P), ModAdd(v, v, P), P);
            uint[] y3 = ModSub(ModMul(r, ModSub(v, x3, P), P), ModMul(s1, hhh, P), P);
            uint[] z3 = ModMul(ModMul(h, left.Z, P), right.Z, P);

            return new Point(x3, y3, z3);
        }

        private static Point Multiply(uint[] scalar, Point point)
        {
            Point result = Infinity();

            for (int bit = 255; bit >= 0; bit--)
            {
                result = Double(result);
                if (((scalar[bit >> 5] >> (bit & 31)) & 1u) != 0) result = Add(result, point);
            }

            return result;
        }

        /// <summary>The affine x of a point: X / Z^2. The caller has ruled out infinity.</summary>
        private static uint[] AffineX(Point point)
        {
            uint[] zinv = Inverse(point.Z, P);
            uint[] zinv2 = ModMul(zinv, zinv, P);
            return ModMul(point.X, zinv2, P);
        }

        private static Point Infinity()
        {
            return new Point(new uint[Limbs], new uint[Limbs], new uint[Limbs]);
        }

        private static uint[] One()
        {
            var one = new uint[Limbs];
            one[0] = 1;
            return one;
        }

        private static uint[] Three()
        {
            var three = new uint[Limbs];
            three[0] = 3;
            return three;
        }

        private static uint[] Four()
        {
            var four = new uint[Limbs];
            four[0] = 4;
            return four;
        }

        private static uint[] Eight()
        {
            var eight = new uint[Limbs];
            eight[0] = 8;
            return eight;
        }

        // ---- the field ------------------------------------------------------------------

        /// <summary>The multiplicative inverse, by Fermat: a^(m-2) with m prime.</summary>
        private static uint[] Inverse(uint[] value, uint[] modulus)
        {
            uint[] exponent = Sub(modulus, Two());
            return ModPow(value, exponent, modulus);
        }

        private static uint[] Two()
        {
            var two = new uint[Limbs];
            two[0] = 2;
            return two;
        }

        private static uint[] ModPow(uint[] value, uint[] exponent, uint[] modulus)
        {
            uint[] result = One();
            uint[] square = value;

            for (int bit = 255; bit >= 0; bit--)
            {
                result = ModMul(result, result, modulus);
                if (((exponent[bit >> 5] >> (bit & 31)) & 1u) != 0) result = ModMul(result, square, modulus);
            }

            return result;
        }

        private static uint[] ModMul(uint[] left, uint[] right, uint[] modulus)
        {
            return Reduce(Mul(left, right), modulus);
        }

        private static uint[] ModAdd(uint[] left, uint[] right, uint[] modulus)
        {
            var sum = new uint[Limbs];
            ulong carry = 0;

            for (int i = 0; i < Limbs; i++)
            {
                ulong t = (ulong)left[i] + right[i] + carry;
                sum[i] = (uint)t;
                carry = t >> 32;
            }

            // The carry is the 257th bit: dropping it is safe only because the subtraction
            // below puts it back, as a wrap of the same 256-bit arithmetic.
            if (carry != 0 || Compare(sum, modulus) >= 0) return Sub(sum, modulus);
            return sum;
        }

        private static uint[] ModSub(uint[] left, uint[] right, uint[] modulus)
        {
            if (Compare(left, right) >= 0) return Sub(left, right);

            // left + modulus - right, in nine limbs: the sum needs the extra one because
            // left < modulus means the result is below the modulus and only the intermediate
            // value can exceed 256 bits.
            var wide = new uint[Limbs + 1];
            ulong carry = 0;

            for (int i = 0; i < Limbs; i++)
            {
                ulong t = (ulong)left[i] + modulus[i] + carry;
                wide[i] = (uint)t;
                carry = t >> 32;
            }

            wide[Limbs] = (uint)carry;

            long borrow = 0;
            for (int i = 0; i < wide.Length; i++)
            {
                long current = (long)wide[i] - (i < Limbs ? right[i] : 0) - borrow;
                wide[i] = (uint)(current & 0xffffffffL);
                borrow = current < 0 ? 1 : 0;
            }

            var result = new uint[Limbs];
            Array.Copy(wide, result, Limbs);
            return result;
        }

        /// <summary>The product as sixteen limbs, schoolbook, carry handled in 64 bits.</summary>
        private static uint[] Mul(uint[] left, uint[] right)
        {
            var product = new uint[Limbs * 2 + 1];

            for (int i = 0; i < Limbs; i++)
            {
                ulong carry = 0;
                for (int j = 0; j < Limbs; j++)
                {
                    ulong t = (ulong)left[i] * right[j] + product[i + j] + carry;
                    product[i + j] = (uint)t;
                    carry = t >> 32;
                }

                int at = i + Limbs;
                ulong last = (ulong)product[at] + carry;
                product[at] = (uint)last;
                product[at + 1] += (uint)(last >> 32);
            }

            return product;
        }

        /// <summary>Reduces a 512-bit number by binary long division: slow, and easy to check.</summary>
        private static uint[] Reduce(uint[] wide, uint[] modulus)
        {
            var remainder = new uint[Limbs + 1];

            // The product is twice as wide as the modulus: every one of its 512 bits is
            // shifted in, or the reduction would keep only a fragment of the product.
            for (int bit = Limbs * 64 - 1; bit >= 0; bit--)
            {
                uint incoming = (wide[bit >> 5] >> (bit & 31)) & 1u;

                for (int i = 0; i < remainder.Length; i++)
                {
                    uint outgoing = remainder[i] >> 31;
                    remainder[i] = (remainder[i] << 1) | incoming;
                    incoming = outgoing;
                }

                if (CompareWide(remainder, modulus) >= 0) SubWide(remainder, modulus);
            }

            var result = new uint[Limbs];
            Array.Copy(remainder, result, Limbs);
            return result;
        }

        private static int CompareWide(uint[] wide, uint[] value)
        {
            for (int i = wide.Length - 1; i >= Limbs; i--)
            {
                if (wide[i] != 0) return 1;
            }

            return Compare(wide, value);
        }

        private static void SubWide(uint[] wide, uint[] value)
        {
            long borrow = 0;

            for (int i = 0; i < wide.Length; i++)
            {
                long current = (long)wide[i] - (i < Limbs ? value[i] : 0) - borrow;
                wide[i] = (uint)(current & 0xffffffffL);
                borrow = current < 0 ? 1 : 0;
            }
        }

        private static uint[] Sub(uint[] left, uint[] right)
        {
            var difference = new uint[Limbs];
            long borrow = 0;

            for (int i = 0; i < Limbs; i++)
            {
                long current = (long)left[i] - right[i] - borrow;
                difference[i] = (uint)(current & 0xffffffffL);
                borrow = current < 0 ? 1 : 0;
            }

            return difference;
        }

        private static int Compare(uint[] left, uint[] right)
        {
            for (int i = Limbs - 1; i >= 0; i--)
            {
                if (left[i] != right[i]) return left[i] > right[i] ? 1 : -1;
            }

            return 0;
        }

        private static bool IsZero(uint[] value)
        {
            for (int i = 0; i < Limbs; i++)
            {
                if (value[i] != 0) return false;
            }

            return true;
        }

        // ---- the encoding ---------------------------------------------------------------

        private static uint[] FromBytes(byte[] bytes, int offset)
        {
            var value = new uint[Limbs];

            for (int limb = 0; limb < Limbs; limb++)
            {
                int at = offset + CoordinateLength - 4 * (limb + 1);
                value[limb] = (uint)((bytes[at] << 24) | (bytes[at + 1] << 16)
                    | (bytes[at + 2] << 8) | bytes[at + 3]);
            }

            return value;
        }

        private static uint[] FromHex(string hex)
        {
            var bytes = new byte[CoordinateLength];
            for (int i = 0; i < CoordinateLength; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return FromBytes(bytes, 0);
        }

        /// <summary>The DER form of a signature: SEQUENCE { INTEGER r, INTEGER s }.</summary>
        private static bool ParseSignature(byte[] der, out byte[] r, out byte[] s)
        {
            r = null;
            s = null;

            if (der.Length < 8 || der[0] != 0x30) return false;

            int at = 1;
            int length = ReadLength(der, ref at);
            if (length <= 0 || at + length > der.Length) return false;

            int end = at + length;

            if (!ReadInteger(der, ref at, end, out r)) return false;
            if (!ReadInteger(der, ref at, end, out s)) return false;

            return true;
        }

        private static bool ReadInteger(byte[] der, ref int at, int end, out byte[] value)
        {
            value = null;

            if (at + 2 > end || der[at] != 0x02) return false;
            at++;

            int length = ReadLength(der, ref at);
            if (length <= 0 || at + length > end) return false;

            int start = at;
            int count = length;

            // A DER integer is signed, so a leading zero is padding and not part of the number.
            while (count > 1 && der[start] == 0)
            {
                start++;
                count--;
            }

            if (count > CoordinateLength) return false;

            value = new byte[CoordinateLength];
            Buffer.BlockCopy(der, start, value, CoordinateLength - count, count);

            at += length;
            return true;
        }

        /// <summary>The DER length at `at`, advancing past it; -1 when there is none.</summary>
        private static int ReadLength(byte[] der, ref int at)
        {
            if (at >= der.Length) return -1;

            int first = der[at++];
            if (first < 0x80) return first;

            int count = first & 0x7f;
            if (count == 0 || count > 3 || at + count > der.Length) return -1;

            int length = 0;
            for (int i = 0; i < count; i++) length = (length << 8) | der[at++];

            return length;
        }
    }
}
