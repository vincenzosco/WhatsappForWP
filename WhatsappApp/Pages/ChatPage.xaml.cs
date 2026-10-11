using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    public sealed partial class ChatPage : Page
    {
        private Contact _contact;
        private ObservableCollection<ChatMessage> _messages;
        // The handler put on ConnectionEstablished while this chat is open: a
        // chat opened before the socket is up asks for nothing, and this is what
        // gives it its history when the connection arrives.
        // The chosen attachment: the name of the file copied into the app folder,
        // plus what is needed to send it. Not the bytes: a whole video in memory
        // is the heaviest thing this page could hold, and it is what used to close
        // the app when sharing a video (see AttachmentInbox).
        private string _selectedLocalFileName;
        private string _selectedMediaFileName;
        private string _selectedMediaMimeType;
        // The conversation's view state: the bind, the scroll and the viewer.
        // It is a module because all three touch the same three things, and the
        // E_UNEXPECTED that left a chat empty lived exactly there.
        private readonly ConversationView _view;

        // What WhatsApp last said about the person in this chat (the three dots).
        private bool _contactTyping;
        private Storyboard _typingStoryboard;

        // Our own typing, toward the contact: whether "composing" has been said
        // and not yet taken back, and when the last key was pressed.
        private bool _typingSent;
        private DispatcherTimer _typingTimer;
        private DateTime _lastKeystroke;
        private const int TypingRefreshSeconds = 4;

        // A message left below because the conversation was being read somewhere
        // else: it is told to the server when the reader gets to it, not before.
        private bool _markReadPending;

        // The recording of a voice note: the state machine that owns idle,
        // starting and recording, plus the half-second timer that draws how long
        // it has been going. The timer is the page's because it only draws; the
        // transitions are the module's.
        private readonly RecordingSession _recording;
        private DispatcherTimer _recordTimer;

        public ChatPage()
        {
            this.InitializeComponent();

            // Icon-only buttons: the label lives in the tooltip.
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
            ToolTipService.SetToolTip(AttachButton, Loc.Get("ChatPage_AttachTooltip", "Attach an image"));
            ToolTipService.SetToolTip(SendButton, Loc.Get("ChatPage_SendTooltip", "Send"));
            ToolTipService.SetToolTip(ClearImageButton, Loc.Get("ChatPage_ClearImageTooltip", "Remove the image"));
            ToolTipService.SetToolTip(RecordButton, Loc.Get("ChatPage_RecordTooltip", "Record a voice note"));
            ToolTipService.SetToolTip(StopRecordButton, Loc.Get("ChatPage_StopRecordTooltip", "Stop the recording"));
            ToolTipService.SetToolTip(RecentMessagesButton, Loc.Get("ChatPage_JumpToRecentTooltip", "Jump to the newest messages"));

            // The picture and the name are two targets: the tooltip tells them apart.
            ToolTipService.SetToolTip(HeaderAvatar, Loc.Get("ChatPage_ProfilePhotoTooltip", "Show the profile photo"));
            ToolTipService.SetToolTip(ContactHeader, Loc.Get("ChatPage_ContactInfoTooltip", "Contact info"));

            // The list is handed to the view module now, before it has a source:
            // binding happens later, once the saved copy is in.
            _view = new ConversationView(MessagesListView, Dispatcher);
            _recording = new RecordingSession();
            Diag.Ok("probe build: scroll waits for the layout, position watch permanent");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var contact = e.Parameter as Contact;
            if (contact != null)
            {
                _contact = contact;
                _contactTyping = false;
                _typingSent = false;
                _markReadPending = false;

                // The avatar and the initials come from the contact: the page does
                // not rebuild them.
                DataContext = contact;

                ContactNameText.Text = contact.Name;

                // No presence: WhatsApp does not expose it through the server we
                // use, and saying "online" or "last seen at HH:mm" was a lie. What
                // is left is the only true extra thing we have: the chat number,
                // when the name is not already the number.
                string number = DisplayNumber(contact.Id);
                bool hasNumber = !string.IsNullOrEmpty(number) && number != contact.Name;
                OnlineStatusText.Text = hasNumber ? number : "";
                OnlineStatusText.Visibility = hasNumber ? Visibility.Visible : Visibility.Collapsed;

                // Load messages: first the ones on the phone, so the conversation
                // shows right away, then the real history. The list is bound after
                // that copy is in, and not here: inserting into a collection the
                // ListView is already watching, in the middle of a navigation,
                // answered E_UNEXPECTED and the conversation stayed empty.
                _messages = DataService.Instance.GetMessages(contact.Id);
                MarkRead();
                // The chat is the active one before the list is bound: the bind
                // waits for the history burst and asks this field whether the page
                // is still the one in front, so the field must answer for this chat
                // before that wait starts.
                DataService.Instance.ActiveChatId = contact.Id;

                // The position of the conversation, watched from here on: the arrow
                // over it is shown while the newest message is off screen, and the
                // message left below is told as read when the reader reaches it. The
                // callback is called once right here, before the list is bound, so the
                // arrow starts in the right state instead of waiting for a scroll.
                _view.WatchPosition(OnViewPositionChanged);

                // The next phone log then reads this line and, if the process still
                // dies after a chat opens, the site it reached is the last thing
                // before App/unhandled: the crash report has to name itself.
                Diag.OkNow("opened chat " + contact.Id + ", memory "
                    + (Windows.System.MemoryManager.AppMemoryUsage / (1024UL * 1024UL)) + " MB");

                // Whether a burst is still on its way decides how the list binds: at
                // once when there is none to wait for, and only when it closes when
                // there is. WhatsApp pushes the history once per login, so a
                // conversation opened before it lands waits for it here, and one
                // opened after binds from the copy already on the phone.
                bool historyRequested = !DataService.Instance.HistoryComplete
                    && DataService.Instance.MarkHistoryRequested(contact.Id);
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ChatPage/cached messages",
                    BindAfterCacheAsync(contact.Id, historyRequested));
#pragma warning restore 4014

                // There is nothing to ask for: the history is pushed once per login
                // and `DataService` puts it in the collection this page binds. This
                // line is the one that tells "the burst is still coming" apart from
                // "this account has nothing older", which look the same on screen.
                Diag.Ok(historyRequested
                    ? "waiting for the history burst of " + contact.Id
                    : "no burst expected for " + contact.Id + " (history complete)");

                // A message that arrives now, for this chat: the row is already in the
                // collection, and this is what scrolls to it and marks it read.
                DataService.Instance.MessageArrived += OnMessageArrived;

                // And for what WhatsApp says about the person of this chat, which
                // is not a message: the three dots of "writing".
                DataService.Instance.TypingChanged += OnTypingChanged;

                // A voice note whose play button was tapped: its bytes arrive later,
                // and the note plays then (see OnAudioArrived).
                DataService.Instance.AudioArrived += OnAudioArrived;

                // The burst of old messages that closes for this chat. The list is
                // bound before the burst of a chat that asked for it late, and the
                // burst is what puts the newest messages in.
                DataService.Instance.HistoryCompleted += OnHistoryCompleted;

                // An image shared from outside may have arrived while this page
                // did not exist (process restarted): it is picked up here, and from
                // here on also on arrival.
                AttachmentInbox.Ready += OnAttachmentReady;
                ShowPendingAttachment();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            DataService.Instance.MessageArrived -= OnMessageArrived;
            DataService.Instance.TypingChanged -= OnTypingChanged;
            DataService.Instance.AudioArrived -= OnAudioArrived;
            DataService.Instance.HistoryCompleted -= OnHistoryCompleted;
            _playWhenReady = null;
            AttachmentInbox.Ready -= OnAttachmentReady;

            // Leaving while writing: the contact must not keep seeing the dots,
            // and the timer that refreshes them has no reason to run any more.
            StopTyping();
            HideTyping();

            // The snapshot of the conversation: leaving is what writes it, not
            // every message, otherwise it would write a file in bursts.
            if (_contact != null && _messages != null)
            {
#pragma warning disable 4014
                MessageCache.SaveAsync(_contact.Id, _messages);
#pragma warning restore 4014
            }

            DataService.Instance.ActiveChatId = null;
            // The viewer belongs to the tree this page is leaving: a cached one
            // would be scrolled through after the next navigation.
            _view.Reset();
            HideFullScreen();
            StopVideo();
            StopVoice();
            // A recording left running would keep the microphone for a page that
            // is gone, and the file it writes would be sent from another chat.
            CancelRecording();
        }

        /// <summary>
        /// Readable number of a JID (e.g. +393401234567 for people). Empty for
        /// groups and for everything that is not a number.
        /// </summary>
        private static string DisplayNumber(string jid)
        {
            if (string.IsNullOrEmpty(jid) || jid.EndsWith("@g.us")) return "";
            string user = jid.Split('@')[0];
            if (user.Length < 8) return "";
            for (int i = 0; i < user.Length; i++)
            {
                if (!char.IsDigit(user[i])) return "";
            }
            return "+" + user;
        }

        /// <summary>
        /// Brings the newest message into view. The queue, the viewer and the
        /// fallback all live in ConversationView: here the page only says which
        /// message, or that the bottom is what it wants.
        /// </summary>
        private void ScrollToMessage(ChatMessage message)
        {
            _view.ScrollTo(message);
        }

        /// <summary>Whether the conversation is showing its newest row.</summary>
        private bool AtBottom()
        {
            return _view.AtBottom();
        }

        private void OnMessageArrived(object sender, ChatMessage message)
        {
            // DataService has already inserted the message into the same collection:
            // here we only scroll, otherwise the bubble would show up twice.
            if (message.ChatId != _contact.Id) return;

            // History is not news. It is the burst this page is waiting for, and
            // it enters the collection without the page touching the view: the
            // bind comes after the burst, on purpose. Acting on it anyway did a
            // walk of the list's visual tree, a queued scroll and a `read` frame
            // for every row of a list that was not bound yet - the phone's run of
            // 2026-10-08 died inside that burst with no line after the first row.
            if (message.IsHistory) return;

            // A message is the end of "someone is writing": WhatsApp does not always
            // send the `paused` when the message follows at once, and the dots would
            // stay on screen for a conversation that is no longer being written.
            HideTyping();

            // The conversation is brought to the newest bubble only when the reader
            // is already at the bottom. Following every message dragged the screen
            // away from whoever was reading something older, and it also marked as
            // read a message that had never been shown: below here the message is
            // left where it is, and the server is told it has been read when the
            // reader really reaches it.
            if (AtBottom())
            {
                ScrollToMessage(message);
                MarkRead();
                return;
            }

            // This handler lives only while this page is the one in front (it is
            // attached in OnNavigatedTo and detached in OnNavigatedFrom), so a
            // message that arrives here is a message the user is one scroll away
            // from. The number on the row stays until then.
            _markReadPending = true;
        }

        // ─── Someone is writing ───────────────────────────────────────────────

        /// <summary>
        /// WhatsApp says that the person of a chat is writing, or has stopped. It
        /// arrives as a `typing` node off the native client; this page is the only
        /// one that can show it, and only for its own chat.
        /// </summary>
        private void OnTypingChanged(object sender, ChatMessage message)
        {
            if (message == null || _contact == null) return;
            if (message.ChatId != _contact.Id) return;

            bool typing = message.State == "composing";
            // The same state arrives again and again while the words keep coming:
            // restarting the animation on each one would make the dots stutter.
            if (typing == _contactTyping) return;

            if (typing) ShowTyping();
            else HideTyping();
        }

        private void ShowTyping()
        {
            _contactTyping = true;
            TypingBar.Visibility = Visibility.Visible;

            if (_typingStoryboard == null) _typingStoryboard = Resources["TypingDots"] as Storyboard;
            if (_typingStoryboard != null) _typingStoryboard.Begin();
        }

        /// <summary>
        /// Takes the dots away, animation included: a storyboard that repeats
        /// forever and is only hidden keeps working in the background.
        /// </summary>
        private void HideTyping()
        {
            _contactTyping = false;
            TypingBar.Visibility = Visibility.Collapsed;
            if (_typingStoryboard != null) _typingStoryboard.Stop();
        }

        // ─── Reading the conversation ─────────────────────────────────────────

        /// <summary>
        /// The newest message is off screen, or it is back on it. One subscription
        /// and two jobs: WhatsApp's arrow is shown only while there is something
        /// below to come back to, and a message left below is told to the server
        /// when the reader gets to it (the work WatchBottom did on its own).
        /// </summary>
        private void OnViewPositionChanged(bool atBottom)
        {
            RecentMessagesButton.Visibility = atBottom ? Visibility.Collapsed : Visibility.Visible;
            if (atBottom) MarkReadWhenAtBottom();
        }

        /// <summary>The arrow was tapped: back to the newest message.</summary>
        private void RecentMessagesButton_Click(object sender, RoutedEventArgs e)
        {
            Diag.Ok("jump to the newest message");
            _view.ScrollToBottom();
        }

        /// <summary>
        /// The burst of old messages has closed for this chat. Its rows went in
        /// below whatever the list was showing, and nothing else moves the view: a
        /// chat that was bound before its burst was requested - opened while the
        /// socket was down, or opened a second time in the same session - stayed at
        /// its oldest row, which is the chat that opens on "those of an earlier
        /// time". OnHistoryCompleted is the answer to that, and to the comment in
        /// ConversationView.QueueScroll that promised it.
        /// </summary>
        private void OnHistoryCompleted(object sender, string chatId)
        {
            if (_contact == null || chatId != _contact.Id) return;
            if (DataService.Instance.ActiveChatId != _contact.Id) return;

            Diag.Ok("burst closed, moved to the newest for " + chatId);
            _view.ScrollToBottom();
        }

        /// <summary>The reader has reached the bottom: the message left below is read.</summary>
        private void MarkReadWhenAtBottom()
        {
            if (!_markReadPending) return;

            _markReadPending = false;
            MarkRead();
        }

        // ─── Our own typing ──────────────────────────────────────────────────

        /// <summary>
        /// Our own typing state, sent while there is something in the box.
        ///
        /// WhatsApp refreshes "composing" while the words keep coming and takes it
        /// back when the writer stops; one repeating timer does both. Emptying the
        /// box counts as stopping, and so does sending: that path sets Text to ""
        /// in code, which lands here like any other change.
        /// </summary>
        private void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string text = (MessageTextBox.Text ?? "").Trim();
            if (text.Length == 0) { StopTyping(); return; }

            _lastKeystroke = DateTime.Now;
            if (_typingTimer == null)
            {
                _typingTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(TypingRefreshSeconds)
                };
                _typingTimer.Tick += OnTypingTimerTick;
            }
            _typingTimer.Start();

            if (_typingSent || !NativeConnection.Instance.IsConnected) return;
            _typingSent = true;
            SendTyping("composing");
        }

        /// <summary>
        /// A whole interval without a keystroke: the writer stopped with the words
        /// still in the box, and the contact must stop seeing the dots. While the
        /// words keep coming the state is refreshed instead.
        /// </summary>
        private void OnTypingTimerTick(object sender, object e)
        {
            if ((DateTime.Now - _lastKeystroke).TotalSeconds >= TypingRefreshSeconds)
            {
                StopTyping();
                return;
            }
            SendTyping("composing");
        }

        /// <summary>Takes our own typing indicator back from the contact.</summary>
        private void StopTyping()
        {
            if (_typingTimer != null) _typingTimer.Stop();
            if (!_typingSent) return;

            _typingSent = false;
            SendTyping("paused");
        }

        /// <summary>
        /// One frame, with the chat in `Text` and the state in `State`. Without a
        /// connection there is nowhere to say it; the next keystroke will.
        /// </summary>
        private void SendTyping(string state)
        {
            if (_contact == null || !NativeConnection.Instance.IsConnected) return;
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatPage/typing",
                NativeConnection.Instance.SendTypingAsync(_contact.Id, state != "paused"));
