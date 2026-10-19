using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The phone's accent colour, the one the WP8.1 app paints its app bar, its
    /// selected tab, its badges and its sent bubbles with.
    ///
    /// Why a service and not literals: the released package asks the theme for
    /// `PhoneAccentBrush` everywhere (`TabHeaderControl`, `MessageBubble`, the
    /// read-only panel), so on a phone whose accent is blue the app is blue. That
    /// is the 1:1 behaviour; `#25D366` is only the fallback for a platform that
    /// does not answer.
    ///
    /// The brushes in `App.xaml` are mutated in place instead of replaced: every
    /// page has already resolved them through `{StaticResource}`, and a brush is a
    /// DependencyObject whose Color change is enough for all of them to repaint.
    /// </summary>
    public static class Accent
    {
        /// <summary>The WhatsApp green, used when the platform gives no answer.</summary>
        public static readonly Color Fallback = Color.FromArgb(255, 37, 211, 102);

        private static Color _color = Fallback;
        private static bool _applied;

        /// <summary>The accent itself.</summary>
        public static Color Color
        {
            get { return _color; }
        }

        /// <summary>
        /// Reads the accent and writes it into the application brushes. Called once
        /// per process, on the UI thread, before the first page: a page built earlier
        /// would keep the fallback until its next load.
        /// </summary>
        public static void Apply()
        {
            if (_applied) return;
            _applied = true;

            // The WP8.1 projection has no UISettings.GetColorValue (it is the
            // Windows 8.1 one): the accent is read from the theme dictionary.
            // The candidate names cover the three names this platform has used.
            _color = Fallback;
            string[] keys = { "SystemAccentColor", "PhoneAccentColor", "SystemColorControlAccentColor" };
            for (int i = 0; i < keys.Length; i++)
            {
                try
                {
                    object value = Application.Current.Resources[keys[i]];
                    if (value is Color)
                    {
                        _color = (Color)value;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Diag.Failed("Accent.Apply/" + keys[i], ex);
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

            Diag.OkNow("accent: #" + _color.ToString());
        }

        /// <summary>Luminance test: white text on a light accent is unreadable.</summary>
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

        private static void SetBrush(string key, Color color)
        {
            var brush = Application.Current.Resources[key] as SolidColorBrush;
            if (brush != null) brush.Color = color;
        }
    }
}
