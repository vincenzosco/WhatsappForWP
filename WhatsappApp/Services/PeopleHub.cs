using System;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The phone's People page, opened from a chat: the person or the group behind a
    /// conversation, in the app the rest of the phone keeps its contacts in.
    ///
    /// Why a task and not the card: the installed WP8.1 Silverlight projection exposes
    /// `ContactManager.ShowContactCard` and the phone answers `NotImplementedException` to
    /// it - it is a projection of a Windows Phone Runtime the phone does not run. The task is
    /// the one the People page itself launches, so the contact is saved, called or messaged
    /// with the rules the phone already has. Nothing is written behind anybody's back: the
    /// page decides what a save means.
    ///
    /// A group is offered the same way, with the one difference that a group has no number to
    /// dial: what the page gets is the subject the list shows. What is still refused is a chat
    /// with no number this phone knows - the digits of a `@lid` are an identifier, and printing
    /// them as one is a wrong number, not a missing one.
    /// </summary>
    public static class PeopleHub
    {
        /// <summary>
        /// Whether this chat is something the People page can show: a person whose number this
        /// phone knows the pair of, or a group. The menu asks first, so it never draws an entry
        /// that would answer with nothing.
        /// </summary>
        public static bool CanShow(string jid)
        {
            return NumberFor(jid) != null || GroupDirectory.IsGroup(jid);
        }

        /// <summary>
        /// Opens the People page for a chat.
        ///
        /// The name the list already shows goes with it, so a person WhatsApp calls `Anna` is
        /// not a number on the page either.
        ///
        /// Never throws: the tap comes from a menu, and a menu entry that throws takes the
        /// page with it. Every outcome is one line of the log.
        /// </summary>
        public static void Show(string jid, string name)
        {
            string shown = NameBook.Clean(name);

            try
            {
                var task = new Microsoft.Phone.Tasks.SaveContactTask();

                if (GroupDirectory.IsGroup(jid))
                {
                    // A group is not a person and there is no number to dial: what the page
                    // gets is the subject the list shows.
                    task.FirstName = string.IsNullOrEmpty(shown) ? jid : shown;
                    task.Show();
                    Diag.Ok("people: the group " + jid + " to the People page");
                    return;
                }

                string digits = NumberFor(jid);
                if (digits == null)
                {
                    Diag.Ok("people: this chat has no number this phone knows, there is no card to show");
                    return;
                }

                string number = "+" + digits;
                task.FirstName = string.IsNullOrEmpty(shown) ? number : shown;
                task.MobilePhone = number;
                task.Show();
                Diag.Ok("people: " + number + " to the People page");
            }
            catch (Exception ex)
            {
                Diag.Failed("PeopleHub/Show", ex);
            }
        }

        /// <summary>
        /// The number behind a chat, whatever spelling the list has it under. A `@lid` is not
        /// a number of its own; the number is the one of the pair this phone knows.
        /// </summary>
        private static string NumberFor(string jid)
        {
            string digits = PhoneNumbers.UserDigits(jid);
            if (digits != null) return digits;

            string phone = NameCache.Book.PhoneNumberFor(jid);
            return phone == null ? null : PhoneNumbers.UserDigits(phone);
        }
    }
}