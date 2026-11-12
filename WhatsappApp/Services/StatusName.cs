namespace WhatsappApp.Services
{
    /// <summary>
    /// Picks the name to show on a status row.
    ///
    /// Names are confusing here because WhatsApp identifies people by two different strings:
    ///
    /// - a phone-number address such as `393401234567@s.whatsapp.net`, which a person can read;
    /// - a `@lid`, a privacy address that is a long run of digits and belongs to nobody.
    ///
    /// The chat list can live with an address (a number is still something you can call), but
    /// a status row that shows fourteen digits looks broken. The post therefore carries its
    /// author's own name (`notify`), and this class decides when to prefer it.
    ///
    /// The rule: show what the phone knows, unless what it knows is only the address and the
    /// post brought a name of its own.
    ///
    /// A plain C# test compiles this file by name (`ProtocolTests/StatusNameTests.cs`).
    /// </summary>
    public static class StatusName
    {
        /// <summary>
        /// The name to display. `resolved` is what the phone knows (a saved contact, a name
        /// learned from the sync, or the address itself); `notified` is the name the author
        /// sent with the post.
        ///
        /// It never throws and never returns null: an empty string means "no name", and the
        /// caller decides what to write instead.
        /// </summary>
        public static string Choose(string resolved, string notified)
        {
            string known = Clean(resolved);
            string said = Clean(notified);

            // One of the two is missing: the other one is the answer.
            if (known.Length == 0) return said;
            if (said.Length == 0) return known;

            // Both exist: prefer what the phone knows, unless it is only an address.
            return LooksLikeAddress(known) ? said : known;
        }

        /// <summary>
        /// True when `name` is really an address: a number with a `+`, a bare run of digits, or
        /// the long identifier of a `@lid`. Neither is a name a person would recognise.
        /// </summary>
        public static bool LooksLikeAddress(string name)
        {
            string text = Clean(name);
            if (text.Length == 0) return false;

            // A leading plus is how a phone number is written: always an address.
            if (text[0] == '+') return true;

            int digits = 0;
            bool allDigits = true;

            // Walk the characters and check that nothing but digits and separators appears.
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                if (c >= '0' && c <= '9') { digits++; continue; }

                // The punctuation people type inside phone numbers. A space alone is not
                // enough to make a name an address: "Rosalba 2" still has letters.
                if (c == ' ' || c == '-' || c == '(' || c == ')' || c == '.') continue;

                // Anything else means this is a word, so it is a name.
                allDigits = false;
                break;
            }

            if (!allDigits) return false;

            // Seven digits is the shortest real phone number; "007" is a number in a film, not
            // somebody's number. A LID is much longer and also passes this test.
            return digits >= 7;
        }

        /// <summary>A null becomes an empty string, and stray spaces are removed at both ends.</summary>
        private static string Clean(string text)
        {
            if (text == null) return string.Empty;

            return text.Trim();
        }
    }
}
