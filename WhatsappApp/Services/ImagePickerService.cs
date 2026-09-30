using Windows.Storage.Pickers;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Opens the WP8.1 file picker.
    ///
    /// It uses PickSingleFileAndContinue and NOT PickSingleFileAsync: the
    /// Microsoft documentation says PickSingleFileAsync is not supported on
    /// Windows Phone (neither for Windows Runtime nor for Silverlight) and points
    /// at PickSingleFileAndContinue. On the phone the first one failed, and the
    /// attach button looked dead.
    ///
    /// The difference that matters: PickSingleFileAndContinue returns nothing. It
    /// deactivates the app, and the chosen file comes back to App.OnActivated as a
    /// PickFileContinuation. The result therefore does not pass through here: App
    /// deposits it in AttachmentInbox.
    /// </summary>
    public static class ImagePickerService
    {
        public static void RequestImage()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".bmp");

            // A video is not an image, but it comes from the same button: the
            // file gives the type, and the send path chooses the right way.
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".3gp");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".webm");

            // CS0618: deprecated since Windows 10, but it is the only one Windows
            // Phone 8.1 implements. It is not a warning to fix, it is the platform.
#pragma warning disable 618
            picker.PickSingleFileAndContinue();
#pragma warning restore 618
        }
    }
}
