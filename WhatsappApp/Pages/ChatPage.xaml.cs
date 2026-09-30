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
        // L'allegato scelto: il nome del file copiato nella cartella dell'app,
        // piu' cio' che serve per spedirlo. Non i byte: un video intero in
        // memoria e' la cosa piu' pesante che questa pagina potrebbe tenere, ed
        // e' quello che faceva chiudere l'app condividendo un video (vedi
        // AttachmentInbox).
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

            // La foto e il nome sono due bersagli: il tooltip li distingue.
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

                // L'avatar e le iniziali vengono dal contatto: la pagina non li
                // ricostruisce.
                DataContext = contact;

                ContactNameText.Text = contact.Name;

                // Nessuna presenza: WhatsApp non la espone tramite il server che
                // usiamo, e dire "online" o "ultimo accesso alle HH:mm" era una
                // bugia. Resta l'unica cosa vera in piu' che abbiamo: il numero
                // della chat, quando il nome non e' gia' il numero.
                string number = DisplayNumber(contact.Id);
                bool hasNumber = !string.IsNullOrEmpty(number) && number != contact.Name;
                OnlineStatusText.Text = hasNumber ? number : "";
                OnlineStatusText.Visibility = hasNumber ? Visibility.Visible : Visibility.Collapsed;

                // Load messages: prima quelli sul telefono, cosi' la
                // conversazione si vede subito, poi la cronologia vera.
                _messages = DataService.Instance.GetMessages(contact.Id);
                MarkRead();
                MessagesListView.ItemsSource = _messages;
#pragma warning disable 4014
                LoadCachedMessagesAsync(contact.Id);
