using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using WhatsappApp.Models;
using WhatsappApp.Pages;
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

            // The tap is written down **first**, before anything can go wrong. A status row
            // asks for a picture, sends a receipt and marks itself seen, and the run of
            // 2026-10-22 ended with no line at all and no guided crash report: whichever of
            // those took the app down has to name itself here, and the next tap will say
            // which one it was.
            Diag.Ok("status: tapped " + row.Id + " by " + row.ChatId);

            try
            {
                // The tap opens the update. Watching it - the `read` receipt - and
                // asking for its bytes belong to the page that shows it, so there is
                // one place that knows what opening a status means, and the list only
                // hands the row over. Before this, the tap sent the receipt and then
                // nothing appeared: the status was watched but never seen.
                var frame = Window.Current.Content as Frame;
                if (frame == null)
                {
                    Diag.Ok("status: no frame to open " + row.Id + " on");
                    return;
                }

                if (!frame.Navigate(typeof(StatusViewerPage), row))
                {
                    Diag.Ok("status: the frame refused to open " + row.Id);
                }
            }
            catch (Exception ex)
            {
                // A status that cannot be opened is one row, not a process: what is caught
                // here is what used to leave the event handler and close the app.
                Diag.Failed("StatusPane/Status_Click", ex);
            }
        }
    }
}
