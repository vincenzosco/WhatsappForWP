using System;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Tasks;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// What the app did, on the phone. The page shows the same history as diag.log
    /// together with the fields that say what state the client is in.
    /// </summary>
    public sealed partial class DiagnosticsPage : PhoneApplicationPage
    {
        public DiagnosticsPage()
        {
            InitializeComponent();
            PageTitleText.Text = Loc.Get("DiagnosticsPage_Title.Text", "Diagnostics");
            HintText.Text = Loc.Get("DiagnosticsPage_Hint.Text",
                "What the app did, and where it tried to connect. Long press the text to select and copy it, select it all, or share it with another app. The same lines are in diag.log in the app's folder.");
            ClearButton.Content = Loc.Get("DiagnosticsPage_Clear.Content", "Clear");
            SelectAllButton.Content = Loc.Get("DiagnosticsPage_SelectAll.Content", "Select all");
            ShareButton.Content = Loc.Get("DiagnosticsPage_Share.Content", "Share");
        }

        public static void Open(PhoneApplicationFrame frame)
        {
            if (frame != null)
                frame.Navigate(new Uri("/Pages/DiagnosticsPage.xaml", UriKind.Relative));
        }

        /// <summary>The chosen background, under the same veil the settings page uses.</summary>
        private void ApplyTheme()
        {
            System.Windows.Media.ImageSource image = ThemeService.ImageFor(ThemeService.Current);
            PageWallpaper.Source = image;
            PageWallpaper.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyTheme();
            Refresh();
        }

        private void Refresh()
        {
            var text = new StringBuilder();
            text.Append("connected: ").Append(NativeConnection.Instance.IsConnected).Append('\n');
            text.Append("status: ").Append(NativeConnection.Instance.Status).Append('\n');
            text.Append("account: ").Append(NativeConnection.Instance.Jid).Append('\n');
            text.Append("device: ").Append(SettingsService.DeviceId).Append('\n');
            text.Append("chats: ").Append(DataService.Instance.Contacts.Count).Append('\n');
            text.Append("background: ").Append(BackgroundSync.IntervalMinutes).Append(" min, registered ")
                .Append(BackgroundSync.IsRegistered() ? "yes" : "no")
                .Append(", permitted ").Append(BackgroundSync.IsPermitted() ? "yes" : "no").Append('\n');
            text.Append("last wake: ").Append(LastWakeText()).Append('\n');
            text.Append("---\n");
            text.Append(Diag.HistoryText());

            DiagnosticsText.Text = text.ToString();
        }

        private static string LastWakeText()
        {
            DateTime wake = BackgroundSync.LastWake;
            if (wake == default(DateTime)) return "never (the task has not run on this phone)";

            string outcome = BackgroundSync.LastOutcome;
            var line = new StringBuilder();
            line.Append(wake.ToLocalTime().ToString("dd/MM HH:mm"))
                .Append(", ").Append(string.IsNullOrEmpty(outcome) ? "unknown" : outcome);

            if (outcome == BackgroundSync.OutcomeRunning)
            {
                line.Append(" (started, no ending: the system took it down)");
            }
            else
            {
                line.Append(", ").Append(BackgroundSync.LastMilliseconds).Append(" ms, ")
                    .Append(BackgroundSync.LastUnread).Append(" unread");
            }

            return line.ToString();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            Diag.Clear();
            Refresh();
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsText.Focus();
            DiagnosticsText.SelectAll();
            ActionStatusText.Text = Loc.Get("DiagnosticsPage_Selected",
                "The whole report is selected: use Copy in the text menu.");
        }

        private void OnShareRequested(EmailComposeTask task)
        {
            task.Subject = Loc.Get("DiagnosticsPage_ShareTitle", "WhatsApp diagnostics");
            task.Body = DiagnosticsText.Text ?? "";
        }

        private void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var task = new EmailComposeTask();
                OnShareRequested(task);
                task.Show();
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
    }
}
