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

        private Windows.System.Display.DisplayRequest _displayRequest;
        private bool _displayRequestActive;
        private Windows.UI.Xaml.DispatcherTimer _qrTimer;

        public ConnectionPage()
        {
            this.InitializeComponent();
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
            // Toggled also fires when IsOn is assigned: the guard avoids
            // rewriting the setting (and turning off the badge) just for reading it.
            _notificationsInitializing = true;
            NotificationsToggle.IsOn = SettingsService.NotificationsEnabled;
            // Same guard for the public-service switch: it also fires when IsOn
            // is assigned.
            PublicServerToggle.IsOn = SettingsService.UsePublicServer;
            UpdateAddressFieldsState();
            _notificationsInitializing = false;
        }

        private bool _notificationsInitializing;

        private void NotificationsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_notificationsInitializing) return;

            SettingsService.NotificationsEnabled = NotificationsToggle.IsOn;
            if (!NotificationsToggle.IsOn) NotificationService.SetUnread(0);
        }

        /// <summary>
        /// Public service or private server. The first has no address to type (it
        /// arrives on its own from the file on GitHub), so with the switch on the
        /// address and port fields turn off instead of sitting there suggesting
        /// they are needed.
        /// </summary>
        private void PublicServerToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_notificationsInitializing) return;

            SettingsService.UsePublicServer = PublicServerToggle.IsOn;
            UpdateAddressFieldsState();
        }

        private void UpdateAddressFieldsState()
        {
            bool manual = !PublicServerToggle.IsOn;
            ServerAddressBox.IsEnabled = manual;
            ServerPortBox.IsEnabled = manual;
            ManualToggleButton.IsEnabled = manual;
            if (!manual) ManualPanel.Visibility = Visibility.Collapsed;
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

            string savedToken = SettingsService.Token;
            if (!string.IsNullOrEmpty(savedToken))
                TokenBox.Text = savedToken;

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
            CommunicationService.Instance.ServerUnavailable += OnServerUnavailable;

            if (CommunicationService.Instance.IsConnected)
            {
                ShowConnectedState();

                // Two separate requests. The state paints the panel; the QR is
                // needed because the connection may have been opened by the
                // automatic start, before this page existed: in that case
                // ConnectionEstablished has already passed and nobody ever asked
                // for the code. The login is done from the phone, so the code is
                // requested on its own on every entry.
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ConnectionPage/status",
                    CommunicationService.Instance.SendControlAsync("status"));
#pragma warning restore 4014
                if (CommunicationService.Instance.WhatsAppState != "connected")
                {
#pragma warning disable 4014
                    Guarded.RunGuardedAsync("ConnectionPage/login.qr",
                        CommunicationService.Instance.SendControlAsync("login.qr"));
#pragma warning restore 4014
                }
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.ConnectionStatusChanged -= OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred -= OnErrorOccurred;
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
            CommunicationService.Instance.ConnectionEstablished -= OnConnectionEstablished;
            CommunicationService.Instance.ServerUnavailable -= OnServerUnavailable;
            DiscoveryService.Instance.ServersChanged -= OnServersChanged;
            StopQrTimer();
            CloseQrOverlay();
        }

        private void OnConnectionEstablished(object sender, EventArgs e)
        {
            ShowConnectedState();

            // The login is done from the phone: the code is requested at once,
            // without the user having to look for the button.
            if (CommunicationService.Instance.WhatsAppState != "connected")
            {
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ConnectionPage/login.qr",
                    CommunicationService.Instance.SendControlAsync("login.qr"));
#pragma warning restore 4014
            }
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            // The public service has no address to type: the switch is the whole
            // configuration. Without this branch the button would connect to the
            // LAN address in the box, which is empty on a first run, and the login
            // panel would never appear.
            if (PublicServerToggle.IsOn)
            {
                await ConnectToPublicServerAsync();
                return;
            }

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
        /// The public service: the address is read from the file on GitHub instead
        /// of typed, because the tunnel that exposes it changes port. AutoConnector
        /// owns that step, so the button and the automatic start cannot disagree
        /// about what the public server is.
        /// </summary>
        private async Task ConnectToPublicServerAsync()
        {
            string username = EnsureUsername();
            KeepTypedToken();

            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = false;
            StatusText.Text = Loc.Get("ConnectionPage_ConnectingPublic",
                "Connecting to the public server...");

            bool connected = await AutoConnector.Instance.TryConnectAsync(username, 8);
            if (connected)
            {
                StatusText.Text = Loc.Get("ConnectionPage_Connected", "Connected!");
                ShowConnectedState();
                await CommunicationService.Instance.SendControlAsync("status");
            }
            else
            {
                // AutoConnector has already said why, through ServerUnavailable.
                ActionButton.IsEnabled = true;
            }
        }

        /// <summary>
        /// The name to send: the one in the box, or the default, put back in the box
        /// so the user sees what was used.
        /// </summary>
        private string EnsureUsername()
        {
            string username = (UsernameBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(username))
            {
                username = Loc.Get("ConnectionPage_DefaultUsername", "User");
                UsernameBox.Text = username;
            }
            return username;
        }

        /// <summary>
        /// Writes down the token typed by hand, if there is one. An empty box is not
        /// an order to forget the token: the server may have handed this phone one,
        /// and that copy is the only one that exists.
        /// </summary>
        private void KeepTypedToken()
        {
            string typed = (TokenBox.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(typed)) SettingsService.Token = typed;
        }

        /// <summary>
        /// The only place where the connection is opened: the button, the list of
        /// found servers and the automatic reconnection all use it.
        /// </summary>
        private async Task ConnectAsync(string address, int port)
        {
            string username = EnsureUsername();

            // The token is read now: the first frame after the connection is what
            // carries it, so changing it later would not send it.
            KeepTypedToken();

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

        // ─── Automatic server discovery ──────────────────────────────────────

        /// <summary>
        /// Opens the beacon listener and, if nothing is saved, tries to connect
        /// to the single adapter that announces itself. Manual entry stays anyway:
        /// some networks filter UDP.
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

            // First "looking", then "found nothing": two different states,
            // because the user must know when to stop waiting.
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

            // A single server in view and no connection: we go there on our own.
            if (found.Count == 1 && !_autoConnectTried && !CommunicationService.Instance.IsConnected)
            {
                _autoConnectTried = true;
#pragma warning disable 4014
                Guarded.RunGuardedAsync("ConnectionPage/auto connect",
                    ConnectAsync(found[0].Address, found[0].Port));
#pragma warning restore 4014
            }
        }

        /// <summary>
        /// Rebuilding the list on every beacon (one every 2 seconds) would lose
        /// the selection and the scroll: it is touched only when it changes.
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
                    CloseQrOverlay();
                    StopQrTimer();
                    break;

                case "waiting":
                    WhatsAppStateText.Text = Loc.Get("ConnectionPage_Waiting",
                        "Waiting for pairing: follow the instructions below.");
                    LoginQrButton.Visibility = Visibility.Visible;
                    PhoneBox.Visibility = Visibility.Visible;
                    LoginCodeButton.Visibility = Visibility.Visible;
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
                    StopQrTimer();
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
                    QrOverlayImage.Visibility = Visibility.Collapsed;
                    QrOverlayCodeText.Text = message.PairCode;
                    OpenQrOverlay();
                    break;

                case "registered":
                    // The service created this phone's token on its first
                    // connection: it is shown, because it is the only copy and the
                    // user may want to keep it.
                    TokenBox.Text = message.Token;
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
                var bitmap = await BitmapFromBase64Async(base64);
                QrImage.Source = bitmap;
                QrOverlayImage.Source = bitmap;
                QrImage.Visibility = Visibility.Visible;

                PairCodeText.Text = "";
                QrOverlayCodeText.Text = "";
                QrOverlayHintText.Text = Loc.Get("ConnectionPage_QrOverlayHint",
                    "WhatsApp, Linked devices, Link a device, then scan. The screen stays on while this page is open.");
                QrInfoText.Text = duration > 0
                    ? string.Format(Loc.Get("ConnectionPage_QrHintDuration",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code (valid for about {0} seconds)."),
                        duration)
                    : Loc.Get("ConnectionPage_QrHint",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code.");

                OpenQrOverlay();
                ScheduleQrRefresh(duration);
            }
            catch (Exception ex)
            {
                Diag.Failed("ShowQrCode", ex);
                QrInfoText.Text = string.Format(
                    Loc.Get("ConnectionPage_QrError", "Could not show the QR code: {0}"), ex.Message);
            }
        }

        // ─── Code screen (the login is done from the phone) ──────────────

        private void OpenQrOverlay()
        {
            QrOverlayImage.Visibility = string.IsNullOrEmpty(QrOverlayCodeText.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
            QrOverlay.Visibility = Visibility.Visible;
            KeepScreenOn();
        }

        private void CloseQrOverlay()
        {
            QrOverlay.Visibility = Visibility.Collapsed;
            ReleaseScreenOn();
        }

        /// <summary>
        /// The screen stays on: if it turns off or dims while the code is on
        /// screen, the code becomes unreadable and the scan fails with no error
        /// message at all.
        ///
        /// All inside the try, construction included: creating the request can
        /// fail on the platform, and a convenience must never stop the code from
        /// being shown. The construction used to sit outside the try, and the
        /// exception escaped the control-frame handler, where nobody catches it.
        /// </summary>
        private void KeepScreenOn()
        {
            if (_displayRequestActive) return;
            try
            {
                if (_displayRequest == null) _displayRequest = new Windows.System.Display.DisplayRequest();
                _displayRequest.RequestActive();
                _displayRequestActive = true;
            }
            catch (Exception ex)
            {
                // Active-request limit reached, or member not implemented on this
                // phone: we carry on without.
                Diag.Failed("KeepScreenOn", ex);
            }
        }

        private void ReleaseScreenOn()
        {
            if (!_displayRequestActive || _displayRequest == null) return;
            try { _displayRequest.RequestRelease(); }
            catch (Exception ex) { Diag.Failed("ReleaseScreenOn", ex); }
            _displayRequestActive = false;
        }

        /// <summary>
        /// Asks for a new code shortly before it expires: the old one is no
        /// longer valid and the app would keep it on screen forever.
        /// </summary>
        private void ScheduleQrRefresh(int duration)
        {
            StopQrTimer();

            int seconds = duration > 10 ? duration - 5 : 10;
            _qrTimer = new Windows.UI.Xaml.DispatcherTimer();
            _qrTimer.Interval = TimeSpan.FromSeconds(seconds);
            _qrTimer.Tick += OnQrTimerTick;
            _qrTimer.Start();
        }

        private void StopQrTimer()
        {
            if (_qrTimer == null) return;
            _qrTimer.Stop();
            _qrTimer.Tick -= OnQrTimerTick;
            _qrTimer = null;
        }

        private async void OnQrTimerTick(object sender, object e)
        {
            StopQrTimer();
            if (!CommunicationService.Instance.IsConnected) return;
            if (CommunicationService.Instance.WhatsAppState == "connected") return;

            QrOverlayHintText.Text = Loc.Get("ConnectionPage_QrOverlayRefreshing", "Refreshing the code...");
            await CommunicationService.Instance.SendControlAsync("login.qr");
        }

        private void QrOverlayCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseQrOverlay();
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

        private void ContinueToChatsPage()
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(ChatsPage));
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            // After a deliberate disconnect, nothing reconnects on its own.
            _autoConnectTried = true;
            CommunicationService.Instance.Disconnect();
            StatusPanel.Visibility = Visibility.Collapsed;
            WhatsAppPanel.Visibility = Visibility.Collapsed;
            ActionButton.Visibility = Visibility.Visible;
            DisconnectButton.Visibility = Visibility.Collapsed;
            ActionButton.IsEnabled = true;
        }

        /// <summary>
        /// Shows only the message: whether the connection succeeded is said by
        /// the ConnectionEstablished event, not the text (which is localized).
        /// </summary>
        private void OnConnectionStatusChanged(object sender, string status)
        {
            StatusText.Text = status;
            StatusPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// The main server did not answer after the automatic attempts. The
        /// sentence is its own, not a socket failure to explain.
        /// </summary>
        private void OnServerUnavailable(object sender, EventArgs e)
        {
            StatusText.Text = Loc.Get("CommService_ServerUnavailable",
                "Server non disponibile, riprova tra qualche minuto :)");
        }

        private void OnErrorOccurred(object sender, string error)
        {
            StatusText.Text = error;
            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = true;
        }

        private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(DiagnosticsPage));
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
