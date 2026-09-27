using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Un file che entra nell'app da fuori: il selettore immagini o la
    /// condivisione di un'altra applicazione.
    ///
    /// In tutti e due i casi la pagina che lo riceverebbe puo' non esistere piu'
    /// nel momento in cui il file arriva: WP8.1 sospende l'app mentre il
    /// selettore e' aperto, e puo' terminarla. Non si puo' quindi tenere un
    /// riferimento al file aspettando una pagina: i byte si leggono subito, e la
    /// pagina che sta davanti li ritira quando puo'.
    ///
    /// Chi riceve un allegato: ChatPage, che lo mostra nella barra di anteprima;
    /// ChatsPage, che dice che c'e' qualcosa da inviare. Entrambe si iscrivono a
    /// Ready, perche' dopo il selettore la pagina e' ancora quella davanti senza
    /// che OnNavigatedTo venga chiamato di nuovo.
    /// </summary>
    public static class AttachmentInbox
    {
        private static string _base64;
        private static string _fileName;
        private static string _mimeType;
        private static string _note;

        /// <summary>Un allegato che nessuno ha ancora ritirato.</summary>
        public static event Action Ready;

        public static bool HasAttachment
        {
            get { return !string.IsNullOrEmpty(_base64); }
        }

        public static string Base64
        {
            get { return _base64; }
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
        /// Deposita un file scelto o condiviso. Si legge adesso, non quando
        /// servira': dopo una riattivazione il riferimento al file puo' non
        /// essere piu' valido.
        /// </summary>
        public static async Task PutAsync(StorageFile file, string note)
        {
            if (file == null) return;

            byte[] buffer;
            using (var stream = await file.OpenReadAsync())
            {
                using (var reader = new DataReader(stream))
                {
                    uint size = (uint)stream.Size;
                    await reader.LoadAsync(size);
                    buffer = new byte[size];
                    reader.ReadBytes(buffer);
                }
            }

            PutBytes(buffer, file.Name, MimeFor(file.FileType), note);
        }

        /// <summary>Deposita i byte gia' letti (una bitmap condivisa, per esempio).</summary>
        public static void PutBytes(byte[] buffer, string fileName, string mimeType, string note)
        {
            if (buffer == null || buffer.Length == 0) return;

            _base64 = Convert.ToBase64String(buffer);
            _fileName = fileName;
            _mimeType = string.IsNullOrEmpty(mimeType) ? "image/jpeg" : mimeType;
            _note = note;

            var handler = Ready;
            if (handler != null) handler();
        }

        /// <summary>Ritira l'allegato: chi lo mostra lo fa una volta sola.</summary>
        public static void Clear()
        {
            _base64 = null;
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
            return "image/jpeg";
        }
    }
}
