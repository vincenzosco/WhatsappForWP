using System;
using System.Windows;
using System.Windows.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Reads the phone's accent from the Silverlight theme resources and applies it to the
    /// app-owned brushes before the first page is created.
    /// </summary>
    public static class Accent
    {
        public static readonly Color Fallback = Color.FromArgb(255, 37, 211, 102);

        private static Color _color = Fallback;
        private static bool _applied;

        public static Color Color
        {
            get { return _color; }
        }

        public static void Apply()
        {
            if (_applied) return;
            _applied = true;

            string source = "the phone accent resource is unavailable";
            ResourceDictionary resources = Application.Current == null ? null : Application.Current.Resources;
            if (resources != null && resources.Contains("PhoneAccentColor"))
            {
                object value = resources["PhoneAccentColor"];
                if (value is Color)
                {
                    _color = (Color)value;
                    source = "PhoneAccentColor";
                }
            }

            SetBrush("WhatsAppAccentBrush", _color);
            SetBrush("WhatsAppAppBarBrush", _color);
            SetBrush("WhatsAppSentBubbleBrush", _color);
            SetBrush("WhatsAppHeaderBrush", _color);
            SetBrush("WhatsAppAccentDarkBrush", Shade(_color, 0.82));
            SetBrush("WhatsAppAccentTextBrush", IsLight(_color)
                ? Color.FromArgb(255, 51, 51, 51)
                : Color.FromArgb(255, 255, 255, 255));

            // The chat palette derives from the same accent: the received bubble
            // is the accent, the sent one its darker shade, and the sender and
            // time lines a lighter, translucent accent - the teal and the cobalt
            // of the reference screenshots are the two accents this picks between.
            SetBrush("ChatIncomingBrush", _color);
            SetBrush("ChatOutgoingBrush", Shade(_color, 0.65));
            SetBrush("ChatBubbleMetaBrush", Meta(_color));

            Diag.OkNow("accent: " + _color.ToString() + " from " + source);
        }

        public static bool IsLight(Color color)
        {
            return (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) > 170;
        }

        private static Color Shade(Color color, double factor)
        {
            return Color.FromArgb(
                color.A,
                (byte)(color.R * factor),
                (byte)(color.G * factor),
                (byte)(color.B * factor));
        }

        /// <summary>The lighter, translucent accent the sender and time lines
        /// inside a bubble are drawn in.</summary>
        private static Color Meta(Color color)
        {
            return Color.FromArgb(
                190,
                (byte)(color.R + (255 - color.R) * 0.45),
                (byte)(color.G + (255 - color.G) * 0.45),
                (byte)(color.B + (255 - color.B) * 0.45));
        }

        private static void SetBrush(string key, Color color)
        {
            Application app = Application.Current;
            if (app == null || !app.Resources.Contains(key)) return;

            SolidColorBrush brush = app.Resources[key] as SolidColorBrush;
            if (brush != null) brush.Color = color;
        }
    }
}
