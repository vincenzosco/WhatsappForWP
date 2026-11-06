using System;
using WhatsappApp.Protocol.Client;

namespace ProtocolTests
{
    /// <summary>
    /// The time in front of a send: what is left of it, and when it is gone.
    ///
    /// The bug these pin: a message took about a minute to leave, because
    /// every question in front of the write waited twenty seconds of its own and nothing
    /// bounded their sum. The budget is the bound, and the arithmetic is the whole of it.
    /// </summary>
    public static class SendBudgetTests
    {
        private const long Start = 1000000L;

        private static long After(int milliseconds)
        {
            return Start + milliseconds * TimeSpan.TicksPerMillisecond;
        }

        [Test]
        public static void AFreshBudgetOffersItsWholeWindow()
        {
            SendBudget budget = SendBudget.Start(Start);

            Assert.AreEqual((long)SendBudget.Milliseconds, budget.MillisecondsLeft(Start));
            Assert.AreEqual(3, budget.SecondsLeft(Start), "three seconds, in whole seconds");
            Assert.IsTrue(!budget.IsSpent(Start));
        }

        [Test]
        public static void HalfWayThroughItOffersWhatIsLeft()
        {
            SendBudget budget = SendBudget.Start(Start);

            Assert.AreEqual(1500L, budget.MillisecondsLeft(After(1500)));
            Assert.AreEqual(1, budget.SecondsLeft(After(1500)),
                "a second and a half is one whole second of deadline, not two");
        }

        [Test]
        public static void ASpentBudgetIsSpentAndNeverNegative()
        {
            SendBudget budget = SendBudget.Start(Start);

            Assert.AreEqual(0L, budget.MillisecondsLeft(After(5000)), "past the end it is zero, not a debt");
            Assert.AreEqual(0, budget.SecondsLeft(After(5000)));
            Assert.IsTrue(budget.IsSpent(After(3000)), "three seconds is the end of it");
            Assert.IsTrue(!budget.IsSpent(After(2999)), "and a millisecond before that it is not");
        }

        [Test]
        public static void AnyTimeLeftIsWorthOneSecondOfDeadline()
        {
            SendBudget budget = SendBudget.Start(Start);

            Assert.AreEqual(1, budget.SecondsLeft(After(2999)),
                "a question asked with a millisecond left still gets a second - a zero deadline fails by construction");
        }

        [Test]
        public static void AClockThatMovesBackDoesNotGiveMoreTime()
        {
            SendBudget budget = SendBudget.Start(Start);

            Assert.AreEqual((long)SendBudget.Milliseconds, budget.MillisecondsLeft(Start - After(10000)),
                "the phone syncing its clock must not turn into a longer wait");
            Assert.AreEqual(3, budget.SecondsLeft(Start - After(10000)));
        }
    }
}
