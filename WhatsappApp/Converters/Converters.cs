using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace WhatsappApp.Converters
{
    // Tutti questi converter sono a senso unico: XAML li usa per leggere, mai per
    // scrivere. ConvertBack restituisce UnsetValue, che e' il modo in cui si dice
    // al motore di binding "lascia stare la sorgente". Lanciare un'eccezione
    // invece no: un TextBox legato a uno di questi (TextBox.Text e' TwoWay per
    // default) la farebbe esplodere addosso all'utente, e per un converter a senso
    // unico l'eccezione non aggiunge nessuna informazione.

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
    public class MessageStatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var status = (Models.MessageStatus)value;
            if (status == Models.MessageStatus.Read)
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 167, 237)); // Blue
            if (status == Models.MessageStatus.Delivered)
                return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128)); // Gray
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128));
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
            // Maschera il bit di segno: Math.Abs(int.MinValue) va in overflow
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

    /// <summary>
    /// Converts MessageType to visibility for the text element (collapsed for Image type that has media)
    /// </summary>
    public class MessageTypeToTextVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var type = (Models.MessageType)value;
            // Only hide text if it's an image type - caption text still shows for images
            return type == Models.MessageType.Audio ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            return Windows.UI.Xaml.DependencyProperty.UnsetValue;
        }
    }
}

