using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The call key expansion, against vectors this code did not produce.
    ///
    /// Each expected value below comes from Node's `crypto.hkdfSync` for the same inputs, so
    /// a mistake in the HKDF port, in the parameter order, or in the UTF-8 encoding of the
    /// call id shows up here instead of as a call that connects and says nothing.
    ///
    /// The shapes being pinned come from the reference's `DefaultCryptoCallback`, decompiled
    /// out of the APK on 2 November 2026 - see `CallKeys` for the exact call sites.
    /// </summary>
    public static class CallKeysTests
    {
        private const string CallId = "CALLBBBB0002";

        [Test]
        public static void TheCallKeyExpandsToFortySixBytes()
        {
            byte[] expanded = CallKeys.Expand(Key(), CallId);

            Assert.AreEqual(CallKeys.ExpandedKeyLength, expanded.Length, "46 bytes, the reference's length");
            Assert.AreEqual(
                "f79d15ce604948cf5c6f3b11a16016be290a4f063da4717d5c01ae26768200afc259c43a7c0f32a8234f8516e895",
                ToHex(expanded), "HKDF-SHA256 with no salt and the call id as info");
        }

        [Test]
        public static void TheExpansionBelongsToItsCall()
        {
            byte[] first = CallKeys.Expand(Key(), "CALLAAAA0001");
            byte[] second = CallKeys.Expand(Key(), "CALLBBBB0002");

            Assert.AreEqual(CallKeys.ExpandedKeyLength, second.Length);
            Assert.IsFalse(ToHex(first) == ToHex(second),
                "two calls with one key must not be given the same media key material");
        }

        [Test]
        public static void TheExpansionIsDeterministic()
        {
            Assert.AreEqual(ToHex(CallKeys.Expand(Key(), CallId)), ToHex(CallKeys.Expand(Key(), CallId)),
                "the other side derives the same bytes from the same call");
        }

        [Test]
        public static void TheSecureSsrcIsFourBytesAndMatchesTheReferenceOrder()
        {
            byte[] ssrc = CallKeys.SecureSsrc(Key(), System.Text.Encoding.UTF8.GetBytes(CallId),
                new byte[] { 0x0b, 0xad, 0xf0, 0x0d });

            Assert.AreEqual(CallKeys.SecureSsrcLength, ssrc.Length);
            Assert.AreEqual("67ea016d", ToHex(ssrc),
                "key first, then the four-byte salt, then the info - the reference's A02 order");
        }

        [Test]
        public static void AKeyThatIsNotThirtyTwoBytesIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { CallKeys.Expand(new byte[31], CallId); });
            Assert.Throws<ProtocolException>(delegate { CallKeys.Expand(new byte[33], CallId); });
            Assert.Throws<ProtocolException>(delegate { CallKeys.Expand(null, CallId); });
        }

        [Test]
        public static void ASsrcSaltThatIsNotFourBytesIsRefused()
        {
            byte[] info = System.Text.Encoding.UTF8.GetBytes(CallId);

            Assert.Throws<ProtocolException>(delegate { CallKeys.SecureSsrc(Key(), info, new byte[0]); });
            Assert.Throws<ProtocolException>(delegate { CallKeys.SecureSsrc(Key(), info, new byte[3]); });
            Assert.Throws<ProtocolException>(delegate { CallKeys.SecureSsrc(Key(), info, new byte[5]); });
            Assert.Throws<ProtocolException>(delegate { CallKeys.SecureSsrc(Key(), info, null); });
        }

        [Test]
        public static void ADerivationWithNoKeyOrNoLengthIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { CallKeys.Derive(null, null, null, 16); });
            Assert.Throws<ProtocolException>(delegate { CallKeys.Derive(new byte[0], null, null, 16); });
            Assert.Throws<ProtocolException>(delegate { CallKeys.Derive(Key(), null, null, 0); });
        }

        [Test]
        public static void TheGeneralDerivationMatchesTheOneTheReferenceUsesForSsrc()
        {
            byte[] info = System.Text.Encoding.UTF8.GetBytes(CallId);
            byte[] salt = new byte[] { 0x0b, 0xad, 0xf0, 0x0d };

            Assert.AreEqual(ToHex(CallKeys.SecureSsrc(Key(), info, salt)),
                ToHex(CallKeys.Derive(Key(), salt, info, 4)),
                "one HKDF, two spellings");
        }

        [Test]
        public static void TheMediaSplitIsThirtyTwoBytesOfKeyAndFourteenOfSalt()
        {
            byte[] expanded = CallKeys.Expand(Key(), CallId);
            CallMediaKeys media = CallKeys.SplitMedia(expanded);

            Assert.AreEqual(CallKeys.MediaKeyLength, media.Key.Length, "32 bytes of AES-256-CM key");
            Assert.AreEqual(CallKeys.MediaSaltLength, media.Salt.Length, "14 bytes of salt");
            Assert.AreEqual(ToHex(expanded), ToHex(media.Key) + ToHex(media.Salt),
                "the two halves are the run, in order, with nothing added or dropped");

            var sender = new SrtpSession(media.Key, media.Salt, 0x01020304);
            var receiver = new SrtpSession(media.Key, media.Salt, 0x01020304);
            byte[] packet = RtpPacket.Opus(1, 960, 0x01020304, new byte[] { 1, 2, 3, 4 }).ToBytes();
            byte[] sealedPacket = sender.Seal(packet, packet.Length);

            Assert.IsTrue(receiver.Open(sealedPacket, sealedPacket.Length) != null,
                "the 46 bytes a call carries seal and open an SRTP packet with AES-256-CM");
        }

        [Test]
        public static void ASplitThatIsNotFortySixBytesIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { CallKeys.SplitMedia(null); });
            Assert.Throws<ProtocolException>(delegate { CallKeys.SplitMedia(new byte[45]); });
        }

        /// <summary>The key the vectors were computed with: the bytes 0x00 .. 0x1f.</summary>
        private static byte[] Key()
        {
            var key = new byte[CallKeys.CallKeyLength];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)i;

            return key;
        }

        /// <summary>
        /// Bytes as lower-case hex, the form the vectors are written in.
        ///
        /// One direction only: an overload that also parsed hex made the expected value of an
        /// assertion resolve to `byte[]` while the actual was a `string`, and two identical
        /// values then compared unequal - the kind of failure that looks like a crypto bug and
        /// is a test-API bug.
        /// </summary>
        private static string ToHex(byte[] bytes)
        {
            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("x2"));

            return text.ToString();
        }
    }
}
