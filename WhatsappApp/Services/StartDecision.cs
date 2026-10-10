namespace WhatsappApp.Services
{
    /// <summary>Where a start ends up.</summary>
    public enum StartTarget
    {
        /// <summary>The QR: there is no usable account on this phone.</summary>
        Pair,

        /// <summary>The chat list, which is the app's front door.</summary>
        Chats,

        /// <summary>The section the user was in when the app was terminated.</summary>
        LastSection
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
    /// last connection was refused, and that fact alone has to win over both the resume
    /// and the chats: a chat list behind a dead session is a screen that waits for
    /// messages that are never coming.
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
        /// <param name="resumed">The app is coming back after the OS terminated it, not starting cold.</param>
        /// <param name="lastSectionIsChats">The section that was on screen was the chats.</param>
        public static StartTarget Decide(bool hasSession, bool sessionExpired, bool resumed, bool lastSectionIsChats)
        {
            if (!hasSession) return StartTarget.Pair;
            if (sessionExpired) return StartTarget.Pair;

            if (resumed)
            {
                return lastSectionIsChats ? StartTarget.Chats : StartTarget.LastSection;
            }

            return StartTarget.Chats;
        }
    }
}
