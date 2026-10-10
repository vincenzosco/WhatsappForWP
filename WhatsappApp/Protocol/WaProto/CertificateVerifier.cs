using System;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// Verifies the certificate chain a `serverHello` carries, which is what turns the
    /// Noise handshake from "encrypted" into "encrypted and authenticated".
    ///
    /// Without this step the handshake only proves that whoever answered knows the
    /// protocol - anybody can generate a Noise key pair. The chain is WhatsApp's way of
    /// signing the server's static key: the root's key is compiled into every client, the
    /// intermediate is signed by it, the leaf is signed by the intermediate, and the leaf
    /// names exactly the static key the handshake decrypted. If all four hold, the party
    /// on the other end of the socket is WhatsApp.
    ///
    /// The root key is a published constant, not a pin that this project chose, and the
    /// checks are the reference clients' - in the reference's order, so a failure names
    /// the same thing a reference failure would:
    ///
    /// 1. the intermediate carries details and a 64-byte signature,
    /// 2. those details verify under the root key,
    /// 3. the intermediate's issuer serial is the root's (`0`),
    /// 4. the intermediate's key is 32 bytes,
    /// 5. the leaf's details and 64-byte signature verify under the intermediate's key,
    /// 6. the leaf's issuer serial is the intermediate's serial,
    /// 7. the leaf's key is the static key the handshake decrypted, and
    /// 8. both certificates are inside their validity window.
    /// </summary>
    public static class CertificateVerifier
    {
        /// <summary>The serial the root certificate issued the intermediate under.</summary>
        public const ulong RootIssuerSerial = 0;

        /// <summary>
        /// WhatsApp's root signing key, the same 32 bytes the reference clients compile
        /// in. `ProtocolTests/XEdDsaTests` checks a real intermediate against it, so a
        /// bad transcription here fails a test rather than a connection.
        /// </summary>
        private static readonly byte[] RootPublicKey = HexBytes(
            "142375574d0a587166aae71ebe516437c4a28b73e3695c6ce1f7f9545da8ee6b");

        private const int SignatureLength = 64;
        private const int KeyLength = 32;

        /// <summary>
        /// Throws <see cref="ProtocolException"/> with the check that failed. `now` is
        /// passed in rather than read here, so the validity windows can be tested against
        /// a fixed moment.
        /// </summary>
        public static void Verify(CertChain chain, byte[] serverStaticKey, DateTime now)
        {
            if (chain == null) throw new ProtocolException("there is no certificate chain to verify");
            if (chain.Leaf == null) throw new ProtocolException("the certificate chain has no leaf");
            if (chain.Intermediate == null)
            {
                throw new ProtocolException("the certificate chain has no intermediate");
            }

            if (serverStaticKey == null || serverStaticKey.Length != KeyLength)
            {
                throw new ProtocolException("the handshake's static key is not 32 bytes");
            }

            CertChain.Certificate intermediate = chain.Intermediate;
            if (intermediate.Details == null || intermediate.Signature == null)
            {
                throw new ProtocolException("the intermediate certificate is missing details or signature");
            }

            if (intermediate.Signature.Length != SignatureLength)
            {
                throw new ProtocolException("the intermediate certificate's signature is "
                    + intermediate.Signature.Length + " bytes, not " + SignatureLength);
            }

            if (!XEdDsa.Verify(RootPublicKey, intermediate.Details, intermediate.Signature))
            {
                throw new ProtocolException("the intermediate certificate is not signed by WhatsApp's root key");
            }

            CertChain.Details intermediateDetails = CertChain.DecodeDetails(intermediate.Details);

            if (intermediateDetails.IssuerSerial != RootIssuerSerial)
            {
                throw new ProtocolException("the intermediate certificate was issued by serial "
                    + intermediateDetails.IssuerSerial + ", not by the root (" + RootIssuerSerial + ")");
            }

            if (intermediateDetails.Key == null || intermediateDetails.Key.Length != KeyLength)
            {
                throw new ProtocolException("the intermediate certificate's key is not 32 bytes");
            }

            CertChain.Certificate leaf = chain.Leaf;
            if (leaf.Details == null || leaf.Signature == null)
            {
                throw new ProtocolException("the leaf certificate is missing details or signature");
            }

            if (leaf.Signature.Length != SignatureLength)
            {
                throw new ProtocolException("the leaf certificate's signature is "
                    + leaf.Signature.Length + " bytes, not " + SignatureLength);
            }

            if (!XEdDsa.Verify(intermediateDetails.Key, leaf.Details, leaf.Signature))
            {
                throw new ProtocolException("the leaf certificate is not signed by the intermediate's key");
            }

            CertChain.Details leafDetails = CertChain.DecodeDetails(leaf.Details);

            if (leafDetails.IssuerSerial != intermediateDetails.Serial)
            {
                throw new ProtocolException("the leaf certificate was issued by serial "
                    + leafDetails.IssuerSerial + ", not by the intermediate ("
                    + intermediateDetails.Serial + ")");
            }

            if (!SameBytes(leafDetails.Key, serverStaticKey))
            {
                throw new ProtocolException(
                    "the leaf certificate does not vouch for the key the handshake produced");
            }

            CheckValidity(intermediateDetails, now, "intermediate");
            CheckValidity(leafDetails, now, "leaf");
        }

        private static void CheckValidity(CertChain.Details details, DateTime now, string which)
        {
            ulong seconds = UnixSeconds(now);

            if (seconds < details.NotBefore)
            {
                throw new ProtocolException("the " + which + " certificate is not valid until "
                    + details.NotBefore + " (now " + seconds + ")");
            }

            if (seconds > details.NotAfter)
            {
                throw new ProtocolException("the " + which + " certificate expired at "
                    + details.NotAfter + " (now " + seconds + ")");
            }
        }

        /// <summary>Seconds since the epoch, which is the unit the certificates use.</summary>
        public static ulong UnixSeconds(DateTime moment)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan since = moment.ToUniversalTime() - epoch;

            return since.TotalSeconds < 0 ? 0UL : (ulong)since.TotalSeconds;
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
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
