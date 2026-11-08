using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The account screen: the number this phone is linked as, what the client is
    /// doing, and the way to link a different account.
    ///
    /// It used to be the server settings page - address, port, name, token, the list
    /// of adapters found on the network, connect and disconnect - and then an account
    /// page that asked a server for a QR code. Both are gone with the server: the
    /// number comes from the protocol's own credential store (this phone is the
    /// client, so it holds its own account), the state line comes from
    /// `NativeConnection`, and the only thing this page can still do is hand the
    /// screen to `PairPage`, which is where a code that links a device is drawn.
    /// </summary>
    public sealed partial class ConnectionPage : PhoneApplicationPage
    {
        private readonly CredentialStore _store = new CredentialStore(new NativeCredentialStorage());

        public ConnectionPage()
        {
            InitializeComponent();
            AutomationProperties.SetName(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
            LinkedLabel.Text = Loc.Get("ConnectionPage_LinkedLabel.Text", "Linked number");
            AccountHintText.Text = Loc.Get("ConnectionPage_NotLinked.Text",
                "This phone is not linked. Tap below to show the QR code.");
            StatusLabel.Text = Loc.Get("ConnectionPage_StatusLabel.Text", "Connection");
            LinkDeviceButton.Content = Loc.Get("ConnectionPage_LinkDevice.Content", "Link a device");
            LinkDeviceHint.Text = Loc.Get("ConnectionPage_LinkDeviceHint.Text",
                "Linking a device replaces the account this phone uses.");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // The background is read on every navigation: the choice can have changed
            // while this page was off screen, and the account screen carries it too.
            ApplyTheme();

            PageTitleText.Text = Loc.Get("ConnectionPage_AccountTitle", "Account");
            ShowNumber();
            ShowStatus();

            // The state line is live: a page that shows what the client is doing has
            // to follow it, or it says "connecting" for as long as it stays open.
            NativeConnection.Instance.ConnectionStatusChanged -= OnStatusChanged;
            NativeConnection.Instance.ConnectionStatusChanged += OnStatusChanged;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            NativeConnection.Instance.ConnectionStatusChanged -= OnStatusChanged;
        }

        /// <summary>
        /// The chosen background, under the veil the home list uses. Plain white is the
        /// absence of an image: the veil over nothing is the page's own background.
        /// </summary>
        private void ApplyTheme()
        {
            ImageSource image = ThemeService.ImageFor(ThemeService.Current);
            PageWallpaper.Source = image;
            PageWallpaper.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// The number of the account this phone holds. A phone with none is not
        /// linked, and the screen says that rather than showing an empty card where
        /// a number should be.
        /// </summary>
        private void ShowNumber()
        {
            string number = StoredNumber();
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
        /// The client's own line about what it is doing (see `NativeConnection.Status`).
        /// It is English and it is not translated on purpose: it is a diagnostic, the
        /// same sentence the log carries, not a sentence written for a person.
        /// </summary>
        private void ShowStatus()
        {
            string status = NativeConnection.Instance.Status;
            StatusText.Text = string.IsNullOrEmpty(status)
                ? Loc.Get("ConnectionPage_ClientIdle", "Not connected.")
                : status;
        }

        private void OnStatusChanged(object sender, string status)
        {
            if (!Deployment.Current.Dispatcher.CheckAccess())
            {
                Deployment.Current.Dispatcher.BeginInvoke(delegate { ShowStatus(); });
                return;
            }

            ShowStatus();
        }

        /// <summary>
        /// Links a different account. The code is drawn by PairPage, which owns the
        /// rotation and the end of an attempt; this page only hands over the screen.
        /// </summary>
        private void LinkDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new Uri("/Pages/PairPage.xaml", UriKind.Relative));
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService.CanGoBack)
                NavigationService.GoBack();
            else
                NavigationService.Navigate(new Uri("/Pages/HomePage.xaml", UriKind.Relative));
        }
    }
}
