using System;
using System.Collections.ObjectModel;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    public sealed partial class ChatPage : Page
    {
        private Contact _contact;
        private ObservableCollection<ChatMessage> _messages;
        private bool _isConnectedMode;
        private StorageFile _selectedImageFile;
        private string _selectedImageBase64;

        public ChatPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is Contact contact)
            {
                _contact = contact;
                _isConnectedMode = CommunicationService.Instance.IsConnected;

                ContactNameText.Text = contact.Name;
                OnlineStatusText.Text = contact.IsOnline ? "in linea" : "ultimo accesso oggi " + DateTime.Now.ToString("HH:mm");

                // Load messages
                _messages = DataService.Instance.GetMessages(contact.Id);
                DataService.Instance.ClearUnread(contact.Id);
                MessagesListView.ItemsSource = _messages;

                // Auto-scroll to bottom
                if (_messages.Count > 0)
                {
                    MessagesListView.UpdateLayout();
                    MessagesListView.ScrollIntoView(_messages[_messages.Count - 1]);
                }

                // Listen for new messages
                CommunicationService.Instance.MessageReceived += OnMessageReceived;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.MessageReceived -= OnMessageReceived;
        }

        private void OnMessageReceived(object sender, ChatMessage message)
        {
            if (message.ChatId == _contact.Id)
            {
                _messages.Add(message);
                MessagesListView.UpdateLayout();
                MessagesListView.ScrollIntoView(message);
            }
        }

        private async void SendMessage()
        {
            string text = MessageTextBox.Text?.Trim();

            // If we have a selected image, send it as an image message
            if (_selectedImageFile != null && _selectedImageBase64 != null)
            {
                await SendImageMessage(text);
                return;
            }

            // Text-only message
            if (string.IsNullOrEmpty(text)) return;

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = text,
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? "Io",
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = MessageType.Text,
                IsIncoming = false,
                Status = _isConnectedMode ? MessageStatus.Sending : MessageStatus.Sent
            };

            AddAndSendMessage(message);
        }

        private async System.Threading.Tasks.Task SendImageMessage(string caption)
        {
            string mimeType = "image/jpeg";
            string extension = _selectedImageFile?.FileType?.ToLower();
            if (extension == ".png") mimeType = "image/png";
            else if (extension == ".gif") mimeType = "image/gif";
            else if (extension == ".bmp") mimeType = "image/bmp";

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = caption ?? "",
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? "Io",
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = MessageType.Image,
                IsIncoming = false,
                Status = _isConnectedMode ? MessageStatus.Sending : MessageStatus.Sent,
                MediaData = _selectedImageBase64,
                MediaMimeType = mimeType,
                MediaFileName = _selectedImageFile?.Name
            };

            AddAndSendMessage(message);

            // Clear image preview
            ClearSelectedImage();
        }

        private async void AddAndSendMessage(ChatMessage message)
        {
            // Add message locally
            _messages.Add(message);
            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";

            // Auto-scroll
            MessagesListView.UpdateLayout();
            MessagesListView.ScrollIntoView(message);

            // Send via network if connected
            if (_isConnectedMode)
            {
                message.Status = MessageStatus.Sending;
                await CommunicationService.Instance.SendMessageAsync(message);
                message.Status = MessageStatus.Sent;
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
        /// Attach button: opens a file picker to select an image
        /// </summary>
        private async void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.Thumbnail,
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary
                };
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".png");
                picker.FileTypeFilter.Add(".gif");
                picker.FileTypeFilter.Add(".bmp");

                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                _selectedImageFile = file;

                // Read the image file and convert to base64
                using (var stream = await file.OpenReadAsync())
                {
                    using (var reader = new DataReader(stream))
                    {
                        uint size = (uint)stream.Size;
                        await reader.LoadAsync(size);
                        byte[] buffer = new byte[size];
                        reader.ReadBytes(buffer);
                        _selectedImageBase64 = System.Convert.ToBase64String(buffer);
                    }
                }

                // Show preview
                using (var stream = await file.OpenReadAsync())
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    SelectedImagePreview.Source = bitmap;
                }

                ImagePreviewBar.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Errore selezione immagine: {ex.Message}");
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
            _selectedImageFile = null;
            _selectedImageBase64 = null;
            SelectedImagePreview.Source = null;
            ImagePreviewBar.Visibility = Visibility.Collapsed;
        }
    }
}
