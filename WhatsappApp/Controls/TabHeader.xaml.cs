using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WhatsappApp.Controls
{
    /// <summary>
    /// The brand row above the pivot: the app name and the unread messages badge.
    /// The section headers belong to the pivot itself.
    /// </summary>
    public sealed partial class TabHeader : UserControl
    {
        public TabHeader()
        {
            InitializeComponent();
            WhatsappTitleLabel.Text = "WhatsApp".ToUpperInvariant();
        }

        /// <summary>The unread messages the badge carries, or nothing for zero.</summary>
        public void SetUnread(int count)
        {
            if (count <= 0)
            {
                WhatsappTitleBadge.Visibility = Visibility.Collapsed;
                return;
            }

            WhatsappTitleBadgeText.Text = count > 99 ? "99+" : count.ToString();
            WhatsappTitleBadge.Visibility = Visibility.Visible;
        }
    }
}
