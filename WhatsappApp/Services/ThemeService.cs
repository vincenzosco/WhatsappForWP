using System;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.Storage;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace WhatsappApp.Services
{
    /// <summary>
    /// One background this app can paint the conversation (and the home list) with.
    /// It is a view model as well as a catalogue entry: the settings grid binds to it
    /// and marks the chosen one, so the check moves without rebuilding the list (a
    /// new ItemsSource would send the grid back to the top).
    /// </summary>
    public sealed class ThemeChoice : INotifyPropertyChanged
    {
        private bool _selected;

        /// <summary>The value stored on the phone: `classic`, `plain` or `wall-N`.</summary>
        public string Key { get; set; }

        /// <summary>The word under the thumbnail, already localized.</summary>
        public string Name { get; set; }

        /// <summary>The full image, `ms-appx:///...`; null for the plain background.</summary>
        public string ImageUri { get; set; }

        /// <summary>The small image the grid draws, `ms-appx:///...`; null for plain.</summary>
        public string ThumbUri { get; set; }

        /// <summary>The one chosen on this phone.</summary>
        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) return;
                _selected = value;
                OnPropertyChanged("Selected");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// The chat wallpaper: the doodle the released app ships, the forty-one others
    /// from its `Images/Wallpapers` folder, and plain white.
    ///
    /// Why not `ChatPreferences`: that file holds one decision per chat, keyed by the
    /// address of a conversation. This is one decision for the whole phone, and it has
    /// to be readable before the first page is built, so it lives in LocalSettings.
    ///
    /// The choice is applied by whoever paints it (ChatPage, HomePage) at navigation
    /// time. `ImageFor` answers a fresh `BitmapImage` every call so two pages cannot
    /// share a decoded image whose lifecycle belongs to one of them.
    /// </summary>
    public static class ThemeService
    {
        public const string ClassicKey = "classic";
        public const string PlainKey = "plain";

        /// <summary>The doodle wallpaper of the released package (`Images/Background.jpg`).</summary>
        private const string ClassicImage = "ms-appx:///Assets/ChatBackground.jpg";

        private const string SettingKey = "ChatWallpaper";

        private static readonly List<ThemeChoice> Known = new List<ThemeChoice>();
        private static bool _loaded;
        private static string _current;

        /// <summary>
        /// Every background, in the order the settings grid shows them: the doodle,
        /// plain white, then the package's own wallpapers by number.
        /// </summary>
        public static IList<ThemeChoice> Choices
        {
            get
            {
                EnsureLoaded();
                return Known;
            }
        }

        /// <summary>The key of the chosen background. Never null.</summary>
        public static string Current
        {
            get
            {
                EnsureLoaded();
                return _current;
            }
        }

        /// <summary>
        /// The chosen background itself, image and all. Null only if the settings hold a key
        /// this build does not know, which `Read` has already turned into one it does.
        ///
        /// Why the entry and not the key: `TileTheme` needs the picture to compose the tile
        /// from, and asking for "the image of what is current" in one call is what keeps the
        /// tile and the list from reading two different answers.
        /// </summary>
        public static ThemeChoice Chosen()
        {
            EnsureLoaded();
            return Find(_current);
        }

        /// <summary>
        /// The image of a key, or null for plain white (and for an unknown key, which
        /// falls back to plain rather than to a broken brush).
        /// </summary>
        public static ImageSource ImageFor(string key)
        {
            ThemeChoice choice = Find(key);
            if (choice == null || string.IsNullOrEmpty(choice.ImageUri)) return null;

            var image = new BitmapImage();
            image.UriSource = new Uri(choice.ImageUri);
            return image;
        }

        /// <summary>
        /// Remembers a choice and marks it in the catalogue. An unknown key is ignored:
        /// a file written by another build must not leave the app with no background.
        /// </summary>
        public static void Choose(string key)
        {
            ThemeChoice choice = Find(key);
            if (choice == null) return;

            _current = choice.Key;
            for (int i = 0; i < Known.Count; i++) Known[i].Selected = Known[i].Key == _current;

            // The tile is painted with this too (`TileTheme`): composed in its own time and
            // drawn when it is ready, so the grid does not wait for a decoder.
#pragma warning disable 4014
            Guarded.RunGuardedAsync("ThemeService/tile theme", TileTheme.RefreshAsync());
#pragma warning restore 4014

            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey] = _current;
            }
            catch (Exception ex)
            {
                // The choice still applies to this run; it just will not survive a restart.
                Diag.Failed("ThemeService/Choose", ex);
            }

            Diag.Ok("theme: " + _current);
        }

        private static ThemeChoice Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            EnsureLoaded();
            for (int i = 0; i < Known.Count; i++)
            {
                if (Known[i].Key == key) return Known[i];
            }
            return null;
        }

        /// <summary>
        /// Builds the catalogue once and reads the stored key. LocalSettings is read
        /// synchronously; there is no async load to wait for at startup.
        /// </summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            Known.Add(new ThemeChoice
            {
                Key = ClassicKey,
                Name = Loc.Get("Settings_ThemeClassic", "Classico"),
                ImageUri = ClassicImage,
                ThumbUri = ClassicImage
            });

            Known.Add(new ThemeChoice
            {
                Key = PlainKey,
                Name = Loc.Get("Settings_ThemePlain", "Bianco")
            });

            // The wallpapers of the package, `Images/Wallpapers/N.jpg` (a handful are
            // PNG). The names are the numbers: WhatsApp does not name them.
            string prefix = Loc.Get("Settings_ThemeWallpaper", "Sfondo");
            for (int n = 1; n <= WallpaperCount; n++)
            {
                string name = n + (IsPng(n) ? ".png" : ".jpg");
                Known.Add(new ThemeChoice
                {
                    Key = "wall-" + n,
                    Name = prefix + " " + n,
                    ImageUri = "ms-appx:///Assets/Wallpapers/" + name,
                    ThumbUri = "ms-appx:///Assets/Wallpapers/thumbs/" + name
                });
            }

            _current = Read();
            for (int i = 0; i < Known.Count; i++) Known[i].Selected = Known[i].Key == _current;
        }

        private const int WallpaperCount = 41;

        /// <summary>The six PNG wallpapers in the package; the rest are JPEG.</summary>
        private static bool IsPng(int number)
        {
            return number == 27 || number == 31 || number == 34
                || number == 36 || number == 38;
        }

        private static string Read()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out value) && value != null)
                {
                    string key = value.ToString();
                    if (!string.IsNullOrEmpty(key)) return key;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("ThemeService/Read", ex);
            }

            return ClassicKey;
        }
    }
}
