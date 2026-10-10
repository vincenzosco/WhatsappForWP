using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// Ed25519 as RFC 8032 defines it: a 32-byte seed, a signature over a message, and
    /// the verification of that signature.
    ///
    /// This is the plain scheme, and it is here for two reasons. Its vectors are
    /// published, so the field arithmetic, the group law, the scalar reduction and the
    /// hash are all checked against something other than themselves - and it is the
    /// subroutine <see cref="XEdDsa"/> verifies WhatsApp's certificates with, which is
    /// the variant Signal actually uses.
    /// </summary>
    public static class Ed25519
    {
        public const int SeedLength = 32;
        public const int PublicKeyLength = 32;
        public const int SignatureLength = 64;

        public static byte[] PublicKey(byte[] seed)
        {
            byte[] scalar = ExpandedScalar(seed);
            return Edwards25519.ScalarMultiplyBase(scalar).Encode();
        }

        /// <summary>
        /// Sign, per RFC 8032 section 5.1.6: `r` and `S` both come from the seed's
        /// SHA-512 expansion, so a signature is deterministic.
        /// </summary>
        public static byte[] Sign(byte[] seed, byte[] message)
        {
            byte[] expanded = Expanded(seed);
            byte[] scalar = Scalar25519.Reduce(Slice(expanded, 0, 32));
            byte[] prefix = Slice(expanded, 32, 32);

            byte[] publicKey = Edwards25519.ScalarMultiplyBase(scalar).Encode();

            byte[] nonce = Scalar25519.Reduce(Sha512.HashConcat(prefix, message));
            byte[] encodedR = Edwards25519.ScalarMultiplyBase(nonce).Encode();

            byte[] challenge = Scalar25519.Reduce(Sha512.HashConcat(encodedR, publicKey, message));
            byte[] s = Scalar25519.MultiplyAdd(challenge, scalar, nonce);

            var signature = new byte[SignatureLength];
            Buffer.BlockCopy(encodedR, 0, signature, 0, 32);
            Buffer.BlockCopy(s, 0, signature, 32, 32);
            return signature;
        }

        /// <summary>
        /// Verify, per RFC 8032 section 5.1.7: `S` must be below `l` (a signature with a
        /// larger `S` is not canonical and is refused rather than reduced), the public key
        /// must be a point on the curve, and `[S]B - [h]A` must encode to the `R` in the
        /// signature.
        /// </summary>
        public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != PublicKeyLength) return false;
            if (signature == null || signature.Length != SignatureLength) return false;

            byte[] s = Slice(signature, 32, 32);
            if (!Scalar25519.IsCanonical(s)) return false;

            Edwards25519 a;
            try
            {
                a = Edwards25519.Decode(publicKey);
            }
            catch (ProtocolException)
            {
                return false;
            }

            byte[] r = Slice(signature, 0, 32);
            byte[] challenge = Scalar25519.Reduce(Sha512.HashConcat(r, publicKey, message));

            Edwards25519 left = Edwards25519.ScalarMultiplyBase(s);
            Edwards25519 right = Edwards25519.ScalarMultiply(a.Negate(), challenge);
            byte[] check = left.Add(right).Encode();

            return SameBytes(check, r);
        }

        /// <summary>
        /// The clamped scalar and the hash prefix, which is the private key's SHA-512
        /// expansion: the first half is the scalar (with its bottom three bits cleared,
        /// its top bit cleared and bit 254 set), the second half is the signing prefix.
        /// </summary>
        internal static byte[] Expanded(byte[] seed)
        {
            if (seed == null || seed.Length != SeedLength)
            {
                throw new ProtocolException("an Ed25519 seed is 32 bytes");
            }

            byte[] expanded = Sha512.Hash(seed);

            expanded[0] &= 248;
            expanded[31] &= 63;
            expanded[31] |= 64;

            return expanded;
        }

        internal static byte[] ExpandedScalar(byte[] seed)
        {
            byte[] expanded = Expanded(seed);
            return Scalar25519.Reduce(Slice(expanded, 0, 32));
        }

        internal static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(source, offset, slice, 0, count);
            return slice;
        }

        internal static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }
    }
}
