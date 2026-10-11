using System;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The account screen: the number this phone is linked as, and the way back to the
    /// code that linked it.
    ///
    /// It used to be the server settings page - address, port, name, token, the list of
    /// adapters found on the network, connect and disconnect. None of that belongs on a
    /// screen a person opens to see which account they are on: the app finds its own way
    /// to the service (AutoConnector, at start-up and from the watchdog), and what is
    /// worth showing here is the number and the QR code that produced it.
    ///
    /// The number comes from the credential store first, because the native client is
    /// what holds the account now; the service's own answer is the fallback, so a phone
    /// linked through the adapter still shows something true.
    /// </summary>
    public sealed partial class ConnectionPage : Page
    {
        private readonly CredentialStore _store = new CredentialStore(new NativeCredentialStorage());

        private Windows.System.Display.DisplayRequest _displayRequest;
        private bool _displayRequestActive;
        private Windows.UI.Xaml.DispatcherTimer _qrTimer;

        public ConnectionPage()
        {
            this.InitializeComponent();
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            PageTitleText.Text = Loc.Get("ConnectionPage_AccountTitle", "Account");
            ShowNumber();
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;

            // The code is asked of the service, so a phone that is not connected says
            // so instead of offering a button that cannot do anything.
            if (!CommunicationService.Instance.IsConnected)
            {
                QrStatusText.Text = Loc.Get("ConnectionPage_QrUnavailable", "QR code not available.");
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            CommunicationService.Instance.ControlMessageReceived -= OnControlMessageReceived;
            CloseQrOverlay();
        }

        /// <summary>
        /// The number, from the store when there is one and from the service when there
        /// is not. A phone with neither is not linked, and the screen says that rather
        /// than showing an empty card where a number should be.
        /// </summary>
        private void ShowNumber()
        {
            string number = StoredNumber();
            if (string.IsNullOrEmpty(number))
            {
                number = DisplayNumber(CommunicationService.Instance.AccountJid);
            }

            bool known = !string.IsNullOrEmpty(number);

            NumberPanel.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
            AccountHintText.Visibility = known ? Visibility.Collapsed : Visibility.Visible;
            if (known) AccountNumberText.Text = number;
        }

        /// <summary>What this phone itself is linked as, or null when nothing is stored.</summary>
        private string StoredNumber()
        {
            if (!_store.HasSession) return null;

            try
            {
                return DisplayNumber(_store.Load().Jid);
            }
            catch (ProtocolException error)
            {
                // HasSession said there is an account and Load disagreed: a store that
                // changed between the two reads is not worth an exception on a screen
                // that only reads it.
                Diag.Failed("ConnectionPage/Load", error);
                return null;
            }
        }

        /// <summary>
        /// A JID the way a person reads a number: the user part, without the device and
        /// without the server, and with the + it is written with.
        /// </summary>
        private static string DisplayNumber(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;

            int at = jid.IndexOf('@');
            string user = at < 0 ? jid : jid.Substring(0, at);

            int device = user.IndexOf(':');
            if (device >= 0) user = user.Substring(0, device);

            if (user.Length == 0) return null;
            return "+" + user;
        }

        /// <summary>
        /// The code, on demand - and only on demand. Asking the service for a login code
        /// restarts its login, so a page that did it by itself could take a working
        /// account offline just by being opened.
        /// </summary>
        private async void ShowQrButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommunicationService.Instance.IsConnected)
            {
                QrStatusText.Text = Loc.Get("ConnectionPage_QrUnavailable", "QR code not available.");
                return;
            }

            if (CommunicationService.Instance.WhatsAppState == "connected")
            {
                // The same reason, for the case the service already says it is linked:
                // there is nothing to scan, and the request would restart the login it
                // is working with.
                QrStatusText.Text = Loc.Get("ConnectionPage_AlreadyLinked",
                    "This phone is already linked to WhatsApp.");
                return;
            }

            QrStatusText.Text = Loc.Get("ConnectionPage_RequestingQr", "Requesting the QR code...");

            try
            {
                await CommunicationService.Instance.SendControlAsync("login.qr");
            }
            catch (Exception error)
            {
                Diag.Failed("ConnectionPage/login.qr", error);
                QrStatusText.Text = string.Format(
                    Loc.Get("ConnectionPage_QrError", "Could not show the QR code: {0}"), error.Message);
            }
        }

        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null) return;

            switch (message.Command)
            {
                case "state":
                    // Two things the answer can change: a number the phone has not
                    // stored yet (a phone linked through the service), and a code that
                    // has just been used - once the service says the account is
                    // connected, the code on screen is spent.
                    ShowNumber();
                    if (message.State == "connected") CloseQrOverlay();
                    break;

                case "qr":
                    ShowQrCode(message.QrImageData, message.QrDuration);
                    break;
            }
        }

        private async void ShowQrCode(string base64, int duration)
        {
            if (string.IsNullOrEmpty(base64))
            {
                QrStatusText.Text = Loc.Get("ConnectionPage_QrUnavailable", "QR code not available.");
                return;
            }

            try
            {
                QrOverlayImage.Source = await BitmapFromBase64Async(base64);

                QrOverlayHintText.Text = Loc.Get("ConnectionPage_QrOverlayHint",
                    "WhatsApp, Linked devices, Link a device, then scan. The screen stays on while this page is open.");
                QrStatusText.Text = duration > 0
                    ? string.Format(Loc.Get("ConnectionPage_QrHintDuration",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code (valid for about {0} seconds)."),
                        duration)
                    : Loc.Get("ConnectionPage_QrHint",
                        "Open WhatsApp, open Linked devices and tap Link a device, then scan the code.");

                OpenQrOverlay();
                ScheduleQrRefresh(duration);
            }
            catch (Exception error)
            {
                Diag.Failed("ShowQrCode", error);
                QrStatusText.Text = string.Format(
                    Loc.Get("ConnectionPage_QrError", "Could not show the QR code: {0}"), error.Message);
            }
        }

        private void OpenQrOverlay()
        {
            QrOverlay.Visibility = Visibility.Visible;
            KeepScreenOn();
        }

        /// <summary>
        /// Takes the code off the screen, and the refresh with it: a code nobody is
        /// looking at is not worth re-asking for, and the ask restarts the login.
        /// </summary>
        private void CloseQrOverlay()
        {
            StopQrTimer();
            QrOverlay.Visibility = Visibility.Collapsed;
            ReleaseScreenOn();
        }

        /// <summary>
        /// The screen stays on: if it turns off or dims while the code is on screen, the
        /// code becomes unreadable and the scan fails with no error message at all.
        ///
        /// All inside the try, construction included: creating the request can fail on
        /// the platform, and a convenience must never stop the code from being shown.
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
            catch (Exception error)
            {
                // Active-request limit reached, or member not implemented on this
                // phone: we carry on without.
                Diag.Failed("KeepScreenOn", error);
            }
        }

        private void ReleaseScreenOn()
        {
            if (!_displayRequestActive || _displayRequest == null) return;
            try { _displayRequest.RequestRelease(); }
            catch (Exception error) { Diag.Failed("ReleaseScreenOn", error); }
            _displayRequestActive = false;
        }

        /// <summary>
        /// Asks for a new code shortly before it expires, because the old one is no
        /// longer valid and the app would otherwise keep it on screen forever.
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

            try
            {
                await CommunicationService.Instance.SendControlAsync("login.qr");
            }
            catch (Exception error)
            {
                Diag.Failed("ConnectionPage/qr refresh", error);
            }
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

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(ChatsPage));
        }
    }
}
