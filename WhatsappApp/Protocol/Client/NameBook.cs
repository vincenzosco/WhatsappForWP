using System;
using System.Collections.Generic;
using System.Globalization;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// What this client knows about who a JID is: the name to show, and the phone number
    /// behind a `@lid`.
    ///
    /// Why it is a rule of its own: three sources say something about a name (the address book
    /// the primary shares with the history, the push name on a message, and the name a group
    /// carries), and two of them can describe the same person under two different addresses -
    /// `150375613116620@lid` and `393892672185@s.whatsapp.net` are one account. A `Dictionary`
    /// in each caller is how the same person ends up twice in a chat list, once named and once
    /// as a number.
    ///
    /// The precedence is the reference's: the name saved in the phone's address book wins,
    /// because it is the one the person chose for that contact; the push name is what the
    /// contact calls themselves and is used only when nothing was saved. Neither is invented:
    /// a JID with no name answers null and the caller falls back to the number.
    ///
    /// It holds no WinRT type and no file: it is the data, and `Services/NameCache` is what
    /// writes it down.
    /// </summary>
    public sealed class NameBook
    {
        /// <summary>How many saved and push names are kept: a large address book, and bounded.</summary>
        public const int MaxNames = 1500;

        /// <summary>How many LID/phone-number pairs are kept. A person has one pair.</summary>
        public const int MaxMappings = 1500;

        /// <summary>JID -> the name saved for it in the primary phone's address book.</summary>
        private readonly Dictionary<string, string> _saved = new Dictionary<string, string>();

        /// <summary>JID -> the name the contact uses on WhatsApp.</summary>
        private readonly Dictionary<string, string> _push = new Dictionary<string, string>();

        /// <summary>`@lid` -> the phone-number JID it stands for.</summary>
        private readonly Dictionary<string, string> _pnByLid = new Dictionary<string, string>();

        /// <summary>The same pair the other way round.</summary>
        private readonly Dictionary<string, string> _lidByPn = new Dictionary<string, string>();

        /// <summary>Keeps the saved name of a contact. False when it changed nothing.</summary>
        public bool RememberSavedName(string jid, string name)
        {
            return Remember(_saved, jid, name);
        }

        /// <summary>Keeps the name a contact goes by on WhatsApp. False when it changed nothing.</summary>
        public bool RememberPushName(string jid, string name)
        {
            return Remember(_push, jid, name);
        }

        /// <summary>
        /// Writes down that a `@lid` and a phone number are the same account. False when this
        /// pair is already known or is not a pair at all.
        ///
        /// The two arguments are checked and not trusted: a phone number that arrives as a LID
        /// (or the other way round) would make `Canonical` map an identity onto itself, and a
        /// non-JID would be stored as if it were one.
        /// </summary>
        public bool RememberMapping(string lid, string phone)
        {
            lid = Normalize(lid);
            phone = Normalize(phone);

            if (lid == null || phone == null) return false;
            if (!lid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase)) return false;
            if (!phone.EndsWith("@" + JidUtils.WhatsappServer, StringComparison.OrdinalIgnoreCase)) return false;

            string known;
            if (_pnByLid.TryGetValue(lid, out known)) return known != phone;

            if (_pnByLid.Count >= MaxMappings) return false;

            _pnByLid[lid] = phone;
            _lidByPn[phone] = lid;

            // A name learned before the pair was known is stored under the LID. The row is
            // keyed on the number from now on, so the name has to travel with it.
            MoveName(_saved, lid, phone);
            MoveName(_push, lid, phone);

            return true;
        }

        /// <summary>
        /// The address a chat is keyed on: the phone number when the LID it arrived with is
        /// known to belong to one, and the JID itself otherwise.
        ///
        /// Why the phone number: WhatsApp is moving chats to LIDs, and a list that kept both
        /// addresses would show the same person twice - one row named, one row a number nobody
        /// recognises. Everything this client keys on a chat (the row, its messages, the pin
        /// and mute preferences, the cache) follows the canonical address, so the pair is one
        /// conversation.
        /// </summary>
        public string Canonical(string jid)
        {
            jid = Normalize(jid);
            if (jid == null) return null;

            string phone;
            if (jid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase)
                && _pnByLid.TryGetValue(jid, out phone))
            {
                return phone;
            }

            return jid;
        }

        /// <summary>The name to show for a chat, or null when none is known.</summary>
        public string NameFor(string jid)
        {
            jid = Canonical(jid);
            if (jid == null) return null;

            string name;
            if (_saved.TryGetValue(jid, out name)) return name;
            if (_push.TryGetValue(jid, out name)) return name;

            return null;
        }

        /// <summary>The name saved in the address book, or null.</summary>
        public string SavedNameFor(string jid)
        {
            jid = Canonical(jid);
            if (jid == null) return null;

            string name;
            return _saved.TryGetValue(jid, out name) ? name : null;
        }

        /// <summary>The name the contact uses on WhatsApp, or null.</summary>
        public string PushNameFor(string jid)
        {
            jid = Canonical(jid);
            if (jid == null) return null;

            string name;
            return _push.TryGetValue(jid, out name) ? name : null;
        }

        /// <summary>
        /// The phone number to print for a chat, or null when WhatsApp only gave an address
        /// this client cannot turn into one.
        ///
        /// A LID with no known pair answers null on purpose: the LID's digits are an opaque
        /// identifier, and printing them as \"+85891192225850\" is a wrong number, not a
        /// missing one.
        /// </summary>
        public string PhoneNumberFor(string jid)
        {
            jid = Canonical(jid);
            if (jid == null) return null;

            return jid.EndsWith("@" + JidUtils.WhatsappServer, StringComparison.OrdinalIgnoreCase) ? jid : null;
        }

        /// <summary>The `@lid` a phone number is also known by, or null.</summary>
        public string LidFor(string phone)
        {
            phone = Normalize(phone);
            if (phone == null) return null;

            string lid;
            return _lidByPn.TryGetValue(phone, out lid) ? lid : null;
        }

        /// <summary>The saved names, for the cache that writes them down.</summary>
        public IList<KeyValuePair<string, string>> SavedEntries()
        {
            return Entries(_saved);
        }

        /// <summary>The push names, for the cache that writes them down.</summary>
        public IList<KeyValuePair<string, string>> PushEntries()
        {
            return Entries(_push);
        }

        /// <summary>The LID/phone-number pairs, for the cache that writes them down.</summary>
        public IList<KeyValuePair<string, string>> MappingEntries()
        {
            return Entries(_pnByLid);
        }

        /// <summary>Forgets everything. Only a test, and a logout.</summary>
        public void Clear()
        {
            _saved.Clear();
            _push.Clear();
            _pnByLid.Clear();
            _lidByPn.Clear();
        }

        /// <summary>
        /// A name as the row should show it, or null when the string carries nothing a person
        /// could read.
        ///
        /// The case it exists for: WhatsApp's placeholder for a profile with no name is
        /// `\"\u3164\"` (a Hangul filler), which is a letter to Unicode and would become the only
        /// text of a row. The same rule is used here and by the stanza reader, so a name that
        /// the wire rejects cannot be put back by the cache.
        /// </summary>
        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            string trimmed = value.Trim();
            if (trimmed.Length == 0) return null;

            for (int i = 0; i < trimmed.Length; i++)
            {
                if (Invisible(trimmed[i])) continue;

                return trimmed;
            }

            return null;
        }

        /// <summary>The JID with its device taken off, or null for a string that is not one.</summary>
        private static string Normalize(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;
            if (jid.IndexOf('@') < 0) return null;

            return JidUtils.Normalize(jid);
        }

        private bool Remember(Dictionary<string, string> table, string jid, string name)
        {
            jid = Canonical(jid);
            name = Clean(name);

            if (jid == null || name == null) return false;

            string current;
            if (table.TryGetValue(jid, out current))
            {
                if (current == name) return false;

                table[jid] = name;
                return true;
            }

            if (table.Count >= MaxNames) return false;

            table[jid] = name;
            return true;
        }

        /// <summary>The name under the address it is now keyed on, when the old one has none.</summary>
        private static void MoveName(Dictionary<string, string> table, string from, string to)
        {
            string name;
            if (!table.TryGetValue(from, out name)) return;
            if (table.ContainsKey(to)) return;

            table[to] = name;
        }

        private static IList<KeyValuePair<string, string>> Entries(Dictionary<string, string> table)
        {
            var entries = new List<KeyValuePair<string, string>>();

            foreach (KeyValuePair<string, string> entry in table)
            {
                entries.Add(entry);
            }

            return entries;
        }

        /// <summary>
        /// Whether a character is a space or one of the invisible fillers a profile name can be
        /// made of: format characters, the Hangul fillers, and the braille blank.
        /// </summary>
        private static bool Invisible(char value)
        {
            if (char.IsWhiteSpace(value)) return true;

            if (value == '\u3164' || value == '\uFFA0' || value == '\u115F' || value == '\u1160'
                || value == '\u2800' || value == '\uFEFF')
            {
                return true;
            }

            return CharUnicodeInfo.GetUnicodeCategory(value) == UnicodeCategory.Format;
        }
    }
}
