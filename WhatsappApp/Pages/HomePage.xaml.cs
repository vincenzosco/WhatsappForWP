using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;
using Microsoft.Phone.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using WhatsappApp.Controls;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The three WhatsApp sections, hosted in the native Windows Phone Pivot.
    /// </summary>
    public sealed partial class HomePage : PhoneApplicationPage
    {
        private const string NavigationParameterKey = "WhatsappApp.NavigationParameter";
        private const int LongPressMilliseconds = 650;

        private ApplicationBar _chatsApplicationBar;
        private ApplicationBar _statusApplicationBar;
        private ApplicationBar _callsApplicationBar;
        private bool _movingTabs;
        private bool _ready;
        private int _section;
        private bool _homeSearching;
        private readonly ObservableCollection<Contact> _matches = new ObservableCollection<Contact>();
        private bool _searching;
        private DispatcherTimer _callTicker;
        private DispatcherTimer _listRefresh;
        private DispatcherTimer _holdTimer;
        private FrameworkElement _heldRow;
        private Contact _heldContact;
        private Action _modalPrimaryAction;
        private Action _modalSecondaryAction;

        public HomePage()
        {
            InitializeComponent();
            _chatsApplicationBar = Resources["ChatsApplicationBar"] as ApplicationBar;
            _statusApplicationBar = Resources["StatusApplicationBar"] as ApplicationBar;
            _callsApplicationBar = Resources["CallsApplicationBar"] as ApplicationBar;

            ChatListView.ItemsSource = DataService.Instance.Contacts;

            // The pivot draws its own headers, the Metro way - the same large
            // lowercase labels the reference client shows, active and inactive
            // handled by the control itself.
            ((PivotItem)Sections.Items[0]).Header = Loc.Get("Nav_Chats", "chats").ToLowerInvariant();
            ((PivotItem)Sections.Items[1]).Header = Loc.Get("Nav_Status", "status").ToLowerInvariant();
            ((PivotItem)Sections.Items[2]).Header = Loc.Get("Nav_Calls", "calls").ToLowerInvariant();

            SearchPrompt.Text = Loc.Get("ChatsPage_Search.PlaceholderText", "Search");
            PendingAttachmentText.Text = Loc.Get("ChatsPage_PendingImage.Text",
                "An image is ready: open a chat to send it");
            DiagnosticsButton.Content = Loc.Get("ChatsPage_Diagnostics.Content", "Why is it empty?");

            SetAutomationName(CloseSearchButton, Loc.Get("ChatsPage_SearchClose", "Close the search"));
            SetAutomationName(ModalDismissButton, Loc.Get("ChatsPage_CloseMenu", "Close menu"));

            AcceptCallButton.Content = Loc.Get("Call_Accept", "Accept");
            DeclineCallButton.Content = Loc.Get("Call_Decline", "Decline");
            EndCallButton.Content = Loc.Get("Call_End", "End");

            //Header.SetUnread(DataService.Instance.UnreadChats());
            _ready = true;
        }

        public AppSection CurrentSection
        {
            get { return SectionOf(_section); }
        }

        /// <summary>
        /// Passes a page argument through the Silverlight navigation state before opening its
        /// XAML route. The destination page consumes this key in OnNavigatedTo.
        /// </summary>
        public static bool Navigate(Type pageType, object parameter)
        {
            if (pageType == null) return false;
            return NavigateToPage(pageType.Name, parameter);
        }

        public static bool NavigateToPage(string pageName, object parameter)
        {
            PhoneApplicationFrame frame = RootFrame;
            if (frame == null || string.IsNullOrEmpty(pageName)) return false;

            if (parameter == null)
            {
                PhoneApplicationService.Current.State.Remove(NavigationParameterKey);
            }
            else
            {
                PhoneApplicationService.Current.State[NavigationParameterKey] = parameter;
            }

            return frame.Navigate(new Uri("/Pages/" + pageName + ".xaml", UriKind.Relative));
        }

        private static PhoneApplicationFrame RootFrame
        {
            get
            {
                if (Application.Current == null) return null;
                return Application.Current.RootVisual as PhoneApplicationFrame;
            }
        }

        private static void SetAutomationName(FrameworkElement element, string name)
        {
            AutomationProperties.SetName(element, name);
        }

        private void Show(int index, bool notify)
        {
            if (index < 0 || index > 2) index = 0;
            if (!_ready) return;

            bool changed = index != _section;
            _section = index;

            if (notify && Sections.SelectedIndex != index)
            {
                _movingTabs = true;
                Sections.SelectedIndex = index;
                _movingTabs = false;
            }

            if (_homeSearching && index != 0) CloseSearch();
            ApplicationBar = index == 0 ? _chatsApplicationBar
                : (index == 1 ? _statusApplicationBar : _callsApplicationBar);
            UpdateChatsMenuItems();

            if (index == 1) StatusView.Activate(); else StatusView.Deactivate();
            if (index == 2) CallsView.Activate(); else CallsView.Deactivate();

            if (index == 2)
                DataService.Instance.MarkCallsSeen();
            else
                SessionService.MarkOpened(SectionOf(index));

            if (index == 0)
            {
                if (changed) ActivateChats();
            }
            else
            {
                DeactivateChats();
            }

            if (changed)
            {
                SessionService.Section = SectionOf(index);
                RefreshBadges();
            }
        }

        private static AppSection SectionOf(int index)
        {
            if (index == 1) return AppSection.Status;
            if (index == 2) return AppSection.Calls;
            return AppSection.Chats;
        }

        private void Sections_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_movingTabs || !_ready) return;
            int index = Sections.SelectedIndex;
            if (index == _section) return;

            _movingTabs = true;
            Show(index, false);
            _movingTabs = false;
        }

        private void RefreshBadges()
        {
            //Header.SetUnread(DataService.Instance.UnreadChats());
            UpdateChatsMenuItems();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Diag.Ok("pair: HomePage is visible");
            SystemTray.IsVisible = true;
            SystemTray.BackgroundColor = Color.FromArgb(255, 8, 11, 12);
            SystemTray.ForegroundColor = Colors.White;
            HomeWallpaper.Visibility = Visibility.Collapsed;
            Show(0, true);
            ActivateChats();
            EvaluateChats();
            ShowConnectionBar();
            RefreshBadges();

            DataService.Instance.MessageArrived -= OnBadgeChanged;
            DataService.Instance.MessageArrived += OnBadgeChanged;
            DataService.Instance.StatusesChanged -= OnStatusesChanged;
            DataService.Instance.StatusesChanged += OnStatusesChanged;
            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            DataService.Instance.CallsScanCompleted += OnCallsScanCompleted;
            DataService.Instance.CallChanged -= OnCallChanged;
            DataService.Instance.CallChanged += OnCallChanged;
            ShowCall();
            OpenPinnedChat();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DeactivateChats();
            StatusView.Deactivate();
            CallsView.Deactivate();

            DataService.Instance.MessageArrived -= OnBadgeChanged;
            DataService.Instance.StatusesChanged -= OnStatusesChanged;
            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            DataService.Instance.CallChanged -= OnCallChanged;
        }

        private void OnCallChanged(object sender, EventArgs e)
        {
            ShowCall();
        }

        private void ShowCall()
        {
            CallSession call = DataService.Instance.Call;
            if (call == null || !call.Shows)
            {
                StopCallTicker();
                CallBanner.Visibility = Visibility.Collapsed;
                return;
            }

            CallBanner.Visibility = Visibility.Visible;
            CallTitleText.Text = NameOf(call.PeerJid);

            string kind = call.IsVideo
                ? Loc.Get("Call_Video", "Video call")
                : Loc.Get("Call_Voice", "Voice call");
            string state;
            switch (call.Phase)
            {
                case CallPhase.Ringing:
                    state = Loc.Get("Call_Incoming", "Incoming call");
                    break;
                case CallPhase.Connecting:
                    state = Loc.Get("Call_Connecting", "Connecting...");
                    break;
                case CallPhase.Active:
                    state = Loc.Get("Call_Active", "In call");
                    break;
                default:
                    state = Loc.Get("Call_Ended", "Call ended");
                    break;
            }

            string detail = kind + " · " + state;
            if (call.WasAnswered) detail = detail + " · " + CallDuration(call.ConnectedSeconds);
            if (call.Phase == CallPhase.Active)
            {
                detail = detail + "\n" + Loc.Get("Call_NoAudio",
                    "No audio yet: this build only signals the call.");
            }
            else if (call.Phase == CallPhase.Ended && !string.IsNullOrEmpty(call.EndedReason))
            {
                detail = detail + " (" + call.EndedReason + ")";
            }
            CallDetailText.Text = detail;

            bool ringing = call.Phase == CallPhase.Ringing;
            PhoneApplicationFrame frame = RootFrame;
            bool onCallPage = frame != null && frame.CurrentSource != null
                && frame.CurrentSource.ToString().EndsWith("/Pages/CallPage.xaml", StringComparison.OrdinalIgnoreCase);
            if (ringing && !onCallPage)
            {
                NavigateToPage("CallPage", null);
                return;
            }

            bool live = call.Phase == CallPhase.Connecting || call.Phase == CallPhase.Active;
            AcceptCallButton.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
            DeclineCallButton.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
            EndCallButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            StartCallTicker();
        }

        private void StartCallTicker()
        {
            if (_callTicker == null)
            {
                _callTicker = new DispatcherTimer();
                _callTicker.Interval = TimeSpan.FromSeconds(1);
                _callTicker.Tick += OnCallTick;
            }
            if (!_callTicker.IsEnabled) _callTicker.Start();
        }

        private void StopCallTicker()
        {
            if (_callTicker != null && _callTicker.IsEnabled) _callTicker.Stop();
        }

        private void OnCallTick(object sender, EventArgs e)
        {
            CallSession call = DataService.Instance.Call;
            if (call == null || !call.Shows)
            {
                StopCallTicker();
                CallBanner.Visibility = Visibility.Collapsed;
                return;
            }

            if (call.Expired(DateTime.UtcNow, DataService.CallSetupSeconds))
            {
                Guarded.RunGuardedAsync("HomePage/no answer", DataService.Instance.EndUnansweredCallAsync());
                return;
            }
            ShowCall();
        }

        private static string CallDuration(int seconds)
        {
            if (seconds < 0) seconds = 0;
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }

        private static string NameOf(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "";
            return DataService.DisplayNameForJid(jid);
        }

        private void AcceptCall_Click(object sender, RoutedEventArgs e)
        {
            Guarded.RunGuardedAsync("HomePage/accept call", DataService.Instance.AcceptCallAsync());
        }

        private void DeclineCall_Click(object sender, RoutedEventArgs e)
        {
            Guarded.RunGuardedAsync("HomePage/decline call", DataService.Instance.DeclineCallAsync());
        }

        private void EndCall_Click(object sender, RoutedEventArgs e)
        {
            if (DataService.Instance.Call.Phase == CallPhase.Ended)
            {
                DataService.Instance.DismissCall();
                return;
            }
            Guarded.RunGuardedAsync("HomePage/end call", DataService.Instance.EndCallAsync());
        }

        private const int ListRefreshSeconds = DataService.ListRefreshSeconds;

        private void StartListRefresh()
        {
            if (_listRefresh == null)
            {
                _listRefresh = new DispatcherTimer();
                _listRefresh.Interval = TimeSpan.FromSeconds(ListRefreshSeconds);
                _listRefresh.Tick += OnListRefreshTick;
            }
            _listRefresh.Start();
        }

        private void StopListRefresh()
        {
            if (_listRefresh != null) _listRefresh.Stop();
        }

        private void OnListRefreshTick(object sender, EventArgs e)
        {
            try
            {
                DataService.Instance.RefreshChatList();
            }
            catch (Exception ex)
            {
                Diag.Failed("HomePage/list refresh", ex);
            }
        }

        private void ActivateChats()
        {
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            DataService.Instance.Contacts.CollectionChanged += Contacts_CollectionChanged;
            DataService.Instance.RestoreAvatars();
            AvatarEnricher.Start();

            AttachmentInbox.Ready -= OnAttachmentReady;
            AttachmentInbox.Ready += OnAttachmentReady;
            UpdatePendingAttachment();

            DataService.Instance.ChatListCompleted -= OnChatListCompleted;
            DataService.Instance.ChatListCompleted += OnChatListCompleted;
            DataService.Instance.SyncProgressChanged -= OnSyncProgressChanged;
            DataService.Instance.SyncProgressChanged += OnSyncProgressChanged;
            UpdateSyncBar();
            StartListRefresh();

            NativeConnection.Instance.ConnectionStatusChanged -= OnClientStatusChanged;
            NativeConnection.Instance.ConnectionStatusChanged += OnClientStatusChanged;
            EvaluateChats();
            ShowConnectionBar();
            RefreshBadges();
        }

        private void DeactivateChats()
        {
            StopListRefresh();
            AvatarEnricher.Stop();
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            AttachmentInbox.Ready -= OnAttachmentReady;
            NativeConnection.Instance.ConnectionStatusChanged -= OnClientStatusChanged;
            DataService.Instance.ChatListCompleted -= OnChatListCompleted;
            DataService.Instance.SyncProgressChanged -= OnSyncProgressChanged;
        }

        private void OnBadgeChanged(object sender, ChatMessage message) { RefreshBadges(); }

        /// <summary>
        /// The history sync is moving. The count is kept on the socket thread, so the bar is
        /// drawn on the dispatcher the page lives on.
        /// </summary>
        private void OnSyncProgressChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateSyncBar));
        }

        /// <summary>
        /// The sync bar, from the three real numbers: how many messages the sync has put in the
        /// chats, about how many it carries in all, and how far along it is. It sits between the
        /// pivot's titles and the first chat, and hides the moment the history is all here.
        /// </summary>
        private void UpdateSyncBar()
        {
            bool running = DataService.Instance.SyncInProgress;
            SyncProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            if (!running) return;

            SyncProgressBar.Value = DataService.Instance.SyncPercent;
            SyncProgressText.Text = string.Format(
                Loc.Get("HomePage_SyncProgress", "Syncing messages: {0} ({1}%)"),
                DataService.Instance.SyncedMessages, DataService.Instance.SyncPercent);
        }
        private void OnStatusesChanged(object sender, EventArgs e) { RefreshBadges(); }
        private void OnCallsScanCompleted(object sender, EventArgs e) { RefreshBadges(); }
        private void OnClientStatusChanged(object sender, string status) { ShowConnectionBar(); }

        private void ShowConnectionBar()
        {
            if (NativeConnection.Instance.IsConnected)
            {
                ConnectionBar.Visibility = Visibility.Collapsed;
                return;
            }
            ConnectionBarText.Text = Loc.Get("ChatsPage_NotConnected", "Connecting to WhatsApp...");
            ConnectionBar.Visibility = Visibility.Visible;
        }

        private void StatusMoreButton_Click(object sender, RoutedEventArgs e)
        {
            ShowActionMenu(Loc.Get("Nav_Status", "status"));
            AddModalOptionHandler(Loc.Get("StatusPage_MarkAllViewed", "Mark all as viewed"), MarkAllStatusesMenuItem_Click);
        }

        private void MarkAllStatusesMenuItem_Click(object sender, RoutedEventArgs e)
        {
            int changed = DataService.Instance.MarkAllStatusesViewed();
            if (changed == 0) Diag.Ok("status: mark all, nothing was unwatched");
            RefreshBadges();
        }

        private void CallsMoreButton_Click(object sender, RoutedEventArgs e)
        {
            ShowActionMenu(Loc.Get("Nav_Calls", "calls"));
            AddModalOptionHandler(Loc.Get("CallsPage_MarkAllSeen", "Mark all as seen"), MarkCallsSeenMenuItem_Click);
        }

        private void MarkCallsSeenMenuItem_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.MarkCallsSeen();
            RefreshBadges();
        }

        private void OnAttachmentReady()
        {
            UpdatePendingAttachment();
        }

        private void UpdatePendingAttachment()
        {
            PendingAttachmentBar.Visibility = AttachmentInbox.HasAttachment
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Contacts_CollectionChanged(object sender,
            System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_searching) ApplyFilter(SearchBox.Text);
            EvaluateChats();
            RefreshBadges();
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchApplicationBarButton_Click(sender, e);
        }

        private void SearchApplicationBarButton_Click(object sender, EventArgs e)
        {
            _homeSearching = true;
            SearchPanel.Visibility = Visibility.Visible;
            SearchPrompt.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplicationBar = null;
            SearchBox.Focus();
        }

        private void CloseSearchButton_Click(object sender, RoutedEventArgs e)
        {
            CloseSearch();
        }

        private void CloseSearch()
        {
            _homeSearching = false;
            SearchBox.Text = "";
            SearchPrompt.Visibility = Visibility.Visible;
            SearchPanel.Visibility = Visibility.Collapsed;
            if (_ready && _section == 0) ApplicationBar = _chatsApplicationBar;
        }

        private void NewChatApplicationBarButton_Click(object sender, EventArgs e)
        {
            NewChatButton_Click(sender, new RoutedEventArgs());
        }

        private void PinChatsAppBarMenuItem_Click(object sender, EventArgs e)
        {
            Guarded.RunGuardedAsync("HomePage/pin chats", ShowPinPickerAsync());
        }

        private void UnpinAllAppBarMenuItem_Click(object sender, EventArgs e)
        {
            UnpinAllMenuItem_Click(sender, new RoutedEventArgs());
        }

        private void RefreshCallsAppBarMenuItem_Click(object sender, EventArgs e)
        {
            CallsView.Refresh();
        }

        private void ReadAllAppBarMenuItem_Click(object sender, EventArgs e)
        {
            ReadAllMenuItem_Click(sender, new RoutedEventArgs());
        }

        private void MarkAllStatusesAppBarMenuItem_Click(object sender, EventArgs e)
        {
            MarkAllStatusesMenuItem_Click(sender, new RoutedEventArgs());
        }

        private void MarkCallsAppBarMenuItem_Click(object sender, EventArgs e)
        {
            MarkCallsSeenMenuItem_Click(sender, new RoutedEventArgs());
        }

        private void SettingsAppBarMenuItem_Click(object sender, EventArgs e)
        {
            SettingsButton_Click(sender, new RoutedEventArgs());
        }


        private void NewCallButton_Click(object sender, EventArgs e)
        {
            var picker = new PhoneNumberChooserTask();
            EventHandler<PhoneNumberResult> handler = null;
            handler = delegate(object source, PhoneNumberResult result)
            {
                picker.Completed -= handler;
                if (result.TaskResult != TaskResult.OK) return;

                string phone = NormalizePhone(result.PhoneNumber);
                if (phone.Length < 6)
                {
                    Diag.Ok("calls: contact picker returned no usable number");
                    return;
                }

                string jid = phone + "@s.whatsapp.net";
                Guarded.RunGuardedAsync("HomePage/start call", StartCallAndOpenAsync(jid));
            };
            picker.Completed += handler;
            try
            {
                picker.Show();
            }
            catch (Exception ex)
            {
                Diag.Failed("HomePage/call contact picker", ex);
            }
        }

        private async Task StartCallAndOpenAsync(string jid)
        {
            await DataService.Instance.StartCallAsync(jid, false);
            CallSession call = DataService.Instance.Call;
            if (call != null && call.Occupied) NavigateToPage("CallPage", null);
        }

        private void RefreshStatusApplicationBarButton_Click(object sender, EventArgs e)
        {
            DataService.Instance.ScanStatuses();
        }

        private void UpdateChatsMenuItems()
        {
            bool hasChats = DataService.Instance.Contacts.Count > 0;
            SetChatMenuItemEnabled(0, hasChats);
            SetChatMenuItemEnabled(1, hasChats);
            SetChatMenuItemEnabled(2, hasChats);
        }

        private void SetChatMenuItemEnabled(int index, bool enabled)
        {
            if (_chatsApplicationBar == null || _chatsApplicationBar.MenuItems.Count <= index) return;
            ApplicationBarMenuItem item = _chatsApplicationBar.MenuItems[index] as ApplicationBarMenuItem;
            if (item != null) item.IsEnabled = enabled;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPrompt.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            ApplyFilter(SearchBox.Text);
        }

        private void ApplyFilter(string text)
        {
            string needle = text == null ? "" : text.Trim();
            if (needle.Length == 0)
            {
                if (!_searching) return;
                _searching = false;
                ChatListView.ItemsSource = DataService.Instance.Contacts;
                _matches.Clear();
                Diag.Ok("chat search: closed, the whole list is back");
                return;
            }

            _searching = true;
            _matches.Clear();
            for (int i = 0; i < DataService.Instance.Contacts.Count; i++)
            {
                Contact contact = DataService.Instance.Contacts[i];
                if (contact == null || !SearchMatches(contact, needle)) continue;
                _matches.Add(contact);
            }
            ChatListView.ItemsSource = _matches;
            Diag.Ok("chat search: " + _matches.Count + " of " + DataService.Instance.Contacts.Count
                + " row(s) for " + needle.Length + " char(s)");
        }

        private static bool SearchMatches(Contact contact, string needle)
        {
            return Contains(contact.Name, needle)
                || Contains(contact.LastMessage, needle)
                || Contains(contact.Id, needle);
        }

        private static bool Contains(string value, string needle)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void EvaluateChats()
        {
            bool empty = DataService.Instance.Contacts.Count == 0;
            bool waiting = empty && !DataService.Instance.HistoryComplete;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitleText.Text = waiting
                ? Loc.Get("ChatsPage_WaitingTitle", "Getting your chats")
                : Loc.Get("ChatsPage_EmptyTitle.Text", "No chats yet");
            EmptyHintText.Text = waiting
                ? Loc.Get("ChatsPage_WaitingHint", "WhatsApp is sending them now")
                : Loc.Get("ChatsPage_EmptyHint.Text", "Tap new chat to start");
            ShowConnectionBar();
        }

        private void OnChatListCompleted(object sender, int rows)
        {
            Diag.OkNow("chat list: " + rows + " row(s), showing "
                + DataService.Instance.Contacts.Count + ", memory "
                + (GC.GetTotalMemory(false) / (1024 * 1024)) + " MB");
            EvaluateChats();
            RefreshBadges();
        }

        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsPage.Open(RootFrame);
        }

        private void ChatListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;
            Contact contact = e.AddedItems[0] as Contact;
            if (contact == null) return;
            NavigateToPage("ChatPage", contact);
            ChatListView.SelectedItem = null;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateToPage("SettingsPage", null);
        }

        private void RefreshStatusButton_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.ScanStatuses();
        }

        private void RefreshCallsButton_Click(object sender, RoutedEventArgs e)
        {
            CallsView.Refresh();
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            ShowActionMenu(Loc.Get("ChatsPage_MoreTooltip", "More"));
            AddModalOptionHandler(Loc.Get("ChatsPage_PinChat", "Pin a chat"), PinChatMenuItem_Click);
            AddModalOptionHandler(Loc.Get("ChatsPage_UnpinAll", "Unpin all"), UnpinAllMenuItem_Click);
            AddModalOptionHandler(Loc.Get("ChatsPage_ReadAll", "Read all chats"), ReadAllMenuItem_Click);
        }

        private async void PinChatMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ShowPinPickerAsync();
        }

        private void UnpinAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            List<Contact> rows = SnapshotContacts();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsPinned) DataService.Instance.SetPinned(rows[i].Id, false);
            }
        }

        private void ReadAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.ClearAllUnread();
            RefreshBadges();
        }

        private static List<Contact> SnapshotContacts()
        {
            var rows = new List<Contact>();
            for (int i = 0; i < DataService.Instance.Contacts.Count; i++)
            {
                Contact contact = DataService.Instance.Contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                rows.Add(contact);
            }
            return rows;
        }

        private async Task ShowPinPickerAsync()
        {
            List<Contact> rows = SnapshotContacts();
            var completion = new TaskCompletionSource<bool>();
            BeginModal(Loc.Get("ChatsPage_PinTitle", "Pin chats"),
                Loc.Get("ChatsPage_PinDone", "Done"), Loc.Get("ChatsPage_Cancel", "Cancel"),
                delegate { CloseModal(); completion.TrySetResult(true); },
                delegate { CloseModal(); completion.TrySetResult(false); });

            AddModalText(Loc.Get("ChatsPage_PinHint",
                "Pinned chats stay at the top of the list, on this phone."));
            for (int i = 0; i < rows.Count; i++)
            {
                var check = new CheckBox
                {
                    Content = rows[i].Name,
                    Tag = rows[i],
                    IsChecked = rows[i].IsPinned,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 235, 241, 241)),
                    Margin = new Thickness(0, 3, 0, 3)
                };
                ModalContent.Children.Add(check);
            }

            bool save = await completion.Task;
            if (!save) return;
            for (int i = 0; i < rows.Count; i++)
            {
                CheckBox check = FindPinCheckBox(rows[i]);
                DataService.Instance.SetPinned(rows[i].Id, check != null && check.IsChecked == true);
            }
        }

        private CheckBox FindPinCheckBox(Contact contact)
        {
            for (int i = 0; i < ModalContent.Children.Count; i++)
            {
                CheckBox check = ModalContent.Children[i] as CheckBox;
                if (check != null && object.ReferenceEquals(check.Tag, contact)) return check;
            }
            return null;
        }

        private void ChatRow_Holding(object sender, ManipulationStartedEventArgs e)
        {
            FrameworkElement row = sender as FrameworkElement;
            if (row == null) return;
            Contact contact = row.DataContext as Contact;
            if (contact == null || string.IsNullOrEmpty(contact.Id)) return;

            _heldRow = row;
            _heldContact = contact;
            if (_holdTimer == null)
            {
                _holdTimer = new DispatcherTimer();
                _holdTimer.Interval = TimeSpan.FromMilliseconds(LongPressMilliseconds);
                _holdTimer.Tick += OnLongPressTick;
            }
            _holdTimer.Stop();
            _holdTimer.Start();
        }

        private void ChatRow_ManipulationCompleted(object sender, ManipulationCompletedEventArgs e)
        {
            if (_holdTimer != null) _holdTimer.Stop();
        }

        private void OnLongPressTick(object sender, EventArgs e)
        {
            _holdTimer.Stop();
            if (_heldRow == null || _heldContact == null) return;
            ShowChatMenu(_heldRow, _heldContact);
            _heldRow = null;
            _heldContact = null;
        }

        private void ShowChatMenu(FrameworkElement row, Contact contact)
        {
            string id = contact.Id;
            bool pinned = contact.IsPinned;
            bool muted = contact.IsMuted;
            ShowActionMenu(contact.Name);

            AddModalOption(pinned
                ? Loc.Get("ChatsPage_Unpin", "Unpin")
                : Loc.Get("ChatsPage_Pin", "Pin"),
                delegate { DataService.Instance.SetPinned(id, !pinned); });
            AddModalOption(muted
                ? Loc.Get("ChatsPage_Unmute", "Unmute")
                : Loc.Get("ChatsPage_Mute", "Mute"),
                delegate { DataService.Instance.SetMuted(id, !muted); });

            bool tiled = PinnedTile.IsPinned(id);
            AddModalOption(tiled
                ? Loc.Get("ChatsPage_RemoveTile", "Remove from Start")
                : Loc.Get("ChatsPage_AddTile", "Add to Start"),
                async delegate { await TogglePinnedTileAsync(id, contact.Name, contact.LastMessage); });

            if (PeopleHub.CanShow(id))
            {
                AddModalOption(Loc.Get("ChatsPage_ShowInPeople", "Show in People"),
                    delegate { PeopleHub.Show(id, contact.Name, BoundsOf(row)); });
            }

            AddModalOption(Loc.Get("ChatsPage_Delete", "Delete chat"),
                async delegate { await ConfirmDeleteAsync(id); });
        }

        private static Rect BoundsOf(FrameworkElement row)
        {
            if (row == null) return new Rect(0, 0, 0, 0);
            try
            {
                UIElement root = Application.Current == null ? null : Application.Current.RootVisual as UIElement;
                if (root == null) return new Rect(0, 0, row.ActualWidth, row.ActualHeight);
                Point origin = row.TransformToVisual(root).Transform(new Point(0, 0));
                return new Rect(origin.X, origin.Y, row.ActualWidth, row.ActualHeight);
            }
            catch (Exception ex)
            {
                // A row that cannot be placed is not a reason to lose the menu, but the
                // run that cannot place one has to say so: the People hub then opens on
                // a rectangle nobody measured.
                Diag.Failed("HomePage/BoundsOf", ex);
                return new Rect(0, 0, 0, 0);
            }
        }

        private static async Task TogglePinnedTileAsync(string chatId, string name, string body)
        {
            try
            {
                await PinnedTile.ToggleAsync(chatId, name, AvatarOf(chatId), body);
            }
            catch (Exception ex)
            {
                Diag.Failed("HomePage/TogglePinnedTile", ex);
            }
        }

        private static string AvatarOf(string chatId)
        {
            Contact row = DataService.Instance.FindContact(chatId);
            if (row != null && !string.IsNullOrEmpty(row.AvatarData)) return row.AvatarData;
            return AvatarCache.Get(chatId);
        }

        public void OpenPinnedChat()
        {
            string chatId = SessionService.PendingChatId;
            if (string.IsNullOrEmpty(chatId)) return;

            string name = SessionService.PendingChatName;
            SessionService.ClearPendingChat();
            Contact contact = DataService.Instance.ContactFor(chatId, name);
            if (contact == null) return;
            if (!NavigateToPage("ChatPage", contact))
            {
                Diag.Ok("pinned tile: the chat " + chatId + " could not be opened");
            }
        }

        private async Task ConfirmDeleteAsync(string chatId)
        {
            bool confirmed;
            try
            {
                confirmed = await AskToDeleteAsync(chatId);
            }
            catch (Exception ex)
            {
                Diag.Failed("HomePage.ConfirmDeleteAsync", ex);
                return;
            }
            if (!confirmed) return;
            DataService.Instance.DeleteChat(chatId);
            RefreshBadges();
        }

        private Task<bool> AskToDeleteAsync(string chatId)
        {
            var completion = new TaskCompletionSource<bool>();
            BeginModal(Loc.Get("ChatsPage_DeleteTitle", "Delete this chat?"),
                Loc.Get("ChatsPage_DeleteConfirm", "Delete"), Loc.Get("ChatsPage_Cancel", "Cancel"),
                delegate { CloseModal(); completion.TrySetResult(true); },
                delegate { CloseModal(); completion.TrySetResult(false); });
            AddModalText(Loc.Get("ChatsPage_DeleteBody",
                "It disappears from this phone. Nothing is deleted from WhatsApp, and a new message brings it back."));
            return completion.Task;
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 28, 34, 35)),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 249, 249)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0, 184, 176)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                FontSize = 18,
                Text = ""
            };
            var prompt = new TextBlock
            {
                Text = Loc.Get("NewChat_Prompt", "Phone number with country code (e.g. 393401234567)"),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 145, 154, 155)),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5)
            };
            var error = new TextBlock
            {
                Text = Loc.Get("NewChat_Invalid", "Enter a valid number with country code (e.g. 393401234567)."),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 116, 116)),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 7, 0, 0),
                Visibility = Visibility.Collapsed
            };
            var pickButton = new Button
            {
                Content = Loc.Get("NewChat_PickContact", "Choose from contacts"),
                Background = new SolidColorBrush(Color.FromArgb(255, 28, 34, 35)),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 235, 241, 241)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 45, 56, 57)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(12, 9, 12, 9)
            };
            pickButton.Click += async delegate
            {
                string picked = await PickFromContactsAsync();
                if (!string.IsNullOrEmpty(picked)) input.Text = picked;
            };

            var completion = new TaskCompletionSource<int>();
            Contact chosen = null;
            string phone = null;
            BeginModal(Loc.Get("NewChat_Title", "New chat"),
                Loc.Get("NewChat_Open", "Open"), Loc.Get("NewChat_Cancel", "Cancel"),
                delegate
                {
                    phone = NormalizePhone(input.Text);
                    if (phone.Length < 6)
                    {
                        error.Visibility = Visibility.Visible;
                        return;
                    }
                    CloseModal();
                    completion.TrySetResult(1);
                },
                delegate { CloseModal(); completion.TrySetResult(0); });

            ModalContent.Children.Add(prompt);
            ModalContent.Children.Add(input);
            ModalContent.Children.Add(pickButton);
            ModalContent.Children.Add(error);

            if (DataService.Instance.Contacts.Count > 0)
            {
                AddModalText(Loc.Get("NewChat_Synced", "Conversations already on the server"));
                List<Contact> rows = SnapshotContacts();
                for (int i = 0; i < rows.Count; i++)
                {
                    Contact contact = rows[i];
                    Button rowButton = CreateModalOption(contact.Name, delegate
                    {
                        chosen = contact;
                        CloseModal();
                        completion.TrySetResult(2);
                    });
                    ModalContent.Children.Add(rowButton);
                }
            }

            input.Focus();
            int result = await completion.Task;
            if (result == 0) return;
            if (chosen != null)
            {
                NavigateToPage("ChatPage", chosen);
                return;
            }

            string jid = phone + "@s.whatsapp.net";
            Contact existing = DataService.Instance.FindContact(jid);
            if (existing != null)
            {
                NavigateToPage("ChatPage", existing);
                return;
            }

            Contact created = DataService.Instance.AddContact(new Contact
            {
                Id = jid,
                Name = "+" + phone,
                Initials = phone.Substring(0, 2).ToUpper(),
                UnreadCount = 0
            });
            if (created != null) NavigateToPage("ChatPage", created);
        }

        private static async Task<string> PickFromContactsAsync()
        {
            try
            {
                var completion = new TaskCompletionSource<string>();
                var picker = new PhoneNumberChooserTask();
                EventHandler<PhoneNumberResult> handler = null;
                handler = delegate(object sender, PhoneNumberResult result)
                {
                    picker.Completed -= handler;
                    completion.TrySetResult(result.TaskResult == TaskResult.OK
                        ? NormalizePhone(result.PhoneNumber)
                        : "");
                };
                picker.Completed += handler;
                picker.Show();
                return await completion.Task;
            }
            catch (Exception ex)
            {
                Diag.Failed("HomePage.PickFromContactsAsync", ex);
                return "";
            }
        }

        private static string NormalizePhone(string value)
        {
            if (value == null) return "";
            var digits = new StringBuilder();
            foreach (char c in value)
            {
                if (char.IsDigit(c)) digits.Append(c);
            }
            return digits.Length >= 6 ? digits.ToString() : "";
        }

        private void ShowActionMenu(string title)
        {
            BeginModal(title, "", "", null, null);
            ModalOverlay.Background = new SolidColorBrush(Colors.Transparent);
            ModalCard.HorizontalAlignment = HorizontalAlignment.Right;
            ModalCard.VerticalAlignment = VerticalAlignment.Bottom;
            ModalCard.Width = 280;
            ModalCard.MaxHeight = 420;
            ModalCard.BorderThickness = new Thickness(0);
            ModalCard.Margin = new Thickness(8, 8, 8, 76);
            ModalTitle.Visibility = Visibility.Collapsed;
            ModalDismissButton.Visibility = Visibility.Visible;
            ModalPrimaryButton.Visibility = Visibility.Collapsed;
            ModalSecondaryButton.Visibility = Visibility.Collapsed;
        }

        private void ModalDismissButton_Click(object sender, RoutedEventArgs e)
        {
            CloseModal();
        }

        private void AddModalOptionHandler(string text, RoutedEventHandler handler)
        {
            AddModalOption(text, delegate { handler(this, new RoutedEventArgs()); });
        }

        private void AddModalOption(string text, Action action)
        {
            Button button = CreateModalOption(text, delegate
            {
                CloseModal();
                action();
            });
            ModalContent.Children.Add(button);
        }

        private Button CreateModalOption(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Tag = action,
                Background = new SolidColorBrush(Color.FromArgb(255, 28, 34, 35)),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 235, 241, 241)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 45, 56, 57)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(12, 9, 12, 9)
            };
            button.Click += ModalOption_Click;
            AutomationProperties.SetName(button, text);
            return button;
        }

        private void ModalOption_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null) return;
            Action action = button.Tag as Action;
            CloseModal();
            if (action != null) action();
        }

        private void AddModalText(string text)
        {
            ModalContent.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 189, 190)),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 8)
            });
        }

        private void BeginModal(string title, string primary, string secondary,
            Action primaryAction, Action secondaryAction)
        {
            ModalContent.Children.Clear();
            ModalCard.HorizontalAlignment = HorizontalAlignment.Center;
            ModalCard.VerticalAlignment = VerticalAlignment.Center;
            ModalCard.Width = double.NaN;
            ModalCard.MaxHeight = 560;
            ModalCard.BorderThickness = new Thickness(1);
            ModalCard.Margin = new Thickness(22, 18, 22, 18);
            ModalOverlay.Background = new SolidColorBrush(Color.FromArgb(217, 0, 0, 0));
            ModalTitle.Visibility = Visibility.Visible;
            ModalDismissButton.Visibility = Visibility.Collapsed;
            ModalTitle.Text = title;
            ModalPrimaryButton.Content = primary;
            ModalSecondaryButton.Content = secondary;
            ModalPrimaryButton.Visibility = string.IsNullOrEmpty(primary) ? Visibility.Collapsed : Visibility.Visible;
            ModalSecondaryButton.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;
            _modalPrimaryAction = primaryAction;
            _modalSecondaryAction = secondaryAction;
            ModalOverlay.Visibility = Visibility.Visible;
        }

        private void CloseModal()
        {
            ModalOverlay.Visibility = Visibility.Collapsed;
            ModalDismissButton.Visibility = Visibility.Collapsed;
            _modalPrimaryAction = null;
            _modalSecondaryAction = null;
        }

        private void ModalPrimaryButton_Click(object sender, RoutedEventArgs e)
        {
            Action action = _modalPrimaryAction;
            if (action != null) action();
            else CloseModal();
        }

        private void ModalSecondaryButton_Click(object sender, RoutedEventArgs e)
        {
            Action action = _modalSecondaryAction;
            if (action != null) action();
            else CloseModal();
        }
    }
}