#pragma warning restore 4014
        }

        /// <summary>
        /// A media of this conversation that does not have the bytes yet: it is a
        /// history row (or a video this phone never fetched), and it can be
        /// requested again from WhatsApp. The type says whether it was an image, a
        /// video, an audio or a document; they can all be requested.
        /// </summary>
        private static bool Downloadable(ChatMessage message)
        {
            if (message == null) return false;

            // Only what the server knows by id can be requested: a message written
            // here carries a local id, and asking the server for it would be a
            // request without an answer.
            // A history row is the exception even when it is outgoing: its id is
            // the server's, so a voice note the person sent from another device
            // (their own bubble, no bytes on this phone) can be fetched on the tap
            // instead of doing nothing.
            if (!message.IsIncoming && !message.IsHistory) return false;

            if (IsFileBacked(message.MediaType))
                return string.IsNullOrEmpty(message.MediaFilePath);

            if (string.Equals(message.MediaType, "image", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(message.MediaData);

            return false;
        }

        /// <summary>The types that arrive as a file: a video, an audio, a document.</summary>
        private static bool IsFileBacked(string mediaType)
        {
            return string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "audio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "document", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Fills the conversation with the copy on the phone, then binds the list
        /// and scrolls to the newest bubble. It must be awaited on the UI thread:
        /// the collection is the one bound to the list.
        ///
        /// When a history burst is on its way, the list waits for it: binding now
        /// would re-lay out the list on every one of its frames, and the burst is
        /// one node per message. The wait is bounded, so a sync that never closes
        /// its burst does not leave the chat empty.
        /// </summary>
        private async System.Threading.Tasks.Task BindAfterCacheAsync(string chatId, bool historyRequested)
        {
            await DataService.Instance.LoadCachedMessagesAsync(chatId);

            if (historyRequested) await WaitForHistoryAsync(chatId);

            // The reader may have left while the burst was coming, and the list
            // must be bound even then: leaving it unbound is an empty conversation
            // the next time this instance is shown. Only the scroll is skipped when
            // the page is no longer the one in front, because a list that is not in
            // the tree has no position to move to.
            bool isActiveChat = DataService.Instance.ActiveChatId == chatId;
            if (!isActiveChat) Diag.Ok("conversation bind skipped for " + chatId);

            _view.Bind(_messages);

            // What the bind cost, next to how many rows it took. The row count on
            // its own cannot tell a burst that never arrived from a list that was
            // laid out to death: this line is the one that names memory when the
            // count is high and the budget of the phone is close.
            // On disk at once: this is the line that has to survive the crash it
            // measures, and the memory it names is what tells a fail-fast of the
            // runtime from the phone terminating the app on its budget.
            Diag.OkNow("bound " + _messages.Count + " rows, memory "
                + (Windows.System.MemoryManager.AppMemoryUsage / (1024UL * 1024UL)) + " MB");

            if (isActiveChat && _messages.Count > 0)
                _view.ScrollTo(_messages[_messages.Count - 1]);

            // The burst closed and the conversation is still empty: this account has
            // nothing older in that chat.
            if (historyRequested && _messages.Count == 0)
            {
                Diag.Ok("history arrived empty for " + chatId);
            }
        }

        /// <summary>
        /// How long the list waits for the end of a history burst before binding
        /// anyway. The burst is a run of frames pushed after a login, and the sync
        /// that closes it was measured at about 15 s from the connection on
        /// 2026-10-07, so a shorter wait binds the list first and lets the whole burst
        /// insert into a list that is already watching the collection - which is the
        /// failure ConversationView.Bind exists to prevent. Twenty seconds covers that
        /// with room to spare, and the wait is still bounded, so a burst that never
        /// closes does not leave the chat empty.
        /// </summary>
        private const int HistoryWaitMaxMilliseconds = 20000;

        /// <summary>
        /// Waits for this chat's burst to close, at most
        /// HistoryWaitMaxMilliseconds. It returns on the frame or on the timeout,
        /// whichever comes first, and it always takes its handler off again.
        /// </summary>
        private async System.Threading.Tasks.Task WaitForHistoryAsync(string chatId)
        {
            var completion = new System.Threading.Tasks.TaskCompletionSource<bool>();
            EventHandler<string> onDone = (sender, id) =>
            {
                if (id == chatId) completion.TrySetResult(true);
            };

            DataService.Instance.HistoryCompleted += onDone;
            try
            {
                await System.Threading.Tasks.Task.WhenAny(
                    completion.Task,
                    System.Threading.Tasks.Task.Delay(HistoryWaitMaxMilliseconds));
            }
            finally
            {
                DataService.Instance.HistoryCompleted -= onDone;
            }
        }

        private void RequestMedia(ChatMessage message)
        {
            if (message == null) return;

            // The spinner starts now: the file is fetched from WhatsApp, decrypted and
            // written before the bubble can draw it, and without this the tap seems to
            // have done nothing.
            message.IsMediaLoading = true;
            // A tap on the play button of a note with no bytes yet: the bytes come
            // later, and this is what makes the note play then instead of staying
            // silent until a second tap.
            _playWhenReady = message;
            DataService.Instance.RequestMedia(message);
        }

        /// <summary>
        /// The bubble decodes at 320 px: enlarging it full screen leaves it blurry.
        /// Here it is decoded at the screen size, and it is released on close: these
        /// are the heaviest pixels this page holds, and they must not outlive the
        /// view.
        /// </summary>
        private const int ViewerDecodePixels = 720;

        private async void Media_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ChatMessage;
            if (message == null) return;
            e.Handled = true;

            // A video has the bytes on disk and opens in the player; an image has
            // the bytes in memory and opens full screen. Without bytes: it is
            // requested from the server, and it will open on the next tap.
            if (message.IsVideo)
            {
                if (!string.IsNullOrEmpty(message.MediaFilePath))
                {
                    PlayVideo(message);
                    return;
                }

                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            // A voice note does not arrive here: its bubble has its own play
            // button, and the border no longer raises this tap.

            if (message.IsDocument)
            {
                if (!string.IsNullOrEmpty(message.MediaFilePath))
                {
                    await OpenDocumentAsync(message);
                    return;
                }

                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            if (!string.IsNullOrEmpty(message.MediaData))
            {
                await ShowFullScreenAsync(message);
                return;
            }

            if (Downloadable(message)) RequestMedia(message);
        }

        private async System.Threading.Tasks.Task ShowFullScreenAsync(ChatMessage message)
        {
            if (message == null) return;

            try
            {
                var bitmap = await message.LoadBitmapAsync(ViewerDecodePixels);
                if (bitmap == null) return;
                ImageViewerImage.Source = bitmap;
                ImageViewer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ShowFullScreenAsync", ex);
                HideFullScreen();
            }
        }

        private void ImageViewer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideFullScreen();
            e.Handled = true;
        }

        /// <summary>
        /// The profile photo full screen: it is decoded at the screen size, as for a
        /// bubble, and it is closed by tapping it (ImageViewer). Without bytes
        /// nothing opens: there is no request to make here, the chat list has
        /// already requested them.
        /// </summary>
        private async void HeaderAvatar_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_contact == null || string.IsNullOrEmpty(_contact.AvatarData)) return;

            try
            {
                // The header is a 40 px circle and the full-screen view is the same
                // picture enlarged: decoding it at the viewer size (720) is a copy
                // held while the conversation behind it is still being laid out, and
                // the 52 px one is indistinguishable on a phone screen.
                var bitmap = await ImageHelper.FromBase64Async(_contact.AvatarData, Contact.AvatarDecodePixels);
                if (bitmap == null) return;
                ImageViewerImage.Source = bitmap;
                ImageViewer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.HeaderAvatar_Tapped", ex);
                HideFullScreen();
            }
        }

        /// <summary>The name opens the info: the picture stays for the picture.</summary>
        private void ContactHeader_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_contact == null) return;
            e.Handled = true;
            Frame.Navigate(typeof(ContactInfoPage), _contact);
        }

        private void HideFullScreen()
        {
            ImageViewer.Visibility = Visibility.Collapsed;
            ImageViewerImage.Source = null;
        }

        /// <summary>
        /// Opens the received video in the full-screen player. The source is the
        /// local file (ms-appdata): the player opens it on its own and there is no
        /// stream to keep open for the life of the page. A voice note does not
        /// come here any more: it plays in its own bubble.
        /// </summary>
        private void PlayVideo(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) return;

            try
            {
                StopVideo();
                VideoPlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
                VideoViewer.Visibility = Visibility.Visible;
                VideoPlayer.Play();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.PlayVideo", ex);
                ShowVideoError();
            }
        }

        // The voice note whose file is loaded in VoicePlayer, whether it is
        // playing or paused, and the name of that file. One player for the page:
        // starting another one rewinds the first.
        private ChatMessage _voiceMessage;
        private string _voiceLoadedFile;
        private DispatcherTimer _playbackTimer;

        // The voice note whose bytes were asked for by a tap on its play button:
        // it plays as soon as they arrive. Null when nobody is waiting.
        private ChatMessage _playWhenReady;

        // A play was asked for on a file the element is still opening. Play() while
        // it is opening does nothing and raises nothing, so the ask is kept and made
        // again on MediaOpened: that silence is the voice note that "does not play".
        private bool _playOnOpen;

        /// <summary>
        /// The play/pause glyph of one bubble. A second tap on the same voice note
        /// pauses it and keeps the position; tapping another one rewinds the first.
        /// With no bytes yet it is the same tap that asks for them, which is what
        /// the old bar did before it could be played.
        ///
        /// Two buttons and not one: the incoming bubble and the outgoing one each
        /// draw their own, and a name lives once in a namescope. Two buttons called
        /// PlayAudioButton in this page single DataTemplate made the template throw
        /// XamlParseException when its first row was realized, so the chat closed
        /// instead of opening. check-actions.js wants a button named X wired to
        /// X_Click, which is why each name has its own handler and both come here.
        /// </summary>
        private void IncomingPlayAudioButton_Click(object sender, RoutedEventArgs e)
        {
            OnPlayAudioClicked(sender);
        }

        private void OutgoingPlayAudioButton_Click(object sender, RoutedEventArgs e)
        {
            OnPlayAudioClicked(sender);
        }

        /// <summary>
        /// A voice note just landed on disk. If the tap that asked for it was a tap
        /// on its play button, the note plays now: the tap used to download only, so
        /// the bar stayed silent and the note seemed not to play at all.
        /// </summary>
        private void OnAudioArrived(object sender, ChatMessage message)
        {
            if (message == null || _playWhenReady == null) return;
            // The id and not the object: the row whose bytes arrived is the one the
            // list holds now, and a reload of the conversation can have replaced the
            // instance the tap was made on with another one carrying the same id.
            if (message.Id != _playWhenReady.Id)
            {
                Diag.Ok("audio arrived: " + message.Id + " was not asked for");
                return;
            }

            _playWhenReady = null;
            Diag.Ok("audio arrived: " + message.Id + " playing it");
            // The same path as a tap on a note that is already here: one player,
            // one bar playing, and the failure line if the phone refuses the file.
            ToggleVoice(message);
        }

        private void OnPlayAudioClicked(object sender)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ChatMessage;
            if (message == null) return;

            if (string.IsNullOrEmpty(message.MediaFilePath))
            {
                // Without this the log of a tap that heard nothing cannot tell a
                // note the app refused to ask for from one whose bytes never came.
                Diag.Ok("voice tap: " + message.Id + ", no bytes here yet");
                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            ToggleVoice(message);
        }

        private void ToggleVoice(ChatMessage message)
        {
            if (message == null) return;

            // The same one, playing: this tap is a pause. The position stays, and
            // the next tap resumes from there.
            if (message.IsPlaying)
            {
                PauseVoice(message);
                return;
            }

            // A different one was playing or paused: it goes back to its start, so
            // that two bars cannot both look active.
            if (_voiceMessage != null && _voiceMessage != message) ResetVoice(_voiceMessage);

            try
            {
                bool anotherMessage = _voiceMessage != message;
                // The name alone is not enough: a replaced file keeps the same name,
                // so the player would go on holding the previous recording.
                if (anotherMessage || _voiceLoadedFile != message.MediaFilePath)
                {
                    _playOnOpen = true;
                    VoicePlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
                    _voiceLoadedFile = message.MediaFilePath;
                }
                message.AudioFailed = false;
                _voiceMessage = message;
                VoicePlayer.Position = TimeSpan.Zero;
                VoicePlayer.Play();
                message.IsPlaying = true;
                StartPlaybackTimer();
                // The line that tells a note the phone refused (the MediaFailed one)
                // from a play that was never asked for. The two used to look alike.
                Diag.Ok("voice play: " + message.Id + " from " + message.MediaFilePath);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ToggleVoice", ex);
                ResetVoice(message);
                message.AudioFailed = true;
            }
        }

        private void PauseVoice(ChatMessage message)
        {
            // A pause asked for while the file is still opening stays a pause.
            _playOnOpen = false;
            try
            {
                VoicePlayer.Pause();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.PauseVoice", ex);
            }

            message.IsPlaying = false;
            StopPlaybackTimer();
        }

        /// <summary>Back to the start, with nothing to show.</summary>
        private void ResetVoice(ChatMessage message)
        {
            if (message == null) return;
            message.IsPlaying = false;
            message.PlaybackProgress = 0;
            message.PlaybackTimeText = "";
        }

        /// <summary>Closes the player: it is called when the page is left.</summary>
        private void StopVoice()
        {
            _playOnOpen = false;
            try
            {
                VoicePlayer.Stop();
                VoicePlayer.Source = null;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.StopVoice", ex);
            }

            ResetVoice(_voiceMessage);
            _voiceMessage = null;
            _voiceLoadedFile = null;
            StopPlaybackTimer();
        }

        /// <summary>
        /// The element has the file: only from here does Play() do anything. A play
        /// asked for while it was opening is made now, and the duration goes in the
        /// log, because a duration that never arrives is what a silent bar looks
        /// like from the inside.
        /// </summary>
        private void VoicePlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            double total = 0;
            try
            {
                if (VoicePlayer.NaturalDuration.HasTimeSpan)
                    total = VoicePlayer.NaturalDuration.TimeSpan.TotalSeconds;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/MediaOpened duration", ex);
            }

            Diag.Ok("voice opened: " + (total > 0 ? total + "s" : "no duration"));

            if (!_playOnOpen) return;
            _playOnOpen = false;
            try
            {
                VoicePlayer.Play();
                if (_voiceMessage != null) _voiceMessage.IsPlaying = true;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/MediaOpened play", ex);
            }
        }

        private void VoicePlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            var message = _voiceMessage;
            if (message != null)
            {
                message.PlaybackProgress = 1;
                message.IsPlaying = false;
                message.PlaybackTimeText = "";
            }
            StopPlaybackTimer();
        }

        /// <summary>
        /// The phone refused the file. On WP8.1 the event carries only the message,
        /// not the exception, so it is logged as one: an unplayable voice note and
        /// a silent one used to look the same.
        /// </summary>
        private void VoicePlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string reason = (e != null && !string.IsNullOrEmpty(e.ErrorMessage))
                ? e.ErrorMessage
                : "media failed";
            Diag.Failed("ChatPage/VoicePlayer", new InvalidOperationException(reason));

            var message = _voiceMessage;
            if (message != null)
            {
                message.IsPlaying = false;
                message.AudioFailed = true;
            }
            StopPlaybackTimer();
        }

        private void StartPlaybackTimer()
        {
            if (_playbackTimer == null)
            {
                _playbackTimer = new DispatcherTimer();
                _playbackTimer.Interval = TimeSpan.FromMilliseconds(250);
                _playbackTimer.Tick += PlaybackTimer_Tick;
            }
            _playbackTimer.Start();
        }

        private void StopPlaybackTimer()
        {
            if (_playbackTimer != null) _playbackTimer.Stop();
        }

        /// <summary>
        /// Four times a second, the position of the one voice note that can be
        /// playing. It is read here and not bound to the MediaElement because
        /// MediaElement.Position is not a dependency property: there is no binding
        /// to hang it on.
        /// </summary>
        private void PlaybackTimer_Tick(object sender, object e)
        {
            var message = _voiceMessage;
            if (message == null)
            {
                StopPlaybackTimer();
                return;
            }

            double total = 0;
            try
            {
                if (VoicePlayer.NaturalDuration.HasTimeSpan)
                    total = VoicePlayer.NaturalDuration.TimeSpan.TotalSeconds;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/NaturalDuration", ex);
            }

            double position = 0;
            try
            {
                position = VoicePlayer.Position.TotalSeconds;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/Position", ex);
            }

            message.PlaybackProgress = total > 0 ? Math.Min(1.0, position / total) : 0;
            message.PlaybackTimeText = total > 0 ? FormatClock(position) + " / " + FormatClock(total) : "";
        }

        private static string FormatClock(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Round(seconds);
            return (total / 60) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Opens a received document with the app the phone uses for that type of
        /// file. If there is none, or the file is no longer there, it says so
        /// instead of doing nothing.
        /// </summary>
        private async System.Threading.Tasks.Task OpenDocumentAsync(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) return;

            // C# 5 does not allow awaiting inside a catch: the failure is noted and
            // awaited afterwards, outside the block.
            bool failed = false;
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(message.MediaFilePath);
                bool opened = await Launcher.LaunchFileAsync(file);
                failed = !opened;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.OpenDocumentAsync", ex);
                failed = true;
            }

            if (failed) await ShowDocumentErrorAsync();
        }

        private async System.Threading.Tasks.Task ShowDocumentErrorAsync()
        {
            var dialog = new MessageDialog(Loc.Get("ChatPage_DocumentError",
                "There is no app on this phone that can open this file."));
            await dialog.ShowAsync();
        }

        private void VideoCloseButton_Click(object sender, RoutedEventArgs e)
        {
            StopVideo();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // A video the phone cannot decode. It used to just close the screen, so
            // a failure and an empty tap looked the same; now the sentence stays. On
            // WP8.1 the event carries only the message, not the exception.
            string reason = (e != null && !string.IsNullOrEmpty(e.ErrorMessage))
                ? e.ErrorMessage
                : "media failed";
            Diag.Failed("ChatPage/VideoPlayer", new InvalidOperationException(reason));
            ShowVideoError();
        }

        private void ShowVideoError()
        {
            try
            {
                VideoPlayer.Stop();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ShowVideoError", ex);
            }
            VideoErrorText.Text = Loc.Get("ChatPage_VideoError", "This video cannot be played.");
            VideoErrorText.Visibility = Visibility.Visible;
        }

        /// <summary>Closes the player. Safe to call even with nothing playing.</summary>
        private void StopVideo()
        {
            try
            {
                VideoPlayer.Stop();
                VideoPlayer.Source = null;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.StopVideo", ex);
            }

            VideoErrorText.Visibility = Visibility.Collapsed;
            VideoViewer.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// This conversation has been shown: the number is cleared here and on the
        /// server. DataService owns both halves now: it clears the row and sends the
        /// `read` receipt, or queues it when the socket is not usable. The other side
        /// counts every incoming message, even the ones the user is watching, so
        /// without telling it the number would come back at the next list update -
        /// and on a socket the OS already closed the receipt was lost silently.
        /// </summary>
        private void MarkRead()
        {
            DataService.Instance.ClearUnread(_contact.Id);
        }

        private async void SendMessage()
        {
            string text = (MessageTextBox.Text ?? "").Trim();

            // If we have a selected image or video, send it as an attachment
            if (_selectedLocalFileName != null)
            {
                await SendAttachmentAsync(text);
                return;
            }

            // Text-only message
            if (string.IsNullOrEmpty(text)) return;

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = text,
                SenderId = NativeConnection.Instance.Jid ?? "me",
                SenderName = Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = MessageType.Text,
                IsIncoming = false,
                Status = MessageStatus.Sending
            };

            AddAndSendMessage(message);
        }

        /// <summary>
        /// An attachment is read whole from the file copied into the app folder (see
        /// AttachmentInbox), encrypted and uploaded by `NativeConnection.SendMediaAsync`:
        /// one POST carries the file to WhatsApp's own media host, and the message that
        /// follows names where it is. The chunk protocol that used to be here belonged to
        /// the adapter and left with it.
        ///
        /// Whole and in memory, and not streamed: the ciphertext carries its own ten-byte
        /// signature at the end, so a sender that streamed the file could not compute the
        /// MAC before the last block either (`MediaUpload.Body`).
        /// </summary>
        private async System.Threading.Tasks.Task SendAttachmentAsync(string caption)
        {
            string localFileName = _selectedLocalFileName;
            if (string.IsNullOrEmpty(localFileName)) return;

            string fileName = _selectedMediaFileName;
            string mimeType = _selectedMediaMimeType ?? "image/jpeg";
            string kind = AttachmentInbox.KindName(mimeType, fileName);

            // A document is not an image: MessageType.Text keeps the empty image
            // box of the bubble from being drawn on top of the card, and
            // MediaType "document" is what makes IsDocument true.
            MessageType type = kind == "video" ? MessageType.Video
                : (kind == "image" ? MessageType.Image
                : (kind == "audio" ? MessageType.Audio : MessageType.Text));

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = caption ?? "",
                SenderId = NativeConnection.Instance.Jid ?? "me",
                SenderName = Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = type,
                IsIncoming = false,
                Status = MessageStatus.Sending,
                // The bytes are on disk: here there is only where to find them.
                MediaFilePath = localFileName,
                MediaMimeType = mimeType,
                MediaFileName = fileName,
                MediaType = kind
            };

            // Local decoding: the sender sees their own image.
            if (message.Type == MessageType.Image) await message.LoadMediaImageAsync();
            // The sender sees their own video cover, when the phone can make one.
            if (message.Type == MessageType.Video) await message.LoadVideoThumbnailAsync();

            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";
            ScrollToMessage(message);
            ClearSelectedImage();

            if (!NativeConnection.Instance.IsConnected)
            {
                message.Status = MessageStatus.Failed;
                return;
            }

            message.Status = MessageStatus.Sending;

            // The video is made smaller here, after the bubble is on screen: the
            // bubble says "sending" while the conversion runs, and what is uploaded
            // below is the smaller file. On a phone that cannot convert it, the
            // original is sent.
            if (kind == "video")
            {
                string smaller = await VideoCompressor.SmallerAsync(localFileName);
                if (!string.IsNullOrEmpty(smaller) && smaller != localFileName)
                {
                    localFileName = smaller;
                    fileName = smaller;
                    mimeType = "video/mp4";
                    message.MediaFilePath = smaller;
                    message.MediaFileName = smaller;
                    message.MediaMimeType = mimeType;
                }
            }

            try
            {
                byte[] file = await ReadWholeFileAsync(localFileName);

                if (file == null || file.Length == 0)
                {
                    // A file with nothing in it: it would be a message with no media at the
                    // other end, and the bubble keeps its checkmark forever.
                    Diag.Failed("ChatPage.SendAttachmentAsync/empty",
                        new InvalidOperationException(fileName + " is 0 bytes"));
                    message.Status = MessageStatus.Failed;
                    return;
                }

                message.MediaSizeBytes = file.Length;

                bool voiceNote = kind == "audio";

                string id = await NativeConnection.Instance.SendMediaAsync(_contact.Id, file, kind,
                    mimeType, fileName, caption ?? "", 0, voiceNote);

                // The wire id becomes the bubble's: a receipt names the message by it, and
                // a bubble with a random id of its own never sees its ticks turn blue.
                message.Id = id;
                message.Status = MessageStatus.Sent;
            }
            catch (WhatsappApp.Protocol.ProtocolException refused)
            {
                // A refused upload is the bubble's failure and it is named: the sentence
                // says which check refused it, which is the difference between a file too
                // big and a socket that is not there.
                Diag.Failed("ChatPage/send attachment", refused);
                message.Status = MessageStatus.Failed;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.SendAttachmentAsync", ex);
                message.Status = MessageStatus.Failed;
            }
        }

        /// <summary>
        /// The attachment, whole, as bytes.
        /// </summary>
        private static async System.Threading.Tasks.Task<byte[]> ReadWholeFileAsync(string localFileName)
        {
            StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(localFileName);
            Windows.Storage.Streams.IBuffer buffer = await FileIO.ReadBufferAsync(file);
            var bytes = new byte[buffer.Length];

            using (var reader = DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(bytes);
            }

            return bytes;
        }

        private async void AddAndSendMessage(ChatMessage message)
        {
            // DataService is the only insertion point: _messages is the same
            // ObservableCollection observed by the ListView.
            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";

            // Auto-scroll
            ScrollToMessage(message);

            // The status is decided now, not when the page was opened: a message
            // written with the socket down stayed "sent" forever without ever
            // leaving. Now it shows as failed and can be rewritten.
            if (!NativeConnection.Instance.IsConnected)
            {
                message.Status = MessageStatus.Failed;
                return;
            }

            message.Status = MessageStatus.Sending;

            // The send is on the phone's own protocol now. A failure is named and the bubble
            // says so: a message that did not leave must not read as sent.
            try
            {
                // The wire id becomes the bubble's: a receipt names the message by it.
                message.Id = await NativeConnection.Instance.SendTextAsync(message.ChatId, message.Text);
                message.Status = MessageStatus.Sent;
            }
            catch (WhatsappApp.Protocol.ProtocolException failed)
            {
                Diag.Failed("ChatPage/send", failed);
                message.Status = MessageStatus.Failed;
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        private void MessageTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                SendMessage();
                e.Handled = true;
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        /// <summary>
        /// The microphone button. It starts a recording; the stop button ends it.
        /// Two buttons instead of one that changes glyph: the page has no
        /// bindable property to hang a second Path on (its DataContext is the
        /// message list), and toggling Visibility from code is what the rest of
        /// this page already does.
        /// </summary>
        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_recording.IsRecording) return;
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatPage/StartRecording", StartRecordingAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// The page's half of the start: it asks the session, and either shows the
        /// sentence or the bar. The single-flight rule and the call-site catch
        /// live in RecordingSession.
        /// </summary>
        private async System.Threading.Tasks.Task StartRecordingAsync()
        {
            if (!await _recording.StartAsync())
            {
                await ShowRecordErrorAsync();
                return;
            }

            RecordTimerText.Text = "0:00";
            RecordingBar.Visibility = Visibility.Visible;
            RecordButton.Visibility = Visibility.Collapsed;
            StopRecordButton.Visibility = Visibility.Visible;
            StartRecordTimer();
        }

        /// <summary>
        /// The sentence of a recording that did not start. It is guarded: a
        /// dialog that cannot be shown is logged, not thrown into a Task
        /// nobody observes.
        /// </summary>
        private async System.Threading.Tasks.Task ShowRecordErrorAsync()
        {
            try
            {
                await new MessageDialog(
                    Loc.Get("ChatPage_RecordError", "Could not start the recording. " +
                        "Check that this app may use the microphone.")).ShowAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/RecordError", ex);
            }
        }

        private void StopRecordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_recording.IsRecording) return;
