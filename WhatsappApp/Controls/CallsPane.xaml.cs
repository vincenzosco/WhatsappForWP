using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using WhatsappApp.Services;

namespace WhatsappApp.Controls
{
    /// <summary>
    /// The call log, the body of the Calls tab. The rows arrive with WhatsApp's
    /// history sync, so this pane asks the client to re-tell them and draws what
    /// comes back - it is not a page of its own any more (the official app keeps
    /// the three sections on one Pivot).
    /// </summary>
    public sealed partial class CallsPane : UserControl
    {
        private bool _active;

        public CallsPane()
        {
            this.InitializeComponent();
            CallsListView.ItemsSource = DataService.Instance.Calls;
        }

        /// <summary>Called when this tab comes to the front.</summary>
        public void Activate()
        {
            if (_active) return;
            _active = true;

            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            DataService.Instance.CallsScanCompleted += OnCallsScanCompleted;

            RefreshCalls();
        }

        /// <summary>Called when this tab leaves the front.</summary>
        public void Deactivate()
        {
            if (!_active) return;
            _active = false;
            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
        }

        /// <summary>The app bar's refresh button.</summary>
        public void Refresh()
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

        private void ShowMessage(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
