using System;
using System.Collections.ObjectModel;
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
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    public sealed partial class ChatPage : Page
    {
        private Contact _contact;
        private ObservableCollection<ChatMessage> _messages;
        // The chosen attachment: the name of the file copied into the app folder,
        // plus what is needed to send it. Not the bytes: a whole video in memory
        // is the heaviest thing this page could hold, and it is what used to close
        // the app when sharing a video (see AttachmentInbox).
        private string _selectedLocalFileName;
        private string _selectedMediaFileName;
        private string _selectedMediaMimeType;
        private ChatMessage _pendingScroll;
        private bool _scrollQueued;

        public ChatPage()
        {
            this.InitializeComponent();

            // Icon-only buttons: the label lives in the tooltip.
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
            ToolTipService.SetToolTip(AttachButton, Loc.Get("ChatPage_AttachTooltip", "Attach an image"));
            ToolTipService.SetToolTip(SendButton, Loc.Get("ChatPage_SendTooltip", "Send"));
            ToolTipService.SetToolTip(ClearImageButton, Loc.Get("ChatPage_ClearImageTooltip", "Remove the image"));

            // The picture and the name are two targets: the tooltip tells them apart.
            ToolTipService.SetToolTip(HeaderAvatar, Loc.Get("ChatPage_ProfilePhotoTooltip", "Show the profile photo"));
            ToolTipService.SetToolTip(ContactHeader, Loc.Get("ChatPage_ContactInfoTooltip", "Contact info"));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var contact = e.Parameter as Contact;
            if (contact != null)
            {
                _contact = contact;

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
                // shows right away, then the real history.
                _messages = DataService.Instance.GetMessages(contact.Id);
                MarkRead();
                MessagesListView.ItemsSource = _messages;
#pragma warning disable 4014
                LoadCachedMessagesAsync(contact.Id);
#pragma warning restore 4014

                // Auto-scroll to bottom
                if (_messages.Count > 0)
                    ScrollToMessage(_messages[_messages.Count - 1]);

                // From here on the messages of this chat are already read
                DataService.Instance.ActiveChatId = contact.Id;

                // The chat history: the adapter answers with the old messages,
                // marked IsHistory, and it is requested once per chat per session.
                // Without this request a chat that was just opened stays empty
                // until something new arrives.
                if (CommunicationService.Instance.IsConnected
                    && DataService.Instance.MarkHistoryRequested(contact.Id))
                {
#pragma warning disable 4014
                    CommunicationService.Instance.SendControlAsync("messages", contact.Id);
#pragma warning restore 4014
                }

                // Listen for new messages
                CommunicationService.Instance.MessageReceived += OnMessageReceived;

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
            AttachmentInbox.Ready -= OnAttachmentReady;

            // The snapshot of the conversation: leaving is what writes it, not
            // every message, otherwise it would write a file in bursts.
            if (_contact != null && _messages != null)
            {
#pragma warning disable 4014
                MessageCache.SaveAsync(_contact.Id, _messages);
#pragma warning restore 4014
            }

            DataService.Instance.ActiveChatId = null;
            _pendingScroll = null;
            HideFullScreen();
            StopVideo();
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
        /// Scrolls to the last message once per burst: a burst of incoming messages
        /// used to do an UpdateLayout + ScrollIntoView for each one, that is a full
        /// layout pass per message.
        /// </summary>
        private void ScrollToMessage(ChatMessage message)
        {
            _pendingScroll = message;
            if (_scrollQueued) return;

            _scrollQueued = true;
#pragma warning disable 4014
            Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                _scrollQueued = false;
                if (_pendingScroll == null) return;

                MessagesListView.ScrollIntoView(_pendingScroll);
                _pendingScroll = null;
            });
#pragma warning restore 4014
        }

        private void OnMessageReceived(object sender, ChatMessage message)
        {
            // DataService has already inserted the message into the same collection:
            // here we only scroll, otherwise the bubble would show up twice.
            if (message.ChatId != _contact.Id) return;

            ScrollToMessage(message);

            // This handler lives only while this page is the one in front (it is
            // attached in OnNavigatedTo and detached in OnNavigatedFrom), so a
            // message that arrives here is a message the user is watching scroll
            // by: it is read now, as on WhatsApp. The number on the row is cleared
            // for this reason, not because of an exclusion in the counter.
            MarkRead();
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

            // Only what arrived from outside has an id the server knows: a message
            // written here carries a local id, and asking the server for it would be
            // a request without an answer.
            if (!message.IsIncoming) return false;

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
        /// Fills the conversation with the copy on the phone and scrolls to the
        /// bottom. It must be awaited on the UI thread: the collection is the one
        /// bound to the list.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCachedMessagesAsync(string chatId)
        {
            await DataService.Instance.LoadCachedMessagesAsync(chatId);
            if (_messages.Count > 0) ScrollToMessage(_messages[_messages.Count - 1]);
        }

        private void RequestMedia(ChatMessage message)
        {
            if (!CommunicationService.Instance.IsConnected) return;
            // The spinner starts now: the first piece can take a while, and without
            // this the tap seems to have done nothing.
            message.IsMediaLoading = true;
#pragma warning disable 4014
            CommunicationService.Instance.RequestMediaAsync(message.ChatId, message.Id);
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
                    PlayMedia(message, false);
                    return;
                }

                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            if (message.IsAudio)
            {
                if (!string.IsNullOrEmpty(message.MediaFilePath))
                {
                    PlayMedia(message, true);
                    return;
                }

                if (Downloadable(message)) RequestMedia(message);
                return;
            }

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
                var bitmap = await ImageHelper.FromBase64Async(_contact.AvatarData, ViewerDecodePixels);
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

        // True when what is playing is a voice note: the view is the same, but the
        // error sentence is not.
        private bool _playingAudio;

        /// <summary>
        /// Opens the received media in the full-screen player. The source is the
        /// local file (ms-appdata): the player opens it on its own and there is no
        /// stream to keep open for the life of the page. It applies to a video and
        /// to a voice note: for an audio the view is black and the transport
        /// controls remain, which are the system ones.
        /// </summary>
        private void PlayMedia(ChatMessage message, bool audio)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) return;

            try
            {
                StopVideo();
                _playingAudio = audio;
                VideoPlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
                VideoViewer.Visibility = Visibility.Visible;
                VideoPlayer.Play();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.PlayMedia", ex);
                ShowVideoError();
            }
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
            VideoErrorText.Text = _playingAudio
                ? Loc.Get("ChatPage_AudioError", "This voice note cannot be played.")
                : Loc.Get("ChatPage_VideoError", "This video cannot be played.");
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

            _playingAudio = false;
            VideoErrorText.Visibility = Visibility.Collapsed;
            VideoViewer.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// This conversation has been shown: the number is cleared here and on the
        /// server. The second part is not a detail: the adapter counts every incoming
        /// message, even the ones the user is watching, so without telling it the
        /// number would come back at the next list update.
        /// </summary>
        private void MarkRead()
        {
            DataService.Instance.ClearUnread(_contact.Id);

            if (!CommunicationService.Instance.IsConnected) return;
#pragma warning disable 4014
            CommunicationService.Instance.SendControlAsync("read", _contact.Id);
#pragma warning restore 4014
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

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = caption ?? "",
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = kind == "video" ? MessageType.Video : MessageType.Image,
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

            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";
            ScrollToMessage(message);
            ClearSelectedImage();

            if (!CommunicationService.Instance.IsConnected)
            {
                message.Status = MessageStatus.Failed;
                return;
            }

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(localFileName);
                ulong length = (await file.GetBasicPropertiesAsync()).Size;
                int total = length == 0
                    ? 0
                    : (int)((length + (ulong)MediaChunkBytes - 1) / (ulong)MediaChunkBytes);

                string transferId = Guid.NewGuid().ToString("N");
                await CommunicationService.Instance.SendMediaBeginAsync(
                    _contact.Id, transferId, fileName, mimeType, total);

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
                message.Status = CommunicationService.Instance.IsConnected
                    ? MessageStatus.Sent
                    : MessageStatus.Failed;
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
        /// Attach button: it asks for the system picker. The answer does not arrive
        /// here - it arrives at App.OnActivated after the app has been reactivated -
        /// so there is nothing to await.
        /// </summary>
        private void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ImagePickerService.RequestImage();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/image", ex);
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

            bool video = AttachmentInbox.KindName(_selectedMediaMimeType, _selectedMediaFileName) == "video";
            PreviewLabel.Text = video
                ? Loc.Get("ChatPage_VideoSelected", "Video selected")
                : Loc.Get("ChatPage_ImageSelected.Text", "Image selected");
            SelectedVideoPreview.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            SelectedImagePreview.Visibility = video ? Visibility.Collapsed : Visibility.Visible;

            ImagePreviewBar.Visibility = Visibility.Visible;
            if (!video)
            {
#pragma warning disable 4014
                ShowLocalPreviewAsync(_selectedLocalFileName);
#pragma warning restore 4014
            }
            else
            {
                // A video is not decoded: there is nothing to draw.
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
            ImagePreviewBar.Visibility = Visibility.Collapsed;
        }
    }
}
