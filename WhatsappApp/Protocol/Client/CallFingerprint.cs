using System;
using System.Text;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The certificate fingerprint a call's media handshake is pinned to, as the signalling
    /// writes it.
    ///
    /// Where it comes from: the peer's native media stack derives it from its own DTLS
    /// certificate (`wa_dtls_get_cached_local_certificate_fingerprint`, "Web P2P: stored peer
    /// DTLS fingerprint (algo=%s)") and sends it in the transport negotiation that carries the
    /// ICE credentials and the candidates - the reference's `Web P2P: sending type 13
    /// (is_caller=%d, has_ufrag=%d, num_cands=%d, has_fp=%d)` line, in the 9,916 strings of
    /// `libwhatsappmerged`,. The Java side never spells "fingerprint" at all:
    /// the child nodes are built with `VoipStanzaChildNode`, and every fingerprint mention in
    /// the APK is inside the native library or a `crypto_info` SQL column of message storage.
    ///
    /// The value is the algorithm and the SHA-256 in the SDP spelling, `sha-256 AB:CD.`: one
    /// string in one attribute, and a form the peer's own engine already parses
    /// (`algo=%s`). A different peer that names another algorithm is refused rather than
    /// matched against nothing, because a fingerprint nobody can compare is not a pin.
    /// </summary>
    public static class CallFingerprint
    {
        /// <summary>The one algorithm this project can compute: `sha-256`.</summary>
        public const string Algorithm = "sha-256";

        /// <summary>The digest length, in bytes.</summary>
        public const int DigestLength = 32;

        /// <summary>The fingerprint of a DER certificate: the SHA-256 of its bytes.</summary>
        public static byte[] Of(byte[] certificate)
        {
            if (certificate == null || certificate.Length == 0)
            {
                throw new ProtocolException("a certificate fingerprint needs the certificate");
            }

            return Sha256.Hash(certificate);
        }

        /// <summary>
        /// The attribute value: `sha-256` and the digest in uppercase hex, separated by colons,
        /// which is how every SDP has written one since RFC 4572.
        /// </summary>
        public static string Format(byte[] digest)
        {
            if (digest == null || digest.Length != DigestLength)
            {
                throw new ProtocolException("a certificate fingerprint is a " + DigestLength + "-byte SHA-256");
            }

            var text = new StringBuilder(Algorithm.Length + 1 + DigestLength * 3);
            text.Append(Algorithm).Append(' ');

            for (int i = 0; i < digest.Length; i++)
            {
                if (i > 0) text.Append(':');
                text.Append(digest[i].ToString("X2"));
            }

            return text.ToString();
        }

        /// <summary>
        /// The 32 bytes of a value, or null when it names another algorithm or is not hex.
        /// Colons, dashes and spaces between the octets are all accepted: a phone that writes a
        /// reference-compatible fingerprint should not be refused for punctuation.
        /// </summary>
        public static byte[] Parse(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            string text = value.Trim();
            int space = text.IndexOf(' ');

            if (space > 0)
            {
                string algorithm = text.Substring(0, space);
                if (!string.Equals(algorithm, Algorithm, StringComparison.OrdinalIgnoreCase)) return null;

                text = text.Substring(space + 1);
            }

            var digest = new byte[DigestLength];
            int count = 0;
            int high = -1;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == ':' || c == '-' || c == ' ') continue;

                int nibble = Hex(c);
                if (nibble < 0) return null;

                if (high < 0)
                {
                    high = nibble;
                    continue;
                }

                if (count >= DigestLength) return null;

                digest[count++] = (byte)((high << 4) | nibble);
                high = -1;
            }

            // A half octet at the end, or a digest of another size, is not this algorithm's.
            if (high >= 0 || count != DigestLength) return null;

            return digest;
        }

        private static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
