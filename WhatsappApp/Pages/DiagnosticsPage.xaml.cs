using System;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
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
    /// This page shows the same history, plus the few fields that say what state
    /// the client is in, so a report is a screenshot instead of a pasted assembly
    /// list.
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
            var text = new StringBuilder();

            // The native client is the whole connection now, so these are the fields
            // that say what it is doing: whether the socket is up, which line of its
            // own state machine it is on, and the account it is linked as.
            text.Append("connected: ").Append(NativeConnection.Instance.IsConnected).Append('\n');
            text.Append("status: ").Append(NativeConnection.Instance.Status).Append('\n');
            text.Append("account: ").Append(NativeConnection.Instance.Jid).Append('\n');
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
        /// Selects the whole report, so the Copy of the text box menu takes all of
        /// it. WP8.1 gives a Runtime app no clipboard of its own (the WinRT
        /// Clipboard type is not in that projection), and the select-and-copy menu
        /// of a TextBox is the only way text leaves the phone - this button leaves
        /// the reader to tap it, with the selection already made.
        /// </summary>
        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsText.Focus(FocusState.Programmatic);
            DiagnosticsText.SelectAll();
            ActionStatusText.Text = Loc.Get("DiagnosticsPage_Selected",
                "The whole report is selected: use Copy in the text menu.");
        }

        /// <summary>
        /// Sends the report through the WP8.1 share sheet (email, OneNote, a
        /// messaging app): the supported way to get it off the phone without a
        /// cable, since there is no clipboard. The frame only supplies its content
        /// when the sheet asks for it, through OnShareRequested.
        /// </summary>
        private void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DataTransferManager manager = DataTransferManager.GetForCurrentView();
                // One subscription only: the manager is per view and outlives this
                // page, so a second tap would otherwise share the report twice.
                manager.DataRequested -= OnShareRequested;
                manager.DataRequested += OnShareRequested;
                DataTransferManager.ShowShareUI();
                ActionStatusText.Text = Loc.Get("DiagnosticsPage_ShareOpened",
                    "Choose an app to send the report to.");
            }
            catch (Exception ex)
            {
                Diag.Failed("DiagnosticsPage/share", ex);
                ActionStatusText.Text = Loc.Get("DiagnosticsPage_ShareFailed",
                    "The report could not be shared.");
            }
        }

        /// <summary>
        /// The share sheet is asking for the content. It is taken off again here,
        /// once: the request is answered, and a handler left on the per-view
        /// manager would answer for a page that is gone.
        /// </summary>
        private void OnShareRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            sender.DataRequested -= OnShareRequested;
            args.Request.Data.Properties.Title = Loc.Get("DiagnosticsPage_ShareTitle",
                "WhatsApp diagnostics");
            args.Request.Data.SetText(DiagnosticsText.Text ?? "");
        }
    }
}
