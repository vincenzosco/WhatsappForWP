using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    public sealed partial class ConnectionPage : Page
    {
        private bool _isFirstRun;

        public ConnectionPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _isFirstRun = !SettingsService.HasSavedSettings;

            string savedAddress = SettingsService.ServerAddress;
            if (!string.IsNullOrEmpty(savedAddress))
                ServerAddressBox.Text = savedAddress;

            int savedPort = SettingsService.ServerPort;
            if (savedPort > 0)
                ServerPortBox.Text = savedPort.ToString();

            string savedUsername = SettingsService.Username;
            if (!string.IsNullOrEmpty(savedUsername))
                UsernameBox.Text = savedUsername;

            PageTitleText.Text = _isFirstRun ? "Prima configurazione" : "Impostazioni Server";

            CommunicationService.Instance.ConnectionStatusChanged += OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred += OnErrorOccurred;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;

            if (CommunicationService.Instance.IsConnected)
            {
                ShowConnectedState();
                // OnNavigatedTo is not async: fire the status request and ignore the task
                CommunicationService.Instance.SendControlAsync("status");
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.ConnectionStatusChanged -= OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred -= OnErrorOccurred;
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            string username = (UsernameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(username))
            {
                username = "Utente";
                UsernameBox.Text = username;
            }

            string address = (ServerAddressBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(address)) address = "192.168.1.100";

            int port = 8585;
            int boxPort;
            if (!string.IsNullOrEmpty(ServerPortBox.Text) &&
                int.TryParse(ServerPortBox.Text.Trim(), out boxPort))
            {
                port = boxPort;
            }

            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = false;
            StatusText.Text = "Connessione a " + address + ":" + port + "...";

            bool connected = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
            if (connected)
            {
                SettingsService.Save(address, port, username);
                StatusText.Text = "Connesso!";
                ShowConnectedState();
                await CommunicationService.Instance.SendControlAsync("status");
            }
            else
            {
                StatusText.Text = "Connessione fallita";
                ActionButton.IsEnabled = true;
            }
        }

        private void ShowConnectedState()
        {
            ActionButton.Visibility = Visibility.Collapsed;
            DisconnectButton.Visibility = Visibility.Visible;
            WhatsAppPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = true;
            UpdateLoginUi(CommunicationService.Instance.WhatsAppState, CommunicationService.Instance.AccountJid);
        }

        private void UpdateLoginUi(string state, string accountJid)
        {
            switch (state)
            {
                case "connected":
                    WhatsAppStateText.Text = string.IsNullOrEmpty(accountJid)
                        ? "WhatsApp connesso!"
                        : "Connesso come " + accountJid.Split('@')[0];
                    LoginQrButton.Visibility = Visibility.Collapsed;
                    QrImage.Visibility = Visibility.Collapsed;
                    PhoneBox.Visibility = Visibility.Collapsed;
                    LoginCodeButton.Visibility = Visibility.Collapsed;
                    QrInfoText.Text = "";
                    PairCodeText.Text = "";
                    ContinueButton.IsEnabled = true;
                    break;

                case "waiting":
                    WhatsAppStateText.Text = "In attesa di abbinamento... segui le istruzioni qui sotto.";
                    LoginQrButton.Visibility = Visibility.Visible;
                    PhoneBox.Visibility = Visibility.Visible;
                    LoginCodeButton.Visibility = Visibility.Visible;
                    ContinueButton.IsEnabled = false;
                    break;

                default:
                    WhatsAppStateText.Text = "Non connesso a WhatsApp. Accedi con QR code o con il numero.";
                    LoginQrButton.Visibility = Visibility.Visible;
                    PhoneBox.Visibility = Visibility.Visible;
                    LoginCodeButton.Visibility = Visibility.Visible;
                    QrImage.Visibility = Visibility.Collapsed;
                    PairCodeText.Text = "";
                    QrInfoText.Text = "";
                    ContinueButton.IsEnabled = false;
                    break;
            }
        }

        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null) return;

            switch (message.Command)
            {
                case "state":
                    UpdateLoginUi(message.State, message.AccountJid);
                    break;

                case "qr":
                    ShowQrCode(message.QrImageData, message.QrDuration);
                    break;

                case "paircode":
                    PairCodeText.Text = "Codice: " + message.PairCode;
                    WhatsAppStateText.Text = "Inserisci questo codice su WhatsApp > Dispositivi collegati > Collega un dispositivo > Collega con numero di telefono.";
                    QrImage.Visibility = Visibility.Collapsed;
                    break;

                case "error":
                    WhatsAppStateText.Text = message.Text;
                    break;
            }
        }

        private async void ShowQrCode(string base64, int duration)
        {
            if (string.IsNullOrEmpty(base64))
            {
                QrInfoText.Text = "QR code non disponibile.";
                return;
            }

            try
            {
                QrImage.Source = await BitmapFromBase64Async(base64);
                QrImage.Visibility = Visibility.Visible;
                PairCodeText.Text = "";
                QrInfoText.Text = duration > 0
                    ? "Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice (valido ~" + duration + "s)."
                    : "Apri WhatsApp > Dispositivi collegati > Collega un dispositivo e inquadra il codice.";
            }
            catch (Exception ex)
            {
                QrInfoText.Text = "Impossibile mostrare il QR code: " + ex.Message;
            }
        }

        private static async Task<BitmapImage> BitmapFromBase64Async(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }
                var bitmap = new BitmapImage();
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }

        private async void LoginQrButton_Click(object sender, RoutedEventArgs e)
        {
            PairCodeText.Text = "";
            QrInfoText.Text = "Richiesta del QR code in corso...";
            await CommunicationService.Instance.SendControlAsync("login.qr");
        }

        private async void LoginCodeButton_Click(object sender, RoutedEventArgs e)
        {
            string phone = (PhoneBox.Text ?? "").Trim();
            phone = phone.Replace("+", "").Replace(" ", "").Replace("-", "");
            if (phone.Length < 6 || !phone.All(char.IsDigit))
            {
                WhatsAppStateText.Text = "Inserisci un numero valido con prefisso internazionale (es. 393401234567).";
                return;
            }

            QrImage.Visibility = Visibility.Collapsed;
            QrInfoText.Text = "";
            WhatsAppStateText.Text = "Richiesta del codice in corso...";
            await CommunicationService.Instance.SendControlAsync("login.code", phone);
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            ContinueToMainPage();
        }

        private void ContinueToMainPage()
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(MainPage));
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            CommunicationService.Instance.Disconnect();
            StatusPanel.Visibility = Visibility.Collapsed;
            WhatsAppPanel.Visibility = Visibility.Collapsed;
            ActionButton.Visibility = Visibility.Visible;
            DisconnectButton.Visibility = Visibility.Collapsed;
            ActionButton.IsEnabled = true;
        }

        private void OnConnectionStatusChanged(object sender, string status)
        {
            StatusText.Text = status;
            StatusPanel.Visibility = Visibility.Visible;
            if (status != null && (status.Contains("Connesso") || status.Contains("avviato")))
                ShowConnectedState();
        }

        private void OnErrorOccurred(object sender, string error)
        {
            StatusText.Text = error;
            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = true;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                ContinueToMainPage();
        }
    }
}
