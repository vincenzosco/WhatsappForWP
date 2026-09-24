using System;
using System.Linq;
using Windows.Phone.UI.Input;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Pages;
using WhatsappApp.Services;

namespace WhatsappApp
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Required;

            // I pulsanti dell'app bar sono solo icone: il testo (che il sistema
            // legge anche come etichetta di accessibilita') e' un tooltip.
            ToolTipService.SetToolTip(NewChatButton, Loc.Get("MainPage_NewChatTooltip", "New chat"));
            ToolTipService.SetToolTip(SettingsButton, Loc.Get("MainPage_SettingsTooltip", "Settings"));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ChatListView.ItemsSource = DataService.Instance.Contacts;

            // Keep the empty state in sync with the contact list
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            DataService.Instance.Contacts.CollectionChanged += Contacts_CollectionChanged;
            UpdateEmptyState();

            // OnNavigatedTo is not async: fire the contacts request and ignore the task
            if (CommunicationService.Instance.IsConnected)
                CommunicationService.Instance.SendControlAsync("contacts");

            // Register the hardware back button
            HardwareButtons.BackPressed += HardwareButtons_BackPressed;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
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

        private void HardwareButtons_BackPressed(object sender, BackPressedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private void ChatListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0)
            {
                var contact = e.AddedItems[0] as Contact;
                if (contact != null)
                {
                    Frame.Navigate(typeof(ChatPage), contact);
                    ChatListView.SelectedItem = null; // Reset selection
                }
            }
        }

        private void ConnectionButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ConnectionPage));
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = Loc.Get("MainPage_NewChatPrompt",
                    "Phone number with country code (e.g. 393401234567)")
            };

            // L'errore di validazione vive dentro la dialog: cosi' l'utente
            // ritrova il numero che aveva digitato invece di ripartire da zero.
            var error = new TextBlock
            {
                Text = Loc.Get("MainPage_NewChatInvalid",
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
                Title = Loc.Get("MainPage_NewChatTitle", "New chat"),
                Content = content,
                PrimaryButtonText = Loc.Get("MainPage_NewChatOpen", "Open"),
                SecondaryButtonText = Loc.Get("MainPage_NewChatCancel", "Cancel")
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

            var existing = DataService.Instance.Contacts.FirstOrDefault(c => c.Id == jid);
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
                AvatarColor = "#FF075E54",
                IsOnline = false,
                UnreadCount = 0
            };
            DataService.Instance.AddContact(contact);
            Frame.Navigate(typeof(ChatPage), contact);
        }
    }
}
