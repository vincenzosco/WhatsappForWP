namespace WhatsappApp.Services
{
    /// <summary>
    /// The two lines a tile has room for: the template draws the first in its larger font
    /// and the second in the wrapping one under it.
    /// </summary>
    public struct TileLines
    {
        public string First;
        public string Second;
    }

    /// <summary>
    /// What a tile's two lines say: who wrote on the large line, and what they said under
    /// it - or how much is waiting when there is nothing to quote.
    ///
    /// Why it is a rule of its own: the app's tile and a pinned chat's tile are two
    /// templates, two payloads and two places in the code, and the request of 23 October
    /// 2026 was that they read the same way - the person in the big line and the message
    /// under it, instead of a count where the eye lands. A rule written twice is a rule that
    /// changes in one file.
    ///
    /// It is pure and it lives where `TileTextTests` can reach it, because neither tile can
    /// be drawn off the phone.
    /// </summary>
    public static class TileText
    {
        /// <summary>How much of a name the large line holds.</summary>
        public const int NameLength = 30;

        /// <summary>
        /// How much of a message the second line holds. The template wraps it over up to
        /// three lines at 150 px, which is about this many characters; past it the shell cuts
        /// mid-word, and an ellipsis of ours reads better than half a word.
        /// </summary>
        public const int BodyLength = 120;

        /// <summary>
        /// The two lines, given the name, the message and the number as the caller wants it
        /// written (`NotificationService.UnreadText`, which is the localised form).
        /// </summary>
        public static TileLines For(string name, string body, string unread)
        {
            var lines = new TileLines();

            string sender = Cut(name, NameLength);
            string text = Cut(body, BodyLength);

            if (sender.Length > 0)
            {
                lines.First = sender;

                // With nothing to quote, the next thing a person wants is how much is
                // waiting; a second line that repeated the name would say nothing. Empty
                // when nothing is waiting, and then the template draws no line at all.
                lines.Second = text.Length > 0 ? text : (unread ?? "");
            }
            else
            {
                // Nobody named yet - a count read back from the copy before any message:
                // the count is the only true line there is, and it takes the large one.
                lines.First = unread ?? "";
                lines.Second = "";
            }

            return lines;
        }

        private static string Cut(string value, int max)
        {
            string text = value ?? "";
            return text.Length <= max ? text : text.Substring(0, max - 1) + "\u2026";
        }
    }
}
