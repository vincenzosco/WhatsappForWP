using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;

namespace ProtocolTests
{
    /// <summary>
    /// The two sides of the reference's conversation, built on demand.
    ///
    /// Two test files need a session - the conversation's own tests, and the store's, which
    /// have nothing to do with the reference's bytes and only want something worth saving -
    /// and neither should build the other's. What is here is the part they share: the
    /// bundle the server would hand out, the receiver's session built from the reference's
    /// first message, and the sender's built from that bundle.
    ///
    /// The random source stays the caller's, because it is the one thing the two files need
    /// differently: the conversation wants the fixture's own keys so its bytes can be
    /// compared, and a test that only saves and loads a session wants keys that are merely
    /// distinct.
    /// </summary>
    internal static class SignalSessions
    {
        /// <summary>The sender's session, from the bundle below.</summary>
        public static SessionRecord Alice(IRandomSource random)
        {
            return SessionRecord.StartAsInitiator(
                SignalFixture.X3dh.AliceIdentityPrivate,
                SignalFixture.X3dh.AliceBasePrivate,
                SignalFixture.X3dh.AliceEphemeralPrivate,
                SignalFixture.X3dh.AliceRegistrationId,
                Bundle(),
                random);
        }

        /// <summary>The receiver's session, built from the reference's own first message.</summary>
        public static SessionRecord Bob(IRandomSource random)
        {
            return SessionRecord.StartAsResponder(
                SignalFixture.BobSide.IdentityPrivate,
                SignalFixture.BobSide.SignedPreKeyPrivate,
                SignalFixture.BobSide.PreKeyPrivate,
                SignalFixture.X3dh.BobRegistrationId,
                PreKeySignalMessage.Decode(SignalEnvelope.Unwrap(FirstMessage())),
                random);
        }

        /// <summary>The reference's own first message: the receiver's session is built from it.</summary>
        public static byte[] FirstMessage()
        {
            return SignalFixture.Hex(SignalFixture.BobSide.Received[0]);
        }

        /// <summary>The receiver's device, as the server would hand it out.</summary>
        public static PreKeyBundle Bundle()
        {
            var bundle = new PreKeyBundle();
            bundle.RegistrationId = SignalFixture.X3dh.BobRegistrationId;
            bundle.IdentityKey = SignalFixture.X3dh.BobIdentityPublic;
            bundle.PreKeyId = SignalFixture.X3dh.BobPreKeyId;
            bundle.HasPreKey = true;
            bundle.PreKeyPublic = SignalFixture.X3dh.BobPreKeyPublic;
            bundle.SignedPreKeyId = SignalFixture.X3dh.BobSignedPreKeyId;
            bundle.HasSignedPreKey = true;
            bundle.SignedPreKeyPublic = SignalFixture.X3dh.BobSignedPreKeyPublic;
            bundle.SignedPreKeySignature = SignalFixture.X3dh.BobSignedPreKeySignature;

            return bundle;
        }

        /// <summary>
        /// A source that answers a different key every time, which is what a conversation
        /// longer than the fixture's needs: the fixture holds one ratchet key per side, and
        /// a second step that drew the first key again would send a message on a chain the
        /// peer has already finished with. The bytes are not the reference's - nothing in a
        /// conversation this long is - and they only have to be keys, so each draw is its
        /// predecessor's bytes plus one: distinct, 32 bytes each.
        /// </summary>
        public sealed class CounterRandom : IRandomSource
        {
            private int _draws;

            public byte[] Bytes(int count)
            {
                _draws++;

                var key = new byte[count];
                for (int i = 0; i < count; i++) key[i] = (byte)(0x10 + _draws);

                return key;
            }
        }
    }
}
