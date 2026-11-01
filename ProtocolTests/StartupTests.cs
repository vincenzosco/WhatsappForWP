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
    ///
    /// The third rule is the one 28 October 2026 added: with a session, a start is always
    /// the chats. It used to be the section the app was last on, and that was the Status
    /// tab - the app opened on somebody's updates instead of on the conversations.
    /// </summary>
    public static class StartupTests
    {
        /// <summary>A phone that has never paired: the QR, whatever was left in the settings.</summary>
        [Test]
        public static void NoSessionStartsOnThePairingPage()
        {
            Assert.AreEqual(StartTarget.Pair, StartDecision.Decide(false, false));
        }

        /// <summary>
        /// An install upgraded from the server build: it still has an address, a port and
        /// the public-server switch in its settings, and no session. None of those is an
        /// input to this rule, so the answer is the QR and not the old connection page.
        /// </summary>
        [Test]
        public static void AnUpgradedInstallWithNoSessionStartsOnThePairingPage()
        {
            Assert.AreEqual(StartTarget.Pair, StartDecision.Decide(false, false));
        }

        /// <summary>
        /// The session is stored and parses, but WhatsApp has unlinked this phone. The
        /// app must land on the QR - it is where the "expired" sentence is shown - rather
        /// than on chats that will never fill.
        /// </summary>
        [Test]
        public static void AnExpiredSessionStartsOnThePairingPage()
        {
            Assert.AreEqual(StartTarget.Pair, StartDecision.Decide(true, true));
        }

        /// <summary>
        /// A start with a session is the chat list, and that is the whole of the rule: the
        /// argument list has no "where was the app last" and no "what was the tile showing",
        /// so no stored section and no tile tap can put the app on another tab.
        /// </summary>
        [Test]
        public static void AStartWithASessionIsTheChats()
        {
            Assert.AreEqual(StartTarget.Chats, StartDecision.Decide(true, false));
        }
    }
}
