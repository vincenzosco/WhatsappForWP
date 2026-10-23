using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decoding of images into bitmaps for XAML binding.
    ///
    /// A `BitmapImage` belongs to the thread that owns the XAML view, and every entry
    /// point here hops to it before building one (see `OnUiAsync`). A caller therefore
    /// does not have to know which thread it is on.
    /// </summary>
    public static class ImageHelper
    {
        /// <summary>
        /// base64 -> bitmap, decoded at `decodePixelWidth` px wide.
        /// </summary>
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
                // Not base64 - a truncated cache entry, or bytes that were never a picture.
                // Answering null draws the initials; letting it out is an unobserved
                // exception in the `async void` handlers that call this.
                Diag.Failed("ImageHelper/base64", ex);
                return null;
            }

            return await FromBytesAsync(bytes, decodePixelWidth);
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
        public static Task<BitmapImage> FromBytesAsync(byte[] bytes, int decodePixelWidth)
        {
            if (bytes == null || bytes.Length == 0) return Done(null);

            return OnUiAsync(delegate { return DecodeBytesAsync(bytes, decodePixelWidth); });
        }

        /// <summary>
        /// bitmap from a file already in the app folder, at the width it is shown
        /// at. It serves the attachments: after the picker or a share the bytes sit
        /// on disk, not in memory.
        /// </summary>
        public static Task<BitmapImage> FromFileAsync(string localFileName, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(localFileName)) return Done(null);

            return OnUiAsync(delegate { return DecodeFileAsync(localFileName, decodePixelWidth); });
        }

        /// <summary>
        /// bitmap from a stream that is already open: it serves the cover frame of
        /// a video, which Windows.Media.Editing hands back as a stream and not as
        /// bytes or a file. The stream is not disposed here; the caller owns it.
        /// </summary>
        public static Task<BitmapImage> FromStreamAsync(IRandomAccessStream stream, int decodePixelWidth)
        {
            if (stream == null) return Done(null);

            return OnUiAsync(delegate { return DecodeStreamAsync(stream, decodePixelWidth); });
        }

        /// <summary>
        /// Builds one bitmap on the thread that owns the XAML view.
        ///
        /// Why the hop is here and not at the call sites: a `BitmapImage` is a XAML
        /// object, and building one on another thread answers `RPC_E_WRONG_THREAD`
        /// (0x8001010E) - the picture is lost and the row keeps its initials. The run of
        /// 2026-10-22 printed exactly one line for it, `Contact.LoadAvatarAsync: Exception
        /// 0x8001010E`, next to a chat list where the picture never appeared; the caller was
        /// the cache loader, on the thread that had just finished reading a file. On this
        /// platform an `await` does not come back to the UI thread by itself, so any decode
        /// that follows a file read, a socket read or a `Task.Delay` is on a pool thread by
        /// the time it asks for the bitmap - and almost every decode here follows one. The
        /// check is one call, and it is the only place that has to make it.
        /// </summary>
        private static async Task<BitmapImage> OnUiAsync(Func<Task<BitmapImage>> build)
        {
            Window window = Window.Current;
            CoreDispatcher dispatcher = window == null ? null : window.Dispatcher;

            // No window means no XAML view: a background task has no one to draw for, and
            // there is no dispatcher to hop to. The caller gets the same answer it would
            // have got before, which is the one it can use (null or a decode that fails).
            if (dispatcher == null || dispatcher.HasThreadAccess) return await build();

            var built = new TaskCompletionSource<BitmapImage>();
            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
            {
                // Every failure is carried to the awaiting caller, not thrown inside the
                // handler: an exception that leaves a dispatcher callback is unobserved,
                // and this file is called from `async void` paths that would lose it.
                try
                {
                    built.SetResult(await build());
                }
                catch (Exception ex)
                {
                    built.SetException(ex);
                }
            });

            return await built.Task;
        }

        /// <summary>A finished task with an answer, for the calls that have nothing to decode.</summary>
        private static Task<BitmapImage> Done(BitmapImage value)
        {
            var done = new TaskCompletionSource<BitmapImage>();
            done.SetResult(value);
            return done.Task;
        }

        private static async Task<BitmapImage> DecodeBytesAsync(byte[] bytes, int decodePixelWidth)
        {
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

        private static async Task<BitmapImage> DecodeFileAsync(string localFileName, int decodePixelWidth)
        {
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

        private static async Task<BitmapImage> DecodeStreamAsync(IRandomAccessStream stream, int decodePixelWidth)
        {
            var bitmap = new BitmapImage();
            if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
    }
}
