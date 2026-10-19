using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using WhatsappApp.Services;

namespace WhatsappApp.Controls
{
    /// <summary>
    /// The three tabs of the home screen, drawn the way the released package
    /// draws them: 32 px words, the one in front in the accent with a 2 px accent
    /// underline, the others in the text colour, a 24 px badge where there is a
    /// count to show.
    ///
    /// It is a control and not part of the page because the header must stay put
    /// while the content swipes: in the official app it belongs to the Pivot, here
    /// it belongs to whichever page hosts the FlipView.
    /// </summary>
    public sealed partial class TabHeader : UserControl
    {
        private static readonly SolidColorBrush Idle =
            new SolidColorBrush(Color.FromArgb(255, 51, 51, 51));

        private int _selected;

        public TabHeader()
        {
            this.InitializeComponent();
            Highlight();
        }

        /// <summary>Raised when a tab is tapped (and when SetSelected moves it).</summary>
        public event EventHandler<int> Selected;

        /// <summary>Index of the tab in front (0 = chats, 1 = status, 2 = calls).</summary>
        public int SelectedIndex
        {
            get { return _selected; }
        }

        /// <summary>The words, written by the page with Loc.Get.</summary>
        public void SetLabels(string chats, string status, string calls)
        {
            ChatsTabLabel.Text = chats;
            StatusTabLabel.Text = status;
            CallsTabLabel.Text = calls;
        }

        /// <summary>Badge counts; 0 hides the badge (the official header does the same).</summary>
        public void SetCounts(int chats, int status, int calls)
        {
            SetBadge(ChatsTabBadge, ChatsTabBadgeText, chats);
            SetBadge(StatusTabBadge, StatusTabBadgeText, status);
            SetBadge(CallsTabBadge, CallsTabBadgeText, calls);
        }

        /// <summary>
        /// Moves the underline without raising <see cref="Selected"/>: the caller is
        /// already the one moving the content (a swipe), and an event back would
        /// move it again.
        /// </summary>
        public void SetSelected(int index)
        {
            if (index < 0 || index > 2 || index == _selected) return;
            _selected = index;
            Highlight();
        }

        private static void SetBadge(Border badge, TextBlock text, int count)
        {
            if (count <= 0)
            {
                badge.Visibility = Visibility.Collapsed;
                return;
            }

            text.Text = count > 99 ? "99+" : count.ToString();
            badge.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// The selected word and its underline take the accent; the others go back
        /// to the text colour. The brushes are read from the application resources
        /// at every call so a reapplied accent is not missed.
        /// </summary>
        private void Highlight()
        {
            var accent = Application.Current.Resources["WhatsAppAccentBrush"] as SolidColorBrush;

            Apply(ChatsTabLabel, ChatsTabUnderline, _selected == 0, accent);
            Apply(StatusTabLabel, StatusTabUnderline, _selected == 1, accent);
            Apply(CallsTabLabel, CallsTabUnderline, _selected == 2, accent);
        }

        private static void Apply(TextBlock label, Rectangle underline, bool on, SolidColorBrush accent)
        {
            label.Foreground = on && accent != null ? accent : Idle;
            underline.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ChatsTabButton_Click(object sender, RoutedEventArgs e) { Choose(0); }
        private void StatusTabButton_Click(object sender, RoutedEventArgs e) { Choose(1); }
        private void CallsTabButton_Click(object sender, RoutedEventArgs e) { Choose(2); }

        private void Choose(int index)
        {
            if (index == _selected) return;
            _selected = index;
            Highlight();

            var handler = Selected;
            if (handler != null) handler(this, index);
        }
    }
}
