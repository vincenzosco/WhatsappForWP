using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// Where the app opens.
    ///
    /// The rule is checked here rather than on the phone because it is the one part of
    /// the start-up decision that is a decision: `App.OnLaunched` reads two facts out of
    /// the storage and hands them over, and this is what turns them into a page. Every
    /// case below is a state a real install can be in, and the two that matter most are
    /// the ones that must **not** reach the chats: no session at all, and a session the
    /// server has already revoked - a chat list behind either of them can never load.
    /// </summary>
    public static class StartupTests
    {
        /// <summary>A phone that has never paired: the QR, whatever was left in the settings.</summary>
        [Test]
        public static void NoSessionStartsOnThePairingPage()
        {
            Assert.AreEqual(StartTarget.Pair, StartDecision.Decide(false, false, false, true));
        }

        /// <summary>
        /// An install upgraded from the server build: it still has an address, a port and
        /// the public-server switch in its settings, and no session. None of those is an
        /// input to this rule, so the answer is the QR and not the old connection page.
        /// </summary>
        [Test]
        public static void AnUpgradedInstallWithNoSessionStartsOnThePairingPage()
        {
            Assert.AreEqual(StartTarget.Pair, StartDecision.Decide(false, false, true, true));
        }

        /// <summary>
        /// The session is stored and parses, but WhatsApp has unlinked this phone. The
        /// app must land on the QR - it is where the "expired" sentence is shown - rather
        /// than on chats that will never fill.
        /// </summary>
        [Test]
        public static void AnExpiredSessionStartsOnThePairingPage()
        {
            Assert.AreEqual(StartTarget.Pair, StartDecision.Decide(true, true, false, true));
            Assert.AreEqual(StartTarget.Pair, StartDecision.Decide(true, true, true, false));
        }

        /// <summary>A terminated app coming back: it resumes the section the user was in.</summary>
        [Test]
        public static void ResumingOutsideTheChatsKeepsTheSection()
        {
            Assert.AreEqual(StartTarget.LastSection, StartDecision.Decide(true, false, true, false));
        }

        /// <summary>
        /// Resuming inside the chats is the chats: `LastSection` would have to name the
        /// section too, and the caller already knows it is not the one it wants.
        /// </summary>
        [Test]
        public static void ResumingInTheChatsIsTheChats()
        {
            Assert.AreEqual(StartTarget.Chats, StartDecision.Decide(true, false, true, true));
        }

        /// <summary>A cold start with a session goes to the chat list, which is the app's front door.</summary>
        [Test]
        public static void AColdStartWithASessionIsTheChats()
        {
            Assert.AreEqual(StartTarget.Chats, StartDecision.Decide(true, false, false, false));
        }
    }
}
