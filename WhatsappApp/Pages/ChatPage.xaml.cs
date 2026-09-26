using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
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
        private StorageFile _selectedImageFile;
        private string _selectedImageBase64;
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
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            var contact = e.Parameter as Contact;
            if (contact != null)
            {
                _contact = contact;

                ContactNameText.Text = contact.Name;

                // Nessuna presenza: WhatsApp non la espone tramite il server che
                // usiamo, e dire "online" o "ultimo accesso alle HH:mm" era una
                // bugia. Resta l'unica cosa vera in piu' che abbiamo: il numero
                // della chat, quando il nome non e' gia' il numero.
                string number = DisplayNumber(contact.Id);
                bool hasNumber = !string.IsNullOrEmpty(number) && number != contact.Name;
                OnlineStatusText.Text = hasNumber ? number : "";
                OnlineStatusText.Visibility = hasNumber ? Visibility.Visible : Visibility.Collapsed;

                // Load messages
                _messages = DataService.Instance.GetMessages(contact.Id);
                DataService.Instance.ClearUnread(contact.Id);
                MessagesListView.ItemsSource = _messages;

                // Auto-scroll to bottom
                if (_messages.Count > 0)
                    ScrollToMessage(_messages[_messages.Count - 1]);

                // Da qui in poi i messaggi di questa chat sono gia' letti
                DataService.Instance.ActiveChatId = contact.Id;

                // Listen for new messages
                CommunicationService.Instance.MessageReceived += OnMessageReceived;
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.MessageReceived -= OnMessageReceived;
            DataService.Instance.ActiveChatId = null;
            _pendingScroll = null;
        }

        /// <summary>
        /// Numero leggibile di un JID (es. +393401234567 per le persone). Vuoto
        /// per i gruppi e per tutto cio' che non e' un numero.
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
        /// Scorre sull'ultimo messaggio una volta per raffica: una raffica di
        /// messaggi in arrivo prima faceva un UpdateLayout + ScrollIntoView
        /// per ognuno, cioe' un giro di layout completo per messaggio.
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
            // DataService ha già inserito il messaggio nella stessa collezione:
            // qui si scorre soltanto, altrimenti la bolla comparirebbe due volte.
            if (message.ChatId == _contact.Id)
                ScrollToMessage(message);
        }

        private async void SendMessage()
        {
            string text = (MessageTextBox.Text ?? "").Trim();

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
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = MessageType.Text,
                IsIncoming = false,
                Status = MessageStatus.Sending
            };

            AddAndSendMessage(message);
        }

        private async System.Threading.Tasks.Task SendImageMessage(string caption)
        {
            string mimeType = "image/jpeg";
            string extension = _selectedImageFile == null ? null : _selectedImageFile.FileType.ToLower();
            if (extension == ".png") mimeType = "image/png";
            else if (extension == ".gif") mimeType = "image/gif";
            else if (extension == ".bmp") mimeType = "image/bmp";

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = caption ?? "",
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = MessageType.Image,
                IsIncoming = false,
                Status = MessageStatus.Sending,
                MediaData = _selectedImageBase64,
                MediaMimeType = mimeType,
                MediaFileName = _selectedImageFile == null ? null : _selectedImageFile.Name
            };

            // Decodifica locale: il mittente deve vedere la propria immagine
            await message.LoadMediaImageAsync();

            AddAndSendMessage(message);

            // Clear image preview
            ClearSelectedImage();
        }

        private async void AddAndSendMessage(ChatMessage message)
        {
            // DataService è l'unico punto di inserimento: _messages è la stessa
            // ObservableCollection osservata dal ListView.
            DataService.Instance.AddMessage(_contact.Id, message);
            MessageTextBox.Text = "";

            // Auto-scroll
            ScrollToMessage(message);

            // Lo stato si decide adesso, non quando la pagina e' stata aperta:
            // un messaggio scritto a socket caduto restava "inviato" per sempre
            // senza essere mai partito. Adesso si vede fallito e si puo'
            // riscrivere.
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

                // Un solo passaggio sul file: i byte servono sia per l'invio
                // (base64) sia per l'anteprima (bitmap). Prima il file veniva
                // letto due volte.
                byte[] buffer;
                using (var stream = await file.OpenReadAsync())
                {
                    using (var reader = new DataReader(stream))
                    {
                        uint size = (uint)stream.Size;
                        await reader.LoadAsync(size);
                        buffer = new byte[size];
                        reader.ReadBytes(buffer);
                    }
                }

                _selectedImageBase64 = System.Convert.ToBase64String(buffer);
                SelectedImagePreview.Source = await ImageHelper.FromBytesAsync(buffer);
                ImagePreviewBar.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/image", ex);
                Debug.WriteLine(
                    string.Format(Loc.Get("ChatPage_ImageError", "Could not open the image: {0}"), ex.Message));
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
