using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using WhatsappApp.Pages;
using WhatsappApp.Services;

namespace WhatsappApp.Controls
{
    /// <summary>The three sections of the app, each with its own page.</summary>
    public enum AppSection
    {
        Chats,
        Status,
        Calls
    }

    /// <summary>
    /// Bottom navigation bar shared by the section pages.
    /// It navigates on the root Frame and removes the section just left from the
    /// stack, so the Back button exits the app instead of walking back through the
    /// sections already seen.
    /// </summary>
    public sealed partial class SectionNav : UserControl
    {
        private static readonly SolidColorBrush SelectedBrush =
            new SolidColorBrush(Color.FromArgb(255, 18, 140, 126));
        private static readonly SolidColorBrush IdleBrush =
            new SolidColorBrush(Color.FromArgb(255, 128, 128, 128));

        private AppSection _current;

        public SectionNav()
        {
            this.InitializeComponent();

            // The buttons are icon-only: the text is a tooltip (which the system
            // also reads as an accessibility label).
            ToolTipService.SetToolTip(ChatsButton, Loc.Get("Nav_Chats", "chats"));
            ToolTipService.SetToolTip(StatusButton, Loc.Get("Nav_Status", "status"));
            ToolTipService.SetToolTip(CallsButton, Loc.Get("Nav_Calls", "calls"));

            UpdateColors();
        }

        /// <summary>Section shown by the page hosting the bar.</summary>
        public AppSection Current
        {
            get { return _current; }
            set
            {
                _current = value;
                UpdateColors();
            }
        }

        private void UpdateColors()
        {
            ChatsIcon.Stroke = _current == AppSection.Chats ? SelectedBrush : IdleBrush;
            StatusIcon.Stroke = _current == AppSection.Status ? SelectedBrush : IdleBrush;
            CallsIcon.Stroke = _current == AppSection.Calls ? SelectedBrush : IdleBrush;
        }

        private void ChatsButton_Click(object sender, RoutedEventArgs e) { Go(AppSection.Chats); }
        private void StatusButton_Click(object sender, RoutedEventArgs e) { Go(AppSection.Status); }
        private void CallsButton_Click(object sender, RoutedEventArgs e) { Go(AppSection.Calls); }

        private void Go(AppSection section)
        {
            if (section == _current) return;

            // A UserControl has no Frame property: the root Frame is the one
            // App.OnLaunched assigns to Window.Current.Content.
            var frame = Window.Current.Content as Frame;
            if (frame == null) return;

            Type target = PageFor(section);
            if (target == null) return;

            frame.Navigate(target);

            int count = frame.BackStack.Count;
            if (count > 0 && IsSectionPage(frame.BackStack[count - 1].SourcePageType))
            {
                frame.BackStack.RemoveAt(count - 1);
            }
        }

        /// <summary>Page of a section (the app startup uses it too).</summary>
        public static Type PageFor(AppSection section)
        {
            switch (section)
            {
                case AppSection.Status: return typeof(StatusPage);
                case AppSection.Calls: return typeof(CallsPage);
                default: return typeof(ChatsPage);
            }
        }

        private static bool IsSectionPage(Type pageType)
        {
            return pageType == typeof(ChatsPage)
                || pageType == typeof(StatusPage)
                || pageType == typeof(CallsPage);
        }
    }
}
