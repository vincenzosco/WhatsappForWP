namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The rungs a message this phone sent climbs - written, taken by the server,
    /// delivered, read - plus the one way off the ladder, a refusal.
    ///
    /// Why it lives in the core and not in the page: the answers a sent message gets
    /// arrive as separate frames. The server's `&lt;ack&gt;` and the recipient's
    /// receipts are told apart by `InboundRouter` and announced by different methods,
    /// and nothing in between orders them. What keeps the bubble honest is therefore
    /// one rule - a status never goes backwards - and a rule is worth a test that does
    /// not need a phone, which is what the protocol suite is.
    ///
    /// The numbers are the ordinals of `MessageStatus` in `Models/ChatMessage.cs`:
    /// Sending, Sent, Delivered, Read, Failed. That order is already a contract, not a
    /// coincidence - the message cache stores the status as this number - so the app
    /// hands its enum over as an int and `StatusLadderTests` pins the values.
    /// </summary>
    public static class StatusLadder
    {
        public const int Sending = 0;
        public const int Sent = 1;
        public const int Delivered = 2;
        public const int Read = 3;
        public const int Failed = 4;

        /// <summary>
        /// Whether `wanted` may replace `current`.
        ///
        /// A refusal is not a rung: only a message still on its way can be declared
        /// failed, so a `delivered` or a `read` that is already on the row is not erased
        /// by a late error - and an error for a message the person has read would be a
        /// lie about a message that arrived.
        ///
        /// A late ack may still repair a refusal, because a refusal is the server's first
        /// word and not its last: the same id acked afterwards is a message that went.
        /// </summary>
        public static bool IsForward(int current, int wanted)
        {
            if (wanted == Failed) return current == Sending;
            if (current == Failed) return true;

            return wanted > current;
        }

        /// <summary>
        /// The status after one event: the rung climbed, or the one already held. The
        /// caller that stores it never has to ask a second question.
        /// </summary>
        public static int Next(int current, int wanted)
        {
            return IsForward(current, wanted) ? wanted : current;
        }

        /// <summary>The name of a rung, for a log line or a test that failed.</summary>
        public static string Name(int status)
        {
            if (status == Sending) return "sending";
            if (status == Sent) return "sent";
            if (status == Delivered) return "delivered";
            if (status == Read) return "read";
            if (status == Failed) return "failed";

            return "unknown";
        }
    }
}
