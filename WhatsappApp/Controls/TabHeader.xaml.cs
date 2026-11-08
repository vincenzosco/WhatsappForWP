using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace WhatsappApp.Controls
{
    /// <summary>
    /// The brand row and the three section headings of the home screen.
    /// </summary>
    public sealed partial class TabHeader : UserControl
    {
        private static readonly SolidColorBrush Accent =
            new SolidColorBrush(Color.FromArgb(255, 0, 168, 199));
        private static readonly SolidColorBrush Idle =
            new SolidColorBrush(Color.FromArgb(255, 146, 153, 154));

        private int _selected;

        public TabHeader()
        {
            InitializeComponent();
            WhatsappTitleLabel.Text = "WhatsApp".ToUpperInvariant();
            Highlight();
        }

        public event EventHandler<int> Selected;

        public int SelectedIndex
        {
            get { return _selected; }
        }

        public void SetLabels(string chats, string status, string calls)
        {
            ChatsTabLabel.Text = (chats ?? "").ToLowerInvariant();
            StatusTabLabel.Text = (status ?? "").ToLowerInvariant();
            CallsTabLabel.Text = (calls ?? "").ToLowerInvariant();
        }

        public void SetCounts(int chats, int status, int calls)
        {
            // The unread messages sit on the brand row, like the original
            // client; the section badges only carry status and call counts.
            SetBadge(WhatsappTitleBadge, WhatsappTitleBadgeText, chats);
            SetBadge(StatusTabBadge, StatusTabBadgeText, status);
            SetBadge(CallsTabBadge, CallsTabBadgeText, calls);
        }

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

        private void Highlight()
        {
            Apply(ChatsTabLabel, _selected == 0);
            Apply(StatusTabLabel, _selected == 1);
            Apply(CallsTabLabel, _selected == 2);
        }

        private static void Apply(TextBlock label, bool selected)
        {
            label.Foreground = selected ? new SolidColorBrush(Colors.White) : Idle;
            label.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        }

        private void ChatsTabButton_Click(object sender, RoutedEventArgs e)
        {
            Choose(0);
        }

        private void StatusTabButton_Click(object sender, RoutedEventArgs e)
        {
            Choose(1);
        }

        private void CallsTabButton_Click(object sender, RoutedEventArgs e)
        {
            Choose(2);
        }

        private void Choose(int index)
        {
            if (index == _selected) return;
            _selected = index;
            Highlight();

            EventHandler<int> handler = Selected;
            if (handler != null) handler(this, index);
        }
    }
}
