namespace WhatsappApp.Services
{
    /// <summary>Where a start ends up.</summary>
    public enum StartTarget
    {
        /// <summary>The QR: there is no usable account on this phone.</summary>
        Pair,

        /// <summary>The chat list, which is the app's front door.</summary>
        Chats
    }

    /// <summary>
    /// The rule that decides which page a start opens on.
    ///
    /// It sits in its own file, with no WinRT type in it and no `AppSection` in its
    /// signature, because it is the only part of `App.OnLaunched` worth a test: the
    /// caller reads the facts out of the storage and this turns them into a page. The
    /// desktop runner compiles this file by name (see `ProtocolTests.csproj`).
    ///
    /// The order of the two refusals is the whole point. A stored session that WhatsApp
    /// has revoked parses perfectly - every key is there, every length is right - and the
    /// app cannot tell it apart from a good one by reading it. What it knows is that the
    /// last connection was refused, and that fact alone has to win: a chat list behind a
    /// dead session is a screen that waits for messages that are never coming.
    ///
    /// Why there is no "the section you left" in this decision any more: it was the tab
    /// the app opened on, and on 28 October 2026 that was the Status tab - the app
    /// started on somebody's updates instead of on the conversations. A start is the
    /// chats, and the other two tabs are one tap away, with their own badges saying which
    /// of them has something new. The route a tile tap writes is still read and named in
    /// the log; it no longer decides the tab.
    ///
    /// Nothing about the old server appears here, and that is deliberate: an app upgraded
    /// in place still has an address, a port and a public-server switch in its settings,
    /// and none of them is an input - so none of them can keep the app off the QR.
    /// </summary>
    public static class StartDecision
    {
        /// <summary>
        /// The page to open.
        /// </summary>
        /// <param name="hasSession">A whole, parseable account is stored (`CredentialStore.HasSession`).</param>
        /// <param name="sessionExpired">The last connection was refused: WhatsApp unlinked this device.</param>
        public static StartTarget Decide(bool hasSession, bool sessionExpired)
        {
            if (!hasSession) return StartTarget.Pair;
            if (sessionExpired) return StartTarget.Pair;

            return StartTarget.Chats;
        }
    }
}
