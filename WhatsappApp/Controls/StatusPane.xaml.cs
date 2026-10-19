using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Controls
{
    /// <summary>
    /// Status: what the contacts have posted, for a day. It is the body of the
    /// Status tab, moved out of its page so the three sections can live on one
    /// surface and swipe into each other (the official app's Pivot).
    /// </summary>
    public sealed partial class StatusPane : UserControl
    {
        private bool _active;

        public StatusPane()
        {
            this.InitializeComponent();
            StatusListView.ItemsSource = DataService.Instance.Statuses;
        }

        /// <summary>Called when this tab comes to the front.</summary>
        public void Activate()
        {
            if (_active) return;
            _active = true;

            DataService.Instance.StatusesChanged -= OnStatusesChanged;
            DataService.Instance.StatusesChanged += OnStatusesChanged;

            // Nothing to ask WhatsApp for: the client re-tells what it holds.
            DataService.Instance.ScanStatuses();
            ShowState();
        }

        /// <summary>Called when this tab leaves the front.</summary>
        public void Deactivate()
        {
            if (!_active) return;
            _active = false;
            DataService.Instance.StatusesChanged -= OnStatusesChanged;
        }

        private void OnStatusesChanged(object sender, EventArgs e)
        {
            ShowState();
        }

        private void ShowState()
        {
            bool any = DataService.Instance.Statuses.Count > 0;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            StatusListView.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Status_Click(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as ChatMessage;
            if (row == null) return;

            DataService.Instance.MarkStatusViewed(row);

            if (row.Type == MessageType.Image && row.MediaImage == null)
            {
                DataService.Instance.RequestStatusMedia(row);
            }
        }
    }
}
