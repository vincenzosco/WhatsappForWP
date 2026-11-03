using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// Reads the P-256 key out of a DER certificate, and nothing else: names, dates and chain
    /// are not checked here - trust is a fingerprint the caller supplies. The walk follows the
    /// structure instead of scanning for an OID, so a certificate of another kind answers null.
    /// </summary>
    public static class X509
    {
        /// <summary>id-ecPublicKey, 1.2.840.10045.2.1.</summary>
        private static readonly byte[] EcPublicKeyOid =
            new byte[] { 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01 };

        /// <summary>prime256v1, 1.2.840.10045.3.1.7.</summary>
        private static readonly byte[] Prime256v1Oid =
            new byte[] { 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07 };

        /// <summary>The 65-byte P-256 point in this certificate, or null for any other key.</summary>
        public static byte[] P256Point(byte[] certificate)
        {
            if (certificate == null || certificate.Length < 16) return null;

            Body outer = Read(certificate, 0);
            if (outer == null) return null;

            // Certificate: the first child is tbsCertificate.
            Body tbs = Read(certificate, outer.At);
            if (tbs == null) return null;

            return SearchTbs(certificate, tbs.At, tbs.At + tbs.Length);
        }

        /// <summary>Walks the fields of tbsCertificate looking for the SPKI.</summary>
        private static byte[] SearchTbs(byte[] der, int from, int to)
        {
            int at = from;

            while (at < to)
            {
                Body body = Read(der, at);
                if (body == null) return null;

                // The version is [0] EXPLICIT and is skipped like any other field: what is
                // looked for is a SEQUENCE with the right shape, not the seventh child.
                byte tag = der[at];
                if (tag == 0x30)
                {
                    byte[] point = PointOfSpki(der, body.At, body.At + body.Length);
                    if (point != null) return point;
                }

                at = body.At + body.Length;
            }

            return null;
        }

        /// <summary>One SubjectPublicKeyInfo: AlgorithmIdentifier, then the BIT STRING.</summary>
        private static byte[] PointOfSpki(byte[] der, int from, int to)
        {
            int at = from;

            Body algorithm = Read(der, at);
            if (algorithm == null) return null;

            int algorithmEnd = algorithm.At + algorithm.Length;

            Body oid = Read(der, algorithm.At);
            if (oid == null) return null;
            if (!Equals(der, oid.At, oid.Length, EcPublicKeyOid)) return null;

            // parameters: the named curve.
            Body curve = Read(der, oid.At + oid.Length);
            if (curve == null) return null;
            if (!Equals(der, curve.At, curve.Length, Prime256v1Oid)) return null;

            at = algorithmEnd;
            if (at >= to) return null;

            Body bits = Read(der, at);
            if (bits == null) return null;

            // BIT STRING: one byte of unused-bit count, then the point itself.
            if (bits.Length != EcP256.PointLength + 1) return null;
            if (der[bits.At] != 0x00) return null;

            var point = new byte[EcP256.PointLength];
            Buffer.BlockCopy(der, bits.At + 1, point, 0, EcP256.PointLength);

            return point;
        }

        /// <summary>One TLV of the walk: where its body starts, and how long it is.</summary>
        private sealed class Body
        {
            public int At;
            public int Length;
        }

        /// <summary>
        /// The body of the TLV at `at`, or null when it does not parse. Lengths of one to three
        /// bytes are accepted, which covers every certificate this is likely to meet without
        /// pretending to support the indefinite form.
        /// </summary>
        private static Body Read(byte[] der, int at)
        {
            if (at < 0 || at + 2 > der.Length) return null;

            // The tag is one byte here: every field this walk meets is a universal tag.
            byte tag = der[at];
            if (tag == 0x1f) return null;

            int cursor = at + 1;
            int first = der[cursor++];
            int length;

            if (first < 0x80)
            {
                length = first;
            }
            else
            {
                int count = first & 0x7f;
                if (count == 0 || count > 3 || cursor + count > der.Length) return null;

                length = 0;
                for (int i = 0; i < count; i++) length = (length << 8) | der[cursor++];
            }

            if (length < 0 || cursor + length > der.Length) return null;

            var body = new Body();
            body.At = cursor;
            body.Length = length;
            return body;
        }

        private static bool Equals(byte[] source, int at, int length, byte[] expected)
        {
            if (length != expected.Length) return false;

            for (int i = 0; i < length; i++)
            {
                if (source[at + i] != expected[i]) return false;
            }

            return true;
        }
    }
}
