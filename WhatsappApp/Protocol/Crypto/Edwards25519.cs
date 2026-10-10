using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// The group of the twisted Edwards curve `-x^2 + y^2 = 1 + d x^2 y^2` over the field
    /// of <see cref="Field25519"/>, which is where Ed25519 lives. Points are held in
    /// extended coordinates `(X : Y : Z : T)` with `x = X/Z`, `y = Y/Z`, `T = XY/Z` - the
    /// form whose addition formula is complete (no special case for the identity or for
    /// a point added to itself).
    ///
    /// Two conventions matter and are easy to get wrong silently:
    ///
    /// - The **encoding** is the little-endian y-coordinate with the sign of x in the top
    ///   bit of byte 31. Decompression recovers x from the curve equation and picks the
    ///   root whose sign matches.
    /// - The **scalar** is used as given. Ed25519's order is `l`, so `[k]B = [k mod l]B`,
    ///   and `XEdDsa` relies on exactly that: it signs with an X25519 private key that was
    ///   never reduced modulo `l`.
    ///
    /// The scalar multiplication walks four bits at a time over a table of the first
    /// sixteen multiples of the point and picks with `Cmov`, so the table index does not
    /// branch on the scalar. It is not a constant-time claim for the whole routine (the
    /// four doublings per window are shared, and the field code is ordinary managed
    /// arithmetic), and the code says so rather than implying more than it delivers.
    /// </summary>
    public sealed class Edwards25519
    {
        private readonly Field25519 _x;
        private readonly Field25519 _y;
        private readonly Field25519 _z;
        private readonly Field25519 _t;

        private static readonly Field25519 D = ComputeD();

        private Edwards25519(Field25519 x, Field25519 y, Field25519 z, Field25519 t)
        {
            _x = x;
            _y = y;
            _z = z;
            _t = t;
        }

        /// <summary>The neutral element, `(0 : 1 : 1 : 0)`.</summary>
        public static Edwards25519 Identity()
        {
            return new Edwards25519(Field25519.Zero(), Field25519.One(), Field25519.One(), Field25519.Zero());
        }

        /// <summary>
        /// The generator of the prime-order subgroup, decoded from its published
        /// encoding rather than assembled from coordinates: decoding it is one more use
        /// of the same code that reads a public key.
        /// </summary>
        public static Edwards25519 BasePoint()
        {
            return Decode(Hex("5866666666666666666666666666666666666666666666666666666666666666"));
        }

        public static Edwards25519 Decode(byte[] encoded)
        {
            if (encoded == null || encoded.Length != 32)
            {
                throw new ProtocolException("an Ed25519 point is 32 bytes");
            }

            bool signBit = (encoded[31] & 0x80) != 0;
            Field25519 y = Field25519.FromBytes(encoded);

            Field25519 ySquared = y.Square();
            Field25519 numerator = ySquared.Sub(Field25519.One());
            Field25519 denominator = D.Mul(ySquared).Add(Field25519.One());
            Field25519 xSquared = numerator.Mul(denominator.Invert());

            Field25519 x = Field25519.TrySqrt(xSquared);
            if (x == null) throw new ProtocolException("these bytes are not a point on the Ed25519 curve");

            if (x.IsNegative() != signBit) x = x.Negate();
            if (x.IsZero() && signBit) throw new ProtocolException("the point's x is zero but the sign bit is set");

            return new Edwards25519(x, y, Field25519.One(), x.Mul(y));
        }

        public byte[] Encode()
        {
            Field25519 inverseZ = _z.Invert();
            Field25519 x = _x.Mul(inverseZ);
            Field25519 y = _y.Mul(inverseZ);

            byte[] encoded = y.ToBytes();
            if (x.IsNegative()) encoded[31] |= 0x80;

            return encoded;
        }

        /// <summary>
        /// Addition on the curve with `a = -1`: the formula from "Twisted Edwards Curves
        /// Revisited", section 5.1, which works for any pair of points including the
        /// identity and equal points.
        /// </summary>
        public Edwards25519 Add(Edwards25519 other)
        {
            Field25519 a = _y.Sub(_x).Mul(other._y.Sub(other._x));
            Field25519 b = _y.Add(_x).Mul(other._y.Add(other._x));
            Field25519 c = _t.Mul(D).Mul(Field25519.FromSmall(2)).Mul(other._t);
            Field25519 d = _z.Mul(Field25519.FromSmall(2)).Mul(other._z);

            Field25519 e = b.Sub(a);
            Field25519 f = d.Sub(c);
            Field25519 g = d.Add(c);
            Field25519 h = b.Add(a);

            return new Edwards25519(e.Mul(f), g.Mul(h), f.Mul(g), e.Mul(h));
        }

        /// <summary>Doubling, same paper, section 5.1's doubling formula.</summary>
        public Edwards25519 Double()
        {
            Field25519 a = _x.Square();
            Field25519 b = _y.Square();
            Field25519 c = _z.Square().Mul(Field25519.FromSmall(2));
            Field25519 d = a.Negate();
            Field25519 e = _x.Add(_y).Square().Sub(a).Sub(b);
            Field25519 g = d.Add(b);
            Field25519 f = g.Sub(c);
            Field25519 h = d.Sub(b);

            return new Edwards25519(e.Mul(f), g.Mul(h), f.Mul(g), e.Mul(h));
        }

        /// <summary>`-P`, which is `(-X : Y : Z : -T)` on this curve.</summary>
        public Edwards25519 Negate()
        {
            return new Edwards25519(_x.Negate(), _y, _z, _t.Negate());
        }

        /// <summary>Copies `other` when `take` is true, for the table lookup.</summary>
        public Edwards25519 Cmov(Edwards25519 other, bool take)
        {
            return new Edwards25519(
                _x.Cmov(other._x, take),
                _y.Cmov(other._y, take),
                _z.Cmov(other._z, take),
                _t.Cmov(other._t, take));
        }

        public static Edwards25519 ScalarMultiply(Edwards25519 point, byte[] scalar)
        {
            if (scalar == null || scalar.Length != 32)
            {
                throw new ProtocolException("an Ed25519 scalar is 32 bytes");
            }

            var table = new Edwards25519[16];
            table[0] = Identity();

            for (int i = 1; i < 16; i++)
            {
                table[i] = table[i - 1].Add(point);
            }

            Edwards25519 result = Identity();

            for (int nibble = 63; nibble >= 0; nibble--)
            {
                result = result.Double();
                result = result.Double();
                result = result.Double();
                result = result.Double();

                int index = (scalar[nibble >> 1] >> ((nibble & 1) * 4)) & 0x0f;

                Edwards25519 selected = table[0];
                for (int i = 1; i < 16; i++) selected = selected.Cmov(table[i], i == index);

                result = result.Add(selected);
            }

            return result;
        }

        public static Edwards25519 ScalarMultiplyBase(byte[] scalar)
        {
            return ScalarMultiply(BasePoint(), scalar);
        }

        /// <summary>
        /// The birational map from a Curve25519 public key to an Ed25519 one:
        /// `y = (u - 1) / (u + 1)`. The result's sign bit is always zero - the sign the
        /// signature carries is put back by <see cref="XEdDsa"/>, not here.
        /// </summary>
        public static byte[] MontgomeryToEdwards(byte[] montgomeryPublicKey)
        {
            Field25519 u = Field25519.FromBytes(montgomeryPublicKey);
            Field25519 y = u.Sub(Field25519.One()).Mul(u.Add(Field25519.One()).Invert());

            byte[] encoded = y.ToBytes();
            encoded[31] &= 0x7f;
            return encoded;
        }

        /// <summary>
        /// Whether the point satisfies the curve equation, for the tests. In projective
        /// coordinates the equation is `-X^2 Z^2 + Y^2 Z^2 = Z^4 + d X^2 Y^2`, which is
        /// the affine one multiplied by `Z^4`.
        /// </summary>
        public bool IsOnCurve()
        {
            Field25519 zSquared = _z.Square();
            Field25519 left = _y.Square().Mul(zSquared).Sub(_x.Square().Mul(zSquared));
            Field25519 right = zSquared.Square().Add(D.Mul(_x.Square()).Mul(_y.Square()));

            return left.IsEqual(right);
        }

        /// <summary>`d = -121665 / 121666`, computed rather than transcribed.</summary>
        private static Field25519 ComputeD()
        {
            Field25519 numerator = Field25519.FromSmall(121665).Negate();
            Field25519 denominator = Field25519.FromSmall(121666).Invert();

            return numerator.Mul(denominator);
        }

        private static byte[] Hex(string text)
        {
            var bytes = new byte[text.Length / 2];

            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(text.Substring(2 * i, 2), 16);
            }

            return bytes;
        }
    }
}
