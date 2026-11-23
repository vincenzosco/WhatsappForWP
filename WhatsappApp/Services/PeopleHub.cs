using System;
using System.Windows;
using Windows.ApplicationModel.Contacts;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The phone's People hub, opened from a chat: the person or the group behind a
    /// conversation, on the card the rest of the phone reads.
    ///
    /// Why a card and not a contact this app writes: the installed WP8.1 Silverlight
    /// projection exposes `ContactManager.ShowContactCard` and does not expose
    /// `ContactStore.AddContactAsync`. The People hub owns the card: from there the person is
    /// saved, called or messaged with the rules the phone already has. A chat that is a number
    /// in this app is a contact on that card, and the dialler and People tile can use it too.
    ///
    /// A group is offered the same way, with the one difference that a group has no number to
    /// dial: its card carries the subject the list shows. What is still refused is a chat with
    /// no number this phone knows - the digits of a `@lid` are an identifier, and printing
    /// them as one is a wrong number, not a missing one.
    /// </summary>
    public static class PeopleHub
    {
        /// <summary>
        /// Whether this chat is something the People hub can show: a person whose number this
        /// phone knows the pair of, or a group. The menu asks first, so it never draws an entry
        /// that would answer with nothing.
        /// </summary>
        public static bool CanShow(string jid)
        {
            return NumberFor(jid) != null || GroupDirectory.IsGroup(jid);
        }

        /// <summary>
        /// Opens the People card for a chat, anchored to the row that was held down.
        ///
        /// The card is given the name the list already shows, so a person WhatsApp calls
        /// `Anna` is not a number on the card either. Nothing is written: the hub decides what
        /// a save means, and this app has no business writing the address book behind its back.
        ///
        /// Never throws: the tap comes from a menu, and a menu entry that throws takes the
        /// page with it. Every outcome is one line of the log.
        /// </summary>
        public static void Show(string jid, string name, Rect anchor)
        {
            string shown = NameBook.Clean(name);

            if (GroupDirectory.IsGroup(jid))
            {
                // A group is not a person and there is no number to dial: its card is the
                // subject the list shows, and a save of that is the hub's business.
                var group = new Contact();
                group.FirstName = string.IsNullOrEmpty(shown) ? jid : shown;
                ShowCard(group, anchor);
                Diag.Ok("people: card for the group " + jid);
                return;
            }

            string digits = NumberFor(jid);
            if (digits == null)
            {
                Diag.Ok("people: this chat has no number this phone knows, there is no card to show");
                return;
            }

            string number = "+" + digits;

            try
            {
                var contact = new Contact();
                contact.FirstName = string.IsNullOrEmpty(shown) ? number : shown;

                var phone = new ContactPhone();
                phone.Number = number;
                phone.Kind = ContactPhoneKind.Mobile;
                contact.Phones.Add(phone);

                ShowCard(contact, anchor);
                Diag.Ok("people: card for " + number);
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

        /// <summary>The card, on the screen, where the row that was held stands.</summary>
        private static void ShowCard(Contact contact, Rect anchor)
        {
            var cardAnchor = new Windows.Foundation.Rect(anchor.X, anchor.Y, anchor.Width, anchor.Height);
            ContactManager.ShowContactCard(contact, cardAnchor);
        }
    }
}