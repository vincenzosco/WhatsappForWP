using System;
using WhatsappApp.Protocol.Crypto;

namespace ProtocolTests
{
    /// <summary>
    /// XEdDSA against **WhatsApp's own certificate chain**, captured from the serverHello
    /// on 2026-10-10, plus the round-trip properties that a real signature needs.
    ///
    /// The fixture is the point of this file. A signature implementation can be checked
    /// against itself all day and still be wrong in a way that only shows up against
    /// another implementation; these two certificates were signed by WhatsApp, and the
    /// root key below is the one every Signal client compiles in. If the intermediate
    /// verifies under it, the sign-bit convention, the birational map, the nonce prefix,
    /// the field arithmetic and the scalar reduction are all right together.
    /// </summary>
    public static class XEdDsaTests
    {
        /// <summary>WhatsApp's root certificate key, from the reference's constant.</summary>
        private const string RootKey = "142375574d0a587166aae71ebe516437c4a28b73e3695c6ce1f7f9545da8ee6b";

        private const string IntermediateDetails =
            "080310001a201c51a9ac303994c6c8d0b92ea1878a533476599cc599fbea35997d9a"
            + "a90cce62208091aebe0628ffdeb7dc06";

        private const string IntermediateSignature =
            "270f294648539fed" + "4870e25054dd4e95" + "983aba29189c2ba6" + "c8eeda7055555f75"
            + "3740f5ec192ab64c" + "26c26d6ade6d20b9" + "f774aee37120a6b2" + "0395f53c66058507";

        /// <summary>
        /// The intermediate's own key, which is the field 3 of its details - the same 32
        /// bytes the handshake's leaf turned out to vouch for.
        /// </summary>
        private const string IntermediateKey =
            "1c51a9ac303994c6c8d0b92ea1878a533476599cc599fbea35997d9aa90cce62";

        private const string LeafDetails =
            "08ea0210031a202638544c3739df67f8190f89a09c9c1c4d8b72e577aa1eb8f2"
            + "4cd584de16b52520d0b98bd20628d0f3a3d706";

        private const string LeafSignature =
            "ddbb98222f048c94a99150bef885bc406618ae29c45582597c3301dca1f0ee4b"
            + "10bc93f11e52b40cfd439a49285a224b12cefd8ea0e05ad0cce50c8f8513870d";

        [Test]
        public static void WhatsApps_intermediate_certificate_verifies_under_its_root_key()
        {
            Assert.IsTrue(XEdDsa.Verify(Hex(RootKey), Hex(IntermediateDetails), Hex(IntermediateSignature)),
                "the intermediate certificate did not verify under WhatsApp's root key");
        }

        [Test]
        public static void WhatsApps_leaf_certificate_verifies_under_the_intermediate_key()
        {
            Assert.IsTrue(XEdDsa.Verify(Hex(IntermediateKey), Hex(LeafDetails), Hex(LeafSignature)),
                "the leaf certificate did not verify under the intermediate's key");
        }

        [Test]
        public static void A_certificate_signed_by_the_wrong_key_is_refused()
        {
            byte[] signature = Hex(IntermediateSignature);

            // The intermediate's key instead of the root's, and one flipped byte in the
            // message and in the signature: none of these may pass.
            Assert.IsFalse(XEdDsa.Verify(Hex(IntermediateKey), Hex(IntermediateDetails), signature));
            Assert.IsFalse(XEdDsa.Verify(Hex(RootKey), Hex(LeafDetails), signature));

            byte[] flippedMessage = Hex(IntermediateDetails);
            flippedMessage[20] ^= 0x01;
            Assert.IsFalse(XEdDsa.Verify(Hex(RootKey), flippedMessage, signature));

            byte[] flippedSignature = (byte[])signature.Clone();
            flippedSignature[33] ^= 0x40;
            Assert.IsFalse(XEdDsa.Verify(Hex(RootKey), Hex(IntermediateDetails), flippedSignature));

            Assert.IsTrue(XEdDsa.Verify(Hex(RootKey), Hex(IntermediateDetails), signature));
        }

        [Test]
        public static void A_signature_verifies_with_the_curve25519_public_key_of_its_private_key()
        {
            // The sign bit of the Ed25519 public key decides where it lands in the
            // signature, so this walks enough keys to see both cases rather than one.
            bool sawSignBitSet = false;

            for (int index = 0; index < 16; index++)
            {
                byte[] privateKey = PrivateKey(index);
                byte[] publicKey = Curve25519.PublicKey(privateKey);
                byte[] message = Message(index);
                byte[] signature = XEdDsa.Sign(privateKey, message);

                Assert.IsTrue(XEdDsa.Verify(publicKey, message, signature),
                    "a signature did not verify under the Curve25519 public key of key " + index);

                bool signBitSet = (XEdDsa.Ed25519PublicKey(privateKey)[31] & 0x80) != 0;
                Assert.AreEqual(signBitSet, (signature[63] & 0x80) != 0);

                if (signBitSet) sawSignBitSet = true;
            }

            // If no key in this deterministic sequence had the bit set, the check above
            // would have passed without ever exercising it.
            Assert.IsTrue(sawSignBitSet);
        }

        [Test]
        public static void The_curve25519_public_key_and_the_edwards_one_are_the_same_point()
        {
            for (int index = 0; index < 4; index++)
            {
                byte[] privateKey = PrivateKey(index);
                byte[] montgomery = Curve25519.PublicKey(privateKey);
                byte[] fromMontgomery = Edwards25519.MontgomeryToEdwards(montgomery);

                // The map drops the sign of x, so the two encodings agree on the
                // y-coordinate and differ at most in the top bit.
                byte[] edwards = XEdDsa.Ed25519PublicKey(privateKey);
                edwards[31] &= 0x7f;

                Assert.AreEqual(Assert.Hex(edwards), Assert.Hex(fromMontgomery));
            }
        }

        [Test]
        public static void A_signature_does_not_verify_for_another_message()
        {
            byte[] privateKey = PrivateKey(0);
            byte[] publicKey = Curve25519.PublicKey(privateKey);
            byte[] signature = XEdDsa.Sign(privateKey, Message(0));

            Assert.IsTrue(XEdDsa.Verify(publicKey, Message(0), signature));
            Assert.IsFalse(XEdDsa.Verify(publicKey, Message(1), signature));
            Assert.IsFalse(XEdDsa.Verify(Curve25519.PublicKey(PrivateKey(1)), Message(0), signature));
        }

        /// <summary>
        /// A private key the way Signal generates one: 32 bytes with the X25519 clamps
        /// applied, because the same bytes are used for the Diffie-Hellman and unreduced
        /// as the signing scalar.
        /// </summary>
        private static byte[] PrivateKey(int index)
        {
            byte[] key = Sha256.HashUtf8("xeddsa test key " + index);

            key[0] &= 248;
            key[31] &= 127;
            key[31] |= 64;

            return key;
        }

        private static byte[] Message(int index)
        {
            if (index == 0) return new byte[0];

            var message = new byte[index * 7];
            for (int i = 0; i < message.Length; i++) message[i] = (byte)(i + index);

            return message;
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
