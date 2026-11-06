using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Two facts about a picture that the person receiving it needs before the picture
    /// itself: how many pixels it has, and a small copy to draw until it downloads.
    ///
    /// Why the sender owes them: `ImageMessage` carries `height` at 6 and `width` at 7, and
    /// both reference clients fill them from the file
    /// (`baileys`, `messages-media.js`, `prepareWAMessageMedia`; `whatsmeow`, `prepareMedia`).
    /// A photo that leaves without them is a bubble the receiving phone has no size for - the
    /// photo arrived as `ack` and drew nothing at all - and the thumbnail
    /// at 13 is what the receiver draws in the chat list before it has the bytes.
    ///
    /// Both are best effort, and that is deliberate: a picture this phone cannot read is
    /// still a picture worth sending, and a send must not fail on its decoration. The caller
    /// gets null and sends without.
    /// </summary>
    public static class PhotoMeasure
    {
        /// <summary>
        /// How large a thumbnail is drawn. Baileys uses 32 for the JPEG it puts in the
        /// message; the phone's own chat list draws about that size.
        /// </summary>
        public const int ThumbnailSide = 32;

        /// <summary>
        /// The most a decode may weigh. This phone has 512 MB and the file it is reading is
        /// already in memory, so a fifteen-megapixel photo (60 MB of pixels) is one the
        /// thumbnail is not worth: the size is still sent, the thumbnail is skipped.
        /// </summary>
        public const long MaxDecodeBytes = 16L * 1024 * 1024;

        /// <summary>The size of a picture, in pixels.</summary>
        public sealed class Sides
        {
            public int Width;
            public int Height;
        }

        /// <summary>
        /// The picture's size, read from its header. Nothing is decoded, so this is cheap
        /// enough to run on every attachment.
        /// </summary>
        public static async Task<Sides> OfAsync(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;

            using (IRandomAccessStream stream = await StreamForAsync(bytes))
            {
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

                var sides = new Sides();
                sides.Width = (int)decoder.PixelWidth;
                sides.Height = (int)decoder.PixelHeight;

                return sides;
            }
        }

        /// <summary>
        /// The picture, smaller than `side` on its longer edge, as JPEG bytes - or null when
        /// it cannot be made (a format this phone cannot decode, or a picture too big to
        /// decode in the memory there is).
        ///
        /// The scaling is done here and not by the decoder: asking `BitmapTransform` for a
        /// `ScaledWidth` is what the pinned tile stopped trusting (, an
        /// E_INVALIDARG, and before that a 96x96 answer to a 336 request). One full decode
        /// plus an average of the blocks is arithmetic that either works or says so.
        /// </summary>
        public static async Task<byte[]> ThumbnailAsync(byte[] bytes, int side)
        {
            if (bytes == null || bytes.Length == 0 || side <= 0) return null;

            using (IRandomAccessStream stream = await StreamForAsync(bytes))
            {
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);

                int width = (int)decoder.PixelWidth;
                int height = (int)decoder.PixelHeight;

                if (width <= 0 || height <= 0) return null;

                long weight = (long)width * height * 4;
                if (weight > MaxDecodeBytes)
                {
                    Diag.Ok("photo: " + width + "x" + height + " is " + (weight / (1024 * 1024))
                        + " MB of pixels, so the thumbnail is skipped and only the size is sent");
                    return null;
                }

                int smallWidth = width;
                int smallHeight = height;

                if (width > side || height > side)
                {
                    if (width >= height)
                    {
                        smallWidth = side;
                        smallHeight = Math.Max(1, (int)Math.Round((double)height * side / width));
                    }
                    else
                    {
                        smallHeight = side;
                        smallWidth = Math.Max(1, (int)Math.Round((double)width * side / height));
                    }
                }

                byte[] pixels = await PixelsAsync(decoder);

                // A picture smaller than the box is its own thumbnail: the encoder is still
                // wanted, because what goes in the message is JPEG and not the original.
                if (smallWidth != width || smallHeight != height)
                {
                    pixels = Shrink(pixels, width, height, smallWidth, smallHeight);
                }

                return await JpegAsync(pixels, smallWidth, smallHeight);
            }
        }

        /// <summary>The file's bytes as a stream the decoder can open.</summary>
        private static async Task<IRandomAccessStream> StreamForAsync(byte[] bytes)
        {
            var stream = new InMemoryRandomAccessStream();

            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
            }

            stream.Seek(0);

            return stream;
        }

        /// <summary>Straight BGRA, the shape `BitmapEncoder.SetPixelData` takes.</summary>
        private static async Task<byte[]> PixelsAsync(BitmapDecoder decoder)
        {
            PixelDataProvider data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            return data.DetachPixelData();
        }

        /// <summary>
        /// The picture averaged down to `smallWidth` by `smallHeight`.
        ///
        /// An average and not the nearest pixel: a thumbnail read at a glance is a shape and
        /// a colour, and taking one pixel out of every block turns a face into noise. The
        /// result is opaque (alpha 255), because a JPEG has no alpha and a photo with a
        /// transparent margin would come out black there.
        /// </summary>
        private static byte[] Shrink(byte[] source, int width, int height, int smallWidth, int smallHeight)
        {
            var small = new byte[smallWidth * smallHeight * 4];

            for (int y = 0; y < smallHeight; y++)
            {
                int top = y * height / smallHeight;
                int bottom = (y + 1) * height / smallHeight;
                if (bottom <= top) bottom = top + 1;

                for (int x = 0; x < smallWidth; x++)
                {
                    int left = x * width / smallWidth;
                    int right = (x + 1) * width / smallWidth;
                    if (right <= left) right = left + 1;

                    long blue = 0;
                    long green = 0;
                    long red = 0;
                    int count = 0;

                    for (int sy = top; sy < bottom; sy++)
                    {
                        int row = sy * width;

                        for (int sx = left; sx < right; sx++)
                        {
                            int at = (row + sx) * 4;
                            blue += source[at];
                            green += source[at + 1];
                            red += source[at + 2];
                            count++;
                        }
                    }

                    int to = (y * smallWidth + x) * 4;
                    small[to] = (byte)(blue / count);
                    small[to + 1] = (byte)(green / count);
                    small[to + 2] = (byte)(red / count);
                    small[to + 3] = 255;
                }
            }

            return small;
        }

        /// <summary>Pixels as a JPEG, in memory.</summary>
        private static async Task<byte[]> JpegAsync(byte[] pixels, int width, int height)
        {
            using (var output = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output);

                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Ignore,
                    (uint)width,
                    (uint)height,
                    96,
                    96,
                    pixels);

                await encoder.FlushAsync();

                var jpeg = new byte[output.Size];
                output.Seek(0);

                using (var reader = new DataReader(output.GetInputStreamAt(0)))
                {
                    await reader.LoadAsync((uint)output.Size);
                    reader.ReadBytes(jpeg);
                }

                return jpeg;
            }
        }
    }
}
