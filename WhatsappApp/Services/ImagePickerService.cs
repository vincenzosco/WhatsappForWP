using Windows.Storage.Pickers;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Apre il selettore di file di WP8.1.
    ///
    /// Si usa PickSingleFileAndContinue e NON PickSingleFileAsync: la
    /// documentazione Microsoft dice che PickSingleFileAsync non e' supportato
    /// su Windows Phone (ne' per Windows Runtime ne' per Silverlight) e indica
    /// PickSingleFileAndContinue. Sul telefono la prima falliva, e il pulsante
    /// allegato sembrava morto.
    ///
    /// La differenza che conta: PickSingleFileAndContinue non restituisce
    /// niente. Deattiva l'app, e il file scelto torna ad App.OnActivated come
    /// PickFileContinuation. Il risultato non passa quindi da qui: lo deposita
    /// App in AttachmentInbox.
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

            // Un video non e' un'immagine, ma arriva dallo stesso pulsante: il
            // tipo lo dice il file, e l'invio sceglie la strada giusta.
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".3gp");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".webm");

            // CS0618: deprecata da Windows 10, ma e' l'unica che Windows Phone
            // 8.1 implementa. Non e' un warning da sistemare, e' la piattaforma.
#pragma warning disable 618
            picker.PickSingleFileAndContinue();
#pragma warning restore 618
        }
    }
}
