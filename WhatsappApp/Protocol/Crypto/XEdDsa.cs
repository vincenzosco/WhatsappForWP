using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// XEdDSA: an Ed25519 signature made with a **Curve25519** key, which is the scheme
    /// every Signal client uses and therefore the one WhatsApp signs with.
    ///
    /// A Curve25519 public key is a Montgomery `u`, an Ed25519 public key is an encoded
    /// Edwards point, and the two are related by `y = (u - 1) / (u + 1)`. That map loses
    /// the sign of x, so the sign is carried in the top bit of the signature's `S` - a
    /// bit an Ed25519 signature never uses, because `S` is below `l < 2^253`.
    ///
    /// Signing also differs from plain Ed25519: the account's private key is an X25519
    /// scalar and is used as it is (no second hash expansion), and the nonce is derived
    /// with an explicit 32-byte domain prefix. Both are transcribed from
    /// `libsignal-protocol-c`'s `curve_sigs.c` and `sign_modified.c`, which is what the
    /// reference clients link:
    ///
    ///     nonce = SHA-512(0xFE || 0xFF * 31 || privateKey || message || random * 64)
    ///     R     = [nonce]B
    ///     hram  = SHA-512(R || publicKey || message)
    ///     S     = hram * privateKey + nonce        (mod l)
    ///     signature[63] |= sign bit of the Ed25519 public key
    ///
    /// The reference passes 64 random bytes; this passes 64 zero bytes. That is not a
    /// shortcut around anything the verifier checks - a different value of `random`
    /// changes `R` and `S` together and verification never looks at it - it removes a
    /// source of non-determinism, and the signature it produces verifies exactly like the
    /// randomised one. What it does give up is the fault-attack hardening a random nonce
    /// was added for, which is stated here rather than implied.
    ///
    /// Both directions were checked against **WhatsApp's own certificate chain**: the
    /// intermediate's signature verifies under the root key WhatsApp publishes, and the
    /// leaf's under the intermediate's own key. `ProtocolTests/XEdDsaTests` freezes that
    /// capture, because a signature we cannot verify against the real server is not
    /// "probably right" - it is unverified.
    /// </summary>
    public static class XEdDsa
    {
        public const int PublicKeyLength = 32;
        public const int PrivateKeyLength = 32;
        public const int SignatureLength = 64;

        /// <summary>
        /// The 32-byte domain prefix that keeps this nonce derivation from being the same
        /// hash as any other use of SHA-512 in the protocol.
        /// </summary>
        private static readonly byte[] NoncePrefix = Prefix();

        private static readonly byte[] NonceNoise = new byte[64];

        /// <summary>
        /// Signs `message` with an X25519 private key. The key is used exactly as stored,
        /// so it must already be the clamped scalar Signal generates - which is what
        /// `Credentials` stores. `Curve25519.ScalarMult` clamps as well, so the same bytes
        /// yield the same public key either way.
        /// </summary>
        public static byte[] Sign(byte[] privateKey, byte[] message)
        {
            if (privateKey == null || privateKey.Length != PrivateKeyLength)
            {
                throw new ProtocolException("an XEdDSA private key is 32 bytes");
            }

            byte[] ed25519PublicKey = Edwards25519.ScalarMultiplyBase(privateKey).Encode();
            byte signBit = (byte)(ed25519PublicKey[31] & 0x80);

            byte[] nonce = Scalar25519.Reduce(Sha512.HashConcat(
                NoncePrefix, privateKey, message, NonceNoise));
            byte[] encodedR = Edwards25519.ScalarMultiplyBase(nonce).Encode();

            byte[] challenge = Scalar25519.Reduce(Sha512.HashConcat(
                encodedR, ed25519PublicKey, message));
            byte[] s = Scalar25519.MultiplyAdd(challenge, privateKey, nonce);

            var signature = new byte[SignatureLength];
            Buffer.BlockCopy(encodedR, 0, signature, 0, 32);
            Buffer.BlockCopy(s, 0, signature, 32, 32);

            signature[63] = (byte)((signature[63] & 0x7f) | signBit);
            return signature;
        }

        /// <summary>
        /// Verifies a signature made with an X25519 key. The public key is moved to the
        /// Edwards curve, the signature's sign bit is moved into the public key where it
        /// belongs, and the rest is Ed25519 verification.
        /// </summary>
        public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
        {
            if (publicKey == null || publicKey.Length != PublicKeyLength) return false;
            if (signature == null || signature.Length != SignatureLength) return false;

            byte[] ed25519PublicKey;
            try
            {
                ed25519PublicKey = Edwards25519.MontgomeryToEdwards(publicKey);
            }
            catch (ProtocolException)
            {
                return false;
            }

            ed25519PublicKey[31] = (byte)((ed25519PublicKey[31] & 0x7f) | (signature[63] & 0x80));

            byte[] masked = Ed25519.Slice(signature, 0, SignatureLength);
            masked[63] &= 0x7f;

            return Ed25519.Verify(ed25519PublicKey, message, masked);
        }

        /// <summary>
        /// The Ed25519 public key a private key corresponds to, for the tests and for the
        /// pairing QR, which publishes both forms of the same identity key.
        /// </summary>
        public static byte[] Ed25519PublicKey(byte[] privateKey)
        {
            return Edwards25519.ScalarMultiplyBase(privateKey).Encode();
        }

        private static byte[] Prefix()
        {
            var prefix = new byte[32];
            prefix[0] = 0xfe;

            for (int i = 1; i < prefix.Length; i++) prefix[i] = 0xff;
            return prefix;
        }
    }
}