#pragma warning restore 4014

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

            // La fotografia della conversazione: e' l'uscita che la scrive,
            // non ogni messaggio, altrimenti scriverebbe un file a raffica.
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
        /// Un media di questa conversazione che non ha ancora i byte: e' una
        /// riga di cronologia (o un video di cui l'adapter non aveva il file),
        /// e si puo' chiedere al server. Il tipo dice che era un'immagine, un
        /// video, un audio o un documento; si possono chiedere tutti.
        /// </summary>
        private static bool Downloadable(ChatMessage message)
        {
            if (message == null) return false;

            // Solo cio' che e' arrivato da fuori ha un id che il server
            // conosce: un messaggio scritto qui porta un id locale, e
            // chiederlo al server sarebbe una richiesta senza risposta.
            if (!message.IsIncoming) return false;

            if (IsFileBacked(message.MediaType))
                return string.IsNullOrEmpty(message.MediaFilePath);

            if (string.Equals(message.MediaType, "image", StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrEmpty(message.MediaData);

            return false;
        }

        /// <summary>I tipi che arrivano su un file: un video, un audio, un documento.</summary>
        private static bool IsFileBacked(string mediaType)
        {
            return string.Equals(mediaType, "video", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "audio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mediaType, "document", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Riempie la conversazione con la copia sul telefono e scorre in fondo.
        /// Va atteso sul thread UI: la collezione e' quella legata alla lista.
        /// </summary>
        private async System.Threading.Tasks.Task LoadCachedMessagesAsync(string chatId)
        {
            await DataService.Instance.LoadCachedMessagesAsync(chatId);
            if (_messages.Count > 0) ScrollToMessage(_messages[_messages.Count - 1]);
        }

        private void RequestMedia(ChatMessage message)
        {
            if (!CommunicationService.Instance.IsConnected) return;
            // Il cerchio parte adesso: il primo pezzo puo' metterci, e senza
            // questo il tocco sembra non aver fatto niente.
            message.IsMediaLoading = true;
#pragma warning disable 4014
            CommunicationService.Instance.RequestMediaAsync(message.ChatId, message.Id);
#pragma warning restore 4014
        }

        /// <summary>
        /// La bolla decodifica a 320 px: ingrandirla a tutto schermo la lascia
        /// sfocata. Qui si decodifica alla misura dello schermo, e si
        /// restituisce chiudendo: sono i pixel piu' pesanti che questa pagina
        /// tiene, e non devono sopravvivere alla vista.
        /// </summary>
        private const int ViewerDecodePixels = 720;

        private async void Media_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ChatMessage;
            if (message == null) return;
            e.Handled = true;

            // Un video ha i byte su disco e si apre nel lettore; un'immagine ha
            // i byte in memoria e si apre a tutto schermo. Senza byte: si chiede
            // al server, e si aprira' al tocco successivo.
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
        /// La foto del profilo a tutto schermo: si decodifica alla misura dello
        /// schermo, come per una bolla, e si chiude toccandola (ImageViewer).
        /// Senza byte non si apre niente: non c'e' una richiesta da fare qui,
        /// l'elenco chat li ha gia' chiesti.
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

        /// <summary>Il nome apre le informazioni: la foto resta per la foto.</summary>
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

        // Vero quando cio' che suona e' un vocale: la scena e' la stessa, ma
        // la frase di errore no.
        private bool _playingAudio;

        /// <summary>
        /// Apre il media ricevuto nel lettore a tutto schermo. La sorgente e' il
        /// file locale (ms-appdata): il lettore lo apre per conto suo e non c'e'
        /// nessun flusso da tenere aperto per la vita della pagina. Vale per un
        /// video e per un vocale: per un audio la scena e' nera e restano i
        /// controlli di trasporto, che sono quelli di sistema.
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
        /// Apre un documento ricevuto con l'app che il telefono usa per quel
        /// tipo di file. Se non ce n'e' una, o il file non e' piu' li', lo dice
        /// invece di non fare niente.
        /// </summary>
        private async System.Threading.Tasks.Task OpenDocumentAsync(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) return;

            // C# 5 non lascia attendere dentro un catch: si prende nota del
            // guasto e si aspetta dopo, fuori dal blocco.
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
            // Un video che il telefono non sa decodificare. Prima si chiudeva lo
            // schermo e basta, quindi un guasto e un tocco a vuoto si vedevano
            // uguali; adesso resta la frase. In WP8.1 l'evento porta solo il
            // messaggio, non l'eccezione.
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

        /// <summary>Chiude il lettore. Sicura da chiamare anche a vuoto.</summary>
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
        /// Quanti BYTE si leggono per pezzo. E' un multiplo di 3: la sua base64
        /// e' quindi lunga esattamente (byte/3)*4 caratteri, senza padding, e i
        /// pezzi si concatenano in base64 senza ricodificare niente. 525000 byte
        /// fanno 700000 caratteri, come MediaChunkChars e come l'adapter.
        /// </summary>
        private const int MediaChunkBytes = 525000;

        /// <summary>
        /// Un allegato si manda a pezzi, letti dal file copiato nella cartella
        /// dell'app (vedi AttachmentInbox). Un frame ha un tetto di 8 MiB e il
        /// contenuto viaggia in base64, che aggiunge un terzo: un video non ci
        /// sta in un frame solo, e non ci sta nemmeno nella memoria del telefono.
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
                // I byte stanno su disco: qui c'e' solo dove trovarli.
                MediaFilePath = localFileName,
                MediaMimeType = mimeType,
                MediaFileName = fileName,
                MediaType = kind
            };

            // Decodifica locale: il mittente vede la propria immagine.
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
                        // ReadBytes legge byte crudi, quindi l'ordine dei byte
                        // non conta qui: si legge a pezzi e si codifica.
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
                // Un video non si decodifica: non c'e' niente da disegnare.
                SelectedImagePreview.Source = null;
            }
        }

        /// <summary>
        /// L'anteprima dell'immagine da spedire: la pagina e' larga 480 px, quindi
        /// 720 la copre anche a 1,5x senza decodificare il file intero.
        /// </summary>
        private const int PreviewDecodePixels = 720;

        /// <summary>Anteprima locale: il mittente vede la propria immagine.</summary>
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
