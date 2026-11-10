using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WhatsappApp.Converters
{
    // These converters are one-way in XAML. ConvertBack returns UnsetValue so a
    // TwoWay target such as TextBox.Text cannot write a presentation value to a model.

    /// <summary>
    /// Converts a boolean to a Visibility value (true = Visible, false = Collapsed)
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = (bool)value;
            bool invert = parameter != null && parameter.ToString() == "Invert";
            if (invert) boolValue = !boolValue;
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return ((Visibility)value) == Visibility.Visible;
        }
    }

    /// <summary>
    /// Converts an outgoing message status to the matching reference icon.
    /// </summary>
    public class MessageStatusToIconConverter : IValueConverter
    {
        private static readonly BitmapImage Sending = Icon("ThemeDark/inline-100-clock.png");
        private static readonly BitmapImage Sent = Icon("ThemeDark/inline-100-check.png");
        private static readonly BitmapImage Delivered = Icon("ThemeDark/inline-100-double-check.png");
        private static readonly BitmapImage Read = Icon("ThemeNeutral/inline-blue-checks.png");
        private static readonly BitmapImage Failed = Icon("ThemeDark/inline-100-error.png");

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is Models.MessageStatus)) return null;

            switch ((Models.MessageStatus)value)
            {
                case Models.MessageStatus.Sending: return Sending;
                case Models.MessageStatus.Sent: return Sent;
                case Models.MessageStatus.Delivered: return Delivered;
                case Models.MessageStatus.Read: return Read;
                case Models.MessageStatus.Failed: return Failed;
                default: return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }

        private static BitmapImage Icon(string name)
        {
            return new BitmapImage(new Uri("/Assets/Reference/" + name, UriKind.Relative));
        }
    }

    /// <summary>
    /// Converts initial string to a deterministic color from WhatsApp palette
    /// </summary>
    public class InitialToColorConverter : IValueConverter
    {
        private static readonly string[] Colors = new[] {
            "#FF525D8A", "#FF1E8E3E", "#FFE37400", "#FFE5252C",
            "#FF0D6B99", "#FF7B1FA2", "#FFC2185B", "#FF388E3C",
            "#FF1976D2", "#FFF57C00", "#FF455A64", "#FFAFB42B",
            "#FF00695C", "#FF5D4037", "#FF512DA8", "#FF303F9F"
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string initials = value as string ?? "?";
            int hash = initials.GetHashCode();
            int index = (hash & 0x7FFFFFFF) % Colors.Length;
            return new SolidColorBrush(ParseColor(Colors[index]));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }

        private Color ParseColor(string hex)
        {
            hex = hex.Replace("#", "");
            byte a = (byte)System.Convert.ToByte(hex.Substring(0, 2), 16);
            byte r = (byte)System.Convert.ToByte(hex.Substring(2, 2), 16);
            byte g = (byte)System.Convert.ToByte(hex.Substring(4, 2), 16);
            byte b = (byte)System.Convert.ToByte(hex.Substring(6, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }
    }

    /// <summary>
    /// A missed call, in the colour the released app paints it. False is the same grey the
    /// second line of the row uses, so an answered call looks exactly as it did.
    /// </summary>
    public class MissedToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool missed = value is bool && (bool)value;
            return missed
                ? new SolidColorBrush(Color.FromArgb(255, 211, 47, 47))
                : new SolidColorBrush(Color.FromArgb(255, 147, 147, 147));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>
    /// Converts unread count to visibility (0 = collapsed, &gt;0 = visible)
    /// </summary>
    public class UnreadCountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            int count = (int)value;
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    /// <summary>
    /// Converts MessageType to visibility for the image element (visible only for Image type)
    /// </summary>
    public class MessageTypeToImageVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var type = (Models.MessageType)value;
            return type == Models.MessageType.Image ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DependencyProperty.UnsetValue;
        }
    }
}
