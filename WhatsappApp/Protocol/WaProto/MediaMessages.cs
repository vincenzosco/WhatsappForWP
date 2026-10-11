using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// The four media messages, read into one shape.
    ///
    /// Four protobufs with four different sets of field numbers is what the server sends, and
    /// it is not worth four types in the app: everything that uses media wants the same six
    /// things - which key, which path, how big, what it is, what it is called, and any words
    /// that came with it. So the numbers live here as constants (pinned by encoding one field
    /// at a time with the reference's own encoder, like the history fields), and the app sees
    /// <see cref="MediaInfo"/>.
    ///
    /// The numbers are *not* the same across the four, which is exactly why this is one file
    /// with four tables and not one table with four names: `fileSha256` is 4 in a photo and 3
    /// in a voice note, `directPath` is 11 in a photo and 9 in a voice note, and a single
    /// shared table would read every voice note's key out of the wrong byte.
    /// </summary>
    public sealed class MediaInfo
    {
        /// <summary>`image`, `video`, `audio` or `document`: the word the keys are derived with.</summary>
        public string Type;

        public byte[] MediaKey;
        public string DirectPath;
        public string Url;
        public byte[] FileSha256;
        public byte[] FileEncSha256;
        public long FileLength;
        public long MediaKeyTimestamp;

        public string MimeType;
        public string FileName;
        public string Title;
        public string Caption;
        public uint Seconds;
        public bool IsVoiceNote;
        public byte[] Thumbnail;

        /// <summary>Whether this media can be fetched at all: a key and a place to go.</summary>
        public bool IsFetchable
        {
            get { return MediaKey != null && MediaKey.Length == 32 && (!string.IsNullOrEmpty(DirectPath) || !string.IsNullOrEmpty(Url)); }
        }
    }

    /// <summary>
    /// Which media a `Message` carries, and how to read it.
    ///
    /// The field of the `Message` that holds each kind is the reference's: a photo is 3, a
    /// document 7, a voice note 8, a video 9 - and a sticker (26) or a contact (4) is *not*
    /// read here, because this client cannot show them yet and pretending otherwise would put
    /// an empty bubble on the screen.
    /// </summary>
    public static class MediaMessages
    {
        public const int ImageField = 3;
        public const int DocumentField = 7;
        public const int AudioField = 8;
        public const int VideoField = 9;

        /// <summary>The media inside a message, or null when it carries none this client reads.</summary>
        public static MediaInfo Of(Message message)
        {
            if (message == null) return null;

            if (message.ImageMessage != null) return Read(message.ImageMessage, MediaKeys.Image);
            if (message.DocumentMessage != null) return Read(message.DocumentMessage, MediaKeys.Document);
            if (message.AudioMessage != null) return Read(message.AudioMessage, MediaKeys.Audio);
            if (message.VideoMessage != null) return Read(message.VideoMessage, MediaKeys.Video);

            return null;
        }

        /// <summary>
        /// One media sub-message, by the table of its own type.
        ///
        /// Four switches and not one shared table with four names, because the numbers really
        /// do differ and a shared table is how a voice note's key gets read out of a photo's
        /// byte. Each case carries its number in the comment, and every one of them was
        /// printed from the reference's encoder (`/tmp/m4ref`, one field at a time).
        /// </summary>
        public static MediaInfo Read(byte[] encoded, string type)
        {
            if (encoded == null) return null;

            var info = new MediaInfo();
            info.Type = type;

            if (type == MediaKeys.Image) ReadImage(info, encoded);
            else if (type == MediaKeys.Document) ReadDocument(info, encoded);
            else if (type == MediaKeys.Audio) ReadAudio(info, encoded);
            else if (type == MediaKeys.Video) ReadVideo(info, encoded);
            else throw new ProtocolException("not a media type this client knows: " + type);

            return info;
        }

        /// <summary>A photo: url 1, mimetype 2, caption 3, fileSha256 4, fileLength 5, height 6, width 7, mediaKey 8, fileEncSha256 9, directPath 11, mediaKeyTimestamp 12, jpegThumbnail 16.</summary>
        private static void ReadImage(MediaInfo info, byte[] encoded)
        {
            var reader = new ProtoReader(encoded);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1: info.Url = Text(reader); break;
                    case 2: info.MimeType = Text(reader); break;
                    case 3: info.Caption = Text(reader); break;
                    case 4: info.FileSha256 = reader.ReadBytes(); break;
                    case 5: info.FileLength = (long)reader.ReadVarint(); break;
                    case 8: info.MediaKey = reader.ReadBytes(); break;
                    case 9: info.FileEncSha256 = reader.ReadBytes(); break;
                    case 11: info.DirectPath = Text(reader); break;
                    case 12: info.MediaKeyTimestamp = (long)reader.ReadVarint(); break;
                    case 16: info.Thumbnail = reader.ReadBytes(); break;
                    default: reader.SkipField(field.WireType); break;
                }
            }
        }

        /// <summary>A document: url 1, mimetype 2, title 3, fileSha256 4, fileLength 5, mediaKey 7, fileName 8, fileEncSha256 9, directPath 10, mediaKeyTimestamp 11, caption 20.</summary>
        private static void ReadDocument(MediaInfo info, byte[] encoded)
        {
            var reader = new ProtoReader(encoded);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1: info.Url = Text(reader); break;
                    case 2: info.MimeType = Text(reader); break;
                    case 3: info.Title = Text(reader); break;
                    case 4: info.FileSha256 = reader.ReadBytes(); break;
                    case 5: info.FileLength = (long)reader.ReadVarint(); break;
                    case 7: info.MediaKey = reader.ReadBytes(); break;
                    case 8: info.FileName = Text(reader); break;
                    case 9: info.FileEncSha256 = reader.ReadBytes(); break;
                    case 10: info.DirectPath = Text(reader); break;
                    case 11: info.MediaKeyTimestamp = (long)reader.ReadVarint(); break;
                    case 20: info.Caption = Text(reader); break;
                    default: reader.SkipField(field.WireType); break;
                }
            }
        }

        /// <summary>A voice note: url 1, mimetype 2, fileSha256 3, fileLength 4, seconds 5, ptt 6, mediaKey 7, fileEncSha256 8, directPath 9, mediaKeyTimestamp 10.</summary>
        private static void ReadAudio(MediaInfo info, byte[] encoded)
        {
            var reader = new ProtoReader(encoded);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1: info.Url = Text(reader); break;
                    case 2: info.MimeType = Text(reader); break;
                    case 3: info.FileSha256 = reader.ReadBytes(); break;
                    case 4: info.FileLength = (long)reader.ReadVarint(); break;
                    case 5: info.Seconds = (uint)reader.ReadVarint(); break;
                    case 6: info.IsVoiceNote = reader.ReadVarint() != 0; break;
                    case 7: info.MediaKey = reader.ReadBytes(); break;
                    case 8: info.FileEncSha256 = reader.ReadBytes(); break;
                    case 9: info.DirectPath = Text(reader); break;
                    case 10: info.MediaKeyTimestamp = (long)reader.ReadVarint(); break;
                    default: reader.SkipField(field.WireType); break;
                }
            }
        }

        /// <summary>A video: url 1, mimetype 2, fileSha256 3, fileLength 4, seconds 5, mediaKey 6, caption 7, fileEncSha256 11, directPath 13, mediaKeyTimestamp 14, jpegThumbnail 16.</summary>
        private static void ReadVideo(MediaInfo info, byte[] encoded)
        {
            var reader = new ProtoReader(encoded);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1: info.Url = Text(reader); break;
                    case 2: info.MimeType = Text(reader); break;
                    case 3: info.FileSha256 = reader.ReadBytes(); break;
                    case 4: info.FileLength = (long)reader.ReadVarint(); break;
                    case 5: info.Seconds = (uint)reader.ReadVarint(); break;
                    case 6: info.MediaKey = reader.ReadBytes(); break;
                    case 7: info.Caption = Text(reader); break;
                    case 11: info.FileEncSha256 = reader.ReadBytes(); break;
                    case 13: info.DirectPath = Text(reader); break;
                    case 14: info.MediaKeyTimestamp = (long)reader.ReadVarint(); break;
                    case 16: info.Thumbnail = reader.ReadBytes(); break;
                    default: reader.SkipField(field.WireType); break;
                }
            }
        }

        private static string Text(ProtoReader reader)
        {
            return Utf8(reader.ReadBytes());
        }

        private static string Utf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
    }
}
