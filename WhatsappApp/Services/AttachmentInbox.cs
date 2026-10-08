using System;
using System.Globalization;
using System.Threading.Tasks;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// A file that enters the app from outside: the image picker or the share of
    /// another application.
    ///
    /// In both cases the page that would receive it may no longer exist when the
    /// file arrives: WP8.1 suspends the app while the picker is open, and may
    /// terminate it. A reference to the file cannot therefore be kept while
    /// waiting for a page.
    ///
    /// It is COPIED into the app folder instead of read into memory: a share is
    /// also a video, and a whole video in a byte[] (plus its base64) is the
    /// fastest way to get the app closed by a 512 MB phone. The name of the copied
    /// file is what the page reads in pieces when it sends (see
    /// ChatPage.SendAttachmentAsync).
    ///
    /// Who receives an attachment: ChatPage, which shows it in the preview bar;
    /// ChatsPage, which says there is something to send. Both subscribe to Ready,
    /// because after the picker the page is still the one in front without
    /// OnNavigatedTo being called again.
    /// </summary>
    public static class AttachmentInbox
    {
        // One name per attachment, never shared: the file a message points at must
        // be the file that message sent. A single fixed name means the next
        // recording replaces the previous one under every bubble that used it.
        private static int _copySequence;

        private static string NextCopyName(string extension)
        {
            _copySequence++;
            return "outgoing_attachment_" + _copySequence.ToString(CultureInfo.InvariantCulture) + extension;
        }

        private static string _localFileName;
        private static string _fileName;
        private static string _mimeType;
        private static string _note;

        /// <summary>An attachment nobody has taken yet.</summary>
        public static event Action Ready;

        public static bool HasAttachment
        {
            get { return !string.IsNullOrEmpty(_localFileName); }
        }

        /// <summary>The name, inside LocalFolder, of the copied file.</summary>
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

        /// <summary>Text that came with the share, if there was one.</summary>
        public static string Note
        {
            get { return _note; }
        }

        /// <summary>
        /// Deposits a chosen or shared file by copying it into the app folder. It
        /// is copied now, not when it will be needed: after a reactivation the
        /// reference to the file may no longer be valid, and the app may have been
        /// terminated.
        /// </summary>
        public static async Task PutAsync(StorageFile file, string note)
        {
            if (file == null) return;

            string mimeType = MimeFor(file.FileType);
            string extension = ExtensionFor(mimeType, file.Name);
            StorageFile copy = await file.CopyAsync(
                ApplicationData.Current.LocalFolder,
                NextCopyName(extension),
                NameCollisionOption.ReplaceExisting);

            PutLocal(copy.Name, file.Name, mimeType, note);
        }

        /// <summary>Deposits the bytes already read (a shared bitmap).</summary>
        public static async Task PutBytesAsync(byte[] buffer, string fileName, string mimeType, string note)
        {
            if (buffer == null || buffer.Length == 0) return;

            string extension = ExtensionFor(mimeType, fileName);
            StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                NextCopyName(extension), CreationCollisionOption.ReplaceExisting);
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

        /// <summary>Takes the attachment: whoever shows it does so once.</summary>
        public static void Clear()
        {
            _localFileName = null;
            _fileName = null;
            _mimeType = null;
            _note = null;
        }

        /// <summary>
        /// The MIME type of an extension, as WhatsApp sends it. The last line is
        /// application/octet-stream and not image/jpeg: a file the list does not
        /// know is a file, and calling it an image is how a PDF used to be sent as
        /// a picture.
        /// </summary>
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
            if (value == ".m4a") return "audio/mp4";
            if (value == ".aac") return "audio/aac";
            if (value == ".mp3") return "audio/mpeg";
            if (value == ".wav") return "audio/wav";
            if (value == ".amr") return "audio/amr";
            if (value == ".ogg") return "audio/ogg";
            if (value == ".oga") return "audio/ogg";
            if (value == ".opus") return "audio/ogg";
            if (value == ".pdf") return "application/pdf";
            if (value == ".doc") return "application/msword";
            if (value == ".docx") return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            if (value == ".xls") return "application/vnd.ms-excel";
            if (value == ".xlsx") return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            if (value == ".ppt") return "application/vnd.ms-powerpoint";
            if (value == ".pptx") return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            if (value == ".txt") return "text/plain";
            if (value == ".csv") return "text/csv";
            if (value == ".rtf") return "application/rtf";
            if (value == ".zip") return "application/zip";
            if (value == ".7z") return "application/x-7z-compressed";
            if (value == ".rar") return "application/vnd.rar";
            return "application/octet-stream";
        }

        /// <summary>The extension of the copied file, from the name or the MIME type.</summary>
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
            if (mime.StartsWith("image/")) return ".jpg";
            // A recording with no name: the format the phone records is the one
            // that comes back. Without this a voice note MIME type would land on
            // ".bin", which is not a file the phone can play.
            if (mime.StartsWith("audio/")) return ".m4a";
            // Anything else is a file, and a file with no name has no extension
            // worth inventing.
            return ".bin";
        }

        /// <summary>
        /// "image", "video", "audio" or "document": the word the adapter and the
        /// app use to decide how to send and how to draw. The MIME type may be
        /// missing (a shared bitmap), so the word is also derived from the
        /// extension.
        ///
        /// Audio and not document: a recorded voice note has no extension the
        /// document list knows, and calling it a document is how it used to be
        /// sent as a file instead of a voice note.
        /// </summary>
        public static string KindName(string mimeType, string fileName)
        {
            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return "video";
            if (mime.StartsWith("image/")) return "image";
            if (mime.StartsWith("audio/")) return "audio";

            string name = (fileName ?? "").ToLower();
            if (name.EndsWith(".mp4") || name.EndsWith(".mov") || name.EndsWith(".3gp")
                || name.EndsWith(".avi") || name.EndsWith(".mkv") || name.EndsWith(".webm"))
            {
                return "video";
            }
            if (name.EndsWith(".m4a") || name.EndsWith(".aac") || name.EndsWith(".mp3")
                || name.EndsWith(".wav") || name.EndsWith(".amr") || name.EndsWith(".ogg")
                || name.EndsWith(".oga") || name.EndsWith(".opus"))
            {
                return "audio";
            }
            if (name.EndsWith(".jpg") || name.EndsWith(".jpeg") || name.EndsWith(".png")
                || name.EndsWith(".gif") || name.EndsWith(".bmp"))
            {
                return "image";
            }
            return "document";
        }
    }
}
