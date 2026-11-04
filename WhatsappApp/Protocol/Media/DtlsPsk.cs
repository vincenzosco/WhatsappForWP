using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The PSK half of a DTLS 1.2 handshake, for the suites RFC 4279 and RFC 5487 define.
    ///
    /// Why it is here: `DtlsClient` completes a handshake only with `ECDHE_ECDSA`, whose
    /// signature it can check. A call's media is not always given a certificate to trust but
    /// often a shared secret - the one the call key material produces - and a PSK suite is then
    /// the shorter path: no certificate, no signature, and the key is the proof. This is the
    /// part of that path that is pure arithmetic and can be tested without a peer: the
    /// premaster secret, the master secret and the key block.
    ///
    /// What is **not** here, and is the next step rather than an oversight: the message sequence
    /// (`ServerKeyExchange` with a `psk_identity_hint`, `ClientKeyExchange` with the identity)
    /// and the choice of suite in `DtlsClient`. Those need a peer to be meaningful.
    ///
    /// The premaster is the shape RFC 4279 section 2 defines for PSK:
    /// `uint16(len(psk)) || len(psk) zero octets || uint16(len(psk)) || psk`. The zero run is
    /// `other_secret`, which a PSK handshake has by definition: there is no Diffie-Hellman
    /// result to mix in, and a client that sends the PSK alone derives different keys from the
    /// server's.
    /// </summary>
    public static class DtlsPsk
    {
        /// <summary>`TLS_PSK_WITH_AES_128_GCM_SHA256` (RFC 5487): the PSK suite with a GCM record.</summary>
        public const ushort SuitePskAes128GcmSha256 = 0x00A8;

        /// <summary>`TLS_PSK_WITH_AES_128_CBC_SHA` (RFC 4279): the oldest PSK suite, for a peer that offers only it.</summary>
        public const ushort SuitePskAes128CbcSha = 0x008C;

        /// <summary>
        /// The premaster secret of a PSK handshake: the identity's length twice, the zero run
        /// `other_secret`, then the secret itself.
        /// </summary>
        public static byte[] PreMaster(byte[] psk)
        {
            if (psk == null || psk.Length == 0)
            {
                throw new ProtocolException("a PSK handshake needs a secret, not an empty one");
            }

            if (psk.Length > 0xFFFF)
            {
                throw new ProtocolException("a PSK is at most 65535 bytes, not " + psk.Length);
            }

            var premaster = new byte[2 + psk.Length + 2 + psk.Length];

            // uint16(len) || other_secret (len zero octets) - the run stays zero, which is what
            // `new byte[]` already gives.
            premaster[0] = (byte)(psk.Length >> 8);
            premaster[1] = (byte)(psk.Length & 0xFF);

            int at = 2 + psk.Length;
            premaster[at] = (byte)(psk.Length >> 8);
            premaster[at + 1] = (byte)(psk.Length & 0xFF);
            Buffer.BlockCopy(psk, 0, premaster, at + 2, psk.Length);

            return premaster;
        }

        /// <summary>
        /// The master secret of a PSK handshake, with the same PRF and label the certificate
        /// suite uses: a Finished message is computed from it, so the two ends have to agree on
        /// this and on nothing else.
        /// </summary>
        public static byte[] MasterSecret(byte[] psk, byte[] clientRandom, byte[] serverRandom)
        {
            Check(clientRandom, "client");
            Check(serverRandom, "server");

            return DtlsPrf.Prf(PreMaster(psk), "master secret",
                Concat(clientRandom, serverRandom), 48);
        }

        /// <summary>
        /// The key block of AES-128-GCM: 16 bytes of client key, 16 of server key and the two
        /// four-byte implicit nonces, in that order (RFC 5288).
        /// </summary>
        public static byte[] KeyBlock(byte[] masterSecret, byte[] clientRandom, byte[] serverRandom)
        {
            if (masterSecret == null || masterSecret.Length != 48)
            {
                throw new ProtocolException("a master secret is 48 bytes");
            }

            Check(clientRandom, "client");
            Check(serverRandom, "server");

            return DtlsPrf.Prf(masterSecret, "key expansion",
                Concat(serverRandom, clientRandom), 40);
        }

        private static void Check(byte[] random, string which)
        {
            if (random == null || random.Length != Dtls.RandomLength)
            {
                throw new ProtocolException("the " + which + " random is " + Dtls.RandomLength + " bytes");
            }
        }

        private static byte[] Concat(byte[] left, byte[] right)
        {
            var joined = new byte[left.Length + right.Length];
            Buffer.BlockCopy(left, 0, joined, 0, left.Length);
            Buffer.BlockCopy(right, 0, joined, left.Length, right.Length);
            return joined;
        }
    }
}
