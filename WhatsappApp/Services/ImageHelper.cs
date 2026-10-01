using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decoding of images into bitmaps for XAML binding. It must be used on the
    /// UI thread: BitmapImage is not view-agnostic.
    /// </summary>
    public static class ImageHelper
    {
        /// <summary>
        /// base64 -> bitmap, decoded at `decodePixelWidth` px wide.
        /// </summary>
        public static async Task<BitmapImage> FromBase64Async(string base64, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            return await FromBytesAsync(Convert.FromBase64String(base64), decodePixelWidth);
        }

        /// <summary>
        /// bytes -> bitmap. `decodePixelWidth` is the width at which the image is
        /// shown: BitmapImage decodes at the requested size instead of the file
        /// size. A 640x640 profile picture decoded at 52 weighs a few tens of KB
        /// instead of almost two MB, and on a 512 MB phone with twenty-five
        /// conversations the difference is tens of MB.
        ///
        /// It must be set BEFORE SetSourceAsync: after the decode it has no effect.
        /// 0 means "at the file size", and must be used only where the file size is
        /// really the one needed.
        /// </summary>
        public static async Task<BitmapImage> FromBytesAsync(byte[] bytes, int decodePixelWidth)
        {
            if (bytes == null || bytes.Length == 0) return null;

            using (var stream = new InMemoryRandomAccessStream())
            {
                using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                }

                var bitmap = new BitmapImage();
                if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }

        /// <summary>
        /// bitmap from a file already in the app folder, at the width it is shown
        /// at. It serves the attachments: after the picker or a share the bytes sit
        /// on disk, not in memory.
        /// </summary>
        public static async Task<BitmapImage> FromFileAsync(string localFileName, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(localFileName)) return null;

            StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(localFileName);
            using (var stream = await file.OpenReadAsync())
            {
                var bitmap = new BitmapImage();
                if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }

        /// <summary>
        /// bitmap from a stream that is already open: it serves the cover frame of
        /// a video, which Windows.Media.Editing hands back as a stream and not as
        /// bytes or a file. The stream is not disposed here; the caller owns it.
        /// </summary>
        public static async Task<BitmapImage> FromStreamAsync(IRandomAccessStream stream, int decodePixelWidth)
        {
            if (stream == null) return null;

            var bitmap = new BitmapImage();
            if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
    }
}
