using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using PickerContact = Windows.ApplicationModel.Contacts.ContactInformation;
using Windows.UI;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Markup;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Controls;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>Chats section: conversation list and new chat.</summary>
    public sealed partial class ChatsPage : Page
    {
        public ChatsPage()
        {
            this.InitializeComponent();
            this.NavigationCacheMode = NavigationCacheMode.Enabled;

            ChatListView.ItemsSource = DataService.Instance.Contacts;

            // Icon-only buttons do not use x:Uid (it would override the Path): the
            // text is a tooltip set here.
            ToolTipService.SetToolTip(NewChatButton, Loc.Get("ChatsPage_NewChatTooltip", "New chat"));
            ToolTipService.SetToolTip(SettingsButton, Loc.Get("ChatsPage_SettingsTooltip", "Settings"));

            // The tooltip is seen by whoever holds the button; the name is read by
            // the screen reader, and the voice says it too. Same text, so same key:
            // if an icon opens the wrong action, you hear it instead of having to
            // guess it.
            AutomationProperties.SetName(NewChatButton,
                Loc.Get("ChatsPage_NewChatTooltip", "New chat"));
            AutomationProperties.SetName(SettingsButton,
                Loc.Get("ChatsPage_SettingsTooltip", "Settings"));

            ToolTipService.SetToolTip(MoreButton, Loc.Get("ChatsPage_MoreTooltip", "More"));
            AutomationProperties.SetName(MoreButton, Loc.Get("ChatsPage_MoreTooltip", "More"));
        }

        /// <summary>
        /// The three-dot menu: what applies to the whole list. The actions of a
        /// single chat are not here, because there is no row here - they are in the
        /// long press (ChatRow_Holding).
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
        /// The rows as they are now, in a list of our own. It is needed because
        /// SetPinned moves the rows (pinned ones go back to the top): a loop that
        /// reads DataService.Contacts while that same collection moves would skip
        /// rows.
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

        /// <summary>
        /// One or more chats to pin, with the pinned ones already selected: the menu
        /// says what to change, it does not make you start over.
        /// </summary>
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

        /// <summary>
        /// Holding a row opens the actions of that chat.
        ///
        /// HoldingState is checked: a long press raises two events, and without this
        /// check the menu would also open when the finger lifts.
        /// </summary>
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

        /// <summary>
        /// Pin, mute and delete of a row. The id and the state are captured now and
        /// not re-read in the handler: when the item is tapped, the row may already
        /// have been removed (delete) and its DataContext is no longer the one the
        /// menu shows.
        /// </summary>
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

            var remove = new MenuFlyoutItem { Text = Loc.Get("ChatsPage_Delete", "Delete chat") };
            // The handler awaits the question instead of launching it and moving on:
            // an unobserved Task is an exception nobody sees (CS4014), and this is
            // the point where the user decides.
            remove.Click += async (s, a) => { await ConfirmDeleteAsync(id); };
            flyout.Items.Add(remove);

            flyout.ShowAt(row);
        }

        /// <summary>
        /// Deleting is the only menu action that cannot be undone with another tap,
        /// and a finger resting for a long time is also a finger that was sliding:
        /// the question is worth a dialog.
        /// </summary>
        private async Task ConfirmDeleteAsync(string chatId)
        {
            // A dialog that does not open (another one already open, a page that is
            // going away) must not bring down the app: it is logged and that is all,
            // and the chat stays.
            bool confirmed;
            try
            {
                confirmed = await AskToDeleteAsync(chatId);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatsPage.ConfirmDeleteAsync", ex);
                return;
            }

            if (!confirmed) return;
            DataService.Instance.DeleteChat(chatId);
        }

        /// <summary>The question: true if the user confirmed.</summary>
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

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            Nav.Current = AppSection.Chats;

            // Keep the empty state in sync with the contact list
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            DataService.Instance.Contacts.CollectionChanged += Contacts_CollectionChanged;
            UpdateEmptyState();

            // The images that are still there as bytes but no longer decoded:
            // MemoryWatcher threw them away, and without this the list stays with
            // the initials until the server sends the rows again.
            DataService.Instance.RestoreAvatars();

            // An attachment can arrive while this page is in front (the app comes
            // back here after the picker) or before it exists (a process started by
            // a share): it is checked in both cases.
            AttachmentInbox.Ready += OnAttachmentReady;
            UpdatePendingAttachment();

            // The request is made again on every entry and on every switch to
            // connected, instead of waiting for someone to open the settings: at
            // startup the connection is not there yet, and the list arrived only if
            // the user came back here after opening them.
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;

            // The server that does not answer and the connection that comes back:
            // the bar is governed by these two events.
            CommunicationService.Instance.ServerUnavailable += OnServerUnavailable;
            CommunicationService.Instance.ConnectionEstablished += OnServerAvailable;

            // The list finished arriving: the count of rows the server answered
            // is the difference between an empty list and a refused one.
            DataService.Instance.ChatListCompleted -= OnChatListCompleted;
            DataService.Instance.ChatListCompleted += OnChatListCompleted;

            // The adapter says nothing on its own until something asks: if the
            // socket came up before this page existed, the state frame has already
            // gone by and the list would wait for another one forever. Asking here
            // makes the answer arrive now, and OnControlMessageReceived turns it
            // into the list request.
            if (CommunicationService.Instance.IsConnected)
            {
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ChatsPage/status",
                    CommunicationService.Instance.SendControlAsync("status"));
