using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Windows.Media.Imaging;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decodes images on the Silverlight dispatcher that owns the visual tree.
    /// </summary>
    public static class ImageHelper
    {
        private static int _saidNoView;

        public static async Task<BitmapImage> FromBase64Async(string base64, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(base64)) return null;

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException ex)
            {
                Diag.Failed("ImageHelper/base64", ex);
                return null;
            }

            return await FromBytesAsync(bytes, decodePixelWidth);
        }

        public static Task<BitmapImage> FromBytesAsync(byte[] bytes, int decodePixelWidth)
        {
            if (bytes == null || bytes.Length == 0) return Done(null);

            return OnUiAsync(delegate { return DecodeBytesAsync(bytes, decodePixelWidth); });
        }

        public static Task<BitmapImage> FromFileAsync(string localFileName, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(localFileName)) return Done(null);

            return OnUiAsync(delegate { return DecodeFileAsync(localFileName, decodePixelWidth); });
        }

        public static Task<BitmapImage> FromStreamAsync(IRandomAccessStream stream, int decodePixelWidth)
        {
            if (stream == null) return Done(null);

            return OnUiAsync(delegate { return DecodeStreamAsync(stream, decodePixelWidth); });
        }

        private static async Task<BitmapImage> OnUiAsync(Func<Task<BitmapImage>> build)
        {
            System.Windows.Threading.Dispatcher dispatcher = UiThread.Dispatcher();
            if (dispatcher == null)
            {
                if (Interlocked.Exchange(ref _saidNoView, 1) == 0)
                {
                    Diag.Ok("image: no window in this process, so no picture is decoded here");
                }

                return null;
            }

            if (HasThreadAccess(dispatcher)) return await build();

            return await RunAsync(dispatcher, build);
        }

        private static bool HasThreadAccess(System.Windows.Threading.Dispatcher dispatcher)
        {
            return dispatcher.CheckAccess();
        }

        private static Task<BitmapImage> RunAsync(
            System.Windows.Threading.Dispatcher dispatcher, Func<Task<BitmapImage>> build)
        {
            var built = new TaskCompletionSource<BitmapImage>();
            try
            {
                dispatcher.BeginInvoke(async delegate
                {
                    try
                    {
                        built.SetResult(await build());
                    }
                    catch (Exception ex)
                    {
                        built.SetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                built.SetException(ex);
            }

            return built.Task;
        }

        private static Task<BitmapImage> Done(BitmapImage value)
        {
            var done = new TaskCompletionSource<BitmapImage>();
            done.SetResult(value);
            return done.Task;
        }

        private static Task<BitmapImage> DecodeBytesAsync(byte[] bytes, int decodePixelWidth)
        {
            using (var stream = new MemoryStream(bytes, false))
            {
                var bitmap = NewBitmap(decodePixelWidth);
                bitmap.SetSource(stream);
                return Done(bitmap);
            }
        }

        private static async Task<BitmapImage> DecodeFileAsync(string localFileName, int decodePixelWidth)
        {
            IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(localFileName);
            StorageFile file = item as StorageFile;
            if (file == null) return null;
            using (IRandomAccessStream random = await file.OpenReadAsync())
            using (Stream stream = random.AsStreamForRead())
            {
                var bitmap = NewBitmap(decodePixelWidth);
                bitmap.SetSource(stream);
                return bitmap;
            }
        }

        private static Task<BitmapImage> DecodeStreamAsync(IRandomAccessStream stream, int decodePixelWidth)
        {
            using (Stream source = stream.AsStreamForRead())
            {
                source.Seek(0, SeekOrigin.Begin);
                var bitmap = NewBitmap(decodePixelWidth);
                bitmap.SetSource(source);
                return Done(bitmap);
            }
        }

        private static BitmapImage NewBitmap(int decodePixelWidth)
        {
            var bitmap = new BitmapImage();
            if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
            return bitmap;
        }
    }
}
