using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Call log. The data is not local: the adapter derives it from the GOWA
    /// history (incoming calls only, from the most recent chats) and sends it
    /// when it receives the "calls" command.
    /// </summary>
    public sealed partial class CallsPage : Page
    {
        public CallsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;

            CallsListView.ItemsSource = DataService.Instance.Calls;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Nav.Current = AppSection.Calls;

            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            DataService.Instance.CallsScanCompleted += OnCallsScanCompleted;

            RefreshCalls();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshCalls();
        }

        private void RefreshCalls()
        {
            if (!CommunicationService.Instance.IsConnected)
            {
                ShowMessage(Loc.Get("CallsPage_NotConnected",
                    "Not connected to the server: call records are unavailable."));
                return;
            }

            DataService.Instance.ClearCalls();
            ShowMessage(Loc.Get("CallsPage_Scanning", "Looking for calls..."));

            // OnNavigatedTo is not async: the request is sent and the
            // "calls.done" frame is awaited to know the adapter has finished.
#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("calls");
#pragma warning restore 4014
        }

        private void OnCallsScanCompleted(object sender, EventArgs e)
        {
            if (DataService.Instance.Calls.Count == 0)
            {
                ShowMessage(Loc.Get("CallsPage_EmptyHint",
                    "Incoming calls found in the most recent chats. Nothing to show yet."));
                return;
            }

            StatusText.Visibility = Visibility.Collapsed;
        }

        /// <summary>Shows a status line instead of (or above) the empty list.</summary>
        private void ShowMessage(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
