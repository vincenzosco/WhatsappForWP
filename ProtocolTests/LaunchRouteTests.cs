using System;
using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// Where a tap on the tile - or on the lock screen - lands.
    ///
    /// Why this is worth a test: a tap on the Start tile reaches `App.OnLaunched` as the
    /// same empty launch the app list sends, so the app has no way to know it was tapped.
    /// What it does know is what the phone was showing, and that record is this rule. An
    /// error here is invisible on a phone: the app simply opens on the wrong tab, or on
    /// the same tab forever, and nothing in the log says it was a decision at all.
    /// </summary>
    public static class LaunchRouteTests
    {
        private static readonly DateTime Never = default(DateTime);
        private static readonly DateTime Midday = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        /// <summary>Nothing arrived: the chats, which is what the app opens on anyway.</summary>
        [Test]
        public static void NothingWaitingIsNoRouteAtAll()
        {
            Assert.AreEqual(null, LaunchRoute.Decide(Never, Never, Never, Never));
            Assert.AreEqual(null, LaunchRoute.Decide(Midday, Midday, Midday, Midday));
        }

        /// <summary>An update nobody watched, posted after the tab was last seen.</summary>
        [Test]
        public static void AnUnseenUpdateOpensTheStatusTab()
        {
            Assert.AreEqual(LaunchRoute.Status,
                LaunchRoute.Decide(Midday, Midday.AddHours(-3), Never, Never));
        }

        /// <summary>A missed call newer than the last look at the log.</summary>
        [Test]
        public static void AMissedCallOpensTheCallsTab()
        {
            Assert.AreEqual(LaunchRoute.Calls,
                LaunchRoute.Decide(Never, Never, Midday, Midday.AddDays(-1)));
        }

        /// <summary>
        /// The update was already on screen and not watched: the tab was opened, so there is
        /// nothing new for the tile to send anybody to. Without this the app would open on
        /// Status at every start until the update was actually watched.
        /// </summary>
        [Test]
        public static void AnUpdateAlreadyOnScreenIsNotNews()
        {
            Assert.AreEqual(null, LaunchRoute.Decide(Midday, Midday.AddMinutes(1), Never, Never));
        }

        /// <summary>The same for the log: a call seen at the time stays seen.</summary>
        [Test]
        public static void AMissedCallAlreadyOnScreenIsNotNews()
        {
            Assert.AreEqual(null, LaunchRoute.Decide(Never, Never, Midday, Midday.AddMinutes(1)));
        }

        /// <summary>
        /// Both are waiting: the later one wins, because that is what the phone would have
        /// told the user about last.
        /// </summary>
        [Test]
        public static void TheLaterOfTheTwoWins()
        {
            Assert.AreEqual(LaunchRoute.Calls,
                LaunchRoute.Decide(Midday, Never, Midday.AddMinutes(5), Never));
            Assert.AreEqual(LaunchRoute.Status,
                LaunchRoute.Decide(Midday.AddMinutes(5), Never, Midday, Never));
        }

        /// <summary>
        /// A route older than the window is not what was tapped: the tile has almost
        /// certainly been drawn again since, or nothing has arrived for so long that the
        /// history it names is gone.
        /// </summary>
        [Test]
        public static void ARouteGoesStale()
        {
            Assert.IsTrue(LaunchRoute.IsFresh(Midday, Midday.AddHours(1)));
            Assert.IsFalse(LaunchRoute.IsFresh(Midday, Midday.AddHours(LaunchRoute.FreshHours + 1)));
            Assert.IsFalse(LaunchRoute.IsFresh(Never, Midday));
        }

        /// <summary>
        /// A clock that was put back gives a negative age, and a record cannot be older
        /// than the moment it was written: dropping it would lose the tap that just came.
        /// </summary>
        [Test]
        public static void AClockPutBackDoesNotThrowTheRouteAway()
        {
            Assert.IsTrue(LaunchRoute.IsFresh(Midday, Midday.AddHours(-2)));
        }
    }
}