#pragma warning disable 4014
            StopRecordingAsync();
#pragma warning restore 4014
        }

        /// <summary>
        /// Ends the recording and puts the file in the waiting slot, the same one
        /// the picker uses: the preview bar shows it, and Send streams it out
        /// through SendAttachmentAsync like every other attachment.
        /// </summary>
        private async System.Threading.Tasks.Task StopRecordingAsync()
        {
            string fileName = await _recording.StopAsync();

            EndRecordingState();

            if (string.IsNullOrEmpty(fileName)) return;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(fileName);
                await AttachmentInbox.PutAsync(file, null);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.StopRecordingAsync/slot", ex);
            }
        }

        /// <summary>
        /// The recording is thrown away: the page is being left. No file is
        /// deposited, so nothing can be sent by mistake from another chat.
        /// </summary>
        private void CancelRecording()
        {
            bool wasRecording = _recording.IsRecording;
            if (wasRecording) EndRecordingState();

            // Always asked: a start that has not answered yet is forgotten here
            // too, and the capture it may create is cancelled if there is one.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatPage.CancelRecording", _recording.CancelAsync());
#pragma warning restore 4014
        }

        /// <summary>The buttons and the bar go back to their resting state.</summary>
        private void EndRecordingState()
        {
            StopRecordTimer();
            RecordingBar.Visibility = Visibility.Collapsed;
            RecordButton.Visibility = Visibility.Visible;
            StopRecordButton.Visibility = Visibility.Collapsed;
        }

        private void StartRecordTimer()
        {
            if (_recordTimer == null)
            {
                _recordTimer = new DispatcherTimer();
                _recordTimer.Interval = TimeSpan.FromMilliseconds(500);
                _recordTimer.Tick += RecordTimer_Tick;
            }
            _recordTimer.Start();
        }

        private void StopRecordTimer()
        {
            if (_recordTimer != null) _recordTimer.Stop();
        }

        private void RecordTimer_Tick(object sender, object e)
        {
            RecordTimerText.Text = FormatClock((DateTime.Now - _recording.StartedAt).TotalSeconds);
        }

        /// <summary>
        /// Attach button: it asks for the system picker. The answer does not arrive
        /// here - it arrives at App.OnActivated after the app has been reactivated -
        /// so there is nothing to await.
        /// </summary>
        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ImagePickerService.RequestFile();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/pick", ex);
                Debug.WriteLine(
                    string.Format(Loc.Get("ChatPage_ImageError", "Could not open the image: {0}"), ex.Message));
            }
        }

        /// <summary>
        /// An attachment arrived while this chat was open: it is the normal case,
        /// because the picker opens from here and the app comes back here.
        /// </summary>
        private void OnAttachmentReady()
        {
            ShowPendingAttachment();
        }

        /// <summary>
        /// Shows the pending attachment, if there is one. Called both when
        /// navigating here and on arrival: after the picker the page is still the
        /// one in front and OnNavigatedTo is not called again.
        /// </summary>
        private void ShowPendingAttachment()
        {
            if (!AttachmentInbox.HasAttachment) return;

            string note = AttachmentInbox.Note;
            _selectedLocalFileName = AttachmentInbox.LocalFileName;
            _selectedMediaFileName = AttachmentInbox.FileName;
            _selectedMediaMimeType = AttachmentInbox.MimeType;
            AttachmentInbox.Clear();

            if (!string.IsNullOrEmpty(note) && string.IsNullOrEmpty(MessageTextBox.Text))
            {
                MessageTextBox.Text = note;
            }

            string kind = AttachmentInbox.KindName(_selectedMediaMimeType, _selectedMediaFileName);
            bool video = kind == "video";
            bool document = kind == "document";
            bool audio = kind == "audio";

            PreviewLabel.Text = video
                ? Loc.Get("ChatPage_VideoSelected", "Video selected")
                : (document
                    ? Loc.Get("ChatPage_DocumentSelected", "Document selected")
                    : (audio
                        ? Loc.Get("ChatPage_AudioSelected", "Voice note selected")
                        : Loc.Get("ChatPage_ImageSelected.Text", "Image selected")));

            SelectedVideoPreview.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            SelectedAudioPreview.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
            SelectedImagePreview.Visibility = (!video && !document && !audio)
                ? Visibility.Visible : Visibility.Collapsed;
            SelectedDocumentPreview.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
            SelectedDocumentPreviewText.Text = document ? _selectedMediaFileName : "";

            ImagePreviewBar.Visibility = Visibility.Visible;
            if (!video && !document && !audio)
            {
#pragma warning disable 4014
                ShowLocalPreviewAsync(_selectedLocalFileName);
#pragma warning restore 4014
            }
            else
            {
                // A video is not decoded here, a document has nothing to draw,
                // and a voice note is not a picture: the bar says what is being
                // sent.
                SelectedImagePreview.Source = null;
            }
        }

        /// <summary>
        /// The preview of the image to send: the page is 480 px wide, so 720 covers
        /// it even at 1.5x without decoding the whole file.
        /// </summary>
        private const int PreviewDecodePixels = 720;

        /// <summary>Local preview: the sender sees their own image.</summary>
        private async System.Threading.Tasks.Task ShowLocalPreviewAsync(string localFileName)
        {
            try
            {
                SelectedImagePreview.Source = await ImageHelper.FromFileAsync(
                    localFileName, PreviewDecodePixels);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ShowLocalPreviewAsync", ex);
            }
        }

        /// <summary>
        /// Clears the selected image
        /// </summary>
        private void ClearImageButton_Click(object sender, RoutedEventArgs e)
        {
            ClearSelectedImage();
        }

        private void ClearSelectedImage()
        {
            _selectedLocalFileName = null;
            _selectedMediaFileName = null;
            _selectedMediaMimeType = null;
            SelectedImagePreview.Source = null;
            SelectedAudioPreview.Visibility = Visibility.Collapsed;
            SelectedDocumentPreview.Visibility = Visibility.Collapsed;
            SelectedDocumentPreviewText.Text = "";
            ImagePreviewBar.Visibility = Visibility.Collapsed;
        }
    }
}
