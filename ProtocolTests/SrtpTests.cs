using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// SRTP: what goes in comes back out, and what was tampered with does not.
    ///
    /// Why there is no RFC vector pinned here: the RFC's sample keys are an expensive
    /// transcription whose value is only against a peer, and this client has no peer to test
    /// with until the call signalling carries a key. What is pinned instead is the contract a
    /// peer depends on - a packet that round-trips byte for byte, a header that stays
    /// readable, the sequence number feeding the keystream, and a tag that is checked before
    /// anything is decrypted - plus the one property the key derivation must have: two
    /// different labels never produce the same key.
    /// </summary>
    public static class SrtpTests
    {
        private const uint Ssrc = 0x01020304;

        private static byte[] Key()
        {
            var key = new byte[16];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
            return key;
        }

        private static byte[] Salt()
        {
            var salt = new byte[14];
            for (int i = 0; i < salt.Length; i++) salt[i] = (byte)(0xA0 + i);
            return salt;
        }

        private static byte[] Frame()
        {
            return new byte[] { 0x78, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        }

        [Test]
        public static void ASealedPacketComesBackByteForByte()
        {
            var sender = new SrtpSession(Key(), Salt(), Ssrc);
            var receiver = new SrtpSession(Key(), Salt(), Ssrc);

            byte[] plain = RtpPacket.Opus(1, 960, Ssrc, Frame()).ToBytes();
            byte[] sealed_ = sender.Seal(plain, plain.Length);
            byte[] opened = receiver.Open(sealed_, sealed_.Length);

            Assert.IsTrue(opened != null, "the tag verified");
            Assert.AreEqual(plain.Length, opened.Length, "same length once the tag is off");

            for (int i = 0; i < plain.Length; i++)
            {
                Assert.AreEqual(plain[i], opened[i], "byte " + i + " survived the round trip");
            }
        }

        [Test]
        public static void TheHeaderStaysReadableAndThePayloadDoesNot()
        {
            var session = new SrtpSession(Key(), Salt(), Ssrc);

            byte[] plain = RtpPacket.Opus(7, 960, Ssrc, Frame()).ToBytes();
            byte[] sealed_ = session.Seal(plain, plain.Length);

            Assert.AreEqual(plain[0], sealed_[0], "the version byte is authenticated, not encrypted");
            Assert.AreEqual(plain[2], sealed_[2], "and the sequence number is what the keystream needs");

            bool payloadDiffers = false;
            for (int i = 12; i < plain.Length; i++)
            {
                if (plain[i] != sealed_[i]) payloadDiffers = true;
            }

            Assert.IsTrue(payloadDiffers, "the payload is not sent in the clear");
            Assert.AreEqual(plain.Length + SrtpSession.TagLength, sealed_.Length);
        }

        [Test]
        public static void ATamperedPayloadIsRefusedBeforeItIsDecrypted()
        {
            var sender = new SrtpSession(Key(), Salt(), Ssrc);
            var receiver = new SrtpSession(Key(), Salt(), Ssrc);

            byte[] plain = RtpPacket.Opus(1, 960, Ssrc, Frame()).ToBytes();
            byte[] sealed_ = sender.Seal(plain, plain.Length);

            sealed_[14] = (byte)(sealed_[14] ^ 0x40);

            Assert.IsTrue(receiver.Open(sealed_, sealed_.Length) == null, "one flipped bit is one dropped packet");
        }

        [Test]
        public static void ATamperedTagIsRefused()
        {
            var sender = new SrtpSession(Key(), Salt(), Ssrc);
            var receiver = new SrtpSession(Key(), Salt(), Ssrc);

            byte[] plain = RtpPacket.Opus(1, 960, Ssrc, Frame()).ToBytes();
            byte[] sealed_ = sender.Seal(plain, plain.Length);

            sealed_[sealed_.Length - 1] = (byte)(sealed_[sealed_.Length - 1] ^ 0x01);

            Assert.IsTrue(receiver.Open(sealed_, sealed_.Length) == null, "a tag that does not verify is not read");
        }

        [Test]
        public static void AnotherSaltProducesAnotherKeystream()
        {
            var sender = new SrtpSession(Key(), Salt(), Ssrc);

            byte[] otherSalt = Salt();
            otherSalt[3] = (byte)(otherSalt[3] ^ 0xFF);
            var stranger = new SrtpSession(Key(), otherSalt, Ssrc);

            byte[] plain = RtpPacket.Opus(1, 960, Ssrc, Frame()).ToBytes();
            byte[] sealed_ = sender.Seal(plain, plain.Length);

            Assert.IsTrue(stranger.Open(sealed_, sealed_.Length) == null, "the wrong session cannot read it");
        }

        /// <summary>
        /// The one property of the derivation that a bug would hide: if the labels were not
        /// mixed in, the encryption key, the authentication key and the salt would be the same
        /// bytes, and every packet's tag would be computable from its keystream.
        /// </summary>
        [Test]
        public static void TheThreeDerivedValuesAreNeverTheSameBytes()
        {
            byte[] encryption = SrtpSession.Derive(Key(), Salt(), 0x00, 16);
            byte[] authentication = SrtpSession.Derive(Key(), Salt(), 0x01, 16);
            byte[] salt = SrtpSession.Derive(Key(), Salt(), 0x02, 16);

            Assert.IsFalse(Same(encryption, authentication), "encryption and authentication keys differ");
            Assert.IsFalse(Same(encryption, salt), "and so does the salt");
            Assert.IsFalse(Same(authentication, salt));

            Assert.IsTrue(Same(encryption, SrtpSession.Derive(Key(), Salt(), 0x00, 16)),
                "and the same inputs always give the same key");
        }

        [Test]
        public static void ASequenceWrapStillDecrypts()
        {
            var sender = new SrtpSession(Key(), Salt(), Ssrc);
            var receiver = new SrtpSession(Key(), Salt(), Ssrc);

            // Across the 16-bit boundary, in order: the rollover counter is what keeps the two
            // ends on the same keystream, and this is the case that gets it wrong.
            for (int sequence = 65534; sequence <= 65537; sequence++)
            {
                byte[] plain = RtpPacket.Opus(sequence, (uint)sequence * 960, Ssrc, Frame()).ToBytes();
                byte[] sealed_ = sender.Seal(plain, plain.Length);
                byte[] opened = receiver.Open(sealed_, sealed_.Length);

                Assert.IsTrue(opened != null, "packet " + sequence + " decrypted");
                Assert.AreEqual(plain[3] & 0xff, opened[3] & 0xff, "with its own sequence number");
            }
        }

        [Test]
        public static void TheWrongKeyOrSaltSizeIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { new SrtpSession(new byte[8], Salt(), Ssrc); });
            Assert.Throws<ProtocolException>(delegate { new SrtpSession(Key(), new byte[4], Ssrc); });
            Assert.Throws<ProtocolException>(delegate { new SrtpSession(null, Salt(), Ssrc); });
        }

        [Test]
        public static void TooShortToBeAPacketIsNotAPacket()
        {
            var session = new SrtpSession(Key(), Salt(), Ssrc);

            Assert.IsTrue(session.Open(null, 0) == null, "no bytes, no packet");
            Assert.IsTrue(session.Open(new byte[8], 8) == null, "eight bytes cannot hold a header and a tag");
        }

        private static bool Same(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }
    }
}
