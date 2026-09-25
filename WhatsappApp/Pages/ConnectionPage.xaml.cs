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

        private readonly System.Collections.ObjectModel.ObservableCollection<DiscoveredServer> _servers =
            new System.Collections.ObjectModel.ObservableCollection<DiscoveredServer>();

        private bool _autoConnectTried;
        private DateTime _discoveryStartedAt;

        public ConnectionPage()
        {
            this.InitializeComponent();
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
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

            ServersList.ItemsSource = _servers;
            _discoveryStartedAt = DateTime.Now;
            DiscoveryService.Instance.ServersChanged += OnServersChanged;
            StartDiscovery();

            PageTitleText.Text = _isFirstRun
                ? Loc.Get("ConnectionPage_FirstRunTitle", "First-time setup")
                : Loc.Get("ConnectionPage_SettingsTitle", "Server settings");

            CommunicationService.Instance.ConnectionStatusChanged += OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred += OnErrorOccurred;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
            CommunicationService.Instance.ConnectionEstablished += OnConnectionEstablished;

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
            CommunicationService.Instance.ConnectionEstablished -= OnConnectionEstablished;
            DiscoveryService.Instance.ServersChanged -= OnServersChanged;
        }

        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            ShowConnectedState();
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            string address = (ServerAddressBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(address)) address = SettingsService.DefaultAddress;

            int port = 8585;
            int boxPort;
            if (!string.IsNullOrEmpty(ServerPortBox.Text) &&
                int.TryParse(ServerPortBox.Text.Trim(), out boxPort))
            {
                port = boxPort;
            }

            await ConnectAsync(address, port);
        }

        /// <summary>
        /// Unico punto in cui si apre la connessione: lo usano il pulsante, la
        /// lista dei server trovati e la riconnessione automatica.
        /// </summary>
        private async Task ConnectAsync(string address, int port)
        {
            string username = (UsernameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(username))
            {
                username = Loc.Get("ConnectionPage_DefaultUsername", "User");
                UsernameBox.Text = username;
            }

            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = false;
            StatusText.Text = string.Format(
                Loc.Get("ConnectionPage_Connecting", "Connecting to {0}:{1}..."), address, port);

            bool connected = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
            if (connected)
            {
                SettingsService.Save(address, port, username);
                StatusText.Text = Loc.Get("ConnectionPage_Connected", "Connected!");
                ShowConnectedState();
                await CommunicationService.Instance.SendControlAsync("status");
            }
            else
            {
                StatusText.Text = Loc.Get("ConnectionPage_ConnectFailed", "Connection failed");
                ActionButton.IsEnabled = true;
            }
        }

        // ─── Scoperta automatica del server ──────────────────────────────────

        /// <summary>
        /// Apre l'ascolto dei beacon e, se non c'e' nulla di salvato, prova a
        /// connettersi all'unico adapter che si annuncia. Resta comunque
        /// l'inserimento manuale: ci sono reti che filtrano l'UDP.
        /// </summary>
        private async void StartDiscovery()
        {
            await DiscoveryService.Instance.StartAsync();
            RefreshServers();

            if (_autoConnectTried || CommunicationService.Instance.IsConnected) return;
            _autoConnectTried = true;
            if (!await AutoConnector.Instance.TryConnectAsync(UsernameBox.Text ?? "", 6))
            {
                RefreshServers();
            }
        }

        private async void OnServersChanged(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, RefreshServers);
        }

        private void RefreshServers()
        {
            System.Collections.Generic.List<DiscoveredServer> found = DiscoveryService.Instance.Snapshot();

            // Prima "sto cercando", poi "non ho trovato niente": due stati
            // diversi, perche' l'utente deve sapere quando smettere di aspettare.
            bool searching = found.Count == 0
                && DiscoveryService.Instance.IsListening
                && (DateTime.Now - _discoveryStartedAt).TotalSeconds < 8;
            DiscoveryStatusText.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
            NoServerText.Visibility = found.Count == 0 && !searching
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (!SameServers(found))
            {
                _servers.Clear();
                foreach (DiscoveredServer server in found) _servers.Add(server);
            }
            ServersList.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Un solo server in vista e nessuna connessione: ci si va da soli.
            if (found.Count == 1 && !_autoConnectTried && !CommunicationService.Instance.IsConnected)
            {
                _autoConnectTried = true;
#pragma warning disable 4014
                ConnectAsync(found[0].Address, found[0].Port);
#pragma warning restore 4014
            }
        }

        /// <summary>
        /// Ricostruire la lista a ogni beacon (uno ogni 2 secondi) farebbe
        /// perdere la selezione e lo scorrimento: si tocca solo se cambia.
        /// </summary>
        private bool SameServers(System.Collections.Generic.List<DiscoveredServer> found)
        {
            if (found.Count != _servers.Count) return false;
            for (int i = 0; i < found.Count; i++)
            {
                if (found[i].Endpoint != _servers[i].Endpoint) return false;
                if (found[i].DisplayName != _servers[i].DisplayName) return false;
                if (found[i].State != _servers[i].State) return false;
            }
            return true;
        }

        private async void ServersList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var server = e.ClickedItem as DiscoveredServer;
            if (server == null) return;

            ServerAddressBox.Text = server.Address;
            ServerPortBox.Text = server.Port.ToString();
            await ConnectAsync(server.Address, server.Port);
        }

        private void ManualToggleButton_Click(object sender, RoutedEventArgs e)
        {
            ManualPanel.Visibility = ManualPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
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
                        ? Loc.Get("ConnectionPage_WhatsAppConnected", "WhatsApp connected!")
                        : string.Format(Loc.Get("ConnectionPage_ConnectedAs", "Connected as {0}"),
                            accountJid.Split('@')[0]);
                    LoginQrButton.Visibility = Visibility.Collapsed;
                    QrImage.Visibility = Visibility.Collapsed;
                    PhoneBox.Visibility = Visibility.Collapsed;
                    LoginCodeButton.Visibility = Visibility.Collapsed;
                    QrInfoText.Text = "";
                    PairCodeText.Text = "";
                    ContinueButton.IsEnabled = true;
                    break;

                case "waiting":
                    WhatsAppStateText.Text = Loc.Get("ConnectionPage_Waiting",
                        "Waiting for pairing: follow the instructions below.");
                    LoginQrButton.Visibility = Visibility.Visible;
                    PhoneBox.Visibility = Visibility.Visible;
                    LoginCodeButton.Visibility = Visibility.Visible;
                    ContinueButton.IsEnabled = false;
                    break;

                default:
                    WhatsAppStateText.Text = Loc.Get("ConnectionPage_WhatsAppDisconnected",
                        "Not connected to WhatsApp. Sign in with the QR code or your phone number.");
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
                    PairCodeText.Text = string.Format(Loc.Get("ConnectionPage_PairCode", "Code: {0}"),
                        message.PairCode);
                    WhatsAppStateText.Text = Loc.Get("ConnectionPage_PairCodeHint",
                        "Enter this code in WhatsApp: Linked devices, Link a device, Link with phone number instead.");
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
                QrInfoText.Text = Loc.Get("ConnectionPage_QrUnavailable", "QR code not available.");
                return;
            }

            try
            {
                QrImage.Source = await BitmapFromBase64Async(base64);
                QrImage.Visibility = Visibility.Visible;
                PairCodeText.Text = "";
                QrInfoText.Text = duration > 0
                    ? string.Format(Loc.Get("ConnectionPage_QrHintDuration",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code (valid for about {0} seconds)."),
                        duration)
                    : Loc.Get("ConnectionPage_QrHint",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code.");
            }
            catch (Exception ex)
            {
                QrInfoText.Text = string.Format(
                    Loc.Get("ConnectionPage_QrError", "Could not show the QR code: {0}"), ex.Message);
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
            QrInfoText.Text = Loc.Get("ConnectionPage_RequestingQr", "Requesting the QR code...");
            await CommunicationService.Instance.SendControlAsync("login.qr");
        }

        private async void LoginCodeButton_Click(object sender, RoutedEventArgs e)
        {
            string phone = (PhoneBox.Text ?? "").Trim();
            phone = phone.Replace("+", "").Replace(" ", "").Replace("-", "");
            if (phone.Length < 6 || !phone.All(char.IsDigit))
            {
                WhatsAppStateText.Text = Loc.Get("ConnectionPage_InvalidPhone",
                    "Enter a valid number with country code (e.g. 393401234567).");
                return;
            }

            QrImage.Visibility = Visibility.Collapsed;
            QrInfoText.Text = "";
            WhatsAppStateText.Text = Loc.Get("ConnectionPage_RequestingCode", "Requesting the code...");
            await CommunicationService.Instance.SendControlAsync("login.code", phone);
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            ContinueToChatsPage();
        }

        private void ContinueToChatsPage()
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(ChatsPage));
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            // Dopo una disconnessione voluta non ci si riconnette da soli.
            _autoConnectTried = true;
            CommunicationService.Instance.Disconnect();
            StatusPanel.Visibility = Visibility.Collapsed;
            WhatsAppPanel.Visibility = Visibility.Collapsed;
            ActionButton.Visibility = Visibility.Visible;
            DisconnectButton.Visibility = Visibility.Collapsed;
            ActionButton.IsEnabled = true;
        }

        /// <summary>
        /// Mostra solo il messaggio: se la connessione e' riuscita lo dice
        /// l'evento ConnectionEstablished, non il testo (che e' localizzato).
        /// </summary>
        private void OnConnectionStatusChanged(object sender, string status)
        {
            StatusText.Text = status;
            StatusPanel.Visibility = Visibility.Visible;
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
                ContinueToChatsPage();
        }
    }
}
