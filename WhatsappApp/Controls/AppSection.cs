namespace WhatsappApp.Controls
{
    /// <summary>
    /// The three tabs of the home screen, in the order they appear on screen.
    ///
    /// This is a C# `enum`: a small list of named values. The compiler numbers them 0, 1, 2,
    /// and that number is what the swipeable home view uses to know which tab is showing.
    /// Because the names are readable, the code says `AppSection.Calls` instead of `2`.
    /// </summary>
    public enum AppSection
    {
        /// <summary>The conversation list, the first tab and index 0.</summary>
        Chats,

        /// <summary>The status list, index 1.</summary>
        Status,

        /// <summary>The call log, index 2.</summary>
        Calls
    }
}
