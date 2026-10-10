using System;
using WhatsappApp.Protocol.Crypto;

namespace ProtocolTests
{
    /// <summary>
    /// The published vectors for SHA-512 (FIPS 180-4) and Ed25519 (RFC 8032 section 7.1),
    /// plus the two internal properties the group code rests on.
    ///
    /// These are the tests that make the hand-written field arithmetic trustworthy: they
    /// exercise every line of `Field25519`, `Edwards25519` and `Scalar25519` at once,
    /// against numbers computed by other people.
    /// </summary>
    public static class Ed25519Tests
    {
        [Test]
        public static void Sha512_matches_fips_180_4()
        {
            Assert.AreEqual(
                "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce"
                + "47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e",
                Assert.Hex(Sha512.Hash(new byte[0])));

            Assert.AreEqual(
                "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a"
                + "2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
                Assert.Hex(Sha512.Hash(Utf8("abc"))));

            // 56 bytes: the padding crosses into a second block, which is where a wrong
            // padding length shows up.
            Assert.AreEqual(
                "204a8fc6dda82f0a0ced7beb8e08a41657c16ef468b228a8279be331a703c335"
                + "96fd15c13b1b07f9aa1d3bea57789ca031ad85c7a71dd70354ec631238ca3445",
                Assert.Hex(Sha512.Hash(Utf8("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"))));

            // 112 bytes: the padding fits in a final block with room to spare.
            Assert.AreEqual(
                "8e959b75dae313da8cf4f72814fc143f8f7779c6eb9f7fa17299aeadb6889018"
                + "501d289e4900f7e4331b99dec4b5433ac7d329eeb6dd26545e96e55b874be909",
                Assert.Hex(Sha512.Hash(Utf8("abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmn"
                    + "hijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu"))));
        }

        [Test]
        public static void Ed25519_matches_rfc_8032_test_one()
        {
            // One empty message.
            Assert.AreEqual(
                "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
                Assert.Hex(Ed25519.PublicKey(Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"))));

            byte[] signature = Ed25519.Sign(
                Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"), new byte[0]);

            Assert.AreEqual(
                "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155"
                + "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b",
                Assert.Hex(signature));

            Assert.IsTrue(Ed25519.Verify(
                Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"),
                new byte[0], signature));
        }

        [Test]
        public static void Ed25519_matches_rfc_8032_test_two()
        {
            // One byte of message.
            Assert.AreEqual(
                "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
                Assert.Hex(Ed25519.PublicKey(Hex("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb"))));

            byte[] signature = Ed25519.Sign(
                Hex("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb"),
                Hex("72"));

            Assert.AreEqual(
                "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da"
                + "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00",
                Assert.Hex(signature));
        }

        [Test]
        public static void Ed25519_matches_rfc_8032_test_three()
        {
            // Two bytes of message.
            byte[] signature = Ed25519.Sign(
                Hex("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7"),
                Hex("af82"));

            Assert.AreEqual(
                "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac"
                + "18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a",
                Assert.Hex(signature));

            Assert.IsTrue(Ed25519.Verify(
                Hex("fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025"),
                Hex("af82"), signature));
        }

        [Test]
        public static void A_signature_that_is_not_canonical_is_refused()
        {
            byte[] seed = Hex("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb");
            byte[] publicKey = Ed25519.PublicKey(seed);
            byte[] signature = Ed25519.Sign(seed, Hex("72"));

            // The top bit of S makes S larger than l, which RFC 8032 says to refuse
            // rather than to reduce: two encodings of one signature would otherwise both
            // be accepted.
            byte[] nonCanonical = (byte[])signature.Clone();
            nonCanonical[63] |= 0x80;

            Assert.IsFalse(Ed25519.Verify(publicKey, Hex("72"), nonCanonical));

            // A signature with a byte changed anywhere else is refused too.
            byte[] tampered = (byte[])signature.Clone();
            tampered[10] ^= 0x01;
            Assert.IsFalse(Ed25519.Verify(publicKey, Hex("72"), tampered));

            Assert.IsFalse(Ed25519.Verify(publicKey, Hex("73"), signature));
            Assert.IsTrue(Ed25519.Verify(publicKey, Hex("72"), signature));
        }

        [Test]
        public static void The_base_point_is_on_the_curve_and_has_order_l()
        {
            Edwards25519 basePoint = Edwards25519.BasePoint();
            Assert.IsTrue(basePoint.IsOnCurve());

            // [l]B is the identity, which is the property every scalar reduction relies on:
            // it is why a scalar may be used unreduced.
            byte[] order = Hex("edd3f55c1a631258d69cf7a2def9de1400000000000000000000000000000010");
            Assert.AreEqual(
                "0100000000000000000000000000000000000000000000000000000000000000",
                Assert.Hex(Edwards25519.ScalarMultiply(basePoint, order).Encode()));

            // And one point doubles to the point a single addition of itself gives, which
            // catches a doubling formula that is only right for a generic pair.
            Assert.AreEqual(
                Assert.Hex(basePoint.Add(basePoint).Encode()),
                Assert.Hex(basePoint.Double().Encode()));
        }

        [Test]
        public static void The_square_root_of_minus_one_is_a_square_root_of_minus_one()
        {
            Field25519 root = Field25519.SqrtMinusOneValue();
            Assert.IsTrue(root.Square().IsEqual(Field25519.One().Negate()));
            Assert.AreEqual(32, root.ToBytes().Length);

            // A value with no square root must come back as null rather than as a wrong
            // answer: decompression depends on telling those apart.
            Assert.IsTrue(Field25519.TrySqrt(Field25519.FromSmall(4)) != null);
            Assert.IsTrue(Field25519.TrySqrt(Field25519.One().Negate()) != null);
        }

        private static byte[] Utf8(string text)
        {
            return System.Text.Encoding.UTF8.GetBytes(text);
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
