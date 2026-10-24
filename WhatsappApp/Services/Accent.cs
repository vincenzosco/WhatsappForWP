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
            // The candidate names cover the three names this platform has used, the one
            // this build answers first.
            //
            // A missing name is asked about with `ContainsKey` and never with a lookup
            // wrapped in a `try`: a `ResourceDictionary` answers a key it does not have with
            // a COMException (0x80004005, "Cannot find a resource with the given key") that
            // is thrown from inside the WinRT map, so the debugger stops on it at every
            // start - `Eccezione generata: COMException ... at WhatsappApp.Services.Accent.Apply`
            // on the output of 2026-10-24 - even though the `catch` below it handled it.
            // Two of the three names are absent on every build, so that was two first-chance
            // exceptions per launch for a question that has a `ContainsKey`.
            _color = Fallback;
            string source = "this build names no accent in its resources";
            ResourceDictionary resources = Application.Current == null ? null : Application.Current.Resources;
            if (resources != null)
            {
                string[] keys = { "SystemColorControlAccentColor", "SystemAccentColor", "PhoneAccentColor" };
                for (int i = 0; i < keys.Length; i++)
                {
                    if (!resources.ContainsKey(keys[i])) continue;

                    object value = resources[keys[i]];
                    if (!(value is Color)) continue;

                    _color = (Color)value;
                    // Which name answered, because the line below is the only evidence a phone
                    // gives about where its colour came from - and "the fallback" and "the
                    // accent" were the same green on a green-accented phone.
                    source = keys[i];
                    break;
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

            // Color.ToString() already begins with '#': the extra one printed "##FF3E65FF".
            Diag.OkNow("accent: " + _color.ToString() + " from " + source);
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
            // The same rule as the accent above: no lookup for a key that is not there, and
            // none at all in a process with no application (the background check imports
            // this file and never builds one).
            Application app = Application.Current;
            if (app == null || !app.Resources.ContainsKey(key)) return;

            var brush = app.Resources[key] as SolidColorBrush;
            if (brush != null) brush.Color = color;
        }
    }
}
