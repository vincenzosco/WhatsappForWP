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
    public static class TileImage
    {
        /// <summary>The ceiling the system accepts for a tile image; over it the update is rejected.</summary>
        private const int MaxImageBytes = 200 * 1024;

        /// <summary>The app mark, for a chat whose picture has not arrived.</summary>
        private const string AppMarkPath = "Assets\\TileIcon.png";

        /// <summary>
        /// The avatar a file was written from, by file name. A chat whose picture has not
        /// changed is not decoded and encoded again for every message that arrives.
        /// </summary>
        private static readonly Dictionary<string, string> Written =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>
        /// Writes the card for one chat and answers the path to put in the tile's `src`, or
        /// null when nothing could be written.
        /// </summary>
        public static async Task<string> StoreAsync(string fileName, string avatarData)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            string from = avatarData ?? "";

            lock (Written)
            {
                string have;
                if (Written.TryGetValue(fileName, out have) && have == from)
                {
                    return "ms-appdata:///local/" + fileName;
                }
            }

            bool composed = false;

            try
            {
                TilePicture picture = await PictureAsync(avatarData);
                if (picture != null)
                {
                    byte[] card = TileCanvas.Card();
                    TileCanvas.DrawPicture(card, picture.Bytes, picture.Side, picture.Side);

                    await WriteAsync(fileName, card);
                    composed = true;
                }
            }
            catch (Exception ex)
            {
                // Named and survived. A phone whose decoder or encoder refuses the card gets
                // the picture as it is - the magnified face this class is here to end, but
                // still a face - and a tile is never left without an image over it.
                Diag.Failed("TileImage/Store", ex);
            }

            if (!composed) return await RawAsync(fileName, avatarData);

            lock (Written) Written[fileName] = from;

            return "ms-appdata:///local/" + fileName;
        }

        /// <summary>
        /// The picture exactly as it arrived, which is what the tile drew before the card
        /// existed. Only the fallback path: `null` when there is no picture or when the bytes
        /// are over the system's ceiling.
        /// </summary>
        private static async Task<string> RawAsync(string fileName, string avatarData)
        {
            if (string.IsNullOrEmpty(avatarData)) return null;

            try
            {
                byte[] bytes = Convert.FromBase64String(avatarData);
                if (bytes.Length == 0 || bytes.Length > MaxImageBytes) return null;

                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, bytes);

                // The file holds this avatar, raw. Recording it makes the next call for the
                // same bytes answer with the path, and keeps a call for other bytes from
                // being told this file is the one it asked for.
                lock (Written) Written[fileName] = avatarData;

                return "ms-appdata:///local/" + fileName;
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

        /// <summary>Pixels and the side they were scaled to, in one answer.</summary>
        private sealed class TilePicture
        {
            public byte[] Bytes;
            public int Side;
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
        /// wider than it is tall. The decoder does the two steps in one pass
        /// (`BitmapTransform.Bounds` plus `ScaledWidth/Height`), which is also the only place
        /// on this phone with a resampler worth using.
        /// </summary>
        private static async Task<TilePicture> PictureOfAsync(IRandomAccessStream stream, int side)
        {
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

            // The square is the rule's, not this method's: `TileCanvas.CentreSquare` is where
            // "lose a slice of the longer side, evenly" is written down and tested.
            TileCrop crop = TileCanvas.CentreSquare((int)decoder.PixelWidth, (int)decoder.PixelHeight);

            var bounds = new BitmapBounds();
            bounds.X = (uint)crop.X;
            bounds.Y = (uint)crop.Y;
            bounds.Width = (uint)crop.Side;
            bounds.Height = (uint)crop.Side;

            var transform = new BitmapTransform();
            transform.Bounds = bounds;
            transform.ScaledWidth = (uint)side;
            transform.ScaledHeight = (uint)side;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;

            // Straight and not premultiplied: the mark's transparent margin has to blend with
            // the card, and a premultiplied edge would arrive already darkened.
            PixelDataProvider data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var picture = new TilePicture();
            picture.Bytes = data.DetachPixelData();
            picture.Side = side;

            return picture;
        }

        /// <summary>
        /// The card as a JPEG in the app's own folder. JPEG because the card is opaque: the
        /// transparency has already been blended into it, and a JPEG of a tile is a tenth of
        /// the bytes of the same tile as a PNG - which matters when the system refuses
        /// anything over 200 kB.
        /// </summary>
        private static async Task WriteAsync(string fileName, byte[] card)
        {
            StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                fileName, CreationCollisionOption.ReplaceExisting);

            using (IRandomAccessStream output = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);

                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    TileCanvas.Size,
                    TileCanvas.Size,
                    96,
                    96,
                    card);

                await encoder.FlushAsync();
            }
        }
    }
}
