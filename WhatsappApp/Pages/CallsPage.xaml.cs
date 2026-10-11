using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Call log. There is nothing to ask a server for: WhatsApp puts the call log in the
    /// history sync (`HistorySync.callLogRecords`), so the rows arrive on their own and this
    /// page is a view over what the client already holds (`DataService.Calls`).
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
            if (!NativeConnection.Instance.IsConnected)
            {
                ShowMessage(Loc.Get("CallsPage_NotConnected",
                    "Not connected to the server: call records are unavailable."));
                return;
            }

            ShowMessage(Loc.Get("CallsPage_Scanning", "Looking for calls..."));

            // The client re-tells the log it holds and raises CallsScanCompleted when it has.
            DataService.Instance.ScanCalls();
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
