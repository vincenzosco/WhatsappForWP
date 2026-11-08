using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Microsoft.Phone.Controls;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The settings screen: the chat wallpaper and the way to the account. It is
    /// the page the app bar's settings button opens; everything about the linked
    /// number and the connection stayed on ConnectionPage, reachable from here.
    /// </summary>
    public sealed partial class SettingsPage : PhoneApplicationPage
    {
        private bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
            AutomationProperties.SetName(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));

            TitleText.Text = Loc.Get("Settings_Title.Text", "Settings").ToUpper(CultureInfo.CurrentCulture);
            ThemeLabel.Text = Loc.Get("Settings_ThemeLabel.Text", "Theme").ToLower(CultureInfo.CurrentCulture);
            ThemeHint.Text = Loc.Get("Settings_ThemeHint.Text",
                "The background of chats and of the list. The choice applies to the whole app.");
            AlertsLabel.Text = Loc.Get("Settings_AlertsLabel.Text", "Notifications").ToLower(CultureInfo.CurrentCulture);
            AlertsHint.Text = Loc.Get("Settings_AlertsHint.Text",
                "Which alerts show up in the notification area. The tile and the number on the icon do not change.");
            MessagesLabel.Text = Loc.Get("Settings_AlertsMessages.Text", "Messages")
                .ToUpper(CultureInfo.CurrentCulture);
            StatusesLabel.Text = Loc.Get("Settings_AlertsStatuses.Text", "Updates")
                .ToUpper(CultureInfo.CurrentCulture);
            CallsLabel.Text = Loc.Get("Settings_AlertsCalls.Text", "Missed calls")
                .ToUpper(CultureInfo.CurrentCulture);
            BackgroundLabel.Text = Loc.Get("Settings_BackgroundLabel.Text", "Background check").ToLower(CultureInfo.CurrentCulture);
            BackgroundHint.Text = Loc.Get("Settings_BackgroundHint.Text",
                "If Windows allows it, the app wakes about every 30 minutes to check messages and update the tile and lock screen. Windows chooses the exact time and may delay or skip a check.");
            BackgroundState.Text = Loc.Get("Settings_BackgroundState.Text",
                "The phone does not allow apps to work in the background: the check will not wake until you turn it back on in the phone's settings.");
            AccountLabel.Text = Loc.Get("Settings_AccountLabel.Text", "Account").ToLower(CultureInfo.CurrentCulture);
            AccountHint.Text = Loc.Get("Settings_AccountHint.Text",
                "The linked number, the state of the connection and linking another phone.");
            AccountButton.Content = Loc.Get("Settings_AccountButton.Content", "Account and connection");

            ThemeGrid.ItemsSource = ThemeService.Choices;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            ApplyTheme();
            Diag.Ok("settings: " + ThemeService.Choices.Count + " theme(s), current " + ThemeService.Current);

            _loading = true;
            MessagesToggle.IsChecked = SettingsService.MessageAlertsEnabled;
            StatusesToggle.IsChecked = SettingsService.StatusAlertsEnabled;
            CallsToggle.IsChecked = SettingsService.CallAlertsEnabled;
            BackgroundToggle.IsChecked = SettingsService.BackgroundCheckEnabled;
            ThemeGrid.SelectedItem = FindSelectedTheme();
            _loading = false;

            ShowBackgroundState();
            PaintToggleStates();
        }

        /// <summary>The big On/Off each toggle row shows beside its switch.</summary>
        private void PaintToggleStates()
        {
            PaintToggleState(MessagesToggleState, MessagesToggle.IsChecked == true);
            PaintToggleState(StatusesToggleState, StatusesToggle.IsChecked == true);
            PaintToggleState(CallsToggleState, CallsToggle.IsChecked == true);
            PaintToggleState(BackgroundToggleState, BackgroundToggle.IsChecked == true);
        }

        private static void PaintToggleState(TextBlock label, bool on)
        {
            label.Text = on
                ? Loc.Get("Settings_On", "On")
                : Loc.Get("Settings_Off", "Off");
        }

        private ThemeChoice FindSelectedTheme()
        {
            for (int i = 0; i < ThemeService.Choices.Count; i++)
            {
                if (ThemeService.Choices[i].Selected) return ThemeService.Choices[i];
            }
            return null;
        }

        private void ApplyTheme()
        {
            ImageSource image = ThemeService.ImageFor(ThemeService.Current);
            PageWallpaper.Source = image;
            PageWallpaper.Visibility = image == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowBackgroundState()
        {
            bool permitted = BackgroundSync.IsPermitted();
            BackgroundState.Visibility = permitted ? Visibility.Collapsed : Visibility.Visible;

            if (!permitted)
            {
                Diag.Ok("settings: the phone refuses background work, the check cannot wake");
            }
        }

        private void MessagesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            SettingsService.MessageAlertsEnabled = MessagesToggle.IsChecked == true;
            PaintToggleStates();
            Diag.Ok("settings: message alerts " + (MessagesToggle.IsChecked == true ? "on" : "off"));
        }

        private void StatusesToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            SettingsService.StatusAlertsEnabled = StatusesToggle.IsChecked == true;
            PaintToggleStates();
            Diag.Ok("settings: status alerts " + (StatusesToggle.IsChecked == true ? "on" : "off"));
        }

        private void CallsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            SettingsService.CallAlertsEnabled = CallsToggle.IsChecked == true;
            PaintToggleStates();
            Diag.Ok("settings: call alerts " + (CallsToggle.IsChecked == true ? "on" : "off"));
        }

        private void BackgroundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            bool enabled = BackgroundToggle.IsChecked == true;
            SettingsService.BackgroundCheckEnabled = enabled;
            PaintToggleStates();

#pragma warning disable 4014
            Guarded.RunGuardedAsync("SettingsPage/background", BackgroundSync.SetEnabledAsync(enabled));
#pragma warning restore 4014

            ShowBackgroundState();
        }

        private void ThemeGrid_ItemClick(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || e.AddedItems.Count == 0) return;

            ThemeChoice choice = e.AddedItems[0] as ThemeChoice;
            if (choice == null) return;

            ThemeService.Choose(choice.Key);
        }

        private void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationService.Navigate(new Uri("/Pages/ConnectionPage.xaml", UriKind.Relative));
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService.CanGoBack)
                NavigationService.GoBack();
            else
                NavigationService.Navigate(new Uri("/Pages/HomePage.xaml", UriKind.Relative));
        }
    }

    public sealed class ThemePlainSampleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string uri = value as string;
            return string.IsNullOrEmpty(uri)
                ? Loc.Get("Settings_ThemePlainSample.Text", "Aa")
                : "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }

    public sealed class ThemeThumbnailConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string uri = value as string;
            if (string.IsNullOrEmpty(uri)) return null;

            var image = new BitmapImage();
            image.UriSource = new Uri(uri, UriKind.RelativeOrAbsolute);
            return image;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return null;
        }
    }
}
