using System;
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

            // Register the hardware back button
            HardwareButtons.BackPressed += HardwareButtons_BackPressed;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            HardwareButtons.BackPressed -= HardwareButtons_BackPressed;
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
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is Contact contact)
            {
                Frame.Navigate(typeof(ChatPage), contact);
                ChatListView.SelectedItem = null; // Reset selection
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

        private void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Open new chat screen
        }
    }
}