#pragma warning restore 4014
            }

            RequestChats();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.Contacts.CollectionChanged -= Contacts_CollectionChanged;
            AttachmentInbox.Ready -= OnAttachmentReady;
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
            CommunicationService.Instance.ServerUnavailable -= OnServerUnavailable;
            CommunicationService.Instance.ConnectionEstablished -= OnServerAvailable;
            DataService.Instance.ChatListCompleted -= OnChatListCompleted;
        }

        /// <summary>
        /// The main server did not answer after the automatic attempts: the bar says
        /// so, and it stays until a connection succeeds.
        /// </summary>
        private void OnServerUnavailable(object sender, EventArgs e)
        {
            ServerUnavailableText.Text = Loc.Get("CommService_ServerUnavailable",
                "Server non disponibile, riprova tra qualche minuto :)");
            ServerUnavailableBar.Visibility = Visibility.Visible;
        }

        private void OnServerAvailable(object sender, EventArgs e)
        {
            ServerUnavailableBar.Visibility = Visibility.Collapsed;

            // The socket is up. Whether WhatsApp is linked is a separate answer,
            // and the list is asked for only once it is connected: this is the
            // request that produces it.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatsPage/status",
                CommunicationService.Instance.SendControlAsync("status"));
#pragma warning restore 4014
        }

        /// <summary>
        /// WhatsApp has just switched to connected. The request made on entry could
        /// not have an answer (the adapter answers "not connected" until the login
        /// is done), and this is the only thing that makes the list show up without
        /// touching anything.
        /// </summary>
        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null || message.Command != "state") return;
            RequestChats();
        }

        /// <summary>
        /// Requests the conversation list. Only if there is someone who can answer:
        /// with WhatsApp not connected the adapter answers with an error and no row.
        /// </summary>
        private void RequestChats()
        {
            if (!CommunicationService.Instance.IsConnected) return;
            if (CommunicationService.Instance.WhatsAppState != "connected") return;

            // The rows that come back are the server order, and this is where that
            // batch starts: everything ApplyChat records from here on belongs to it.
            DataService.Instance.BeginChatList();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatsPage/chats",
                CommunicationService.Instance.SendControlAsync("chats"));
#pragma warning restore 4014
        }

        private void OnAttachmentReady()
        {
            UpdatePendingAttachment();
        }

        /// <summary>The top row says there is an image to send.</summary>
        private void UpdatePendingAttachment()
        {
            PendingAttachmentBar.Visibility = AttachmentInbox.HasAttachment
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void Contacts_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyState();
        }

        private void UpdateEmptyState()
        {
            bool empty = DataService.Instance.Contacts.Count == 0;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ConnectionPage));
        }

        /// <summary>
        /// The server answered the list, with this many rows in it. Written to
        /// the diagnostics: a row count of zero with a healthy adapter on the
        /// list of what the phone can see is a finding, not a failure to show.
        /// </summary>
        private void OnChatListCompleted(object sender, int rows)
        {
            Diag.Ok("chat list: " + rows + " row(s), showing "
                + DataService.Instance.Contacts.Count);
        }

        /// <summary>
        /// The empty state has its own way to the diagnostics: an empty list is
        /// the one moment the answer is wanted, and walking back to the settings
        /// to find it is a step too many.
        /// </summary>
        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsPage.Open(Frame);
        }

        private void ChatListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0) return;

            var contact = e.AddedItems[0] as Contact;
            if (contact == null) return;

            Frame.Navigate(typeof(ChatPage), contact);
            ChatListView.SelectedItem = null; // Reset selection
        }

        private async void NewChatButton_Click(object sender, RoutedEventArgs e)
        {
            var input = new TextBox
            {
                PlaceholderText = Loc.Get("NewChat_Prompt",
                    "Phone number with country code (e.g. 393401234567)")
            };

            // The validation error lives inside the dialog: this way the user finds
            // the number they typed again instead of starting over.
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

            // The system contact picker fills the field: it is the user consent, so
            // the app does not read the address book on its own.
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

            // WP8.1 ContentDialog has no CloseButtonText: the cancel text is the
            // secondary button, and the dialog can also be dismissed with the
            // hardware back button (result = None).
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

            // The conversations the server already knows: choosing one avoids typing
            // a number the user probably has in sight.
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

                // A chat chosen from the list closes the dialog on its own: the
                // result is None, so the choice is checked first.
                if (chosen != null)
                {
                    Frame.Navigate(typeof(ChatPage), chosen);
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
                Frame.Navigate(typeof(ChatPage), existing);
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
            Frame.Navigate(typeof(ChatPage), contact);
        }

        /// <summary>
        /// Opens the system contact picker and returns the first number found,
        /// cleaned up. Empty if the user cancels or the contact has no numbers: it
        /// is not an error, it is a choice.
        /// </summary>
        // CS0618: the compiler suggests Contact/PickContactAsync, which are the
        // Windows 10 API. On WP8.1 the only available one is ContactInformation:
        // the warning is correct and there is nothing to do, so it is not printed on
        // every build (otherwise a new warning would no longer be noticed).
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
                // Some devices reject the picker: it is not a crash.
                Diag.Failed("ChatsPage.PickFromContactsAsync", ex);
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
