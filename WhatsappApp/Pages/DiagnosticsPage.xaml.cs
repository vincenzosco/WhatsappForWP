using System.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// What the app did, on the phone.
    ///
    /// Why it exists: Diag writes to Debug.WriteLine, which needs a PC with a
    /// debugger attached - and the run being diagnosed is the one on the phone.
    /// This page shows the same history, plus the connection fields that decide
    /// whether the adapter is reachable at all, so a report is a screenshot
    /// instead of a pasted assembly list.
    /// </summary>
    public sealed partial class DiagnosticsPage : Page
    {
        public DiagnosticsPage()
        {
            this.InitializeComponent();
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));
        }

        /// <summary>
        /// Opens this page from wherever the user asked for it. The settings page
        /// and the empty chat list both carry a button for it, and the navigation
        /// is the same line in both: it lives here so the two cannot drift apart.
        /// </summary>
        public static void Open(Frame frame)
        {
            if (frame != null) frame.Navigate(typeof(DiagnosticsPage));
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Refresh();
        }

        private void Refresh()
        {
            var comm = CommunicationService.Instance;
            var text = new StringBuilder();

            text.Append("connected: ").Append(comm.IsConnected).Append('\n');
            text.Append("whatsapp: ").Append(comm.WhatsAppState).Append('\n');
            text.Append("account: ").Append(comm.AccountJid).Append('\n');
            text.Append("server: ").Append(comm.ServerAddress).Append('\n');
            text.Append("device: ").Append(SettingsService.DeviceId).Append('\n');
            text.Append("chats: ").Append(DataService.Instance.Contacts.Count).Append('\n');
            text.Append("---\n");
            text.Append(Diag.HistoryText());

            DiagnosticsText.Text = text.ToString();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack) Frame.GoBack();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            Diag.Clear();
            Refresh();
        }

        /// <summary>
        /// Sends exactly what the screen shows to the adapter, which writes it to
        /// the container log.
        ///
        /// Why it exists: the phone has no clipboard worth the name on WP8.1, so
        /// getting this text onto a PC meant a screenshot and a cable. The report
        /// is the same string either way, so the log and a pasted copy cannot
        /// disagree.
        /// </summary>
        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CommunicationService.Instance.IsConnected)
            {
                SendStatusText.Text = Loc.Get("DiagnosticsPage_SendOffline",
                    "Not connected: the report could not be sent.");
                return;
            }

            string report = DiagnosticsText.Text ?? "";
#pragma warning disable 4014
            Guarded.RunGuardedAsync("DiagnosticsPage/send",
                CommunicationService.Instance.SendControlAsync("diag", report));
#pragma warning restore 4014
            SendStatusText.Text = Loc.Get("DiagnosticsPage_Sent", "Report sent to the server.");
        }
    }
}
