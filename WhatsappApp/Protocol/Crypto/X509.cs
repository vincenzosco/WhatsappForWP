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

        // ---- writing --------------------------------------------------------------------

        /// <summary>id-ecPublicKey's full TLV, which an AlgorithmIdentifier carries.</summary>
        private static readonly byte[] EcPublicKeyOidTlv = new byte[]
        {
            0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01
        };

        /// <summary>prime256v1's full TLV.</summary>
        private static readonly byte[] Prime256v1OidTlv = new byte[]
        {
            0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07
        };

        /// <summary>ecdsa-with-SHA256 (1.2.840.10045.4.3.2).</summary>
        private static readonly byte[] EcdsaWithSha256OidTlv = new byte[]
        {
            0x06, 0x08, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x04, 0x03, 0x02
        };

        /// <summary>commonName (2.5.4.3).</summary>
        private static readonly byte[] CommonNameOidTlv = new byte[] { 0x06, 0x03, 0x55, 0x04, 0x03 };

        /// <summary>
        /// A self-signed P-256 certificate, valid from 2026 to 2036, carrying nothing but the
        /// key: a DTLS server has to send one, and this project has no certificate store to take
        /// one from. The signature is real ECDSA-SHA256 over the tbsCertificate, so the file is a
        /// certificate and not a shape that happens to parse - but the trust decision stays where
        /// `DtlsClient` puts it, on the SHA-256 fingerprint the signalling carried.
        ///
        /// The encoding is built rather than scanned: every length is written from the bytes it
        /// covers, which is the one thing a hand-rolled DER gets wrong. `P256Point` reads the
        /// result back, and the tests round-trip it.
        /// </summary>
        public static byte[] SelfSignedP256(byte[] privateScalar, string commonName)
        {
            if (privateScalar == null || privateScalar.Length != EcP256.CoordinateLength)
            {
                throw new ProtocolException("an ECDSA private key is " + EcP256.CoordinateLength + " bytes");
            }

            byte[] point = EcP256.PublicPoint(privateScalar);
            if (point == null) throw new ProtocolException("an ECDSA private key is 1..n-1");

            byte[] name = Name(commonName);

            byte[] algorithm = Tlv(0x30, Concat(EcPublicKeyOidTlv, Prime256v1OidTlv));
            byte[] signatureAlgorithm = Tlv(0x30, EcdsaWithSha256OidTlv);

            byte[] spki = Tlv(0x30, Concat(algorithm, BitString(point, 0)));

            byte[] tbs = Tlv(0x30, Concat(Concat(Concat(
                Tlv(0xa0, Tlv(0x02, new byte[] { 0x02 })),       // version: v3
                DerInteger(One())),
                signatureAlgorithm),
                Concat(Concat(Concat(name, Validity()), name), spki)));

            byte[] signature = EcP256.Sign(tbs, privateScalar);

            return Tlv(0x30, Concat(Concat(tbs, signatureAlgorithm), BitString(signature, 0)));
        }

        /// <summary>A DER INTEGER holding a small positive number.</summary>
        private static byte[] DerInteger(byte[] value)
        {
            int start = 0;
            while (start < value.Length - 1 && value[start] == 0) start++;

            bool pad = (value[start] & 0x80) != 0;
            int length = value.Length - start + (pad ? 1 : 0);

            var body = new byte[length];
            if (pad) body[0] = 0x00;
            Buffer.BlockCopy(value, start, body, pad ? 1 : 0, value.Length - start);

            return Tlv(0x02, body);
        }

        private static byte[] One()
        {
            return new byte[] { 0x01 };
        }

        /// <summary>The two UTCTimes: fixed dates, because nothing here checks them.</summary>
        private static byte[] Validity()
        {
            return Tlv(0x30, Concat(
                Tlv(0x17, Ascii("260101000000Z")),
                Tlv(0x17, Ascii("361231235959Z"))));
        }

        /// <summary>An X.501 name with one commonName in it: RDNSequence of one SET of one AttributeTypeAndValue.</summary>
        private static byte[] Name(string commonName)
        {
            string value = commonName == null || commonName.Length == 0 ? "dtls" : commonName;

            byte[] attribute = Tlv(0x30, Concat(CommonNameOidTlv, Tlv(0x0c, Utf8(value))));
            return Tlv(0x30, Tlv(0x31, attribute));
        }

        /// <summary>A BIT STRING with its unused-bit count in front, as every key and signature is encoded.</summary>
        private static byte[] BitString(byte[] value, int unusedBits)
        {
            var body = new byte[1 + value.Length];
            body[0] = (byte)unusedBits;
            Buffer.BlockCopy(value, 0, body, 1, value.Length);
            return Tlv(0x03, body);
        }

        private static byte[] Tlv(byte tag, byte[] body)
        {
            byte[] length = Length(body.Length);

            var encoded = new byte[1 + length.Length + body.Length];
            encoded[0] = tag;
            Buffer.BlockCopy(length, 0, encoded, 1, length.Length);
            Buffer.BlockCopy(body, 0, encoded, 1 + length.Length, body.Length);
            return encoded;
        }

        /// <summary>DER's length: one byte below 128, otherwise the count and the bytes that hold it.</summary>
        private static byte[] Length(int length)
        {
            if (length < 0x80) return new byte[] { (byte)length };

            int count = 0;
            int copy = length;
            while (copy > 0)
            {
                count++;
                copy >>= 8;
            }

            var encoded = new byte[1 + count];
            encoded[0] = (byte)(0x80 | count);

            for (int i = 0; i < count; i++)
            {
                encoded[1 + i] = (byte)(length >> (8 * (count - 1 - i)));
            }

            return encoded;
        }

        private static byte[] Concat(byte[] left, byte[] right)
        {
            var joined = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, joined, 0, left.Length);
            Buffer.BlockCopy(right, 0, joined, left.Length, right.Length);
            return joined;
        }

        private static byte[] Ascii(string text)
        {
            var bytes = new byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
            return bytes;
        }

        private static byte[] Utf8(string text)
        {
            return System.Text.Encoding.UTF8.GetBytes(text);
        }
    }
}
