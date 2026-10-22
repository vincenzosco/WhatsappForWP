using System;
using WhatsappApp.Protocol.Client;

namespace ProtocolTests
{
    /// <summary>
    /// The chat list is ordered by when a conversation was last active, so the one thing this
    /// has to get right is refusing a time that cannot be one. The dates that were tested are
    /// the ones the phone showed on 2026-10-22: 12 October stored as 10 December.
    /// </summary>
    public static class ActivityTimeTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 22, 8, 0, 0, DateTimeKind.Local);

        [Test]
        public static void ATimeInThePastIsATime()
        {
            var at = new DateTime(2026, 10, 12, 14, 21, 54, DateTimeKind.Local);

            Assert.IsTrue(ActivityTime.IsSane(at, Now), "the real date of that chat is a real date");
            Assert.AreEqual(at, ActivityTime.Trust(at, DateTime.MinValue, Now));
        }

        [Test]
        public static void TheSwappedDateIsRefusedAndTheRealOneKept()
        {
            var swapped = new DateTime(2026, 12, 10, 14, 21, 54, DateTimeKind.Local);
            var real = new DateTime(2026, 10, 12, 14, 21, 54, DateTimeKind.Local);

            Assert.IsFalse(ActivityTime.IsSane(swapped, Now),
                "10 December is seven weeks away, so it is the day/month of 12 October");
            Assert.AreEqual(real, ActivityTime.Trust(swapped, real, Now),
                "the row keeps the time it already had instead of moving to the top");
        }

        [Test]
        public static void ARowWithNoBelievableTimeKeepsNone()
        {
            var swapped = new DateTime(2026, 12, 10, 14, 21, 54, DateTimeKind.Local);

            Assert.AreEqual(DateTime.MinValue, ActivityTime.Trust(swapped, DateTime.MinValue, Now),
                "a row with no true time belongs at the bottom, not above the chat just read");
        }

        [Test]
        public static void AFewMinutesOfClockDriftAreNotAFuture()
        {
            DateTime soon = Now.AddMinutes(4);

            Assert.IsTrue(ActivityTime.IsSane(soon, Now),
                "a server four minutes ahead is a clock, not a bug");
        }

        [Test]
        public static void TheToleranceIsTheBoundary()
        {
            Assert.IsTrue(ActivityTime.IsSane(Now + ActivityTime.Tolerance, Now),
                "exactly the tolerance is still believed");
            Assert.IsFalse(ActivityTime.IsSane(Now + ActivityTime.Tolerance + TimeSpan.FromSeconds(1), Now));
        }

        [Test]
        public static void NoTimeIsNotATime()
        {
            Assert.IsFalse(ActivityTime.IsSane(DateTime.MinValue, Now));
            Assert.AreEqual(DateTime.MinValue, ActivityTime.Trust(DateTime.MinValue, DateTime.MinValue, Now));
        }
    }
}
