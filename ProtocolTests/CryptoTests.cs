using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Crypto;

namespace ProtocolTests
{
    /// <summary>
    /// Every primitive against its published vector. The vectors are named where they
    /// come from, because a failure here has to be looked up, and because a hand-rolled
    /// implementation that agrees with itself proves nothing: this is the code the
    /// phone will use to talk to WhatsApp, and the only way to know it is right before
    /// it is on the phone is to check it against the standard.
    /// </summary>
    public static class CryptoTests
    {
        [Test]
        public static void Sha256_matches_fips_180_4()
        {
            Assert.ByteArraysEqual(
                Hex("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),
                Sha256.HashUtf8("abc"));

            Assert.ByteArraysEqual(
                Hex("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
                Sha256.Hash(new byte[0]));

            // The two-block message of FIPS-197's test, so the padding path past one
            // block is exercised too.
            Assert.ByteArraysEqual(
                Hex("248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1"),
                Sha256.HashUtf8("abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq"));
        }

        [Test]
        public static void Hmac_matches_rfc_4231()
        {
            var key = new byte[20];
            for (int i = 0; i < key.Length; i++) key[i] = 0x0b;

            Assert.ByteArraysEqual(
                Hex("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7"),
                Hmac.Compute(key, System.Text.Encoding.ASCII.GetBytes("Hi There")));
        }

        [Test]
        public static void Hkdf_matches_rfc_5869_case_one()
        {
            var ikm = new byte[22];
            for (int i = 0; i < ikm.Length; i++) ikm[i] = 0x0b;

            byte[] salt = Hex("000102030405060708090a0b0c");
            byte[] info = Hex("f0f1f2f3f4f5f6f7f8f9");

            byte[] pseudorandomKey = Hkdf.Extract(salt, ikm);
            Assert.ByteArraysEqual(
                Hex("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5"),
                pseudorandomKey);

            Assert.ByteArraysEqual(
                Hex("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"),
                Hkdf.Expand(pseudorandomKey, info, 42));
        }

        [Test]
        public static void Aes_128_and_256_match_fips_197()
        {
            byte[] plaintext = Hex("00112233445566778899aabbccddeeff");

            Assert.ByteArraysEqual(
                Hex("69c4e0d86a7b0430d8cdb78070b4c55a"),
                Aes.EncryptBlock(Hex("000102030405060708090a0b0c0d0e0f"), plaintext));

            Assert.ByteArraysEqual(
                Hex("8ea2b7ca516745bfeafc49904b496089"),
                Aes.EncryptBlock(Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"), plaintext));

            // The inverse cipher, which no round trip of the forward one would catch.
            Assert.ByteArraysEqual(
                plaintext,
                Aes.DecryptBlock(Hex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"),
                    Hex("8ea2b7ca516745bfeafc49904b496089")));
        }

        [Test]
        public static void Aes_cbc_matches_nist_sp_800_38a_and_round_trips()
        {
            byte[] key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
            byte[] iv = Hex("000102030405060708090a0b0c0d0e0f");
            byte[] plaintext = Hex("6bc1bee22e409f96e93d7e117393172a");

            byte[] encrypted = Aes.CbcEncrypt(key, iv, plaintext);

            // PKCS#7 always adds a block, so the vector's block is the first one.
            Assert.AreEqual(32, encrypted.Length);
            var firstBlock = new byte[16];
            Buffer.BlockCopy(encrypted, 0, firstBlock, 0, 16);
            Assert.ByteArraysEqual(Hex("7649abac8119b246cee98e9b12e9197d"), firstBlock);

            // And the unpadding path, on a plaintext that is not a block multiple.
            byte[] shortPlaintext = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
            Assert.ByteArraysEqual(shortPlaintext, Aes.CbcDecrypt(key, iv, Aes.CbcEncrypt(key, iv, shortPlaintext)));
        }

        [Test]
        public static void Aes_gcm_matches_nist_case_two()
        {
            byte[] sealedBytes = AesGcm.Seal(new byte[16], new byte[12], new byte[16], null);

            Assert.ByteArraysEqual(
                Hex("0388dace60b6a392f328c2b971b2fe78ab6e47d42cec13bdf53a67b21257bddf"),
                sealedBytes);
        }

        [Test]
        public static void Aes_gcm_matches_nist_case_three()
        {
            byte[] key = Hex("feffe9928665731c6d6a8f9467308308");
            byte[] nonce = Hex("cafebabefacedbaddecaf888");
            byte[] plaintext = Hex(
                "d9313225f88406e5a55909c5aff5269a"
                + "86a7a9531534f7da2e4c303d8a318a72"
                + "1c3c0c95956809532fcf0e2449a6b525"
                + "b16aedf5aa0de657ba637b391aafd255");

            Assert.ByteArraysEqual(
                Hex("42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e"
                    + "21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091473f5985"
                    + "4d5c2af327cd64a62cf35abd2ba6fab4"),
                AesGcm.Seal(key, nonce, plaintext, null));

            Assert.ByteArraysEqual(plaintext, AesGcm.Open(key, nonce,
                Hex("42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e"
                    + "21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091473f5985"
                    + "4d5c2af327cd64a62cf35abd2ba6fab4"), null));
        }

        [Test]
        public static void Aes_gcm_matches_nist_case_four_with_additional_data()
        {
            byte[] key = Hex("feffe9928665731c6d6a8f9467308308");
            byte[] nonce = Hex("cafebabefacedbaddecaf888");
            byte[] aad = Hex("feedfacedeadbeeffeedfacedeadbeefabaddad2");
            byte[] plaintext = Hex(
                "d9313225f88406e5a55909c5aff5269a"
                + "86a7a9531534f7da2e4c303d8a318a72"
                + "1c3c0c95956809532fcf0e2449a6b525"
                + "b16aedf5aa0de657ba637b39");

            Assert.ByteArraysEqual(
                Hex("42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e"
                    + "21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091"
                    + "5bc94fbc3221a5db94fae95ae7121a47"),
                AesGcm.Seal(key, nonce, plaintext, aad));
        }

        [Test]
        public static void A_tampered_tag_is_refused()
        {
            byte[] sealedBytes = AesGcm.Seal(new byte[16], new byte[12], Hex("00112233"), null);
            sealedBytes[sealedBytes.Length - 1] ^= 0x01;

            Assert.Throws<ProtocolException>(delegate
            {
                AesGcm.Open(new byte[16], new byte[12], sealedBytes, null);
            });
        }

        [Test]
        public static void X25519_matches_rfc_7748_section_six()
        {
            byte[] shared = Curve25519.ScalarMult(
                Hex("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
                Hex("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"));

            Assert.ByteArraysEqual(
                Hex("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552"),
                shared);
        }

        [Test]
        public static void X25519_matches_rfc_7748_section_five_two()
        {
            byte[] alicePrivate = Hex("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            byte[] bobPrivate = Hex("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");

            byte[] alicePublic = Curve25519.PublicKey(alicePrivate);
            byte[] bobPublic = Curve25519.PublicKey(bobPrivate);

            Assert.ByteArraysEqual(
                Hex("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a"),
                alicePublic);

            Assert.ByteArraysEqual(
                Hex("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f"),
                bobPublic);

            // Both directions land on the same secret: that is the property the
            // handshake depends on, and the one a sign error in the ladder breaks.
            byte[] aliceShared = Curve25519.SharedSecret(alicePrivate, bobPublic);
            byte[] bobShared = Curve25519.SharedSecret(bobPrivate, alicePublic);

            Assert.ByteArraysEqual(Hex("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742"), aliceShared);
            Assert.ByteArraysEqual(aliceShared, bobShared);
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
