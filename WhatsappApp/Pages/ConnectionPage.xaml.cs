using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    public sealed partial class ConnectionPage : Page
    {
        private bool _isServerMode = false;
        private int _serverPort = 8585;
        private bool _isFirstRun;

        public ConnectionPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _isFirstRun = !SettingsService.HasSavedSettings;

            // Prefill the fields with the saved settings
            string savedAddress = SettingsService.ServerAddress;
            if (!string.IsNullOrEmpty(savedAddress))
            {
                ServerAddressBox.Text = savedAddress;
            }

            int savedPort = SettingsService.ServerPort;
            if (savedPort > 0)
            {
                ServerPortBox.Text = savedPort.ToString();
                _serverPort = savedPort;
                ServerPortValueText.Text = savedPort.ToString();
                ServerPortInfoText.Text = savedPort.ToString();
            }

            string savedUsername = SettingsService.Username;
            if (!string.IsNullOrEmpty(savedUsername))
            {
                UsernameBox.Text = savedUsername;
            }

            PageTitleText.Text = _isFirstRun ? "Prima configurazione" : "Impostazioni Server";

            CommunicationService.Instance.ConnectionStatusChanged += OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred += OnErrorOccurred;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.ConnectionStatusChanged -= OnConnectionStatusChanged;
            CommunicationService.Instance.ErrorOccurred -= OnErrorOccurred;
        }

        private void ClientMode_Checked(object sender, RoutedEventArgs e)
        {
            _isServerMode = false;
            ClientSettings.Visibility = Visibility.Visible;
            ServerSettings.Visibility = Visibility.Collapsed;
            ActionButton.Content = "Connetti al server";
        }

        private void ServerMode_Checked(object sender, RoutedEventArgs e)
        {
            _isServerMode = true;
            ClientSettings.Visibility = Visibility.Collapsed;
            ServerSettings.Visibility = Visibility.Visible;
            ActionButton.Content = "Avvia server";
            ServerPortValueText.Text = _serverPort.ToString();
            ServerPortInfoText.Text = _serverPort.ToString();
        }

        private async void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            string username = UsernameBox.Text?.Trim();
            if (string.IsNullOrEmpty(username))
            {
                username = "Utente";
                UsernameBox.Text = username;
            }

            StatusPanel.Visibility = Visibility.Visible;
            ActionButton.IsEnabled = false;

            if (_isServerMode)
            {
                StatusText.Text = $"Avvio server sulla porta {_serverPort}...";
                await CommunicationService.Instance.StartServerAsync(username, _serverPort);
            }
            else
            {
                string address = ServerAddressBox.Text?.Trim();
                if (string.IsNullOrEmpty(address)) address = "192.168.1.100";

                // Read the port from the client-mode port field
                int port = _serverPort;
                if (!string.IsNullOrEmpty(ServerPortBox.Text) &&
                    int.TryParse(ServerPortBox.Text.Trim(), out int boxPort))
                {
                    port = boxPort;
                    _serverPort = port;
                }

                StatusText.Text = $"Connessione a {address}:{port}...";
                bool connected = await CommunicationService.Instance.ConnectToServerAsync(
                    address, port, username);

                if (connected)
                {
                    // Remember the settings for next time
                    SettingsService.Save(address, port, username);

                    StatusText.Text = "Connesso!";
                    ActionButton.Visibility = Visibility.Collapsed;
                    DisconnectButton.Visibility = Visibility.Visible;

                    // Continue to the chat list (first run) or back to it (settings)
                    ContinueToMainPage();
                }
                else
                {
                    StatusText.Text = "Connessione fallita";
                    ActionButton.IsEnabled = true;
                }
            }
        }

        private void ContinueToMainPage()
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else
            {
                Frame.Navigate(typeof(MainPage));
            }
        }

        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            CommunicationService.Instance.Disconnect();
            StatusPanel.Visibility = Visibility.Collapsed;
            ActionButton.Visibility = Visibility.Visible;
            DisconnectButton.Visibility = Visibility.Collapsed;
            ActionButton.IsEnabled = true;
        }

        private void OnConnectionStatusChanged(object sender, string status)
        {
            StatusText.Text = status;
            if (status.Contains("Connesso") || status.Contains("avviato"))
            {
                ActionButton.Visibility = Visibility.Collapsed;
                DisconnectButton.Visibility = Visibility.Visible;
            }
        }

        private void OnErrorOccurred(object sender, string error)
        {
            StatusText.Text = error;
            ActionButton.IsEnabled = true;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
            else
            {
                // First run with no saved settings: allow skipping the setup
                ContinueToMainPage();
            }
        }

        private void ServerPortDown_Click(object sender, RoutedEventArgs e)
        {
            if (_serverPort > 1024)
            {
                _serverPort -= 1;
                ServerPortValueText.Text = _serverPort.ToString();
                ServerPortInfoText.Text = _serverPort.ToString();
            }
        }

        private void ServerPortUp_Click(object sender, RoutedEventArgs e)
        {
            if (_serverPort < 65535)
            {
                _serverPort += 1;
                ServerPortValueText.Text = _serverPort.ToString();
                ServerPortInfoText.Text = _serverPort.ToString();
            }
        }
    }
}
