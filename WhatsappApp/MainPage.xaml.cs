using System;
using System.Linq;
using Windows.Phone.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement search functionality
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Show more options menu
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = "Numero con prefisso internazionale, es. 393401234567"
            };

            // WP8.1 ContentDialog has no CloseButtonText: "Annulla" is the
            // secondary button, and the dialog can also be dismissed with the
            // hardware back button (result = None).
            var dialog = new ContentDialog
            {
                Title = "Nuova chat",
                Content = input,
                PrimaryButtonText = "Apri",
                SecondaryButtonText = "Annulla"
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            string phone = (input.Text ?? "").Trim()
                .Replace("+", "").Replace(" ", "").Replace("-", "");
            if (phone.Length < 6 || !phone.All(char.IsDigit))
            {
                return;
            }

            string jid = phone.Contains("@") ? phone : phone + "@s.whatsapp.net";

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
