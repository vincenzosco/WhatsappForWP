using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The settings screen: the chat wallpaper and the way to the account. It is
    /// the page the app bar's settings button opens; everything about the linked
    /// number and the connection stayed on `ConnectionPage`, reachable from here,
    /// so the two do not become one page that does everything.
    ///
    /// The wallpapers are the released package's own images (`Images/Wallpapers`),
    /// not a palette copied from a screenshot: choosing one paints the conversation
    /// and the home list exactly as the official app would.
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        /// <summary>
        /// True while the page is filling itself: setting `IsOn` raises `Toggled`, and
        /// without this the page would write the setting it just read and register the
        /// task again every time it opens.
        /// </summary>
        private bool _loading;

        public SettingsPage()
        {
            this.InitializeComponent();
            ToolTipService.SetToolTip(BackButton, Loc.Get("ChatPage_BackTooltip", "Back"));

            // The grid binds the catalogue itself; `Selected` on each entry is what
            // the check mark follows, so a tap repaints one cell instead of
            // rebuilding the list and sending the scroll back to the top.
            ThemeGrid.ItemsSource = ThemeService.Choices;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Diag.Ok("settings: " + ThemeService.Choices.Count + " theme(s), current " + ThemeService.Current);

            _loading = true;
            BackgroundToggle.IsOn = SettingsService.BackgroundCheckEnabled;
            _loading = false;
        }

        /// <summary>
        /// The periodic check is a registration with the system, not only a flag: the
        /// switch writes the choice and registers or cancels the task (see
        /// `BackgroundSync`). The first time it is turned on, Windows may ask for the
        /// permission a background task needs.
        /// </summary>
        private void BackgroundToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            SettingsService.BackgroundCheckEnabled = BackgroundToggle.IsOn;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("SettingsPage/background",
                BackgroundSync.SetEnabledAsync(BackgroundToggle.IsOn));
#pragma warning restore 4014
        }

        /// <summary>
        /// One tap decides. The choice is written down and the pages that paint it
        /// read it on their next navigation: the home list is cached and gets an
        /// `OnNavigatedTo` when this page pops, and a chat is built every time.
        /// </summary>
        private void ThemeGrid_ItemClick(object sender, ItemClickEventArgs e)
        {
            var choice = e.ClickedItem as ThemeChoice;
            if (choice == null) return;

            ThemeService.Choose(choice.Key);
        }

        private void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ConnectionPage));
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
