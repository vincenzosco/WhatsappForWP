using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The picture a tile shows, written as a file the shell can draw.
    ///
    /// The problem it solves: a tile template that carries an image draws it over the whole
    /// tile, and a profile picture from WhatsApp is 96x96 (the preview), so the shell stretched
    /// it across the tile and the phone of 2026-10-22 showed a magnified face - and, with no
    /// picture at all, a magnified WhatsApp mark, because `TileIcon.png` is a glyph with a
    /// transparent margin. Every tile looked zoomed.
    ///
    /// What happens instead: the picture - the person's, or the app mark - is decoded, cropped
    /// to its centre square, scaled once to the side it will be drawn at, and composed onto a
    /// card of the app's teal. A chat's own picture takes the **whole** side, so the tile has no
    /// border around it (the margin the phone of 2026-10-22 showed, and the reason a face looked
    /// small); the app mark takes half of it, because a glyph cropped by the wide tile is a
    /// mistake and a glyph with air around it is a mark.
    ///
    /// The file that leaves here is a card the size of the tile, so the shell has nothing to
    /// magnify and nothing to crop.
    ///
    /// Every failure ends in `null`, and `null` is what the callers already understood:
    /// the tile falls back to the app's own asset rather than to a rejected update.
    /// </summary>
    /// <summary>
    /// The three files one chat's picture is written to: the square card, which the medium
    /// and the large tiles draw, the wide card for the 310x150 one, and the small card for
    /// the 71x71 one. One card per tile shape because a binding fills the tile with the image
    /// it is given and crops what does not fit: a 336 card in the 170 px small tile is the
    /// centre of the face magnified twice, which is what the phone showed on 23 October 2026.
    /// </summary>
    public sealed class TileCard
    {
        public string Square;
        public string Wide;
        public string Small;
    }

    public static class TileImage
    {
        /// <summary>The ceiling the system accepts for a tile image; over it the update is rejected.</summary>
        private const int MaxImageBytes = 200 * 1024;

        /// <summary>The app mark, for a chat whose picture has not arrived.</summary>
        private const string AppMarkPath = "Assets\\TileIcon.png";

        /// <summary>
        /// The card one file name stands for, and the picture it was composed from. A chat
        /// whose picture has not changed is not decoded and encoded again for every message
        /// that arrives; the card it already has is the answer.
        /// </summary>
        private sealed class WrittenCard
        {
            public string Picture;
            public TileCard Card;
            public TileCard Previous;
        }

        private static readonly Dictionary<string, WrittenCard> Written =
            new Dictionary<string, WrittenCard>(StringComparer.Ordinal);

        /// <summary>
        /// The card writes, one at a time.
        ///
        /// Why: two updates of one chat - two unread counts published a moment apart, which is
        /// exactly what a burst of messages is - used to compose at once and open the same two
        /// files at once. `DataService` publishes the count from every path that changes it,
        /// and `PinnedTile.Update` fires each publish without awaiting it, so the second
        /// `OpenAsync(ReadWrite)` on a file the first still holds is the
        /// `UnauthorizedAccessException` (E_ACCESSDENIED) the phone showed on 23 October 2026.
        /// One writer, and a card already written is not written again.
        /// </summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        /// <summary>
        /// Writes the card for one chat and answers the paths to put in the tiles' `src`, or
        /// null when nothing could be written.
        /// </summary>
        public static async Task<TileCard> StoreAsync(string fileName, string avatarData)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            // The fast path, and the reason a burst of messages costs one card: the first
            // call composes, every later one for the same picture is answered from here.
            TileCard have = Cached(fileName, avatarData);
            if (have != null) return have;

            return await Writes.RunAsync(delegate { return ComposeAsync(fileName, avatarData); });
        }

        /// <summary>The card this chat already has, when it was drawn from the same bytes.</summary>
        private static TileCard Cached(string fileName, string picture)
        {
            lock (Written)
            {
                WrittenCard entry;
                if (!Written.TryGetValue(fileName, out entry)) return null;

                return entry.Picture == (picture ?? "") ? entry.Card : null;
            }
        }

        /// <summary>
        /// The card, composed and written - inside the queue, so a second update for the same
        /// chat either finds the first card or waits for it.
        /// </summary>
        private static async Task<TileCard> ComposeAsync(string fileName, string avatarData)
        {
            // Again, in the queue: another update for this chat may have written the card
            // while this one was waiting its turn.
            TileCard have = Cached(fileName, avatarData);
            if (have != null) return have;

            string picture = avatarData ?? "";
            TileCard card = null;

            try
            {
                TilePicture decoded = await PictureAsync(avatarData);
                if (decoded != null)
                {
                    // The name carries the picture and the rules (see `TileCanvas.CardFileName`):
                    // a file that is already there is this very card, so it is not rewritten -
                    // and a file the shell is drawing is never opened for writing.
                    string name = TileCanvas.CardFileName(fileName, picture);
                    string wide = TileCanvas.WideFileName(name);

                    byte[] square = TileCanvas.Card();
                    TileCanvas.DrawPicture(square, decoded.Bytes, decoded.Side, decoded.Side);

                    if (!await ExistsAsync(name))
                    {
                        await WriteAsync(name, square, TileCanvas.Size, TileCanvas.Size);
                    }

                    if (!await ExistsAsync(wide))
                    {
                        await WriteAsync(wide, TileCanvas.WideCard(square),
                            TileCanvas.WideWidth, TileCanvas.WideHeight);
                    }

                    // The 71x71 tile gets a card composed at its own side. Composed from the
                    // composed card and not from a second decode: the picture has already
                    // been cropped and scaled once, and this is only the same face smaller.
                    string small = TileCanvas.SmallFileName(name);
                    if (!await ExistsAsync(small))
                    {
                        byte[] smallPicture = TileCanvas.ResizeSquare(
                            square, TileCanvas.Size, TileCanvas.SmallSize);
                        byte[] smallCard = TileCanvas.Card(TileCanvas.SmallSize);

                        TileCanvas.DrawInto(smallCard, TileCanvas.SmallSize, smallPicture,
                            TileCanvas.SmallSize, TileCanvas.SmallSize, 0, 0);

                        await WriteAsync(small, smallCard, TileCanvas.SmallSize, TileCanvas.SmallSize);
                    }

                    card = CardOf(name, wide, small);

                    // Named, because it is what a device log needs in order to say that the
                    // tile composed at all, and from a picture of which size.
                    Diag.Ok("tile: card v" + TileCanvas.CardVersion + " for " + fileName
                        + " from a " + decoded.Source + "x" + decoded.Source + " picture");
                }
            }
            catch (Exception ex)
            {
                // Named and survived. A phone whose decoder or encoder refuses the card gets
                // the picture as it is - the magnified face this class is here to end, but
                // still a face - and a tile is never left without an image over it.
                Diag.Failed("TileImage/Store", ex);
            }

            if (card == null) card = await RawAsync(fileName, avatarData);
            if (card == null) return null;

            TileCard stale = Remember(fileName, picture, card);
            if (stale != null) await ForgetAsync(stale);

            return card;
        }

        /// <summary>
        /// Records the card as this chat's, and answers the generation it displaces.
        ///
        /// Two are kept - the one the tile draws and the one before it - because a file is
        /// handed to the shell before this process knows whether the shell took it; the one
        /// before that is nobody's.
        /// </summary>
        private static TileCard Remember(string fileName, string picture, TileCard card)
        {
            lock (Written)
            {
                WrittenCard entry;
                if (!Written.TryGetValue(fileName, out entry))
                {
                    entry = new WrittenCard();
                    Written[fileName] = entry;
                }

                TileCard stale = entry.Previous;

                entry.Previous = entry.Card;
                entry.Card = card;
                entry.Picture = picture;

                return stale;
            }
        }

        /// <summary>
        /// A card two generations old: nothing points at it any more. Best effort - a file the
        /// shell is still drawing refuses to go, and that is a file it keeps, not a failure.
        /// </summary>
        private static async Task ForgetAsync(TileCard card)
        {
            if (card == null) return;

            await DeleteQuietlyAsync(card.Square);
            await DeleteQuietlyAsync(card.Wide);
            await DeleteQuietlyAsync(card.Small);
        }

        private static async Task DeleteQuietlyAsync(string card)
        {
            if (string.IsNullOrEmpty(card)) return;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileNameOf(card));
                await file.DeleteAsync();
            }
            catch (Exception)
            {
                // See above: a file in use is kept.
            }
        }

        /// <summary>Whether a card of this name is already on the phone.</summary>
        private static async Task<bool> ExistsAsync(string fileName)
        {
            try
            {
                await ApplicationData.Current.LocalFolder.GetFileAsync(fileName);
                return true;
            }
            catch (System.IO.FileNotFoundException)
            {
                return false;
            }
        }

        /// <summary>The three paths one card name stands for.</summary>
        private static TileCard CardOf(string squareName, string wideName, string smallName)
        {
            var card = new TileCard();
            card.Square = "ms-appdata:///local/" + squareName;
            card.Wide = "ms-appdata:///local/" + wideName;
            card.Small = "ms-appdata:///local/" + smallName;

            return card;
        }

        /// <summary>The bare file name behind a card path.</summary>
        private static string FileNameOf(string card)
        {
            const string Local = "ms-appdata:///local/";

            return card.StartsWith(Local, StringComparison.Ordinal) ? card.Substring(Local.Length) : card;
        }

        /// <summary>
        /// The wide file that belongs to a square one, for the callers that carry one path in
        /// their model and need both: a notification writes one binding per tile size.
        /// </summary>
        public static string WideOf(string square)
        {
            return VariantOf(square, true);
        }

        /// <summary>The 71x71 file that belongs to a square one: the third binding of a turn.</summary>
        public static string SmallOf(string square)
        {
            return VariantOf(square, false);
        }

        private static string VariantOf(string square, bool wide)
        {
            if (string.IsNullOrEmpty(square)) return square;

            const string Local = "ms-appdata:///local/";
            if (square.StartsWith(Local, StringComparison.Ordinal))
            {
                return Local + VariantName(square.Substring(Local.Length), wide);
            }

            int slash = square.LastIndexOf('/');
            if (slash < 0) return square;

            return square.Substring(0, slash + 1) + VariantName(square.Substring(slash + 1), wide);
        }

        private static string VariantName(string name, bool wide)
        {
            return wide ? TileCanvas.WideFileName(name) : TileCanvas.SmallFileName(name);
        }

        /// <summary>
        /// Whether a path was written by the composition rules of this build.
        ///
        /// Why it matters: `NotificationService` remembers the last turn's picture across
        /// processes, and a name from an older build has no `-small` card beside it - a binding
        /// pointed at it would be empty rather than wrong, but empty is a tile with a hole.
        /// </summary>
        public static bool IsCurrentCard(string card)
        {
            if (string.IsNullOrEmpty(card)) return false;

            return card.IndexOf("-v" + TileCanvas.CardVersion + "-", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// The picture exactly as it arrived, which is what the tile drew before the card
        /// existed. Only the fallback path: `null` when there is no picture or when the bytes
        /// are over the system's ceiling.
        ///
        /// Written to both names, so the wide binding still has a file to point at: the shell
        /// then fills and crops it, which is the shape this class exists to avoid - but only
        /// after a decoder or an encoder has already refused the card.
        /// </summary>
        private static async Task<TileCard> RawAsync(string fileName, string avatarData)
        {
            if (string.IsNullOrEmpty(avatarData)) return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(avatarData);
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;

                // The same naming as the card, for the same reasons: a picture written here
                // is written once, and never over a file the shell may be drawing - and
                // under a name of its own, because a card's name is the promise that this
                // picture was composed (see `TileCanvas.FallbackFileName`).
                string name = TileCanvas.FallbackFileName(fileName, avatarData);
                string wide = TileCanvas.WideFileName(name);

                if (!await ExistsAsync(name))
                {
                    StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        name, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(file, bytes);
                }

                if (!await ExistsAsync(wide))
                {
                    StorageFile wideFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        wide, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(wideFile, bytes);
                }

                string small = TileCanvas.SmallFileName(name);
                if (!await ExistsAsync(small))
                {
                    StorageFile smallFile = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        small, CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteBytesAsync(smallFile, bytes);
                }

                return CardOf(name, wide, small);
            }
            catch (Exception ex)
            {
                Diag.Failed("TileImage/Raw", ex);
                return null;
            }
        }

        /// <summary>
        /// The picture as BGRA pixels, with the side they were scaled to: the chat's own when it
        /// has one - the whole tile, so nothing frames it - and the app mark when it does not,
        /// at `TileCanvas.Picture`, so the glyph keeps its air.
        ///
        /// Scaled by the decoder and not by a loop here: the decoder has the interpolation,
        /// and a 96x96 face enlarged to 336 with nearest neighbours is the same smear this
        /// whole class exists to stop.
        /// </summary>
        private static async Task<TilePicture> PictureAsync(string avatarData)
        {
            if (!string.IsNullOrEmpty(avatarData))
            {
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(avatarData);
                }
                catch (FormatException)
                {
                    // Not a picture after all: the mark is better than nothing.
                    bytes = null;
                }

                if (bytes != null && bytes.Length > 0 && bytes.Length <= MaxImageBytes)
                {
                    // Both are held for the length of the decode and not left to the
                    // finalizer: on a 512 MB phone the buffer behind this stream is exactly
                    // what MemoryWatcher is trying to keep free.
                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        using (var writer = new DataWriter(stream))
                        {
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                            await writer.FlushAsync();
                            writer.DetachStream();
                        }

                        stream.Seek(0);
                        return await PictureOfAsync(stream, TileCanvas.Size);
                    }
                }
            }

            StorageFile mark = await PackageFolder().GetFileAsync(AppMarkPath);
            using (IRandomAccessStream stream = await mark.OpenAsync(FileAccessMode.Read))
            {
                return await PictureOfAsync(stream, TileCanvas.Picture);
            }
        }

        /// <summary>Pixels, the side they were scaled to, and the side they arrived at.</summary>
        private sealed class TilePicture
        {
            public byte[] Bytes;

            /// <summary>The side the card draws: what `TileCanvas.Draw` is given.</summary>
            public int Side;

            /// <summary>
            /// The side the picture has after the crop, before it was scaled. It is what the
            /// answer was measured at, which is the only number a log can be believed on.
            /// </summary>
            public int Source;
        }

        /// <summary>The package's own folder: where the app's assets live.</summary>
        private static StorageFolder PackageFolder()
        {
            return Windows.ApplicationModel.Package.Current.InstalledLocation;
        }

        /// <summary>
        /// The stream, cropped to its centre square and scaled to `side` pixels.
        ///
        /// The crop is what lets a picture fill the tile without being stretched: a photo that
        /// is not square loses a slice of its longer side rather than gaining a face that is
        /// wider than it is tall.
        ///
        /// The decoder is asked for **the crop and nothing else**. Not a scaled crop: on
        /// 23 October 2026 the pinned tile composed from a full-size profile picture stopped on
        /// `System.ArgumentException` / E_INVALIDARG ("Value does not fall within the expected
        /// range") inside this method, and the only two WinRT calls it makes are
        /// `BitmapDecoder.CreateAsync` and `GetPixelDataAsync` - so the two parameters it had
        /// never been shown to accept are the suspects: `ScaledWidth`/`ScaledHeight` and
        /// `InterpolationMode`. There is also proof the scale was never the decoder's part:
        /// the 96x96 preview, asked for at 336x336, came back as 96x96 (the log of that run
        /// says so in these words), and the card has always scaled it here afterwards. So the
        /// request is the crop, and the scaling is `TileCanvas.ResizeSquare` - measured, and
        /// pinned by the tests.
        ///
        /// The answer is measured, not assumed: whatever the decoder hands back is measured
        /// with `TileCanvas.SideOf` and scaled to the side the card is drawn at, so the two
        /// cannot disagree.
        /// </summary>
        private static async Task<TilePicture> PictureOfAsync(IRandomAccessStream stream, int side)
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

            int width = (int)decoder.PixelWidth;
            int height = (int)decoder.PixelHeight;

            // The square is the rule's, not this method's: `TileCanvas.CentreSquare` is where
            // "lose a slice of the longer side, evenly" is written down and tested.
            TileCrop crop = TileCanvas.CentreSquare(width, height);

            byte[] pixels = await CroppedAsync(decoder, crop);

            if (pixels == null)
            {
                // The decoder refused even the crop. The whole picture is the request nothing
                // can refuse, and the square is cut out of it here, where it is arithmetic
                // with a test instead of a parameter of a platform API.
                byte[] whole = await PixelsAsync(decoder, new BitmapTransform());
                pixels = TileCanvas.CentreSquarePixels(whole, width, height);
            }

            int decoded = TileCanvas.SideOf(pixels.Length);

            if (decoded != side)
            {
                // Named, because it is the difference between the picture the card was
                // meant to draw and the one it would have read: a side, not a crash.
                Diag.Ok("tile: the decoder answered "
                    + (decoded > 0 ? decoded + "x" + decoded : pixels.Length + " byte(s)")
                    + " instead of " + side + "x" + side + ", so it is scaled here");

                pixels = TileCanvas.ResizeSquare(pixels, decoded, side);
            }

            var picture = new TilePicture();
            picture.Bytes = pixels;
            picture.Side = side;
            picture.Source = decoded;

            return picture;
        }

        /// <summary>
        /// The decoder's answer to the crop, or null when it refused it.
        ///
        /// Why null and not an exception: the caller has a second way to get the same picture,
        /// and C# 5 allows no `await` inside a `catch` (CS1985, `tools/check-csharp5.js`) - the
        /// fallback has to run after this answer, not while this method is unwinding.
        /// E_INVALIDARG arrives as `ArgumentException`.
        /// </summary>
        private static async Task<byte[]> CroppedAsync(BitmapDecoder decoder, TileCrop crop)
        {
            var bounds = new BitmapBounds();
            bounds.X = (uint)crop.X;
            bounds.Y = (uint)crop.Y;
            bounds.Width = (uint)crop.Side;
            bounds.Height = (uint)crop.Side;

            var transform = new BitmapTransform();
            transform.Bounds = bounds;

            try
            {
                return await PixelsAsync(decoder, transform);
            }
            catch (ArgumentException error)
            {
                Diag.Failed("TileImage/crop", error);
            }
            catch (System.Runtime.InteropServices.COMException error)
            {
                Diag.Failed("TileImage/crop", error);
            }

            return null;
        }

        /// <summary>
        /// One transform's pixels, as straight BGRA.
        ///
        /// Straight and not premultiplied: the mark's transparent margin has to blend with the
        /// card, and a premultiplied edge would arrive already darkened.
        /// </summary>
        private static async Task<byte[]> PixelsAsync(BitmapDecoder decoder, BitmapTransform transform)
        {
            PixelDataProvider data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return data.DetachPixelData();
        }

        /// <summary>
        /// The card as a JPEG in the app's own folder. JPEG because the card is opaque: the
        /// transparency has already been blended into it, and a JPEG of a tile is a tenth of
        /// the bytes of the same tile as a PNG - which matters when the system refuses
        /// anything over 200 kB.
        /// </summary>
        private static async Task WriteAsync(string cardName, byte[] card, int width, int height)
        {
            // `cardName` and never the chat's own file name: the name of a card carries the
            // picture it was composed from, so a file that is already there is this card and
            // the caller does not write it again.
            StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                cardName, CreationCollisionOption.ReplaceExisting);

            using (IRandomAccessStream output = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);

                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    (uint)width,
                    (uint)height,
                    96,
                    96,
                    card);

                await encoder.FlushAsync();
            }
        }
    }
}
