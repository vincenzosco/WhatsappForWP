using WhatsappApp.Protocol.Client;

namespace ProtocolTests
{
    /// <summary>
    /// The status of a message this phone sent, against the order the frames come in.
    ///
    /// Why these cases: on 2026-10-30 the bubble was written with whatever status the
    /// frame carried. The ack and the receipts are separate frames with no ordering
    /// between them, so a `delivered` landing behind the `read` it belongs with - or a
    /// slow ack arriving last - put a message the person had already read back to one
    /// tick. These tests are the wire order that caused it, one sequence at a time.
    /// </summary>
    public static class StatusLadderTests
    {
        /// <summary>
        /// The numbers are the app model's `MessageStatus` ordinals, and the cache stores
        /// the status as that number: a reordering here would silently rewrite every
        /// stored message's state on the next start.
        /// </summary>
        [Test]
        public static void TheRungsAreTheStatusesOfTheMessageModel()
        {
            Assert.AreEqual(0, StatusLadder.Sending, "MessageStatus.Sending is the first member");
            Assert.AreEqual(1, StatusLadder.Sent, "MessageStatus.Sent follows it");
            Assert.AreEqual(2, StatusLadder.Delivered, "MessageStatus.Delivered is the third");
            Assert.AreEqual(3, StatusLadder.Read, "MessageStatus.Read is the fourth");
            Assert.AreEqual(4, StatusLadder.Failed, "MessageStatus.Failed is the fifth");

            Assert.IsTrue(StatusLadder.Sending < StatusLadder.Sent
                && StatusLadder.Sent < StatusLadder.Delivered
                && StatusLadder.Delivered < StatusLadder.Read,
                "a receipt is only an event if the ladder goes up");
        }

        /// <summary>
        /// The sequence that lost the ticks: a `delivered` behind the `read`, and the ack
        /// last of all. The bubble ends on the highest rung any frame named, and never on
        /// a lower one than it had.
        /// </summary>
        [Test]
        public static void AReceiptThatArrivesLateNeverMovesTheBubbleBack()
        {
            int state = After(StatusLadder.Sending,
                StatusLadder.Delivered,
                StatusLadder.Read,
                StatusLadder.Delivered,
                StatusLadder.Sent);

            Assert.AreEqual(StatusLadder.Read, state,
                "a message that was read stays read when the frames arrive out of order");

            // And the same, frame by frame: the value on the row may only go up.
            state = StatusLadder.Sending;
            int[] frames = { StatusLadder.Sent, StatusLadder.Read, StatusLadder.Delivered, StatusLadder.Sent };

            for (int i = 0; i < frames.Length; i++)
            {
                int before = state;
                state = StatusLadder.Next(state, frames[i]);

                Assert.IsTrue(state >= before,
                    "the status went back from " + StatusLadder.Name(before) + " to "
                    + StatusLadder.Name(state) + " on frame " + (i + 1));
            }

            Assert.AreEqual(StatusLadder.Read, state);
        }

        /// <summary>
        /// A refusal is not the end of the story: the ack for the same id is the server
        /// saying it has the message after all, and the bubble must stop saying it failed.
        /// </summary>
        [Test]
        public static void ALateAckRepairsAMessageTheServerHadRefused()
        {
            Assert.AreEqual(StatusLadder.Failed,
                After(StatusLadder.Sending, StatusLadder.Failed),
                "a message on its way can be declared failed");

            Assert.AreEqual(StatusLadder.Sent,
                After(StatusLadder.Sending, StatusLadder.Failed, StatusLadder.Sent),
                "an ack after the refusal is a message that did go");
        }

        /// <summary>
        /// And the other way round: a refusal cannot take away a message that has already
        /// arrived. An error for a read message is the `ack` of another stanza reusing an
        /// id, or a server that answered late - neither is a reason to redraw the bubble.
        /// </summary>
        [Test]
        public static void ARefusalCannotEraseAMessageThatArrived()
        {
            Assert.AreEqual(StatusLadder.Delivered,
                After(StatusLadder.Delivered, StatusLadder.Failed),
                "a delivered message keeps its ticks when an error names its id");

            Assert.AreEqual(StatusLadder.Read,
                After(StatusLadder.Read, StatusLadder.Failed),
                "a read message keeps its blue ticks when an error names its id");

            Assert.AreEqual(StatusLadder.Sent,
                After(StatusLadder.Sent, StatusLadder.Failed),
                "only a message still on its way can be declared failed");
        }

        /// <summary>
        /// A run of frames against one message, in the order they arrived: where the
        /// bubble's status ends. It is the whole of what the app does - `Next` per frame -
        /// without a page, a socket or a phone.
        /// </summary>
        private static int After(int start, params int[] frames)
        {
            int state = start;

            for (int i = 0; i < frames.Length; i++) state = StatusLadder.Next(state, frames[i]);

            return state;
        }
    }
}
