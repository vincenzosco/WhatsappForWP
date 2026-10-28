using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    ///
    /// The list draws one row per person and not one per update. A contact who
    /// posted three clips is one line that says "3 updates", and the tap plays
    /// the three in `StatusViewerPage`; the rows of the day still come from the
    /// one collection `DataService.Statuses` keeps, because that is the one place
    /// an arriving status, its bytes and its `read` receipt are applied.
    /// </summary>
    public sealed partial class StatusPane : UserControl
    {
        /// <summary>The rows on screen: one per author, newest first.</summary>
        private readonly ObservableCollection<StatusGroup> _groups =
            new ObservableCollection<StatusGroup>();

        private bool _active;

        public StatusPane()
        {
            this.InitializeComponent();
            StatusListView.ItemsSource = _groups;
        }

        /// <summary>Called when this tab comes to the front.</summary>
        public void Activate()
        {
            if (_active) return;
            _active = true;

            DataService.Instance.StatusesChanged -= OnStatusesChanged;
            DataService.Instance.StatusesChanged += OnStatusesChanged;

            // What has lived its day leaves before the list is drawn: the feed is
            // only pruned on the way out of the cache and when it is written, so a
            // tab left open across midnight would keep offering a status nobody can
            // open any more (`StatusCache.LifetimeHours` is WhatsApp's own day).
            DataService.Instance.PruneExpiredStatuses();

            // The rings are rebuilt here too, and not only on a change of the feed:
            // returning from the viewer, every update just watched has cleared its
            // `read` outside this collection, so the row has to ask again.
            Rebuild();

            // Nothing to ask WhatsApp for: the client re-tells what it holds.
            DataService.Instance.ScanStatuses();
            ShowState();

            // Why the row exists: "the statuses stopped appearing" arrived with a log that
            // said nothing at all about the feed, so the tab now writes what it was handed -
            // how many updates the day holds, and how many authors that is on screen.
            Diag.Ok("status: tab opened with " + DataService.Instance.Statuses.Count
                + " update(s) and " + _groups.Count + " author(s) on screen");
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
            Rebuild();
            ShowState();
        }

        /// <summary>
        /// The day's updates folded into one row per author, in the order the feed
        /// holds them (newest first, the author of the newest update first).
        ///
        /// The rows that are already on screen are updated in place and not replaced:
        /// this runs once per arriving status, and a sync burst is dozens of them, so
        /// replacing the collection would rebuild every row dozens of times.
        /// </summary>
        private void Rebuild()
        {
            var order = new List<StatusGroup>();
            var byAuthor = new Dictionary<string, StatusGroup>(StringComparer.Ordinal);

            ObservableCollection<ChatMessage> feed = DataService.Instance.Statuses;

            for (int i = 0; i < feed.Count; i++)
            {
                ChatMessage row = feed[i];
                if (row == null || string.IsNullOrEmpty(row.ChatId)) continue;

                StatusGroup group;
                if (!byAuthor.TryGetValue(row.ChatId, out group))
                {
                    group = new StatusGroup();
                    group.Author = row.ChatId;
                    byAuthor[row.ChatId] = group;
                    order.Add(group);
                }

                group.Count++;

                // "Seen" is per update, and the ring is per person: one unwatched
                // update of the day is what keeps the circle drawn.
                if (!row.IsStatusViewed) group.Unseen++;

                // The feed is newest first, so the first update met is the one the
                // row shows and the one the tap opens.
                if (group.Latest == null) group.Latest = row;
            }

            for (int i = 0; i < order.Count; i++) order[i].Finish();

            for (int i = 0; i < order.Count; i++)
            {
                if (i < _groups.Count && _groups[i].Author == order[i].Author)
                {
                    _groups[i].Adopt(order[i]);
                }
                else if (i < _groups.Count)
                {
                    _groups[i] = order[i];
                }
                else
                {
                    _groups.Add(order[i]);
                }
            }

            while (_groups.Count > order.Count)
            {
                _groups.RemoveAt(_groups.Count - 1);
            }
        }

        private void ShowState()
        {
            bool any = _groups.Count > 0;
            EmptyState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
            StatusListView.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Status_Click(object sender, ItemClickEventArgs e)
        {
            var group = e.ClickedItem as StatusGroup;
            if (group == null || group.Latest == null) return;

            ChatMessage row = group.Latest;

            // The tap is written down **first**, before anything can go wrong. A status row
            // asks for a picture, sends a receipt and marks itself seen, and the run of
            // 2026-10-22 ended with no line at all and no guided crash report: whichever of
            // those took the app down has to name itself here, and the next tap will say
            // which one it was.
            Diag.Ok("status: tapped " + row.Id + " by " + row.ChatId
                + " (" + group.Count + " update(s))");

            try
            {
                // The tap opens the day of the person tapped, not the one row: the page
                // resolves the rest of that author's updates itself and starts at the
                // first one nobody has watched, which is where WhatsApp starts.
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
