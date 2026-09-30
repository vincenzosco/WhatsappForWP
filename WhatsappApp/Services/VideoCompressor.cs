using System;
using System.Threading.Tasks;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Makes a video smaller before it is sent.
    ///
    /// Why it exists: sending is what the phone pays for, and a clip straight out
    /// of the camera is tens of megabytes that travel, base64-encoded, through one
    /// socket. Re-encoding it to 480 lines shrinks it by an order of magnitude and
    /// the send takes a fraction of the time.
    ///
    /// It never throws and never returns nothing: a phone that cannot convert the
    /// file - the platform refusing, no encoder for that format, no space - gets
    /// the original name back, and the adapter shrinks the video as a last resort
    /// (see WhatsappBridge/ffmpeg.js).
    /// </summary>
    public static class VideoCompressor
    {
        /// <summary>
        /// Below this the conversion is not worth its cost: it holds the processor
        /// for as long as the video is long, and there is little to save.
        /// </summary>
        private const ulong MinBytes = 2 * 1024 * 1024;

        /// <summary>
        /// The file to send: the smaller copy when one was made, the original
        /// otherwise. Never null, never an exception.
        /// </summary>
        public static async Task<string> SmallerAsync(string localFileName)
        {
            if (string.IsNullOrEmpty(localFileName)) return localFileName;

            StorageFile target = null;
            bool keep = false;
            string result = localFileName;

            // One body, one return: nothing leaves the try early, so the copy that
            // is not sent is always discarded. The discard waits outside, because
            // awaiting inside a catch or a finally block is C# 6.
            try
            {
                StorageFolder folder = ApplicationData.Current.LocalFolder;
                StorageFile source = await folder.GetFileAsync(localFileName);

                ulong originalSize = (await source.GetBasicPropertiesAsync()).Size;
                if (originalSize >= MinBytes)
                {
                    // A unique name: sending a second video must not overwrite the
                    // file the first message still plays.
                    target = await folder.CreateFileAsync(
                        "small-" + Guid.NewGuid().ToString("N") + ".mp4",
                        CreationCollisionOption.ReplaceExisting);

                    MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Wvga);
                    var transcoder = new MediaTranscoder();
                    PrepareTranscodeResult prepared =
                        await transcoder.PrepareFileTranscodeAsync(source, target, profile);

                    if (!prepared.CanTranscode)
                    {
                        Diag.Failed("VideoCompressor/cannot",
                            new InvalidOperationException(prepared.FailureReason.ToString()));
                    }
                    else
                    {
                        await prepared.TranscodeAsync().AsTask();

                        ulong smallerSize = (await target.GetBasicPropertiesAsync()).Size;
                        // Growing a video is not shrinking one: the original goes.
                        if (smallerSize > 0 && smallerSize < originalSize)
                        {
                            Diag.Ok("video shrunk: " + originalSize + " -> " + smallerSize + " bytes");
                            result = target.Name;
                            keep = true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("VideoCompressor.SmallerAsync", ex);
            }

            if (!keep) await Discard(target);
            return result;
        }

        private static async Task Discard(StorageFile file)
        {
            if (file == null) return;
            try { await file.DeleteAsync(StorageDeleteOption.PermanentDelete); }
            catch (Exception ex) { Diag.Failed("VideoCompressor/discard", ex); }
        }
    }
}
