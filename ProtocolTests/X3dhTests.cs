using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Signal;

namespace ProtocolTests
{
    /// <summary>
    /// The agreement on its own, against a value that was computed from the same keys by
    /// something else.
    ///
    /// `SignalSessionTests` already reproduces the reference's conversation byte for byte,
    /// and that test covers this code - but only through the four-DH form, and only in the
    /// direction the conversation happens to take. What is here instead is the arithmetic:
    /// both sides of the agreement, with and without the one-time pre-key, against the root
    /// key `signal-root-key.js` derives with OpenSSL's X25519 and HKDF.
    ///
    /// Why a script and not a specification vector: the X3DH specification's revision 1
    /// publishes none, and neither does the Double Ratchet's revision 4. The primitives do
    /// have published vectors - RFC 7748 for the curve and RFC 5869 for HKDF, both in
    /// `CryptoTests` - so what was missing was the *composition* of them, and that is what
    /// the fixture's `RootKey` is.
    /// </summary>
    public static class X3dhTests
    {
        /// <summary>
        /// The sender's half is the fixture's root key, byte for byte.
        /// </summary>
        [Test]
        public static void TheSenderAgreesOnTheRootKeyTheReferenceKeysProduce()
        {
            Assert.ByteArraysEqual(
                SignalFixture.X3dh.RootKey,
                X3dh.AliceSecret(
                    SignalFixture.X3dh.AliceIdentityPrivate,
                    SignalFixture.X3dh.AliceBasePrivate,
                    SignalFixture.X3dh.BobIdentityPublic,
                    SignalFixture.X3dh.BobSignedPreKeyPublic,
                    SignalFixture.X3dh.BobPreKeyPublic));
        }

