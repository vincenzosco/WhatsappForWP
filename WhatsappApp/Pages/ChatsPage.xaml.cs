using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using PickerContact = Windows.ApplicationModel.Contacts.ContactInformation;
using Windows.UI;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Automation;
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

            // Il tooltip lo vede chi tiene premuto; il nome lo legge il lettore di
            // schermo, e lo dice anche la voce. Stesso testo, quindi stessa chiave:
            // se un'icona apre l'azione sbagliata, la si sente dire invece di
            // doverla indovinare.
            AutomationProperties.SetName(NewChatButton,
                Loc.Get("ChatsPage_NewChatTooltip", "New chat"));
            AutomationProperties.SetName(SettingsButton,
                Loc.Get("ChatsPage_SettingsTooltip", "Settings"));

            ToolTipService.SetToolTip(MoreButton, Loc.Get("ChatsPage_MoreTooltip", "More"));
            AutomationProperties.SetName(MoreButton, Loc.Get("ChatsPage_MoreTooltip", "More"));
        }

        /// <summary>
        /// Il menu dei tre puntini: quello che vale per l'elenco intero. Le
        /// azioni di una singola chat non stanno qui, perche' qui non c'e' una
        /// riga - stanno nella pressione prolungata (ChatRow_Holding).
        /// </summary>
        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var pinItem = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_PinChat", "Pin a chat") };
            pinItem.Click += PinChatMenuItem_Click;
            flyout.Items.Add(pinItem);

            var unpinItem = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_UnpinAll", "Unpin all") };
            unpinItem.Click += UnpinAllMenuItem_Click;
            flyout.Items.Add(unpinItem);

            flyout.ShowAt(MoreButton);
        }

        private async void PinChatMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ShowPinPickerAsync();
        }

        private void UnpinAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var rows = SnapshotContacts();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsPinned) DataService.Instance.SetPinned(rows[i].Id, false);
            }
        }

        /// <summary>
        /// Le righe come sono adesso, in una lista nostra. Serve perche'
        /// SetPinned sposta le righe (le fissate tornano in cima): un ciclo che
        /// legge DataService.Contacts mentre quella stessa collezione si muove
        /// salterebbe delle righe.
        /// </summary>
        private static List<Contact> SnapshotContacts()
        {
            var rows = new List<Contact>();
            for (int i = 0; i < DataService.Instance.Contacts.Count; i++)
            {
                var contact = DataService.Instance.Contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                rows.Add(contact);
            }
            return rows;
        }

        /// <summary>
        /// Una o piu' chat da fissare, con le fissate gia' scelte: il menu dice
        /// cosa cambiare, non fa ricominciare da zero.
        /// </summary>
        private async Task ShowPinPickerAsync()
        {
            var rows = SnapshotContacts();

            var list = new ListView
            {
                ItemsSource = DataService.Instance.Contacts,
                SelectionMode = ListViewSelectionMode.Multiple,
                Height = 320
            };
            list.ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<TextBlock Text=\"{Binding Name}\" Foreground=\"Black\" FontSize=\"16\" Margin=\"0,8,0,8\"/>" +
                "</DataTemplate>");

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsPinned) list.SelectedItems.Add(rows[i]);
            }

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = Loc.Get("ChatsPage_PinHint", "Pinned chats stay at the top of the list, on this phone."),
                Foreground = new SolidColorBrush(Colors.Gray),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            content.Children.Add(list);

            var dialog = new ContentDialog
            {
                Title = Loc.Get("ChatsPage_PinTitle", "Pin chats"),
                Content = content,
                PrimaryButtonText = Loc.Get("ChatsPage_PinDone", "Done"),
                SecondaryButtonText = Loc.Get("ChatsPage_Cancel", "Cancel")
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            for (int i = 0; i < rows.Count; i++)
            {
                DataService.Instance.SetPinned(rows[i].Id, list.SelectedItems.Contains(rows[i]));
            }
        }

        /// <summary>
        /// Tenere premuta una riga apre le azioni di quella chat.
        ///
        /// Si guarda HoldingState: un tocco prolungato ne alza due, e senza
        /// questo controllo il menu si aprirebbe anche quando il dito si alza.
        /// </summary>
        private void ChatRow_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started) return;

            var row = sender as FrameworkElement;
            if (row == null) return;

            var contact = row.DataContext as Contact;
            if (contact == null || string.IsNullOrEmpty(contact.Id)) return;

            e.Handled = true;
            ShowChatMenu(row, contact);
        }

        /// <summary>
        /// Pin, silenzio ed eliminazione di una riga. L'id e lo stato si
        /// catturano adesso e non si rileggono nel gestore: quando si tocca la
        /// voce, la riga puo' essere gia' stata rimossa (eliminazione) e il suo
        /// DataContext non e' piu' quello che il menu mostra.
        /// </summary>
        private void ShowChatMenu(FrameworkElement row, Contact contact)
        {
            string id = contact.Id;
            bool pinned = contact.IsPinned;
            bool muted = contact.IsMuted;

            var flyout = new MenuFlyout();

            var pin = new MenuFlyoutItem
            {
                Text = pinned
                    ? Loc.Get("ChatsPage_Unpin", "Unpin")
                    : Loc.Get("ChatsPage_Pin", "Pin")
            };
            pin.Click += delegate { DataService.Instance.SetPinned(id, !pinned); };
            flyout.Items.Add(pin);

            var mute = new MenuFlyoutItem
            {
                Text = muted
                    ? Loc.Get("ChatsPage_Unmute", "Unmute")
                    : Loc.Get("ChatsPage_Mute", "Mute")
            };
            mute.Click += delegate { DataService.Instance.SetMuted(id, !muted); };
            flyout.Items.Add(mute);

            var remove = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_Delete", "Delete chat") };
            // Il gestore aspetta la domanda invece di lanciarla e andare
            // avanti: un Task che nessuno guarda e' un'eccezione che nessuno
            // vede (CS4014), e questo e' il punto in cui l'utente decide.
            remove.Click += async (s, a) => { await ConfirmDeleteAsync(id); };
            flyout.Items.Add(remove);

            flyout.ShowAt(row);
        }

        /// <summary>
        /// Eliminare e' l'unica azione del menu che non si puo' disfare con un
        /// altro tocco, e un dito appoggiato a lungo e' anche un dito che
        /// scivolava: la domanda vale una dialog.
        /// </summary>
        private async Task ConfirmDeleteAsync(string chatId)
        {
            // Una dialog che non si apre (un'altra gia' aperta, una pagina che
            // se ne sta andando) non deve far cadere l'app: si registra e basta,
            // e la chat resta.
            bool confirmed;
            try
            {
                confirmed = await AskToDeleteAsync(chatId);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatsPage.ConfirmDeleteAsync", ex);
                return;
            }

            if (!confirmed) return;
            DataService.Instance.DeleteChat(chatId);
        }

        /// <summary>La domanda: vero se l'utente ha confermato.</summary>
        private static async Task<bool> AskToDeleteAsync(string chatId)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("ChatsPage_DeleteTitle", "Delete this chat?"),
                Content = new TextBlock
                {
                    Text = Loc.Get("ChatsPage_DeleteBody",
                        "It disappears from this phone. Nothing is deleted from WhatsApp, and a new message brings it back."),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Loc.Get("ChatsPage_DeleteConfirm", "Delete"),
                SecondaryButtonText = Loc.Get("ChatsPage_Cancel", "Cancel")
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            Nav.Current = AppSection.Chats;

            // Keep the empty state in sync with the contact list
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            DataService.Instance.Contacts.CollectionChanged += Contacts_CollectionChanged;
            UpdateEmptyState();

            // Le immagini che ci sono ancora in byte ma non piu' decodificate:
            // MemoryWatcher le ha buttate via, e senza questo l'elenco resta con
            // le iniziali finche' il server non rimanda le righe.
            DataService.Instance.RestoreAvatars();

            // Un'allegato puo' arrivare mentre questa pagina e' davanti (l'app
            // torna qui dopo il selettore) oppure prima che esista (processo
            // avviato da una condivisione): si guarda in tutti e due i casi.
            AttachmentInbox.Ready += OnAttachmentReady;
            UpdatePendingAttachment();

            // La richiesta si rifa' a ogni ingresso e a ogni passaggio a
            // connected, invece di aspettare che qualcuno apra le impostazioni:
            // all'avvio la connessione non c'e' ancora, e la lista arrivava solo
            // se l'utente tornava qui dopo averla aperta.
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;

            // Il server che non risponde e la connessione che torna: la
            // striscia la governano questi due eventi.
            CommunicationService.Instance.ServerUnavailable += OnServerUnavailable;
            CommunicationService.Instance.ConnectionEstablished += OnServerAvailable;
            RequestChats();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            AttachmentInbox.Ready -= OnAttachmentReady;
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
            CommunicationService.Instance.ServerUnavailable -= OnServerUnavailable;
            CommunicationService.Instance.ConnectionEstablished -= OnServerAvailable;
        }

        /// <summary>
        /// Il server principale non ha risposto dopo i tentativi automatici: la
        /// striscia lo dice, e resta finche' una connessione non riesce.
        /// </summary>
        private void OnServerUnavailable(object sender, EventArgs e)
        {
            ServerUnavailableText.Text = Loc.Get("CommService_ServerUnavailable",
                "Server non disponibile, riprova tra qualche minuto :)");
            ServerUnavailableBar.Visibility = Visibility.Visible;
        }

        private void OnServerAvailable(object sender, EventArgs e)
        {
            ServerUnavailableBar.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// WhatsApp e' passato a connected adesso. La richiesta fatta
        /// all'ingresso non poteva avere risposta (l'adapter risponde "non
        /// collegato" finche' il login non e' finito), e questa e' la sola cosa
        /// che fa comparire l'elenco senza toccare niente.
        /// </summary>
        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null || message.Command != "state") return;
            RequestChats();
        }

        /// <summary>
        /// Chiede l'elenco delle conversazioni. Solo se c'e' qualcuno che puo'
        /// rispondere: con WhatsApp non collegato l'adapter risponde con un
        /// errore e nessuna riga.
        /// </summary>
        private void RequestChats()
        {
            if (!CommunicationService.Instance.IsConnected) return;
            if (CommunicationService.Instance.WhatsAppState != "connected") return;

#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("chats");
#pragma warning restore 4014
        }

        private void OnAttachmentReady()
        {
            UpdatePendingAttachment();
        }

        /// <summary>La riga in cima dice che c'e' un'immagine da mandare.</summary>
        private void UpdatePendingAttachment()
        {
            PendingAttachmentBar.Visibility = AttachmentInbox.HasAttachment
                ? Visibility.Visible
                : Visibility.Collapsed;
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
