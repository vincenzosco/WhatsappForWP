using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using PickerContact = Windows.ApplicationModel.Contacts.ContactInformation;
using Windows.UI;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The home screen, rebuilt as the released WP8.1 package has it: one surface
    /// with the three sections, a tab strip that stays put, the content swiping
    /// under it and the WP8 app bar at the bottom.
    ///
    /// Why it is one page and not three: the official home is a `Pivot` over
    /// chats, status and calls (`WhatsApp.ContactsPage`), and a WinRT 8.1 app has
    /// no Pivot control (`Windows.winmd` carries Hub, not Pivot). The FlipView
    /// gives the swipe and the three panes keep their own bindings, so the
    /// difference from the official is the transition animation, not the layout.
    /// </summary>
    public sealed partial class HomePage : Page
    {
        private bool _movingTabs;
        private bool _ready;
        private int _section;

        public HomePage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;

            ChatListView.ItemsSource = DataService.Instance.Contacts;

            Header.SetLabels(
                Loc.Get("Nav_Chats", "chats"),
                Loc.Get("Nav_Status", "status"),
                Loc.Get("Nav_Calls", "calls"));

            NewChatLabel.Text = Loc.Get("ChatsPage_NewChatTooltip", "New chat");
            MoreLabel.Text = Loc.Get("ChatsPage_MoreTooltip", "More");
            SettingsLabel.Text = Loc.Get("ChatsPage_SettingsTooltip", "Settings");
            RefreshStatusLabel.Text = Loc.Get("Common_Refresh", "refresh");
            RefreshCallsLabel.Text = Loc.Get("Common_Refresh", "refresh");

            // Icon-only buttons do not take x:Uid (it would overwrite the Path):
            // the tooltip and the spoken name are set here.
            ToolTipService.SetToolTip(NewChatButton, NewChatLabel.Text);
            ToolTipService.SetToolTip(MoreButton, MoreLabel.Text);
            ToolTipService.SetToolTip(SettingsButton, SettingsLabel.Text);
            ToolTipService.SetToolTip(RefreshStatusButton, RefreshStatusLabel.Text);
            ToolTipService.SetToolTip(RefreshCallsButton, RefreshCallsLabel.Text);

            AutomationProperties.SetName(NewChatButton, NewChatLabel.Text);
            AutomationProperties.SetName(MoreButton, MoreLabel.Text);
            AutomationProperties.SetName(SettingsButton, SettingsLabel.Text);
            AutomationProperties.SetName(RefreshStatusButton, RefreshStatusLabel.Text);
            AutomationProperties.SetName(RefreshCallsButton, RefreshCallsLabel.Text);

            Header.Selected += Header_Selected;
            _ready = true;
        }

        /// <summary>The section in front, for the app's suspend state.</summary>
        public AppSection CurrentSection
        {
            get { return SectionOf(_section); }
        }

        // ---------------------------------------------------------------- tabs

        /// <summary>
        /// Moves the whole screen to a section: header, content and app bar in one
        /// step. `notify` is false when the move comes from the FlipView, so the
        /// FlipView is not told to move where it already is.
        /// </summary>
        private void Show(int index, bool notify)
        {
            if (index < 0 || index > 2) index = 0;

            if (!_ready) return;

            bool changed = index != _section;
            _section = index;

            Header.SetSelected(index);
            if (notify && Sections.SelectedIndex != index)
            {
                // Moving the FlipView raises SelectionChanged, which would call back
                // here: the flag makes the handler leave that echo alone.
                _movingTabs = true;
                Sections.SelectedIndex = index;
                _movingTabs = false;
            }

            ChatsBar.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusBar.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            CallsBar.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

            if (index == 1) StatusView.Activate(); else StatusView.Deactivate();
            if (index == 2) CallsView.Activate(); else CallsView.Deactivate();

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

        private void Header_Selected(object sender, int index)
        {
            Show(index, true);
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

        /// <summary>The three counts on the tab strip.</summary>
        private void RefreshBadges()
        {
            Header.SetCounts(
                DataService.Instance.UnreadChats(),
                DataService.Instance.UnseenStatuses(),
                DataService.Instance.MissedCalls());
        }

        // -------------------------------------------------------------- chats

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // The wallpaper may have changed in the settings while this page was
            // cached: it is read again here, which is the one navigation every
            // return from that page passes through.
            ApplyTheme();

            int index = SectionIndex(SessionService.Section);
            Show(index, true);

            // Show() only activates the chats when it moves to them: coming back
            // from a chat it may already be on index 0 while the tab was stopped.
            if (index == 0) ActivateChats();

            EvaluateChats();
            ShowConnectionBar();
            RefreshBadges();

            DataService.Instance.MessageArrived -= OnBadgeChanged;
            DataService.Instance.MessageArrived += OnBadgeChanged;
            DataService.Instance.StatusesChanged -= OnStatusesChanged;
            DataService.Instance.StatusesChanged += OnStatusesChanged;
            DataService.Instance.CallsScanCompleted -= OnCallsScanCompleted;
            DataService.Instance.CallsScanCompleted += OnCallsScanCompleted;

            // A tile on the Start screen asked for a chat before this page existed: the
            // arguments are read by `App`, and this is the first moment there is a page to
            // open it on.
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
        }

        private static int SectionIndex(AppSection section)
        {
            if (section == AppSection.Status) return 1;
            if (section == AppSection.Calls) return 2;
            return 0;
        }

        /// <summary>
        /// Everything the chat tab does while it is in front. It is not called when
        /// the page is not on the chat tab: the pictures are one round trip each and
        /// the list is not on screen.
        /// </summary>
        /// <summary>
        /// How often the list is re-read from what the client already holds: thirty seconds,
        /// which is the ceiling asked for. It is a net, not a clock - the socket is live - so
        /// it costs one walk over rows already in memory and asks WhatsApp nothing.
        /// </summary>
        private const int ListRefreshSeconds = DataService.ListRefreshSeconds;

        /// <summary>The timer of that refresh, alive only while the chat tab is in front.</summary>
        private DispatcherTimer _listRefresh;

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

        /// <summary>
        /// The list, re-read: the names that arrived after their rows, the two addresses that
        /// are one chat, and the order the times ask for. A failure here is one line and not a
        /// dead timer: the next tick tries again.
        /// </summary>
        private void OnListRefreshTick(object sender, object e)
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

            // The images that are still there as bytes but no longer decoded:
            // MemoryWatcher threw them away, and without this the list stays with
            // the initials until the server sends the rows again.
            DataService.Instance.RestoreAvatars();

            // And the ones that were never asked for: a chat row off the native client
            // carries no picture at all, so the list starts every person and every group on
            // its initials until this fetches them (AvatarEnricher, one chat at a time).
            AvatarEnricher.Start();

            // An attachment can arrive while this tab is in front (the app comes
            // back here after the picker) or before it exists (a process started by
            // a share): it is checked in both cases.
            AttachmentInbox.Ready -= OnAttachmentReady;
            AttachmentInbox.Ready += OnAttachmentReady;
            UpdatePendingAttachment();

            DataService.Instance.ChatListCompleted -= OnChatListCompleted;
            DataService.Instance.ChatListCompleted += OnChatListCompleted;

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
        }

        private void OnBadgeChanged(object sender, ChatMessage message)
        {
            RefreshBadges();
        }

        private void OnStatusesChanged(object sender, EventArgs e)
        {
            RefreshBadges();
        }

        private void OnCallsScanCompleted(object sender, EventArgs e)
        {
            RefreshBadges();
        }

        private void OnClientStatusChanged(object sender, string status)
        {
            ShowConnectionBar();
        }

        /// <summary>
        /// The bar while the client is not connected. The list stays on screen either
        /// way - it is a photograph of the last session, kept on the phone - and the bar
        /// is what says the rows under it are not live yet.
        /// </summary>
        private void ShowConnectionBar()
        {
            if (NativeConnection.Instance.IsConnected)
            {
                ConnectionBar.Visibility = Visibility.Collapsed;
                return;
            }

            ConnectionBarText.Text = Loc.Get("ChatsPage_NotConnected",
                "Connecting to WhatsApp...");
            ConnectionBar.Visibility = Visibility.Visible;
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

        private void Contacts_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            EvaluateChats();
            RefreshBadges();
        }

        /// <summary>The empty state and the connection bar, in one place.</summary>
        private void EvaluateChats()
        {
            bool empty = DataService.Instance.Contacts.Count == 0;

            // An empty list is not "no chats" until WhatsApp has said it has sent them all.
            // After a pairing the history lands conversation by conversation - the phone run
            // of 2026-10-22 showed its first 16 rows a minute before the other 153 - and the
            // screen used to answer those sixty seconds with "No chats yet", which is the
            // sentence a person reads as "the pairing did not work". `HistoryComplete` is the
            // one fact that tells the two states apart (`NativeChats.IsSyncComplete`), and it
            // is what `DataService` raises `ChatListCompleted` for.
            bool waiting = empty && !DataService.Instance.HistoryComplete;

            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            // The two sentences the markup binds by `x:Uid` are the two `.Text` keys: the
            // waiting pair is a plain key, because nothing in the XAML names it.
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
                + (Windows.System.MemoryManager.AppMemoryUsage / (1024UL * 1024UL)) + " MB");

            // The history is over: a list that is still empty is empty for real, so the
            // waiting sentence comes off the screen with the sync that earned it.
            EvaluateChats();
            RefreshBadges();
        }

        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsPage.Open(RootFrame);
        }

        private Frame RootFrame
        {
            get { return Window.Current.Content as Frame; }
        }

        private void ChatListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;

            var contact = e.AddedItems[0] as Contact;
            if (contact == null) return;

            Frame frame = RootFrame;
            if (frame != null) frame.Navigate(typeof(ChatPage), contact);
            ChatListView.SelectedItem = null;
        }

        /// <summary>
        /// Puts the chosen background behind the three sections. Plain white is the
        /// absence of an image, not a white picture: the veil over nothing is the
        /// page's own background.
        /// </summary>
        private void ApplyTheme()
        {
            ImageSource image = ThemeService.ImageFor(ThemeService.Current);
            HomeWallpaper.Source = image;
            HomeWallpaper.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// The app bar's settings button. The account moved one level down: this
        /// page is about how the app looks, and the account and the connection have
        /// their own screen behind it.
        /// </summary>
        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame frame = RootFrame;
            if (frame != null) frame.Navigate(typeof(SettingsPage));
        }

        private void RefreshStatusButton_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.ScanStatuses();
        }

        private void RefreshCallsButton_Click(object sender, RoutedEventArgs e)
        {
            CallsView.Refresh();
        }

        /// <summary>
        /// The three-dot menu: what applies to the whole list. The actions of a
        /// single chat are in the long press, not here.
        /// </summary>
        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();

            var pinItem = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_PinChat", "Pin a chat") };
            pinItem.Click += PinChatMenuItem_Click;
            flyout.Items.Add(pinItem);

            var unpinItem = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_UnpinAll", "Unpin all") };
            unpinItem.Click += UnpinAllMenuItem_Click;
            flyout.Items.Add(unpinItem);

            var readItem = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_ReadAll", "Read all chats") };
            readItem.Click += ReadAllMenuItem_Click;
            flyout.Items.Add(readItem);

            flyout.ShowAt(MoreButton);
        }

        private async void PinChatMenuItem_Click(object sender, RoutedEventArgs e)
        {
            await ShowPinPickerAsync();
        }

        private void UnpinAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var rows = SnapshotContacts();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsPinned) DataService.Instance.SetPinned(rows[i].Id, false);
            }
        }

        /// <summary>
        /// The whole list stops being new. It is the same "read" the chat page
        /// sends, decided for every chat at once instead of one at a time.
        /// </summary>
        private void ReadAllMenuItem_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.ClearAllUnread();
            RefreshBadges();
        }

        /// <summary>
        /// The rows as they are now. Needed because SetPinned moves the rows:
        /// a loop reading DataService.Contacts while the collection moves would
        /// skip rows.
        /// </summary>
        private static List<Contact> SnapshotContacts()
        {
            var rows = new List<Contact>();
            for (int i = 0; i < DataService.Instance.Contacts.Count; i++)
            {
                var contact = DataService.Instance.Contacts[i];
                if (contact == null || string.IsNullOrEmpty(contact.Id)) continue;
                rows.Add(contact);
            }
            return rows;
        }

        private async Task ShowPinPickerAsync()
        {
            var rows = SnapshotContacts();

            var list = new ListView
            {
                ItemsSource = DataService.Instance.Contacts,
                SelectionMode = ListViewSelectionMode.Multiple,
                Height = 320
            };
            list.ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<TextBlock Text=\"{Binding Name}\" Foreground=\"Black\" FontSize=\"16\" Margin=\"0,8,0,8\"/>" +
                "</DataTemplate>");

            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].IsPinned) list.SelectedItems.Add(rows[i]);
            }

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = Loc.Get("ChatsPage_PinHint", "Pinned chats stay at the top of the list, on this phone."),
                Foreground = new SolidColorBrush(Colors.Gray),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            content.Children.Add(list);

            var dialog = new ContentDialog
            {
                Title = Loc.Get("ChatsPage_PinTitle", "Pin chats"),
                Content = content,
                PrimaryButtonText = Loc.Get("ChatsPage_PinDone", "Done"),
                SecondaryButtonText = Loc.Get("ChatsPage_Cancel", "Cancel")
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            for (int i = 0; i < rows.Count; i++)
            {
                DataService.Instance.SetPinned(rows[i].Id, list.SelectedItems.Contains(rows[i]));
            }
        }

        private void ChatRow_Holding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started) return;

            var row = sender as FrameworkElement;
            if (row == null) return;

            var contact = row.DataContext as Contact;
            if (contact == null || string.IsNullOrEmpty(contact.Id)) return;

            e.Handled = true;
            ShowChatMenu(row, contact);
        }

        private void ShowChatMenu(FrameworkElement row, Contact contact)
        {
            string id = contact.Id;
            bool pinned = contact.IsPinned;
            bool muted = contact.IsMuted;

            var flyout = new MenuFlyout();

            var pin = new MenuFlyoutItem
            {
                Text = pinned
                    ? Loc.Get("ChatsPage_Unpin", "Unpin")
                    : Loc.Get("ChatsPage_Pin", "Pin")
            };
            pin.Click += delegate { DataService.Instance.SetPinned(id, !pinned); };
            flyout.Items.Add(pin);

            var mute = new MenuFlyoutItem
            {
                Text = muted
                    ? Loc.Get("ChatsPage_Unmute", "Unmute")
                    : Loc.Get("ChatsPage_Mute", "Mute")
            };
            mute.Click += delegate { DataService.Instance.SetMuted(id, !muted); };
            flyout.Items.Add(mute);

            // The chat's own tile on the Start screen: its picture, its name and how many
            // of its messages are waiting, and a tap that opens this conversation. It is
            // decided here, by the long press, and it is about one chat - unlike the pin
            // above, which only orders this list.
            bool tiled = PinnedTile.IsPinned(id);
            var tile = new MenuFlyoutItem
            {
                Text = tiled
                    ? Loc.Get("ChatsPage_RemoveTile", "Remove from Start")
                    : Loc.Get("ChatsPage_AddTile", "Add to Start")
            };
            tile.Click += async (s, a) => { await TogglePinnedTileAsync(id, contact.Name, contact.LastMessage); };
            flyout.Items.Add(tile);

            var remove = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_Delete", "Delete chat") };
            remove.Click += async (s, a) => { await ConfirmDeleteAsync(id); };
            flyout.Items.Add(remove);

            flyout.ShowAt(row);
        }

        /// <summary>
        /// Pins the chat to the Start screen, or takes its tile away.
        ///
        /// No question is asked first: `SecondaryTile.RequestCreateAsync` shows Windows' own
        /// sheet, which is the confirmation and where the tile gets its name. A failure is
        /// written down and nothing else happens - a menu that did nothing is still better
        /// than a dialog over a system sheet that was just dismissed.
        /// </summary>
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

        /// <summary>The picture of a chat, from the row or from the copy kept on the phone.</summary>
        private static string AvatarOf(string chatId)
        {
            var row = DataService.Instance.FindContact(chatId);
            if (row != null && !string.IsNullOrEmpty(row.AvatarData)) return row.AvatarData;

            return AvatarCache.Get(chatId);
        }

        /// <summary>
        /// A tile on the Start screen was tapped: open that chat.
        ///
        /// Called from here (`OnNavigatedTo`, which is the cold start: the page is built
        /// after the launch arguments arrived) and from `App.OnLaunched`, which is the launch
        /// that finds the page already alive. The pending chat is taken once, so coming back
        /// to this page does not reopen it.
        /// </summary>
        public void OpenPinnedChat()
        {
            string chatId = SessionService.PendingChatId;
            if (string.IsNullOrEmpty(chatId)) return;

            string name = SessionService.PendingChatName;
            SessionService.ClearPendingChat();

            var contact = DataService.Instance.ContactFor(chatId, name);
            if (contact == null) return;

            Frame frame = RootFrame;
            if (frame == null) return;

            if (!frame.Navigate(typeof(ChatPage), contact))
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

        private static async Task<bool> AskToDeleteAsync(string chatId)
        {
            var dialog = new ContentDialog
            {
                Title = Loc.Get("ChatsPage_DeleteTitle", "Delete this chat?"),
                Content = new TextBlock
                {
                    Text = Loc.Get("ChatsPage_DeleteBody",
                        "It disappears from this phone. Nothing is deleted from WhatsApp, and a new message brings it back."),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = Loc.Get("ChatsPage_DeleteConfirm", "Delete"),
                SecondaryButtonText = Loc.Get("ChatsPage_Cancel", "Cancel")
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = Loc.Get("NewChat_Prompt",
                    "Phone number with country code (e.g. 393401234567)")
            };

            var error = new TextBlock
            {
                Text = Loc.Get("NewChat_Invalid",
                    "Enter a valid number with country code (e.g. 393401234567)."),
                Foreground = new SolidColorBrush(Colors.Red),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Visibility = Visibility.Collapsed
            };

            var pickButton = new Button
            {
                Content = Loc.Get("NewChat_PickContact", "Choose from contacts"),
                Margin = new Thickness(0, 12, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            var content = new StackPanel();
            content.Children.Add(input);
            content.Children.Add(pickButton);
            content.Children.Add(error);

            var dialog = new ContentDialog
            {
                Title = Loc.Get("NewChat_Title", "New chat"),
                Content = content,
                PrimaryButtonText = Loc.Get("NewChat_Open", "Open"),
                SecondaryButtonText = Loc.Get("NewChat_Cancel", "Cancel")
            };

            Contact chosen = null;

            pickButton.Click += async (s, a) =>
            {
                string picked = await PickFromContactsAsync();
                if (!string.IsNullOrEmpty(picked)) input.Text = picked;
            };

            var known = new ListView
            {
                ItemsSource = DataService.Instance.Contacts,
                MaxHeight = 220,
                SelectionMode = ListViewSelectionMode.Single
            };
            known.ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<TextBlock Text=\"{Binding Name}\" Foreground=\"Black\" FontSize=\"16\" Margin=\"0,8,0,8\"/>" +
                "</DataTemplate>");
            known.SelectionChanged += (s, a) =>
            {
                if (a.AddedItems.Count == 0) return;
                chosen = a.AddedItems[0] as Contact;
                dialog.Hide();
            };

            if (DataService.Instance.Contacts.Count > 0)
            {
                content.Children.Add(new TextBlock
                {
                    Text = Loc.Get("NewChat_Synced", "Conversations already on the server"),
                    Foreground = new SolidColorBrush(Colors.Gray),
                    FontSize = 13,
                    Margin = new Thickness(0, 12, 0, 4)
                });
                content.Children.Add(known);
            }

            string jid = null;
            string phone = null;
            while (jid == null)
            {
                var result = await dialog.ShowAsync();

                if (chosen != null)
                {
                    Frame frame = RootFrame;
                    if (frame != null) frame.Navigate(typeof(ChatPage), chosen);
                    return;
                }

                if (result != ContentDialogResult.Primary) return;

                phone = NormalizePhone(input.Text);
                if (phone.Length < 6)
                {
                    error.Visibility = Visibility.Visible;
                    continue;
                }

                jid = phone + "@s.whatsapp.net";
            }

            var existing = DataService.Instance.FindContact(jid);
            if (existing != null)
            {
                Frame frame = RootFrame;
                if (frame != null) frame.Navigate(typeof(ChatPage), existing);
                return;
            }

            var contact = new Contact
            {
                Id = jid,
                Name = "+" + phone,
                Initials = phone.Substring(0, 2).ToUpper(),
                UnreadCount = 0
            };
            DataService.Instance.AddContact(contact);

            Frame root = RootFrame;
            if (root != null) root.Navigate(typeof(ChatPage), contact);
        }

        /// <summary>
        /// Opens the system contact picker and returns the first number found,
        /// cleaned up. Empty if the user cancels or the contact has no numbers.
        /// </summary>
        // CS0618: the compiler suggests Contact/PickContactAsync, which are the
        // Windows 10 API. On WP8.1 the only available one is ContactInformation.
#pragma warning disable 618
        private static async Task<string> PickFromContactsAsync()
        {
            try
            {
                var picker = new Windows.ApplicationModel.Contacts.ContactPicker();
                PickerContact contact = await picker.PickSingleContactAsync();
                if (contact == null || contact.PhoneNumbers == null || contact.PhoneNumbers.Count == 0) return "";

                foreach (var phone in contact.PhoneNumbers)
                {
                    string number = NormalizePhone(phone.Value);
                    if (!string.IsNullOrEmpty(number)) return number;
                }
                return "";
            }
            catch (Exception ex)
            {
                Diag.Failed("HomePage.PickFromContactsAsync", ex);
                return "";
            }
        }
#pragma warning restore 618

        /// <summary>Digits only: the number must stay the one the app expects.</summary>
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
    }
}
