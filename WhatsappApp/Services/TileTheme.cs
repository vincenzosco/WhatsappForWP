using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The picture the app's own tile turns are drawn with: the background the user chose
    /// in Settings - Tema (one of the package's wallpapers, or the doodle) or, when that
    /// choice is plain white, the phone's accent colour.
    ///
    /// Why it exists: the tile is the app's face on the Start screen, and it stayed the
    /// green app mark whatever the conversation and the list were painted with - a phone
    /// whose chats were a blue wallpaper had a green tile. What the phone can carry in a
    /// notification is an image path, so the choice is turned into one here, once per
    /// choice, through the same composer the contact pictures use (`TileImage`): the card
    /// names, the 200 KB ceiling and the single writer are already solved there.
    ///
    /// Why the accent colour when the theme is plain: "plain" is the absence of a picture,
    /// and a tile with no picture is a tile in the phone's own colour - that is what the
    /// Start screen looks like around it. It is drawn as a one-pixel PNG of the accent and
    /// scaled up by the composer, which is the cheapest way to get a solid card out of a
    /// decoder that only reads pictures.
    ///
    /// Nothing here is fatal: a card that cannot be composed leaves `Square` null and every
    /// caller falls back to the app mark, which is what the builds before this one drew.
    /// </summary>
    public static class TileTheme
    {
        /// <summary>The composed cards, or null until one has been composed.</summary>
        private static TileCard _card;

        /// <summary>
        /// The theme the card was composed for, so a redraw of the same choice does not
        /// compose again - the file names already make that cheap, this makes it free.
        /// </summary>
        private static string _composedFor;

        /// <summary>The square card, for the medium tile and the small one.</summary>
        public static string Square
        {
            get { return _card == null ? null : _card.Square; }
        }

        /// <summary>The wide card, for the 310x150 tile.</summary>
        public static string Wide
        {
            get { return _card == null ? null : _card.Wide; }
        }

        /// <summary>The 71x71 card.</summary>
        public static string Small
        {
            get { return _card == null ? null : _card.Small; }
        }

        private const string KeySquare = "TileTheme.Square";
        private const string KeyWide = "TileTheme.Wide";
        private const string KeySmall = "TileTheme.Small";
        private const string KeyComposed = "TileTheme.Key";

        /// <summary>
        /// The card the last run composed, read back from the phone.
        ///
        /// Why it is needed: a background check is a new process, and it draws the tile
        /// before anything has had time to decode a wallpaper - without this the phone would
        /// go from the theme's card to the app mark at every wake and back again. Reading
        /// three stored paths costs nothing and keeps the tile the same picture all day.
        /// </summary>
        public static void Restore()
        {
            if (_card != null) return;

            try
            {
                ApplicationDataContainer settings = ApplicationData.Current.LocalSettings;

                object square;
                if (!settings.Values.TryGetValue(KeySquare, out square) || square == null) return;
                if (string.IsNullOrEmpty(square.ToString())) return;

                var card = new TileCard();
                card.Square = square.ToString();
                card.Wide = Text(settings, KeyWide, card.Square);
                card.Small = Text(settings, KeySmall, card.Square);

                _card = card;
                _composedFor = Text(settings, KeyComposed, null);
            }
            catch (Exception ex)
            {
                Diag.Failed("TileTheme/Restore", ex);
            }
        }

        /// <summary>Writes the card down, for the next run to draw without composing.</summary>
        private static void Remember(string key)
        {
            try
            {
                ApplicationDataContainer settings = ApplicationData.Current.LocalSettings;
                settings.Values[KeySquare] = _card.Square;
                settings.Values[KeyWide] = _card.Wide;
                settings.Values[KeySmall] = _card.Small;
                settings.Values[KeyComposed] = key;
            }
            catch (Exception ex)
            {
                Diag.Failed("TileTheme/Remember", ex);
            }
        }

        private static string Text(ApplicationDataContainer settings, string key, string fallback)
        {
            object stored;
            if (settings.Values.TryGetValue(key, out stored) && stored != null
                && !string.IsNullOrEmpty(stored.ToString())) return stored.ToString();

            return fallback;
        }

        /// <summary>
        /// Composes the card for what is chosen now, and redraws the tile if it changed.
        ///
        /// Called at startup and when the theme is chosen. The accent is part of the name
        /// because it is part of the picture: a phone whose accent changed while the theme
        /// stayed plain has a different card to draw.
        /// </summary>
        public static async Task RefreshAsync()
        {
            try
            {
                string theme = ThemeService.Current;
                string accent = Hex(Accent.Color);
                string key = theme + "/" + accent;

                Restore();
                if (key == _composedFor && _card != null) return;
                _composedFor = key;

                // The name has to survive being a file name: `CardFileName` builds on it.
                string name = "theme-" + Safe(theme) + "-" + accent;

                string picture = await PictureAsync();

                // The theme is a **background**, not the tile's subject: the composer draws it
                // edge to edge, dims it and puts the app mark over it. Handed to the chat
                // path, the wallpaper covered the tile and the logo disappeared with it.
                _card = await TileImage.StoreThemeAsync(name, picture);

                if (_card != null) Remember(key);

                Diag.Ok("tile: the theme " + theme + " is " + (_card == null ? "not composable, the app mark stays" : "on the tile"));

                // The card is what every later draw uses, so the tile on screen has to be
                // drawn again: until it is, the phone keeps showing the mark of the run before.
                if (_card != null) NotificationService.Refresh();
            }
            catch (Exception ex)
            {
                Diag.Failed("TileTheme/RefreshAsync", ex);
            }
        }

        /// <summary>
        /// The picture to compose the card from, as base64, which is what `TileImage` takes.
        /// The chosen wallpaper when there is one, and the accent colour otherwise.
        /// </summary>
        private static async Task<string> PictureAsync()
        {
            ThemeChoice chosen = ThemeService.Chosen();
            if (chosen != null && !string.IsNullOrEmpty(chosen.ImageUri))
            {
                try
                {
                    // A packaged asset, read by its ms-appx URI: the same file the chat list
                    // and the conversations are painted with, so the tile cannot disagree.
                    StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(chosen.ImageUri));
                    IBuffer buffer = await FileIO.ReadBufferAsync(file);
                    return Convert.ToBase64String(buffer.ToArray());
                }
                catch (Exception ex)
                {
                    // A wallpaper the package does not carry falls back to the colour, which
                    // is still the phone's own: better a plain tile than no tile.
                    Diag.Failed("TileTheme/Picture", ex);
                }
            }

            return await SolidAsync(Accent.Color);
        }

        /// <summary>
        /// One pixel of the accent colour, as a PNG. The composer scales it to the card, and
        /// a solid colour survives that: it is what "plain" means here.
        /// </summary>
        private static async Task<string> SolidAsync(Color color)
        {
            using (var stream = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

                // BGRA, not ARGB: that is the order the encoder's pixel format asks for.
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                    1, 1, 96, 96, new byte[] { color.B, color.G, color.R, color.A });

                await encoder.FlushAsync();

                stream.Seek(0);
                var bytes = new byte[stream.Size];
                await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);

                return Convert.ToBase64String(bytes);
            }
        }

        /// <summary>The colour as eight hex digits, which is both a name and a comparison.</summary>
        private static string Hex(Color color)
        {
            return color.A.ToString("x2") + color.R.ToString("x2")
                + color.G.ToString("x2") + color.B.ToString("x2");
        }

        /// <summary>The theme key with the characters a file name cannot take replaced.</summary>
        private static string Safe(string value)
        {
            if (string.IsNullOrEmpty(value)) return "none";

            var text = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                text.Append(char.IsLetterOrDigit(c) ? c : '-');
            }

            return text.ToString();
        }
    }
}
