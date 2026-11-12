using System;
using System.Collections.Generic;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decides where a message goes inside a conversation.
    ///
    /// A conversation on screen is a list of messages in time order. A new message can
    /// arrive three ways - it is typed here, it arrives live, or it comes from the history
    /// download - and it must land in the right place in that list, not simply at the end.
    /// Otherwise a message from yesterday would appear under today's messages.
    ///
    /// This file has no phone-specific types on purpose, so a plain C# test can compile it:
    /// see `ProtocolTests/MessageOrderTests.cs`.
    /// </summary>
    public static class MessageOrder
    {
        /// <summary>
        /// The position `moment` should have in a list of message times.
        ///
        /// The answer is "after the last message that is not newer than this one". Two
        /// details matter:
        ///
        /// - `&lt;=` and not `&lt;`: when two messages have the same time (the history
        ///   download only stores whole seconds, so this happens often), the newer arrival
        ///   still goes after the older one, keeping the order they were received in;
        /// - the whole list is scanned: the answer must not depend on the list already being
        ///   sorted, because a list read back from the phone may be mixed up.
        /// </summary>
        public static int InsertIndex(IList<DateTime> times, DateTime moment)
        {
            // No list at all: the message is the first one.
            if (times == null) return 0;

            // Start at the top; every time we find a message that is not newer, remember
            // "just after it". The last hit wins, which is what "after the last one" means.
            int index = 0;
            for (int i = 0; i < times.Count; i++)
            {
                if (times[i] <= moment) index = i + 1;
            }

            return index;
        }

        /// <summary>
        /// The order rows should have, expressed as positions in `times`.
        ///
        /// For example, if the times are 10:02, 10:00, 10:00, the answer is 1, 2, 0: the row
        /// at position 1 comes first, then the row at position 2, then the row at position 0.
        /// Two rows with the same time keep the order they already have (a "stable" sort).
        ///
        /// This is an insertion sort: it walks the list once and slides each row left until it
        /// meets a row that is not newer. On a list that is already nearly in order - the usual
        /// case when a conversation is read back from the phone - it is almost free.
        /// </summary>
        public static int[] StableOrder(IList<DateTime> times)
        {
            if (times == null) return new int[0];

            // `order` starts as "no change": position i holds the row i.
            var order = new int[times.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            // Position 0 cannot move left, so the loop starts at 1.
            for (int i = 1; i < order.Length; i++)
            {
                int moving = order[i];
                int at = i - 1;

                // Slide the rows that are strictly newer one place to the right, opening the
                // hole where `moving` belongs. "Strictly" `>` and not `>=` is what keeps two
                // rows of the same time in their original order.
                while (at >= 0 && times[order[at]] > times[moving])
                {
                    order[at + 1] = order[at];
                    at--;
                }

                // `at` is now the row just before the hole, so the hole is `at + 1`.
                order[at + 1] = moving;
            }

            return order;
        }
    }
}
