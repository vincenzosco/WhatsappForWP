using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Signal;

namespace ProtocolTests
{
    /// <summary>
    /// What a send owes the store: the session it just moved, written back.
    ///
    /// `Encrypt` advances the sending chain, and only the file says
    /// where that chain is. On the app encrypted, sent, and saved nothing -
    /// every send reloaded the record as it was before the first message and re-encrypted the
    /// same key. The server accepted all three (`the server has message`), the recipient's
    /// phone had already spent that key on the first copy, and it dropped the other two as
    /// duplicates without answering anything: one delivery, one read receipt, for three
    /// messages.
    /// </summary>
    public static class SessionSaveTests
    {
        private static readonly byte[] Plain = { 1, 2, 3, 4, 5 };

        /// <summary>The failure itself: the same state twice is the same key twice.</summary>
        [Test]
        public static void ASessionThatIsNotWrittenBackSendsTheSameKeyTwice()
        {
            var random = new SignalSessions.CounterRandom();
            SessionRecord alice = SignalSessions.Alice(random);

            // What the store still holds after a send that forgot to write it back.
            byte[] asItWasBefore = alice.Encode();

            byte[] first = alice.Encrypt(Plain);

            SessionRecord reloaded = SessionRecord.Decode(asItWasBefore, random);
            byte[] again = reloaded.Encrypt(Plain);

            Assert.ByteArraysEqual(first, again,
                "a session left at its pre-send state re-encrypts the same key, and the "
                + "recipient drops the copy as already read");
        }

        /// <summary>
        /// The rule: a record written back after the send moves, and the peer reads both
        /// messages.
        /// </summary>
        [Test]
        public static void ASessionWrittenBackMovesAndThePeerReadsBoth()
        {
            var random = new SignalSessions.CounterRandom();
            SessionRecord bob = SignalSessions.Bob(random);
            SessionRecord alice = SignalSessions.Alice(random);

            bool preKey = alice.PreKeyPending;

            byte[] first = alice.Encrypt(Plain);

            // What the app does now, right after the stanza is on the wire.
            SessionRecord writtenBack = SessionRecord.Decode(alice.Encode(), random);
            bool secondPreKey = writtenBack.PreKeyPending;
            byte[] second = writtenBack.Encrypt(Plain);

            Assert.IsFalse(SameKey(first, second),
                "the chain moved, so the second message is not the first in another envelope");

            Assert.ByteArraysEqual(Plain, bob.Decrypt(first, preKey));
            Assert.ByteArraysEqual(Plain, bob.Decrypt(second, secondPreKey),
                "and the copy that used to be a duplicate now opens");
        }

        /// <summary>Two ciphertexts carry the same message key when their bodies are equal.</summary>
        private static bool SameKey(byte[] left, byte[] right)
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
