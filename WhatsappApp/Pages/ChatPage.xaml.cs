using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
        // L'immagine scelta: i byte, piu' cio' che serve per inviarla. Non il
        // StorageFile: dopo il selettore il file puo' appartenere a un processo
        // che non c'e' piu' (vedi AttachmentInbox).
        private string _selectedImageBase64;
        private string _selectedImageFileName;
        private string _selectedImageMimeType;
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
                MarkRead();
                MessagesListView.ItemsSource = _messages;

                // Auto-scroll to bottom
                if (_messages.Count > 0)
                    ScrollToMessage(_messages[_messages.Count - 1]);

                // Da qui in poi i messaggi di questa chat sono gia' letti
                DataService.Instance.ActiveChatId = contact.Id;

                // La cronologia della chat: l'adapter risponde con i messaggi
                // vecchi, marcati IsHistory, e si chiede una volta per chat per
                // sessione. Senza questa richiesta una conversazione appena
                // aperta resta vuota finche' non arriva qualcosa di nuovo.
                if (CommunicationService.Instance.IsConnected
                    && DataService.Instance.MarkHistoryRequested(contact.Id))
                {
#pragma warning disable 4014
                    CommunicationService.Instance.SendControlAsync("messages", contact.Id);
#pragma warning restore 4014
                }

                // Listen for new messages
                CommunicationService.Instance.MessageReceived += OnMessageReceived;

                // Un'immagine arrivata da fuori puo' essere arrivata mentre
                // questa pagina non c'era (processo riavviato): si ritira qui, e
                // da qui in poi anche all'arrivo.
                AttachmentInbox.Ready += OnAttachmentReady;
                ShowPendingAttachment();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.MessageReceived -= OnMessageReceived;
            AttachmentInbox.Ready -= OnAttachmentReady;
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
            if (message.ChatId != _contact.Id) return;

            ScrollToMessage(message);

            // Questo handler vive solo mentre questa pagina e' quella davanti
            // (si aggancia in OnNavigatedTo e si stacca in OnNavigatedFrom),
            // quindi un messaggio che arriva qui e' un messaggio che l'utente
            // sta vedendo scorrere: e' letto adesso, come su WhatsApp. Il
            // numero sulla riga si azzera per questo, non per un'esclusione nel
            // contatore.
            MarkRead();
        }

        /// <summary>
        /// Questa conversazione e' stata mostrata: il numero si azzera qui e sul
        /// server. Il secondo pezzo non e' un dettaglio: l'adapter conta ogni
        /// messaggio in arrivo, anche quelli che l'utente sta guardando, quindi
        /// senza dirglielo il numero tornerebbe a comparire al prossimo
        /// aggiornamento dell'elenco.
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

            // If we have a selected image, send it as an image message
            if (_selectedImageBase64 != null)
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
            // Il tipo lo decide chi ha consegnato l'immagine: AttachmentInbox lo
            // ricava dall'estensione una volta sola.
            string mimeType = _selectedImageMimeType ?? "image/jpeg";

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
                MediaFileName = _selectedImageFileName
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
        /// Pulsante allegato: chiede il selettore di sistema. La risposta non
        /// arriva qui - arriva ad App.OnActivated dopo che l'app e' stata
        /// riattivata - quindi non c'e' niente da attendere.
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
        /// Un allegato e' arrivato mentre questa chat era aperta: e' il caso
        /// normale, perche' il selettore si apre da qui e l'app torna qui.
        /// </summary>
        private void OnAttachmentReady()
        {
            ShowPendingAttachment();
        }

        /// <summary>
        /// Mostra l'allegato in attesa, se c'e'. Chiamato sia navigando qui sia
        /// all'arrivo: dopo il selettore la pagina e' ancora quella davanti e
        /// OnNavigatedTo non viene richiamato.
        /// </summary>
        private void ShowPendingAttachment()
        {
            if (!AttachmentInbox.HasAttachment) return;

            string note = AttachmentInbox.Note;
            _selectedImageBase64 = AttachmentInbox.Base64;
            _selectedImageFileName = AttachmentInbox.FileName;
            _selectedImageMimeType = AttachmentInbox.MimeType;
            AttachmentInbox.Clear();

            if (!string.IsNullOrEmpty(note) && string.IsNullOrEmpty(MessageTextBox.Text))
            {
                MessageTextBox.Text = note;
            }

            ImagePreviewBar.Visibility = Visibility.Visible;
#pragma warning disable 4014
            ShowLocalPreviewAsync(_selectedImageBase64);
#pragma warning restore 4014
        }

        /// <summary>
        /// L'anteprima dell'immagine da spedire: la pagina e' larga 480 px, quindi
        /// 720 la copre anche a 1,5x senza decodificare il file intero.
        /// </summary>
        private const int PreviewDecodePixels = 720;

        /// <summary>Anteprima locale: il mittente vede la propria immagine.</summary>
        private async System.Threading.Tasks.Task ShowLocalPreviewAsync(string base64)
        {
            SelectedImagePreview.Source = await ImageHelper.FromBase64Async(base64, PreviewDecodePixels);
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
            _selectedImageBase64 = null;
            _selectedImageFileName = null;
            _selectedImageMimeType = null;
            SelectedImagePreview.Source = null;
            ImagePreviewBar.Visibility = Visibility.Collapsed;
        }
    }
}
