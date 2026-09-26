using System;
using System.Text;
using System.Threading.Tasks;
using PickerContact = Windows.ApplicationModel.Contacts.ContactInformation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>Sezione chat: elenco conversazioni e nuova chat.</summary>
    public sealed partial class ChatsPage : Page
    {
        public ChatsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;

            ChatListView.ItemsSource = DataService.Instance.Contacts;

            // I pulsanti con la sola icona non usano x:Uid (sovrascriverebbe il
            // Path): il testo e' un tooltip impostato qui.
            ToolTipService.SetToolTip(NewChatButton, Loc.Get("ChatsPage_NewChatTooltip", "New chat"));
            ToolTipService.SetToolTip(SettingsButton, Loc.Get("ChatsPage_SettingsTooltip", "Settings"));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            Nav.Current = AppSection.Chats;

            // Keep the empty state in sync with the contact list
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            DataService.Instance.Contacts.CollectionChanged += Contacts_CollectionChanged;
            UpdateEmptyState();

            // OnNavigatedTo is not async: fire the chats request and ignore the
            // task. Si chiede l'elenco delle conversazioni, non la rubrica:
            // /user/my/contacts e' vuota su un account appena collegato mentre
            // /chats e' piena. Solo se la lista e' vuota: l'adapter risponde con
            // un frame per conversazione.
            if (CommunicationService.Instance.IsConnected && DataService.Instance.Contacts.Count == 0)
#pragma warning disable 4014
                CommunicationService.Instance.SendControlAsync("chats");
#pragma warning restore 4014
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
        }

        private void Contacts_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            bool empty = DataService.Instance.Contacts.Count == 0;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ConnectionPage));
        }

        private void ChatListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;

            var contact = e.AddedItems[0] as Contact;
            if (contact == null) return;

            Frame.Navigate(typeof(ChatPage), contact);
            ChatListView.SelectedItem = null; // Reset selection
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = Loc.Get("NewChat_Prompt",
                    "Phone number with country code (e.g. 393401234567)")
            };

            // L'errore di validazione vive dentro la dialog: cosi' l'utente
            // ritrova il numero che aveva digitato invece di ripartire da zero.
            var error = new TextBlock
            {
                Text = Loc.Get("NewChat_Invalid",
                    "Enter a valid number with country code (e.g. 393401234567)."),
                Foreground = new SolidColorBrush(Colors.Red),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };

            // Il selettore contatti del sistema riempie il campo: e' il consenso
            // dell'utente, quindi l'app non legge la rubrica per conto suo.
            var pickButton = new Button
            {
                Content = Loc.Get("NewChat_PickContact", "Choose from contacts"),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var content = new StackPanel();
            content.Children.Add(input);
            content.Children.Add(pickButton);
            content.Children.Add(error);

            // WP8.1 ContentDialog has no CloseButtonText: the cancel text is the
            // secondary button, and the dialog can also be dismissed with the
            // hardware back button (result = None).
            var dialog = new ContentDialog
            {
                Title = Loc.Get("NewChat_Title", "New chat"),
                Content = content,
                PrimaryButtonText = Loc.Get("NewChat_Open", "Open"),
                SecondaryButtonText = Loc.Get("NewChat_Cancel", "Cancel")
            };

            Contact chosen = null;

            pickButton.Click += async (s, a) =>
            {
                string picked = await PickFromContactsAsync();
                if (!string.IsNullOrEmpty(picked)) input.Text = picked;
            };

            // Le conversazioni che il server conosce gia': sceglierne una evita
            // di digitare un numero che l'utente probabilmente ha sott'occhio.
            var known = new ListView
            {
                ItemsSource = DataService.Instance.Contacts,
                MaxHeight = 220,
                SelectionMode = ListViewSelectionMode.Single
            };
            known.ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<TextBlock Text=\"{Binding Name}\" Foreground=\"Black\" FontSize=\"16\" Margin=\"0,8,0,8\"/>" +
                "</DataTemplate>");
            known.SelectionChanged += (s, a) =>
            {
                if (a.AddedItems.Count == 0) return;
                chosen = a.AddedItems[0] as Contact;
                dialog.Hide();
            };

            if (DataService.Instance.Contacts.Count > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = Loc.Get("NewChat_Synced", "Conversations already on the server"),
                    Foreground = new SolidColorBrush(Colors.Gray),
                    FontSize = 13,
                    Margin = new Thickness(0, 12, 0, 4)
                });
                content.Children.Add(known);
            }

            string jid = null;
            string phone = null;
            while (jid == null)
            {
                var result = await dialog.ShowAsync();

                // Una chat scelta dall'elenco chiude la dialog da sola: il
                // risultato e' None, quindi si controlla la scelta per prima.
                if (chosen != null)
                {
                    Frame.Navigate(typeof(ChatPage), chosen);
                    return;
                }

                if (result != ContentDialogResult.Primary) return;

                phone = NormalizePhone(input.Text);
                if (phone.Length < 6)
                {
                    error.Visibility = Visibility.Visible;
                    continue;
                }

                jid = phone + "@s.whatsapp.net";
            }

            var existing = DataService.Instance.FindContact(jid);
            if (existing != null)
            {
                Frame.Navigate(typeof(ChatPage), existing);
                return;
            }

            var contact = new Contact
            {
                Id = jid,
                Name = "+" + phone,
                Initials = phone.Substring(0, 2).ToUpper(),
                UnreadCount = 0
            };
            DataService.Instance.AddContact(contact);
            Frame.Navigate(typeof(ChatPage), contact);
        }

        /// <summary>
        /// Apre il selettore contatti del sistema e restituisce il primo numero
        /// trovato, ripulito. Vuoto se l'utente annulla o il contatto non ha
        /// numeri: non e' un errore, e' una scelta.
        /// </summary>
        // CS0618: il compilatore propone Contact/PickContactAsync, che sono
        // l'API di Windows 10. Su WP8.1 l'unica disponibile e' ContactInformation:
        // l'avviso e' corretto e non c'e' niente da fare, quindi non si stampa
        // ad ogni build (altrimenti un avviso nuovo non si nota piu').
#pragma warning disable 618
        private static async Task<string> PickFromContactsAsync()
        {
            try
            {
                var picker = new Windows.ApplicationModel.Contacts.ContactPicker();
                PickerContact contact = await picker.PickSingleContactAsync();
                if (contact == null || contact.PhoneNumbers == null || contact.PhoneNumbers.Count == 0) return "";

                foreach (var phone in contact.PhoneNumbers)
                {
                    string number = NormalizePhone(phone.Value);
                    if (!string.IsNullOrEmpty(number)) return number;
                }
                return "";
            }
            catch (Exception ex)
            {
                // Alcuni dispositivi rifiutano il selettore: non e' un crash.
                Diag.Failed("ChatsPage.PickFromContactsAsync", ex);
                return "";
            }
        }
#pragma warning restore 618

        /// <summary>Solo le cifre: il numero deve restare quello che l'app si aspetta.</summary>
        private static string NormalizePhone(string value)
        {
            if (value == null) return "";
            var digits = new StringBuilder();
            foreach (char c in value)
            {
                if (char.IsDigit(c)) digits.Append(c);
            }
            return digits.Length >= 6 ? digits.ToString() : "";
        }
    }
}
