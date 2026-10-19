using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.Contacts;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The names saved in the phone's own address book, matched to the numbers the
    /// WhatsApp chats are keyed on.
    ///
    /// Why it exists: the history a linked device receives carries the names of
    /// groups and the push names of the people who wrote, but not the address book
    /// of the primary phone - on this account the sync named 144 LID pairs and no
    /// contacts at all. Without this lookup every person is a number, while the
    /// official app on the same account calls them what the phone calls them.
    ///
    /// The address book is read once per launch, in the background, and the result
    /// is kept in three lookup tables: the full number, its last nine digits and its
    /// last eight. That is what makes a contact saved as `333 123 4567` match the
    /// international JID WhatsApp sends (`PhoneNumbers.Same` is the same rule, and
    /// the tables are its index).
    ///
    /// The names are written into `NameBook` as *saved* names, so the precedence
    /// the rest of the app already applies holds - the address book wins over the
    /// push name - and `NameCache` writes them to disk like any other.
    /// </summary>
    public static class ContactDirectory
    {
        /// <summary>Digits to the name saved for them.</summary>
        private static Dictionary<string, string> _exact = new Dictionary<string, string>();

        private static Dictionary<string, string> _tail9 = new Dictionary<string, string>();
        private static Dictionary<string, string> _tail8 = new Dictionary<string, string>();

        private static readonly object Gate = new object();

        private static bool _started;
        private static bool _loaded;

        /// <summary>True once the address book has been read. For the log and the tests.</summary>
        public static bool IsLoaded
        {
            get { return _loaded; }
        }

        /// <summary>
        /// Reads the address book in the background. Called once per process, from
        /// `DataService` after the names of the last session are in: the rows are
        /// already on screen, and `ApplyAddressBook` renames them when this answers.
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("ContactDirectory/Start", LoadAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// One pass over the address book. Every number of every contact goes into the
        /// tables with that contact's display name; a contact with no name is skipped,
        /// and so is a contact whose numbers are all short codes.
        ///
        /// Never an exception: a phone that refuses the book (no permission, no store)
        /// leaves the app with the numbers it showed before, and one log line.
        /// </summary>
        public static async Task LoadAsync()
        {
            try
            {
                ContactStore store = await ContactManager.RequestStoreAsync();
                if (store == null)
                {
                    Diag.Ok("contacts: the phone has no address book to read");
                    return;
                }

                IReadOnlyList<Contact> contacts = await store.FindContactsAsync();
                if (contacts == null || contacts.Count == 0)
                {
                    Diag.Ok("contacts: the address book is empty");
                    return;
                }

                var exact = new Dictionary<string, string>();
                var tail9 = new Dictionary<string, string>();
                var tail8 = new Dictionary<string, string>();

                int named = 0;
                for (int i = 0; i < contacts.Count; i++)
                {
                    Contact contact = contacts[i];
                    if (contact == null) continue;

                    string name = NameBook.Clean(contact.DisplayName);
                    if (name == null) continue;

                    IList<ContactPhone> phones = contact.Phones;
                    if (phones == null) continue;

                    for (int p = 0; p < phones.Count; p++)
                    {
                        ContactPhone phone = phones[p];
                        if (phone == null) continue;

                        string digits = PhoneNumbers.Digits(phone.Number);
                        if (digits == null) continue;

                        exact[digits] = name;
                        if (digits.Length >= 9) tail9[PhoneNumbers.Tail(digits, 9)] = name;
                        if (digits.Length >= 8) tail8[PhoneNumbers.Tail(digits, 8)] = name;
                        named++;
                    }
                }

                lock (Gate)
                {
                    _exact = exact;
                    _tail9 = tail9;
                    _tail8 = tail8;
                    _loaded = true;
                }

                Diag.Ok("contacts: " + contacts.Count + " in the address book, "
                    + named + " number(s) named");

                await ApplyToTheListAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("ContactDirectory/Load", ex);
            }
        }

        /// <summary>
        /// The name the address book has for a chat, or null. Only a phone number can
        /// match: a group is not a contact, and the digits of a `@lid` are an
        /// identifier, not a number (`PhoneNumbers.UserDigits` refuses both).
        /// </summary>
        public static string NameForJid(string jid)
        {
            string digits = PhoneNumbers.UserDigits(jid);
            if (digits == null) return null;

            lock (Gate)
            {
                string name;
                if (_exact.TryGetValue(digits, out name)) return name;

                if (digits.Length >= 9 && _tail9.TryGetValue(PhoneNumbers.Tail(digits, 9), out name)) return name;
                if (digits.Length >= 8 && _tail8.TryGetValue(PhoneNumbers.Tail(digits, 8), out name)) return name;
            }

            return null;
        }

        /// <summary>
        /// The rows are drawn on the UI thread and were filled on another one: the
        /// rename goes through the dispatcher, like every other change a page reads.
        /// </summary>
        private static async Task ApplyToTheListAsync()
        {
            try
            {
                CoreWindow window = CoreApplication.MainView.CoreWindow;
                if (window == null || window.Dispatcher == null) return;

                await window.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                {
                    DataService.Instance.ApplyAddressBook();
                });
            }
            catch (Exception ex)
            {
                Diag.Failed("ContactDirectory/Apply", ex);
            }
        }
    }
}
