using System;
using System.Linq;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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

            var content = new StackPanel();
            content.Children.Add(input);
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

            string jid = null;
            string phone = null;
            while (jid == null)
            {
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;

                phone = (input.Text ?? "").Trim()
                    .Replace("+", "").Replace(" ", "").Replace("-", "");
                if (phone.Length < 6 || !phone.All(char.IsDigit))
                {
                    error.Visibility = Visibility.Visible;
                    continue;
                }

                jid = phone.Contains("@") ? phone : phone + "@s.whatsapp.net";
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
    }
}
