using System;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The cover frame of a video, for the bubble.
    ///
    /// Why it exists: a received video showed a dark box with a triangle and
    /// nothing else, so two videos looked the same and the box said nothing about
    /// which one it was. WhatsApp draws a frame; this is how this platform can do
    /// the same. Windows Phone 8.1 ships Windows.Media.Editing, so the frame comes
    /// from the phone and no thumbnail travels over the socket.
    ///
    /// It is best effort by design: a codec the phone cannot open, a file that is
    /// gone, a device that refuses the composition - each returns null, the caller
    /// records it, and the bubble keeps the plain box. A missing frame is not an
    /// error the user has to see.
    /// </summary>
    public static class VideoThumbnail
    {
        /// <summary>
        /// A frame of the video already in the app folder, at the width it is
        /// shown at, or null.
        /// </summary>
        public static async Task<BitmapImage> FromFileAsync(string localFileName, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(localFileName)) return null;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(localFileName);
                MediaClip clip = await MediaClip.CreateFromFileAsync(file);

                var composition = new MediaComposition();
                composition.Clips.Add(clip);

                // The first frame, at the width the bubble draws (480 covers the
                // 220 px box at 1.5x). A height of 0 keeps the aspect ratio.
                IRandomAccessStreamWithContentType frame = await composition.GetThumbnailAsync(
                    TimeSpan.Zero, decodePixelWidth, 0, VideoFramePrecision.NearestKeyFrame);
                if (frame == null) return null;

                using (frame)
                {
                    return await ImageHelper.FromStreamAsync(frame, decodePixelWidth);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("VideoThumbnail.FromFileAsync", ex);
                return null;
            }
        }
    }
}
