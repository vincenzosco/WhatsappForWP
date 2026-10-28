namespace WhatsappApp.Services
{
    /// <summary>
    /// The name a status row shows.
    ///
    /// Why it is a rule of its own: a status arrives from an **author** the phone may know
    /// only as an address - the `participant` of a `status@broadcast` message is often a
    /// `@lid`, and a LID is fourteen digits that look like a phone number this phone has
    /// never seen. `DataService.DisplayNameForJid` resolves what it can and falls back to
    /// the address, which is right for a chat (a number is a name you can call) and wrong
    /// for a status list: WhatsApp sends the poster's own name in the same stanza
    /// (`notify`), so the list showed `+150375613116620` where the official app shows
    /// "Rosalba" - the report of 28 October 2026.
    ///
    /// The rule is one sentence: what the phone resolved, unless that is the address
    /// itself and the post carries a name of its own.
    ///
    /// No WinRT types here: the desktop runner compiles this file by name
    /// (`ProtocolTests/StatusNameTests.cs`), because it is the part of the row that is a
    /// decision.
    /// </summary>
    public static class StatusName
    {
        /// <summary>
        /// The name to show. `resolved` is what the phone knows (a saved contact, a pair
        /// learned from the sync, or the address as a last resort); `notified` is the
        /// `notify` of the post, which is the poster's own word for themselves.
        ///
        /// Never throws and never returns null: an empty answer is an empty row, which the
        /// caller's voice writes as its own.
        /// </summary>
        public static string Choose(string resolved, string notified)
        {
            string known = Clean(resolved);
            string said = Clean(notified);

            if (known.Length == 0) return said;
            if (said.Length == 0) return known;

            return LooksLikeAddress(known) ? said : known;
        }

        /// <summary>
        /// Whether a name is really the address: a number with a `+`, a bare run of digits,
        /// or the author's own user part. `+150375613116620` and `150375613116620` are both
        /// the LID read out loud, and neither is a name.
        /// </summary>
        public static bool LooksLikeAddress(string name)
        {
            string text = Clean(name);
            if (text.Length == 0) return false;

            if (text[0] == '+') return true;

            int digits = 0;
            bool allDigits = true;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '0' && c <= '9') { digits++; continue; }

                // The separators a phone number is written with, and nothing else: a name
                // with a space in it ("Rosalba 2") is a name.
                if (c == ' ' || c == '-' || c == '(' || c == ')' || c == '.') continue;

                allDigits = false;
                break;
            }

            if (!allDigits) return false;

            // A LID is fourteen digits and a number is at least seven: below that the run is
            // a word ("007") and a word is a name.
            return digits >= 7;
        }

        private static string Clean(string text)
        {
            if (text == null) return string.Empty;

            return text.Trim();
        }
    }
}
