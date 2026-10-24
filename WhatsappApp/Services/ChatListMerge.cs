using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Services
{
    /// <summary>
    /// One chat row as the merge reads it: the fields that decide whether two rows are one
    /// conversation, and which of them says the truth about it.
    ///
    /// It is a plain shape and not the `Contact` the list binds, because `Contact` carries a
    /// `BitmapImage` and this file is compiled off the phone (the protocol tests run it):
    /// a rule about identity that could only be read on a device would be a rule nothing
    /// checks.
    /// </summary>
    public sealed class ChatRowState
    {
        /// <summary>The chat's address, in the form this row is filed under.</summary>
        public string Id;

        /// <summary>What the row is called: a saved name, a subject, a push name, or a fallback.</summary>
        public string Name;

        /// <summary>The last message's words, as the row shows them.</summary>
        public string Preview;

        /// <summary>When the last message happened.</summary>
        public DateTime At;

        /// <summary>How many messages are waiting.</summary>
        public int Unread;

        public bool Pinned;
        public bool Muted;
    }

    /// <summary>
    /// Which ids are the same chat, and what one row made of two says.
    ///
    /// Why it exists: WhatsApp names one person two ways - a phone number and a `@lid` - and
    /// one conversation can be spelled with a device (`393...:5@s.whatsapp.net`) or with the
    /// old server (`@c.us`). The chat list was keyed on the exact string, so a message that
    /// arrived under the other spelling did not find its row: it created a second one, the
    /// old row kept yesterday's preview, a message this phone sent from that second row was
    /// saved in a conversation the reader never opened, and a group showed up twice - once
    /// with its subject and once as "Gruppo 1203...". `NameBook.Canonical` already knows the
    /// pair (`@lid` to number, device stripped); this file is the rule that decides when two
    /// rows are one, and how to merge them without losing a count, a pin or a preview.
    ///
    /// It never decides *order*: that stays where it was (`DataService.PlaceContact`).
    /// </summary>
    public static class ChatListMerge
    {
        /// <summary>
        /// The one spelling of a chat every row and every message is keyed on: the number for
        /// a `@lid` whose pair is known, no device, and `@c.us` as `s.whatsapp.net`.
        ///
        /// A pair this device has not learned yet cannot be folded - the two addresses are
        /// then two addresses - so the answer is the row's own id, and the next refresh after
        /// the pair arrives folds it.
        /// </summary>
        public static string CanonicalId(string jid, NameBook book)
        {
            if (string.IsNullOrEmpty(jid)) return null;

            string canonical = book == null ? JidUtils.Normalize(jid) : book.Canonical(jid);
            if (string.IsNullOrEmpty(canonical)) canonical = jid;

            return canonical;
        }

        /// <summary>Whether two addresses name the same conversation.</summary>
        public static bool SameChat(string left, string right, NameBook book)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

            return string.Equals(CanonicalId(left, book), CanonicalId(right, book), StringComparison.Ordinal);
        }

        /// <summary>
        /// One row made of two: the newer preview says what the chat last said, the counts add
        /// up, and a pin or a mute survives whichever row carried it.
        ///
        /// The name is the one thing that is not arithmetic. A row created by a live message
        /// before the history named it is called `Gruppo 1203...` or `+39389...`, and that is
        /// a description of the id, not a name a person chose: the other row's name wins.
        /// </summary>
        public static ChatRowState Fuse(ChatRowState keep, ChatRowState folded)
        {
            if (keep == null) return folded;
            if (folded == null) return keep;

            var fused = new ChatRowState();
            fused.Id = keep.Id;
            fused.Name = BestName(keep.Name, keep.Id, folded.Name, folded.Id);

            // The newer one is what the chat last said, and the one with any text wins a tie:
            // a media message whose row carries no words must not blank a caption.
            bool foldedIsNewer = folded.At > keep.At;
            ChatRowState newer = foldedIsNewer ? folded : keep;
            ChatRowState older = foldedIsNewer ? keep : folded;

            fused.At = newer.At;
            fused.Preview = !string.IsNullOrEmpty(newer.Preview) ? newer.Preview : older.Preview;

            fused.Unread = keep.Unread + folded.Unread;
            fused.Pinned = keep.Pinned || folded.Pinned;
            fused.Muted = keep.Muted || folded.Muted;

            return fused;
        }

        /// <summary>
        /// The name two rows for one chat agree on: the one a person chose, never the one
        /// that is only the address written out.
        /// </summary>
        public static string BestName(string keepName, string keepId, string foldedName, string foldedId)
        {
            bool keepFallback = LooksLikeFallback(keepName, keepId);
            bool foldedFallback = LooksLikeFallback(foldedName, foldedId);

            if (keepFallback && !foldedFallback) return foldedName;
            return keepName;
        }

        /// <summary>
        /// Whether a name is really the address written out: `+393892672185`,
        /// `393892672185` or `Gruppo 120363412599616890` - what a row is called when nobody
        /// has told this phone what it is called. Such a name must never beat a real one.
        /// </summary>
        public static bool LooksLikeFallback(string name, string jid)
        {
            if (string.IsNullOrEmpty(name)) return true;

            string user = UserPart(jid);
            if (!string.IsNullOrEmpty(user))
            {
                if (string.Equals(name, user, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(name, "+" + user, StringComparison.OrdinalIgnoreCase)) return true;
            }

            // A phone number written out, whatever the row's address is: `+393892672185` is
            // what this list calls a `@lid` whose number it knows, and the name is then the
            // number of the *other* row, not this one's.
            if (IsNumbers(name)) return true;

            // The two words this app builds such a row with (`DataService.DisplayNameForJid`,
            // and the one the phone's own strings carry for the same reason).
            if (name.StartsWith("Gruppo ", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("Group ", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>`+393892672185` or `393892672185`: a number, and numbers are not names.</summary>
        private static bool IsNumbers(string name)
        {
            int start = name.Length > 0 && name[0] == '+' ? 1 : 0;
            if (name.Length - start < 5) return false;

            for (int i = start; i < name.Length; i++)
            {
                if (name[i] < '0' || name[i] > '9') return false;
            }

            return true;
        }

        /// <summary>The user half of a JID, without the device, or null when it is not one.</summary>
        private static string UserPart(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;

            int at = jid.IndexOf('@');
            if (at <= 0) return null;

            string user = jid.Substring(0, at);
            int colon = user.IndexOf(':');
            if (colon >= 0) user = user.Substring(0, colon);

            return user;
        }
    }
}
