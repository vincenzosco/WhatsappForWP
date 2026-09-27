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
        /// <summary>
        /// base64 -> bitmap, decodificata a `decodePixelWidth` px di larghezza.
        /// </summary>
        public static async Task<BitmapImage> FromBase64Async(string base64, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            return await FromBytesAsync(Convert.FromBase64String(base64), decodePixelWidth);
        }

        /// <summary>
        /// bytes -> bitmap. `decodePixelWidth` e' la larghezza a cui l'immagine
        /// viene mostrata: BitmapImage decodifica alla misura chiesta invece che a
        /// quella del file. Un'immagine del profilo da 640x640 decodificata a 52
        /// pesa qualche decina di KB invece di quasi due MB, e su un telefono da
        /// 512 MB con venticinque conversazioni la differenza e' di decine di MB.
        ///
        /// Va impostato PRIMA di SetSourceAsync: dopo la decodifica non ha piu'
        /// effetto. 0 significa "alla misura del file", e va usato solo dove la
        /// misura del file e' davvero quella che serve.
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
    }
}
