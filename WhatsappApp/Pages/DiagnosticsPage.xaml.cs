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
    }
}
