using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Un file che entra nell'app da fuori: il selettore immagini o la
    /// condivisione di un'altra applicazione.
    ///
    /// In tutti e due i casi la pagina che lo riceverebbe puo' non esistere piu'
    /// nel momento in cui il file arriva: WP8.1 sospende l'app mentre il
    /// selettore e' aperto, e puo' terminarla. Non si puo' quindi tenere un
    /// riferimento al file aspettando una pagina.
    ///
    /// Si COPIA nella cartella dell'app invece di leggerlo in memoria: una
    /// condivisione e' anche un video, e un video intero in un byte[] (piu' la
    /// sua base64) e' il modo piu' veloce per farsi chiudere l'app da un
    /// telefono da 512 MB. Il nome del file copiato e' quello che la pagina
    /// legge a pezzi quando spedisce (vedi ChatPage.SendAttachmentAsync).
    ///
    /// Chi riceve un allegato: ChatPage, che lo mostra nella barra di anteprima;
    /// ChatsPage, che dice che c'e' qualcosa da inviare. Entrambe si iscrivono a
    /// Ready, perche' dopo il selettore la pagina e' ancora quella davanti senza
    /// che OnNavigatedTo venga chiamato di nuovo.
    /// </summary>
    public static class AttachmentInbox
    {
        // Il nome del file copiato: uno solo in attesa alla volta.
        private const string CopyBaseName = "outgoing_attachment";

        private static string _localFileName;
        private static string _fileName;
        private static string _mimeType;
        private static string _note;

        /// <summary>Un allegato che nessuno ha ancora ritirato.</summary>
        public static event Action Ready;

        public static bool HasAttachment
        {
            get { return !string.IsNullOrEmpty(_localFileName); }
        }

        /// <summary>Il nome, dentro LocalFolder, del file copiato.</summary>
        public static string LocalFileName
        {
            get { return _localFileName; }
        }

        public static string FileName
        {
            get { return _fileName; }
        }

        public static string MimeType
        {
            get { return _mimeType; }
        }

        /// <summary>Testo che accompagnava la condivisione, se ce n'era uno.</summary>
        public static string Note
        {
            get { return _note; }
        }

        /// <summary>
        /// Deposita un file scelto o condiviso copiandolo nella cartella
        /// dell'app. Si copia adesso, non quando servira': dopo una
        /// riattivazione il riferimento al file puo' non essere piu' valido, e
        /// l'app puo' essere stata terminata.
        /// </summary>
        public static async Task PutAsync(StorageFile file, string note)
        {
            if (file == null) return;

            string mimeType = MimeFor(file.FileType);
            string extension = ExtensionFor(mimeType, file.Name);
            StorageFile copy = await file.CopyAsync(
                ApplicationData.Current.LocalFolder,
                CopyBaseName + extension,
                NameCollisionOption.ReplaceExisting);

            PutLocal(copy.Name, file.Name, mimeType, note);
        }

        /// <summary>Deposita i byte gia' letti (una bitmap condivisa).</summary>
        public static async Task PutBytesAsync(byte[] buffer, string fileName, string mimeType, string note)
        {
            if (buffer == null || buffer.Length == 0) return;

            string extension = ExtensionFor(mimeType, fileName);
            StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                CopyBaseName + extension, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, buffer);

            PutLocal(file.Name, fileName, mimeType, note);
        }

        private static void PutLocal(string localName, string fileName, string mimeType, string note)
        {
            _localFileName = localName;
            _fileName = fileName;
            _mimeType = string.IsNullOrEmpty(mimeType) ? "image/jpeg" : mimeType;
            _note = note;

            var handler = Ready;
            if (handler != null) handler();
        }

        /// <summary>Ritira l'allegato: chi lo mostra lo fa una volta sola.</summary>
        public static void Clear()
        {
            _localFileName = null;
            _fileName = null;
            _mimeType = null;
            _note = null;
        }

        /// <summary>Il tipo MIME di un'estensione, come lo manda WhatsApp.</summary>
        private static string MimeFor(string extension)
        {
            string value = (extension ?? "").ToLower();
            if (value == ".png") return "image/png";
            if (value == ".gif") return "image/gif";
            if (value == ".bmp") return "image/bmp";
            if (value == ".mp4") return "video/mp4";
            if (value == ".mov") return "video/quicktime";
            if (value == ".3gp") return "video/3gpp";
            if (value == ".avi") return "video/x-msvideo";
            if (value == ".mkv") return "video/x-matroska";
            if (value == ".webm") return "video/webm";
            return "image/jpeg";
        }

        /// <summary>L'estensione del file copiato, dal nome o dal tipo MIME.</summary>
        private static string ExtensionFor(string mimeType, string fileName)
        {
            string name = fileName ?? "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) return name.Substring(dot).ToLower();

            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return ".mp4";
            if (mime == "image/png") return ".png";
            if (mime == "image/gif") return ".gif";
            if (mime == "image/bmp") return ".bmp";
            return ".jpg";
        }

        /// <summary>
        /// "image" o "video": la parola che l'adapter e l'app usano per decidere
        /// come spedire e come disegnare. Il tipo MIME puo' mancare (una bitmap
        /// condivisa), quindi la parola si ricava anche dall'estensione.
        /// </summary>
        public static string KindName(string mimeType, string fileName)
        {
            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return "video";
            if (mime.StartsWith("image/")) return "image";

            string name = (fileName ?? "").ToLower();
            if (name.EndsWith(".mp4") || name.EndsWith(".mov") || name.EndsWith(".3gp")
                || name.EndsWith(".avi") || name.EndsWith(".mkv") || name.EndsWith(".webm"))
            {
                return "video";
            }
            return "image";
        }
    }
}
