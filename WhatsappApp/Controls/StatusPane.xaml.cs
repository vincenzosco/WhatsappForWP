using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Phone.Controls;
using WhatsappApp.Models;
using WhatsappApp.Pages;
using WhatsappApp.Services;

namespace WhatsappApp.Controls
{
    /// <summary>
    /// The Status tab's one-row-per-author view of the updates held by the client.
    /// </summary>
    public sealed partial class StatusPane : UserControl
    {
        private readonly ObservableCollection<StatusGroup> _groups =
            new ObservableCollection<StatusGroup>();
        private bool _active;

        public StatusPane()
        {
            InitializeComponent();
            StatusListView.ItemsSource = _groups;
            EmptyTitle.Text = Loc.Get("StatusPage_EmptyTitle.Text", "No status updates");
            EmptyHint.Text = Loc.Get("StatusPage_EmptyHint.Text",
                "Updates your contacts post appear here, and tapping one tells them you have seen it.");
        }

        public void Activate()
        {
            if (_active) return;
            _active = true;

            DataService.Instance.StatusesChanged -= OnStatusesChanged;
            DataService.Instance.StatusesChanged += OnStatusesChanged;
            DataService.Instance.PruneExpiredStatuses();
            Rebuild();
            DataService.Instance.ScanStatuses();
            ShowState();
            AvatarEnricher.Start();

            Diag.Ok("status: tab opened with " + DataService.Instance.Statuses.Count
                + " update(s) and " + _groups.Count + " author(s) on screen");
        }

        public void Deactivate()
        {
            if (!_active) return;
            _active = false;
            DataService.Instance.StatusesChanged -= OnStatusesChanged;
            AvatarEnricher.Stop();
        }

        private void OnStatusesChanged(object sender, EventArgs e)
        {
            Rebuild();
            ShowState();
        }

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
                if (!row.IsStatusViewed) group.Unseen++;
                if (group.Latest == null) group.Latest = row;
            }

            for (int i = 0; i < order.Count; i++)
            {
                order[i].Finish();
                order[i].Owner = DataService.Instance.FindContact(order[i].Author);
            }

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

        private void Status_Click(object sender, SelectionChangedEventArgs e)
        {
            var group = StatusListView.SelectedItem as StatusGroup;
            StatusListView.SelectedItem = null;
            if (group == null || group.Latest == null) return;

            ChatMessage row = group.Latest;
            Diag.Ok("status: tapped " + row.Id + " by " + row.ChatId
                + " (" + group.Count + " update(s))");

            try
            {
                if (!HomePage.Navigate(typeof(StatusViewerPage), row))
                {
                    Diag.Ok("status: no frame to open " + row.Id + " on");
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("StatusPane/Status_Click", ex);
            }
        }
    }
}
