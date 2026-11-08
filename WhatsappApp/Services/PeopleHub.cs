using System;
using System.Windows;
using Windows.ApplicationModel.Contacts;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The phone's People hub, opened from a chat: the person behind a conversation, on the
    /// card the rest of the phone reads.
    ///
    /// Why a card and not a contact this app writes: the installed WP8.1 Silverlight
    /// projection exposes `ContactManager.ShowContactCard` and does not expose
    /// `ContactStore.AddContactAsync`. The People hub owns the card: from there the person is
    /// saved, called or messaged with the rules the phone already has. A chat that is a number
    /// in this app is a contact on that card, and the dialler and People tile can use it too.
    ///
    /// What is deliberately not offered: a group (a group is not a person and has no number
    /// to show), a `@lid` (its digits are an identifier, not a number) and a chat that has no
    /// number yet - `PhoneNumbers.UserDigits` refuses all three, and the menu asks before it
    /// draws the entry.
    /// </summary>
    public static class PeopleHub
    {
        /// <summary>
        /// Whether this chat is a person the People hub can show. The menu asks first, so a
        /// group never shows an entry that would answer with nothing.
        /// </summary>
        public static bool CanShow(string jid)
        {
            return PhoneNumbers.UserDigits(jid) != null;
        }

        /// <summary>
        /// Opens the People card for a chat, anchored to the row that was held down.
        ///
        /// The card is given the number and the name the list already shows, so a person
        /// WhatsApp calls `Anna` is not a number on the card either. Nothing is written: the
        /// hub decides what a save means, and this app has no business writing the address
        /// book behind its back.
        ///
        /// Never throws: the tap comes from a menu, and a menu entry that throws takes the
        /// page with it. Every outcome is one line of the log.
        /// </summary>
        public static void Show(string jid, string name, Rect anchor)
        {
            string digits = PhoneNumbers.UserDigits(jid);
            if (digits == null)
            {
                Diag.Ok("people: this chat is not a person, there is no card to show");
                return;
            }

            string number = "+" + digits;

            try
            {
                var contact = new Contact();

                string shown = NameBook.Clean(name);
                contact.FirstName = string.IsNullOrEmpty(shown) ? number : shown;

                var phone = new ContactPhone();
                phone.Number = number;
                phone.Kind = ContactPhoneKind.Mobile;
                contact.Phones.Add(phone);

                var cardAnchor = new Windows.Foundation.Rect(anchor.X, anchor.Y, anchor.Width, anchor.Height);
                ContactManager.ShowContactCard(contact, cardAnchor);

                Diag.Ok("people: card for " + number);
            }
            catch (Exception ex)
            {
                Diag.Failed("PeopleHub/Show", ex);
            }
        }
    }
}
