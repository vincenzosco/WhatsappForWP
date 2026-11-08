using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Phone.Controls;
using WhatsappApp.Services;

namespace WhatsappApp.Controls
{
    /// <summary>
    /// The Calls tab's view of the call records supplied by WhatsApp history sync.
    /// </summary>
    public sealed partial class CallsPane : UserControl
    {
        private bool _active;

        public CallsPane()
        {
            InitializeComponent();
            CallsListView.ItemsSource = DataService.Instance.Calls;
        }

        public void Activate()
        {
            if (_active) return;
            _active = true;

            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            DataService.Instance.CallsScanCompleted += OnCallsScanCompleted;
            RefreshCalls();
        }

        public void Deactivate()
        {
            if (!_active) return;
            _active = false;
            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
        }

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
            DataService.Instance.MarkCallsSeen();
        }

        private void CallsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CallsListView.SelectedItem = null;
        }

        private void ShowMessage(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
