using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace WhatsappApp.Converters
{
    // All these converters are one-way: XAML uses them to read, never to write.
    // ConvertBack returns UnsetValue, which is how the binding engine is told
    // "leave the source alone". Throwing instead would not: a TextBox bound to one
    // of these (TextBox.Text is TwoWay by default) would blow up on the user, and
    // for a one-way converter the exception adds no information.

    /// <summary>
    /// Converts a boolean to a Visibility value (true = Visible, false = Collapsed)
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool boolValue = (bool)value;
            bool invert = parameter != null && parameter.ToString() == "Invert";
            if (invert) boolValue = !boolValue;
            return boolValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return ((Visibility)value) == Visibility.Visible;
        }
    }

    /// <summary>
    /// Converts message status to a readable string with check marks
    /// </summary>
    public class MessageStatusToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var status = (Models.MessageStatus)value;
            switch (status)
            {
                case Models.MessageStatus.Sending: return "\u25CB";       // ○
                case Models.MessageStatus.Sent: return "\u2713";          // ✓
                case Models.MessageStatus.Delivered: return "\u2713\u2713"; // ✓✓
                case Models.MessageStatus.Read: return "\u2713\u2713";    // ✓✓ (blue)
                case Models.MessageStatus.Failed: return "\u2717";        // ✗
                default: return "";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return Windows.UI.Xaml.DependencyProperty.UnsetValue;
        }
    }

    /// <summary>
    /// Converts message status to color brush for read receipts
    /// </summary>
    /// <remarks>
    /// The ticks sit on the bubble, whose background *is* the accent: sent and delivered take
    /// the text colour that accent already uses (white on a dark accent, #333 on a light one -
    /// `Accent.Apply` decides), and read takes a dark blue. That was asked for on 1 November
    /// 2026, and it is not WhatsApp's own light blue: the accent here is periwinkle, where
    /// #0B3D91 reads as "read" without being the same tone as the bubble.
    /// </remarks>
    public class MessageStatusToColorConverter : IValueConverter
    {
        /// <summary>The two ticks of a message that was read.</summary>
        public static readonly Windows.UI.Color Read = Windows.UI.Color.FromArgb(255, 11, 61, 145);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var status = (Models.MessageStatus)value;
            if (status == Models.MessageStatus.Read) return new SolidColorBrush(Read);

            // Everything before "read" is the bubble's own text colour, and asking the accent
            // is what keeps the ticks legible when the accent is light; a fixed white was
            // invisible there.
            Windows.UI.Color text = Services.Accent.IsLight(Services.Accent.Color)
                ? Windows.UI.Color.FromArgb(255, 51, 51, 51)
                : Windows.UI.Color.FromArgb(255, 255, 255, 255);

            return new SolidColorBrush(text);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return Windows.UI.Xaml.DependencyProperty.UnsetValue;
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

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string initials = value as string ?? "?";
            int hash = initials.GetHashCode();
            // Mask off the sign bit: Math.Abs(int.MinValue) overflows
            int index = (hash & 0x7FFFFFFF) % Colors.Length;
            return new SolidColorBrush(ParseColor(Colors[index]));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return Windows.UI.Xaml.DependencyProperty.UnsetValue;
        }

        private Windows.UI.Color ParseColor(string hex)
        {
            hex = hex.Replace("#", "");
            byte a = (byte)System.Convert.ToByte(hex.Substring(0, 2), 16);
            byte r = (byte)System.Convert.ToByte(hex.Substring(2, 2), 16);
            byte g = (byte)System.Convert.ToByte(hex.Substring(4, 2), 16);
            byte b = (byte)System.Convert.ToByte(hex.Substring(6, 2), 16);
            return Windows.UI.Color.FromArgb(a, r, g, b);
        }
    }

    /// <summary>
    /// A missed call, in the colour the released app paints it. False is the same grey the
    /// second line of the row uses, so an answered call looks exactly as it did.
    /// </summary>
    public class MissedToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool missed = value is bool && (bool)value;
            return missed
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 211, 47, 47))
                : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 147, 147, 147));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return Windows.UI.Xaml.DependencyProperty.UnsetValue;
        }
    }

    /// <summary>
    /// Converts unread count to visibility (0 = collapsed, >0 = visible)
    /// </summary>
    public class UnreadCountToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            int count = (int)value;
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return Windows.UI.Xaml.DependencyProperty.UnsetValue;
        }
    }

    /// <summary>
    /// Converts MessageType to visibility for the image element (visible only for Image type)
    /// </summary>
    public class MessageTypeToImageVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var type = (Models.MessageType)value;
            return type == Models.MessageType.Image ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return Windows.UI.Xaml.DependencyProperty.UnsetValue;
        }
    }

}

