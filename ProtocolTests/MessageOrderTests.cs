using System;
using System.Collections.Generic;
using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// The rule that keeps a conversation in time order, in and out of the phone's own
    /// arrivals.
    ///
    /// Why it is worth a test: the four things that fill a conversation (this phone's sends,
    /// a live message, the history sync, and the echo of another device's send) arrive in
    /// whatever order the network feels like, and a rule that appended them showed the
    /// conversation with yesterday's rows under today's.
    /// </summary>
    public static class MessageOrderTests
    {
        private static readonly DateTime Ten = new DateTime(2026, 11, 1, 10, 0, 0);

        [Test]
        public static void AMessageGoesAfterTheLastOneItIsNotOlderThan()
        {
            var times = new List<DateTime> { Ten, Ten.AddMinutes(1), Ten.AddMinutes(2) };

            Assert.AreEqual(3, MessageOrder.InsertIndex(times, Ten.AddMinutes(3)),
                "the newest message goes at the bottom");
            Assert.AreEqual(0, MessageOrder.InsertIndex(times, Ten.AddMinutes(-1)),
                "and an older one at the top");
            Assert.AreEqual(1, MessageOrder.InsertIndex(times, Ten),
                "a message with the time of the first one goes after it");
        }

        [Test]
        public static void AMessageOfTheSameSecondKeepsTheArrivalOrder()
        {
            var times = new List<DateTime> { Ten, Ten };

            Assert.AreEqual(2, MessageOrder.InsertIndex(times, Ten),
                "the sync writes whole seconds, and a `<` would put every one of them ahead");
        }

        [Test]
        public static void AListAlreadyOutOfOrderStillTakesTheMessageInTime()
        {
            // The phone's own conversation: ten, twelve, eleven - a live message that arrived
            // late, which is the shape this rule exists for.
            var times = new List<DateTime> { Ten, Ten.AddMinutes(2), Ten.AddMinutes(1) };

            Assert.AreEqual(3, MessageOrder.InsertIndex(times, Ten.AddMinutes(3)),
                "newer than every row, so it goes at the bottom - after the last one, not past it");
            Assert.AreEqual(0, MessageOrder.InsertIndex(times, Ten.AddMinutes(-5)),
                "older than every row, so it goes at the top");

            // Half past eleven: after the eleven, which is the third row - not after the
            // first row that is newer (the twelve). An insertion that stopped at the first
            // newer row would put it above the eleven and leave the list as mixed as it was.
            Assert.AreEqual(3, MessageOrder.InsertIndex(times, Ten.AddMinutes(1).AddSeconds(30)),
                "a message lands after the last row it is not older than, wherever that is");
        }

        [Test]
        public static void AnEmptyConversationTakesTheFirstMessage()
        {
            Assert.AreEqual(0, MessageOrder.InsertIndex(new List<DateTime>(), Ten));
            Assert.AreEqual(0, MessageOrder.InsertIndex(null, Ten), "no rows is index zero");
        }
    }
}
