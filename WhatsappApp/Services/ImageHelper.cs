using System;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decodifica di immagini in bitmap per il binding XAML. Va usato sul
    /// thread UI: BitmapImage non e' agnostico rispetto alla view.
    /// </summary>
    public static class ImageHelper
    {
        public static async Task<BitmapImage> FromBase64Async(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            return await FromBytesAsync(Convert.FromBase64String(base64));
        }

        public static async Task<BitmapImage> FromBytesAsync(byte[] bytes)
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
                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }
    }
}
