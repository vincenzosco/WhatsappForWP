using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// Status: what the contacts have posted, for a day.
    ///
    /// It is not a chat and it is not fetched from a server list: the updates arrive with the
    /// history sync and with the live messages (`NativeConnection.StatusArrived`), and the
    /// rows live in `DataService.Statuses` beside every other row the pages bind. Tapping one
    /// is what watches it - the `read` receipt goes to the broadcast list and the author's
    /// ring fills (`StatusFeed.Viewed`) - and a photo whose bytes did not come with it is
    /// fetched on that same tap.
    /// </summary>
    public sealed partial class StatusPage : Page
    {
        public StatusPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;

            StatusListView.ItemsSource = DataService.Instance.Statuses;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Nav.Current = AppSection.Status;

            DataService.Instance.StatusesChanged -= OnStatusesChanged;
            DataService.Instance.StatusesChanged += OnStatusesChanged;

            // What has arrived since the app opened is re-told here: there is nothing to ask
            // WhatsApp for, so this is the page asking the client to say what it holds.
            DataService.Instance.ScanStatuses();
            ShowState();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.StatusesChanged -= OnStatusesChanged;
        }

        private void OnStatusesChanged(object sender, EventArgs e)
        {
            ShowState();
        }

        /// <summary>
        /// An empty feed and a feed still filling are two different things, and the second of
        /// them is collapsed only when a row exists: the page does not claim there are no
        /// updates while it is still waiting for one.
        /// </summary>
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

            // A photo the history named but never brought: the tap is the moment its bytes
            // are asked for, exactly as a chat bubble's tap is.
            if (row.Type == MessageType.Image && row.MediaImage == null)
            {
                DataService.Instance.RequestStatusMedia(row);
            }
        }
    }
}
