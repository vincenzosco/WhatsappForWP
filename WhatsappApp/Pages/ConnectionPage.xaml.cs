using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
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
    public sealed partial class ConnectionPage : Page
    {
        private readonly CredentialStore _store = new CredentialStore(new NativeCredentialStorage());

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
            ShowStatus();

            // The state line is live: a page that shows what the client is doing has
            // to follow it, or it says "connecting" for as long as it stays open.
            NativeConnection.Instance.ConnectionStatusChanged += OnStatusChanged;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            NativeConnection.Instance.ConnectionStatusChanged -= OnStatusChanged;
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
            ShowStatus();
        }

        /// <summary>
        /// Links a different account. The code is drawn by PairPage, which owns the
        /// rotation and the end of an attempt; this page only hands over the screen.
        /// </summary>
        private void LinkDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(PairPage));
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
                Frame.GoBack();
            else
                Frame.Navigate(typeof(HomePage));
        }
    }
}