        /// <summary>
        /// And so does the receiver's, from the private halves and the two public keys the
        /// message would carry. This is the half that the first ciphertext cannot check on
        /// its own: a DH in the wrong slot here would still decrypt anything the same
        /// mistake on the other side produced.
        /// </summary>
        [Test]
        public static void TheReceiverAgreesOnTheSameRootKey()
        {
            Assert.ByteArraysEqual(
                SignalFixture.X3dh.RootKey,
                X3dh.BobSecret(
                    SignalFixture.BobSide.IdentityPrivate,
                    SignalFixture.BobSide.SignedPreKeyPrivate,
                    SignalFixture.BobSide.PreKeyPrivate,
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceIdentityPrivate),
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceBasePrivate)));
        }

        /// <summary>
        /// Without a one-time pre-key the agreement is the three-DH form, and both sides
        /// still have to land on the same key: the ordinary case once a device has handed
        /// out all the pre-keys it published.
        /// </summary>
        [Test]
        public static void WithoutAOneTimePreKeyBothSidesStillAgree()
        {
            byte[] alice = X3dh.AliceSecret(
                SignalFixture.X3dh.AliceIdentityPrivate,
                SignalFixture.X3dh.AliceBasePrivate,
                SignalFixture.X3dh.BobIdentityPublic,
                SignalFixture.X3dh.BobSignedPreKeyPublic,
                null);

            Assert.ByteArraysEqual(SignalFixture.X3dh.RootKeyWithoutPreKey, alice);

            Assert.ByteArraysEqual(
                SignalFixture.X3dh.RootKeyWithoutPreKey,
                X3dh.BobSecret(
                    SignalFixture.BobSide.IdentityPrivate,
                    SignalFixture.BobSide.SignedPreKeyPrivate,
                    null,
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceIdentityPrivate),
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceBasePrivate)));
        }

        /// <summary>
        /// The two forms are two keys, which is the one thing a sender and a receiver can
        /// disagree about without noticing: if the fourth DH were dropped - or added when
        /// there is no pre-key - both sides would agree on a key nobody else could derive,
        /// and the session would work against itself and against nobody else.
        /// </summary>
        [Test]
        public static void TheTwoFormsAreTwoKeys()
        {
            Assert.IsFalse(AreEqual(SignalFixture.X3dh.RootKey, SignalFixture.X3dh.RootKeyWithoutPreKey),
                "a one-time pre-key has to change the root key");
        }

        /// <summary>
        /// A key that is not a key is refused rather than padded, truncated or read past:
        /// 31 bytes and 34 bytes are not an X25519 key either way, and a 33-byte one has to
        /// start with the type byte rather than with whatever the buffer happens to hold.
        ///
        /// Every argument of both sides is exercised, because a length check that is only
        /// on the first parameter is a check the caller of the second one does not get.
        /// </summary>
        [Test]
        public static void AKeyOfTheWrongLengthIsRefused()
        {
            var short31 = new byte[31];
            var long34 = new byte[34];

            var wrongType = new byte[SignalConstants.TypedKeyLength];
            wrongType[0] = 0x00;   // 33 bytes, and not the 0x05 of a typed public key

            // The sender's half: her identity, her ephemeral, his identity, his signed
            // pre-key, and the one-time pre-key that may be absent.
            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.AliceSecret(short31, SignalFixture.X3dh.AliceBasePrivate,
                    SignalFixture.X3dh.BobIdentityPublic, SignalFixture.X3dh.BobSignedPreKeyPublic, null);
            });

            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.AliceSecret(SignalFixture.X3dh.AliceIdentityPrivate, long34,
                    SignalFixture.X3dh.BobIdentityPublic, SignalFixture.X3dh.BobSignedPreKeyPublic, null);
            });

            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.AliceSecret(SignalFixture.X3dh.AliceIdentityPrivate, SignalFixture.X3dh.AliceBasePrivate,
                    wrongType, SignalFixture.X3dh.BobSignedPreKeyPublic, null);
            });

            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.AliceSecret(SignalFixture.X3dh.AliceIdentityPrivate, SignalFixture.X3dh.AliceBasePrivate,
                    SignalFixture.X3dh.BobIdentityPublic, wrongType, null);
            });

            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.AliceSecret(SignalFixture.X3dh.AliceIdentityPrivate, SignalFixture.X3dh.AliceBasePrivate,
                    SignalFixture.X3dh.BobIdentityPublic, SignalFixture.X3dh.BobSignedPreKeyPublic, short31);
            });

            // And the receiver's, where the sender's two halves arrive as the public keys
            // the message carries.
            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.BobSecret(short31, SignalFixture.BobSide.SignedPreKeyPrivate, null,
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceIdentityPrivate),
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceBasePrivate));
            });

            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.BobSecret(SignalFixture.BobSide.IdentityPrivate, short31, null,
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceIdentityPrivate),
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceBasePrivate));
            });

            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.BobSecret(SignalFixture.BobSide.IdentityPrivate, SignalFixture.BobSide.SignedPreKeyPrivate, null,
                    wrongType, X3dh.TypedPublicKey(SignalFixture.X3dh.AliceBasePrivate));
            });

            Assert.Throws<ProtocolException>(delegate
            {
                X3dh.BobSecret(SignalFixture.BobSide.IdentityPrivate, SignalFixture.BobSide.SignedPreKeyPrivate, null,
                    X3dh.TypedPublicKey(SignalFixture.X3dh.AliceIdentityPrivate), long34);
            });

            // The one-time pre-key is optional, which means null is the *absence* of an
            // argument and not a bad one: absent is the three-DH form, and a zero-length
            // key is the same thing (a caller with an empty buffer).
            Assert.ByteArraysEqual(
                X3dh.AliceSecret(SignalFixture.X3dh.AliceIdentityPrivate, SignalFixture.X3dh.AliceBasePrivate,
                    SignalFixture.X3dh.BobIdentityPublic, SignalFixture.X3dh.BobSignedPreKeyPublic, null),
                X3dh.AliceSecret(SignalFixture.X3dh.AliceIdentityPrivate, SignalFixture.X3dh.AliceBasePrivate,
                    SignalFixture.X3dh.BobIdentityPublic, SignalFixture.X3dh.BobSignedPreKeyPublic, new byte[0]));
        }

        /// <summary>`Assert` has no byte comparison that answers a question; this is the one the test above needs.</summary>
        private static bool AreEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }
    }
}
