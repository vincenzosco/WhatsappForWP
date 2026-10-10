using WhatsappApp.Protocol.Signal;

namespace ProtocolTests
{
    /// <summary>
    /// The oracle's own shape.
    ///
    /// These tests do not check arithmetic - none of it exists yet. They check that the
    /// two things every later test leans on are really there: the constants the
    /// reference printed, and the reference's frozen conversation in
    /// `ProtocolTests/Vectors/SignalFixture.cs`. A fixture that quietly emptied itself
    /// (a generator that renamed a field, a transcription that lost a row) would make
    /// every test built on it pass by checking nothing, and this is the check that
    /// notices.
    /// </summary>
    public static class SignalFixtureTests
    {
        /// <summary>The constants, as `SignalConstants` documents them, from the reference's source.</summary>
        [Test]
        public static void TheConstantsAreTheReferences()
        {
            Assert.AreEqual(32, SignalConstants.AgreementPrefix.Length);

            // Cast, because Assert.AreEqual compares objects: a `byte` and an `int` of
            // the same value are not equal, and the failure reads "expected <255> but was
            // <255>" - which is the sort of message that wastes an afternoon.
            Assert.AreEqual((byte)0xFF, SignalConstants.AgreementPrefix[0]);
            Assert.AreEqual((byte)0xFF, SignalConstants.AgreementPrefix[31]);

            Assert.AreEqual("WhisperText", SignalConstants.InfoText);
            Assert.AreEqual("WhisperRatchet", SignalConstants.InfoRatchet);
            Assert.AreEqual("WhisperMessageKeys", SignalConstants.InfoMessageKeys);

            // (3 << 4) | 3, which is the 0x33 that starts every ciphertext below.
            // (3 << 4) | 3 is the 0x33 that starts every ciphertext below; the MAC is
            // the first 8 bytes of the HMAC; the key type byte is what makes an
            // identity key 33 bytes; and the two seeds are the chain's HMAC bytes.
            Assert.AreEqual((byte)0x33, SignalConstants.MessageVersionByte);
            Assert.AreEqual(8, SignalConstants.MacLength);
            Assert.AreEqual((byte)0x05, SignalConstants.KeyTypeByte);
            Assert.AreEqual((byte)1, SignalConstants.MessageKeySeed);
            Assert.AreEqual((byte)2, SignalConstants.ChainKeySeed);
        }

        /// <summary>
        /// The reference's keys. The identity is 33 bytes and the pre-keys are 33 too:
        /// the type byte is part of them, and a fixture with 32-byte keys would be a
        /// fixture for a different protocol.
        /// </summary>
        [Test]
        public static void TheFixtureCarriesTheReferenceKeys()
        {
            Assert.AreEqual(32, SignalFixture.X3dh.AliceIdentityPrivate.Length);
            Assert.AreEqual(32, SignalFixture.X3dh.AliceBasePrivate.Length);
            Assert.AreEqual(32, SignalFixture.X3dh.AliceEphemeralPrivate.Length);
            Assert.AreEqual((uint)10290, SignalFixture.X3dh.AliceRegistrationId);

            Assert.AreEqual(SignalConstants.TypedKeyLength, SignalFixture.X3dh.BobIdentityPublic.Length);
            Assert.AreEqual(SignalConstants.KeyTypeByte, SignalFixture.X3dh.BobIdentityPublic[0]);
            Assert.AreEqual(SignalConstants.TypedKeyLength, SignalFixture.X3dh.BobPreKeyPublic.Length);
            Assert.AreEqual(SignalConstants.TypedKeyLength, SignalFixture.X3dh.BobSignedPreKeyPublic.Length);
            Assert.AreEqual(64, SignalFixture.X3dh.BobSignedPreKeySignature.Length);
            Assert.AreEqual(0xD00Du, SignalFixture.X3dh.BobRegistrationId);
        }

        /// <summary>
        /// The conversation: two messages Alice sends with their expected ciphertexts,
        /// and two of Bob's replies with the text they must decrypt to. The first byte
        /// of every ciphertext is the version byte, which is also the first thing the
        /// wire format has to get right.
        /// </summary>
        [Test]
        public static void TheConversationIsFrozen()
        {
            Assert.AreEqual(2, SignalFixture.Conversation.Plaintexts.Length);
            Assert.AreEqual("A", SignalFixture.Conversation.Plaintexts[0]);
            Assert.AreEqual("B", SignalFixture.Conversation.Plaintexts[1]);

            Assert.AreEqual(2, SignalFixture.Conversation.Ciphertexts.Length);
            Assert.AreEqual(SignalConstants.MessageVersionByte,
                SignalFixture.Hex(SignalFixture.Conversation.Ciphertexts[0])[0]);
            Assert.IsTrue(SignalFixture.Conversation.Ciphertexts[0] != SignalFixture.Conversation.Ciphertexts[1],
                "two messages are two ciphertexts");

            Assert.AreEqual(2, SignalFixture.Conversation.Received.Length);
            Assert.AreEqual("C", SignalFixture.Conversation.ReceivedTexts[0]);
            Assert.AreEqual("D", SignalFixture.Conversation.ReceivedTexts[1]);

            // The first reply arrives with a new ratchet key, which is what makes it a
            // ratchet rather than a chain.
            Assert.AreEqual(32, SignalFixture.Hex(SignalFixture.Conversation.ReceivedEphemeralKeys[0]).Length);
        }
    }
}
