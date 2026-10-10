using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    /// <summary>
    /// The whole chain, from the capture of 2026-10-10, run through the verifier the
    /// handshake will use - including `CertChain.Decode`, so the fixture travels the same
    /// path the server's bytes do.
    ///
    /// The success case is the milestone: a chain WhatsApp signed, verified by our own
    /// XEdDSA against the root key every client compiles in. The failure cases are the
    /// ones that matter more, because each of them is a way a man in the middle would
    /// otherwise be accepted.
    /// </summary>
    public static class CertificateVerifierTests
    {
        private const string IntermediateDetails =
            "080310001a201c51a9ac303994c6c8d0b92ea1878a533476599cc599fbea35997d9a"
            + "a90cce62208091aebe0628ffdeb7dc06";

        private const string IntermediateSignature =
            "270f294648539fed" + "4870e25054dd4e95" + "983aba29189c2ba6" + "c8eeda7055555f75"
            + "3740f5ec192ab64c" + "26c26d6ade6d20b9" + "f774aee37120a6b2" + "0395f53c66058507";

        private const string LeafDetails =
            "08ea0210031a202638544c3739df67f8190f89a09c9c1c4d8b72e577aa1eb8f2"
            + "4cd584de16b52520d0b98bd20628d0f3a3d706";

        private const string LeafSignature =
            "ddbb98222f048c94a99150bef885bc406618ae29c45582597c3301dca1f0ee4b"
            + "10bc93f11e52b40cfd439a49285a224b12cefd8ea0e05ad0cce50c8f8513870d";

        /// <summary>The static key that leaf certifies, decrypted by the same handshake.</summary>
        private const string ServerStaticKey =
            "2638544c3739df67f8190f89a09c9c1c4d8b72e577aa1eb8f24cd584de16b525";

        /// <summary>A moment inside both certificates' validity windows.</summary>
        private static readonly DateTime Captured = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        [Test]
        public static void A_real_chain_verifies_against_the_key_the_handshake_produced()
        {
            CertChain chain = CertChain.Decode(ChainBytes());
            CertificateVerifier.Verify(chain, Hex(ServerStaticKey), Captured);
        }

        [Test]
        public static void A_chain_whose_leaf_vouches_for_another_key_is_refused()
        {
            CertChain chain = CertChain.Decode(ChainBytes());
            byte[] otherKey = Hex(ServerStaticKey);
            otherKey[3] ^= 0x01;

            Assert.Throws<ProtocolException>(delegate
            {
                CertificateVerifier.Verify(chain, otherKey, Captured);
            });
        }

        [Test]
        public static void A_chain_with_a_tampered_signature_is_refused()
        {
            byte[] sig = Hex(LeafSignature);
            sig[40] ^= 0x08;

            byte[] leaf = new ProtoWriter()
                .Bytes(1, Hex(LeafDetails))
                .Bytes(2, sig)
                .ToArray();

            byte[] intermediate = new ProtoWriter()
                .Bytes(1, Hex(IntermediateDetails))
                .Bytes(2, Hex(IntermediateSignature))
                .ToArray();

            CertChain chain = CertChain.Decode(new ProtoWriter().Bytes(1, leaf).Bytes(2, intermediate).ToArray());

            Assert.Throws<ProtocolException>(delegate
            {
                CertificateVerifier.Verify(chain, Hex(ServerStaticKey), Captured);
            });
        }

        [Test]
        public static void An_expired_certificate_is_refused()
        {
            CertChain chain = CertChain.Decode(ChainBytes());

            Assert.Throws<ProtocolException>(delegate
            {
                CertificateVerifier.Verify(chain, Hex(ServerStaticKey), Captured.AddYears(5));
            });

            // The other end of the window: a clock far enough behind that the
            // certificate is not valid yet is refused as well.
            Assert.Throws<ProtocolException>(delegate
            {
                CertificateVerifier.Verify(chain, Hex(ServerStaticKey), Captured.AddYears(-5));
            });
        }

        /// <summary>`CertChain{leaf{details, signature}, intermediate{details, signature}}`.</summary>
        private static byte[] ChainBytes()
        {
            byte[] leaf = new ProtoWriter()
                .Bytes(1, Hex(LeafDetails))
                .Bytes(2, Hex(LeafSignature))
                .ToArray();

            byte[] intermediate = new ProtoWriter()
                .Bytes(1, Hex(IntermediateDetails))
                .Bytes(2, Hex(IntermediateSignature))
                .ToArray();

            return new ProtoWriter().Bytes(1, leaf).Bytes(2, intermediate).ToArray();
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
