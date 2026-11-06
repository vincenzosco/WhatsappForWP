using System;
using System.Collections.Generic;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Where a message goes in a conversation, as a rule of its own.
    ///
    /// A conversation is filled by four different
    /// arrivals - a message written here, one that arrives live, the history sync, and the
    /// echo of something another device of this account sent - and only the history was
    /// inserted by time. The other three appended, so a message older than the last one on
    /// screen sat at the bottom: the conversation opened with yesterday's
    /// rows under today's, and the history arriving afterwards was sorted by a scan that
    /// assumes the list is already in order.
    ///
    /// It is here, with no WinRT type in it, because it is the one part of that worth a
    /// test: `ProtocolTests/MessageOrderTests.cs` compiles this file by name.
    /// </summary>
    public static class MessageOrder
    {
        /// <summary>
        /// The index `moment` belongs at: after every message stamped at or before it, wherever
        /// that one is.
        ///
        /// Two properties, and both are the point:
        ///
        /// - **at or before**, so two messages of the same second keep the order they arrived
        /// in - the sync writes whole seconds, and a `<` would put each of them ahead of the
        /// one before it;
        /// - **wherever that one is**, so the answer does not depend on the list already being
        /// sorted. The phone that opened on yesterday under today was already mixed, and an
        /// insertion that stopped at the first newer row would keep it mixed for ever.
        /// </summary>
        public static int InsertIndex(IList<DateTime> times, DateTime moment)
        {
            if (times == null) return 0;

            int index = 0;
            for (int i = 0; i < times.Count; i++)
            {
                if (times[i] <= moment) index = i + 1;
            }

            return index;
        }
    }
}
