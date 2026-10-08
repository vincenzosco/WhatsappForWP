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
        private EventHandler _connectionEstablished;
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

            // The picture and the name are two targets: the tooltip tells them apart.
            ToolTipService.SetToolTip(HeaderAvatar, Loc.Get("ChatPage_ProfilePhotoTooltip", "Show the profile photo"));
            ToolTipService.SetToolTip(ContactHeader, Loc.Get("ChatPage_ContactInfoTooltip", "Contact info"));

            // The list is handed to the view module now, before it has a source:
            // binding happens later, once the saved copy is in.
            _view = new ConversationView(MessagesListView, Dispatcher);
            _recording = new RecordingSession();
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

                // The next phone log then reads this line and, if the process still
                // dies after a chat opens, the site it reached is the last thing
                // before App/unhandled: the crash report has to name itself.
                Diag.OkNow("opened chat " + contact.Id + ", memory "
                    + (Windows.System.MemoryManager.AppMemoryUsage / (1024UL * 1024UL)) + " MB");

                // Whether the history goes out now decides how the list binds: at
                // once when nothing was asked for, and only when the burst closes
                // when there is one on the way.
                bool historyRequested = CommunicationService.Instance.IsConnected
                    && DataService.Instance.MarkHistoryRequested(contact.Id);
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ChatPage/cached messages",
                    BindAfterCacheAsync(contact.Id, historyRequested));
#pragma warning restore 4014

                // The chat history: the adapter answers with the old messages,
                // marked IsHistory, and it is requested once per chat per session.
                // Without this request a chat that was just opened stays empty
                // until something new arrives.
                if (historyRequested)
                {
                    Diag.Ok("history requested for " + contact.Id);
#pragma warning disable 4014
                    Guarded.RunGuardedAsync("ChatPage/messages",
                        CommunicationService.Instance.SendControlAsync("messages", contact.Id));
#pragma warning restore 4014
                }
                else
                {
                    // Asked for by nobody: this line tells apart a chat that was
                    // already served this session from one opened with no socket,
                    // which is the only case that asks for nothing at all.
                    Diag.Ok("history not requested for " + contact.Id
                        + (CommunicationService.Instance.IsConnected ? " (already asked)" : " (offline)"));
                }

                // A chat opened before the socket is up asked for nothing above,
                // and this is what gives it its history when the connection
                // arrives instead of an empty conversation for as long as it is
                // open. MarkHistoryRequested is the gate, so a chat that was
                // already served asks for nothing here.
                _connectionEstablished = OnConnectionEstablished;
                CommunicationService.Instance.ConnectionEstablished += _connectionEstablished;

                // Listen for new messages
                CommunicationService.Instance.MessageReceived += OnMessageReceived;

                // And for what WhatsApp says about the person of this chat, which
                // is not a message: the three dots of "writing".
                DataService.Instance.TypingChanged += OnTypingChanged;

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
            CommunicationService.Instance.MessageReceived -= OnMessageReceived;
            if (_connectionEstablished != null)
            {
                CommunicationService.Instance.ConnectionEstablished -= _connectionEstablished;
                _connectionEstablished = null;
            }
            DataService.Instance.TypingChanged -= OnTypingChanged;
            AttachmentInbox.Ready -= OnAttachmentReady;

            // Leaving while writing: the contact must not keep seeing the dots,
            // and the timer that refreshes them has no reason to run any more.
            StopTyping();
            HideTyping();
            _view.UnwatchBottom();

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

        private void OnMessageReceived(object sender, ChatMessage message)
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
            WatchBottomReached();
        }

        // ─── Someone is writing ───────────────────────────────────────────────

        /// <summary>
        /// WhatsApp says that the person of a chat is writing, or has stopped. It
        /// is forwarded by the adapter as a `typing` frame; this page is the only
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
        /// Starts watching the position of the conversation, so that a message left
        /// below is marked as read when the reader reaches the bottom.
        /// </summary>
        private void WatchBottomReached()
        {
            _view.WatchBottom(MarkReadWhenAtBottom);
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

            if (_typingSent || !CommunicationService.Instance.IsConnected) return;
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
            if (_contact == null || !CommunicationService.Instance.IsConnected) return;
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatPage/typing",
                CommunicationService.Instance.SendControlAsync("typing", _contact.Id, state));
#pragma warning restore 4014
        }

        /// <summary>
        /// A media of this conversation that does not have the bytes yet: it is a
        /// history row (or a video whose file the adapter did not have), and it can
        /// be requested from the server. The type says whether it was an image, a
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
        /// one frame per message. The wait is bounded, so an adapter that does not
        /// send the closing frame does not leave the chat empty.
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

            // The burst closed and the conversation is still empty: the request
            // went out and the adapter answered, so it was the wrong moment - the
            // account was not connected yet on the server side, or the frame
            // arrived before the request. One more ask, once per chat per session.
            if (historyRequested && _messages.Count == 0
                && CommunicationService.Instance.IsConnected
                && DataService.Instance.MarkHistoryRetried(chatId))
            {
                Diag.Ok("history retried for " + chatId);
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ChatPage/messages retry",
                    CommunicationService.Instance.SendControlAsync("messages", chatId));
#pragma warning restore 4014
            }
        }

        /// <summary>
        /// How long the list waits for the end of a history burst before binding
        /// anyway. The adapter's first read of the account is an HTTP round trip
        /// through GOWA and was measured at about 15 s on 2026-10-07, so a shorter
        /// wait binds the list first and lets the whole burst insert into a list
        /// that is already watching the collection - which is the failure
        /// ConversationView.Bind exists to prevent. Twenty seconds covers that
        /// read with room to spare, and the wait is still bounded, so an adapter
        /// without the closing frame does not leave the chat empty.
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

        /// <summary>
        /// The socket became ready while this conversation is the one in front.
        /// A chat opened before the connection asked for nothing, and this is
        /// what gives it its history instead of an empty list for as long as it
        /// stays open.
        /// </summary>
        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            if (_contact == null) return;
            if (DataService.Instance.ActiveChatId != _contact.Id) return;
            if (!DataService.Instance.MarkHistoryRequested(_contact.Id)) return;

            Diag.Ok("history requested on connect for " + _contact.Id);
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatPage/messages on connect",
                CommunicationService.Instance.SendControlAsync("messages", _contact.Id));
#pragma warning restore 4014
        }

        private void RequestMedia(ChatMessage message)
        {
            if (!CommunicationService.Instance.IsConnected) return;
            // The spinner starts now: the first piece can take a while, and without
            // this the tap seems to have done nothing.
            message.IsMediaLoading = true;
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ChatPage/request media",
                CommunicationService.Instance.RequestMediaAsync(message.ChatId, message.Id));
#pragma warning restore 4014
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

        private void OnPlayAudioClicked(object sender)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ChatMessage;
            if (message == null) return;

            if (string.IsNullOrEmpty(message.MediaFilePath))
            {
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
                    VoicePlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
                    _voiceLoadedFile = message.MediaFilePath;
                }
                message.AudioFailed = false;
                _voiceMessage = message;
                VoicePlayer.Position = TimeSpan.Zero;
                VoicePlayer.Play();
                message.IsPlaying = true;
                StartPlaybackTimer();
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
        /// `read` frame, or queues it when the socket is not usable. The adapter
        /// counts every incoming message, even the ones the user is watching, so
        /// without telling it the number would come back at the next list update -
        /// and on a socket the OS already closed the frame was lost silently.
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
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = MessageType.Text,
                IsIncoming = false,
                Status = MessageStatus.Sending
            };

            AddAndSendMessage(message);
        }

        /// <summary>
        /// How many BYTES are read per piece. It is a multiple of 3: its base64 is
        /// therefore exactly (bytes/3)*4 characters long, with no padding, and the
        /// pieces concatenate in base64 without re-encoding anything. 525000 bytes
        /// make 700000 characters, like MediaChunkChars and like the adapter.
        /// </summary>
        private const int MediaChunkBytes = 525000;

        /// <summary>
        /// An attachment is sent in pieces, read from the file copied into the app
        /// folder (see AttachmentInbox). A frame has an 8 MiB ceiling and the
        /// content travels in base64, which adds a third: a video does not fit in a
        /// single frame, and does not fit in the phone memory either.
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
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
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

            if (!CommunicationService.Instance.IsConnected)
            {
                message.Status = MessageStatus.Failed;
                return;
            }

            // The video is made smaller here, after the bubble is on screen: the
            // bubble says "sending" while the conversion runs, and what is read
            // and uploaded below is the smaller file. On a phone that cannot
            // convert it, the original is sent and the adapter shrinks it.
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
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(localFileName);
                ulong length = (await file.GetBasicPropertiesAsync()).Size;

                if (length == 0)
                {
                    // Not a smaller voice note: a file with nothing in it. Today it
                    // started a transfer with total = 0, the adapter's mediaEnd
                    // returned in silence, and the bubble kept its checkmark.
                    Diag.Failed("ChatPage.SendAttachmentAsync/empty",
                        new InvalidOperationException(fileName + " is 0 bytes"));
                    message.Status = MessageStatus.Failed;
                    return;
                }
                message.MediaSizeBytes = (long)length;
                int total = length == 0
                    ? 0
                    : (int)((length + (ulong)MediaChunkBytes - 1) / (ulong)MediaChunkBytes);

                string transferId = Guid.NewGuid().ToString("N");
                await CommunicationService.Instance.SendMediaBeginAsync(
                    _contact.Id, message.Id, transferId, fileName, mimeType, total);

                using (var stream = await file.OpenReadAsync())
                {
                    using (var reader = new DataReader(stream))
                    {
                        // ReadBytes reads raw bytes, so the byte order does not matter
                        // here: it is read in pieces and encoded.
                        for (int i = 0; i < total; i++)
                        {
                            ulong offset = (ulong)i * (ulong)MediaChunkBytes;
                            int size = (int)Math.Min((ulong)MediaChunkBytes, length - offset);

                            while (reader.UnconsumedBufferLength < size)
                            {
                                uint loaded = await reader.LoadAsync(
                                    (uint)(size - (int)reader.UnconsumedBufferLength));
                                if (loaded == 0) break;
                            }

                            byte[] buffer = new byte[size];
                            reader.ReadBytes(buffer);
                            await CommunicationService.Instance.SendMediaChunkAsync(
                                transferId, i, Convert.ToBase64String(buffer));
                        }
                    }
                }

                await CommunicationService.Instance.SendMediaEndAsync(transferId, caption);
                // The status is the adapter's answer, not the state of the socket:
                // it arrives as attachment.sent or error and DataService writes it.
                // The bubble keeps Sending until then.
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.SendAttachmentAsync", ex);
                message.Status = MessageStatus.Failed;
            }
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
            if (!CommunicationService.Instance.IsConnected)
            {
                message.Status = MessageStatus.Failed;
                return;
            }

            message.Status = MessageStatus.Sending;
            await CommunicationService.Instance.SendMessageAsync(message);
            message.Status = CommunicationService.Instance.IsConnected
                ? MessageStatus.Sent
                : MessageStatus.Failed;
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
