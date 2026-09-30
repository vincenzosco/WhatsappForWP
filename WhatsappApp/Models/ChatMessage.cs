using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    public enum MessageStatus
    {
        Sending,
        Sent,
        Delivered,
        Read,
        Failed
    }

    public enum MessageType
    {
        Text,
        Image,
        Audio,
        System,
        Video
    }

    [DataContract]
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _id;
        private string _text;
        private string _senderId;
        private string _senderName;
        private string _chatId;
        private DateTime _timestamp = DateTime.Now;

        // The value of the Timestamp field as it arrived. It is a string and not a
        // DateTime because deserialization must not be able to fail: a date the
        // phone does not recognize used to bring down the whole frame ("String was
        // not recognized as a valid DateTime", 0x8013150C) and the message vanished
        // without the user seeing anything.
        private string _timestampWire;
        private MessageStatus _status;
        private MessageType _type;
        private bool _isIncoming;
        private string _formattedTime;
        private string _mediaData;      // base64-encoded media content
        private string _mediaMimeType;  // e.g. "image/jpeg", "image/png"
        private string _mediaFileName;  // optional filename
        private string _mediaTransferId;   // id of an attachment that travels in pieces
        private int _mediaChunkIndex;      // which piece this is
        private int _mediaChunkTotal;      // how many pieces in all
        private string _mediaType;         // "image", "video": the type declared by the server
        private string _command;        // control frame command (see adapter protocol)
        private string _state;          // "disconnected" | "waiting" | "connected"
        private string _pairCode;       // pairing code for phone-number login
        private string _qrImageData;    // base64 PNG of the login QR code
        private int _qrDuration;        // QR validity in seconds
        private string _avatarData;     // profile picture, base64
        private bool _isGroup;          // the chat is a group
        private bool _isHistory;        // old message, sent when the chat is opened
        private int _unreadCount;       // chat-list row: how many unread messages
        private string _accountJid;     // WhatsApp JID of the logged-in account
        private string _callId;             // call id, from GOWA
        private string _callReason;         // outcome reported by GOWA (timeout, reject, ...)
        private int _callDurationSeconds;   // duration in seconds, 0 when unknown
        private bool _callIsVideo;          // video call
        private string _relatedMessageId;   // message touched by a revocation or an edit
        private string _token;              // token of the shared service (see handshake)
        private string _mediaFilePath;       // local file of the received video (client-side, not on the wire)
        private BitmapImage _mediaImage; // decoded MediaData, for the XAML image binding

        // One serializer per type, not one per message: DataContractJsonSerializer
        // builds the contract graph internally on every instance.
        private static readonly DataContractJsonSerializer JsonSerializer =
            new DataContractJsonSerializer(typeof(ChatMessage));

        [DataMember]
        public string Id
        {
            get { return _id; }
            set { _id = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string Text
        {
            get { return _text; }
            set
            {
                _text = value;
                OnPropertyChanged();
                OnPropertyChanged("ShowsText");
            }
        }

        [DataMember]
        public string SenderId
        {
            get { return _senderId; }
            set { _senderId = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string SenderName
        {
            get { return _senderName; }
            set { _senderName = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string ChatId
        {
            get { return _chatId; }
            set { _chatId = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// The field that travels on the wire, as it is. When the message was
        /// built here (and not read from a frame) it is empty, and the getter
        /// writes it from the DateTime: it is the only source of the /Date(ms)/
        /// shape.
        /// </summary>
        [DataMember(Name = "Timestamp")]
        public string TimestampWire
        {
            get { return _timestampWire == null ? FormatWire(_timestamp) : _timestampWire; }
            set
            {
                _timestampWire = value;
                _timestamp = ParseWire(value);
                FormattedTime = FormatTime(_timestamp);
                OnPropertyChanged("TimestampWire");
                OnPropertyChanged("FormattedTime");
            }
        }

        /// <summary>The same date as the app uses it. It is not a [DataMember]: the string goes on the wire.</summary>
        public DateTime Timestamp
        {
            get { return _timestamp; }
            set
            {
                _timestamp = value;
                _timestampWire = null;   // rewritten from the DateTime at the next serialization
                FormattedTime = FormatTime(value);
                OnPropertyChanged("Timestamp");
                OnPropertyChanged("FormattedTime");
            }
        }

        [DataMember]
        public MessageStatus Status
        {
            get { return _status; }
            set { _status = value; OnPropertyChanged(); }
        }

        [DataMember]
        public MessageType Type
        {
            get { return _type; }
            set
            {
                _type = value;
                OnPropertyChanged();
                OnPropertyChanged("IsVideo");
                OnPropertyChanged("IsAudio");
                OnPropertyChanged("ShowsText");
            }
        }

        [DataMember]
        public bool IsIncoming
        {
            get { return _isIncoming; }
            set { _isIncoming = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string MediaData
        {
            get { return _mediaData; }
            set { _mediaData = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string MediaMimeType
        {
            get { return _mediaMimeType; }
            set { _mediaMimeType = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string MediaFileName
        {
            get { return _mediaFileName; }
            set { _mediaFileName = value; OnPropertyChanged(); }
        }

        /// <summary>Identifier of an attachment that travels in pieces (media.begin/end).</summary>
        [DataMember]
        public string MediaTransferId
        {
            get { return _mediaTransferId; }
            set { _mediaTransferId = value; OnPropertyChanged(); }
        }

        /// <summary>Which piece of an attachment this frame is.</summary>
        [DataMember]
        public int MediaChunkIndex
        {
            get { return _mediaChunkIndex; }
            set { _mediaChunkIndex = value; OnPropertyChanged(); }
        }

        /// <summary>How many pieces the attachment has in all.</summary>
        [DataMember]
        public int MediaChunkTotal
        {
            get { return _mediaChunkTotal; }
            set { _mediaChunkTotal = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// The media type as the server declares it ("image", "video"). It is not
        /// the MIME type: it tells that a history row *is* an image even when the
        /// bytes did not arrive.
        /// </summary>
        [DataMember]
        public string MediaType
        {
            get { return _mediaType; }
            set
            {
                _mediaType = value;
                OnPropertyChanged();
                OnPropertyChanged("IsVideo");
                OnPropertyChanged("IsAudio");
                OnPropertyChanged("IsDocument");
                OnPropertyChanged("ShowsText");
            }
        }

        /// <summary>
        /// The local file with the bytes of a received video. It is not a
        /// [DataMember]: a video does not travel in the model (too much for one
        /// frame), so on the wire there is only the file name in the local cache.
        /// Empty until the bytes have arrived.
        /// </summary>
        public string MediaFilePath
        {
            get { return _mediaFilePath; }
            set
            {
                _mediaFilePath = value;
                OnPropertyChanged();
                OnPropertyChanged("IsVideo");
                OnPropertyChanged("ShowsText");
            }
        }

        private bool _isMediaLoading;

        /// <summary>
        /// True while the bytes of this media are arriving. It is not wire data:
        /// the page raises it when it asks for the media and the data service lowers
        /// it when the pieces are all there, or when the server says it is gone. It
        /// is what spins the indicator in the bubble.
        /// </summary>
        public bool IsMediaLoading
        {
            get { return _isMediaLoading; }
            set { _isMediaLoading = value; OnPropertyChanged(); }
        }

        /// <summary>Command of the control frames sent/received by the adapter (Type = System).</summary>
        [DataMember]
        public string Command
        {
            get { return _command; }
            set { _command = value; OnPropertyChanged(); }
        }

        /// <summary>WhatsApp connection state: "disconnected", "waiting" or "connected".</summary>
        [DataMember]
        public string State
        {
            get { return _state; }
            set { _state = value; OnPropertyChanged(); }
        }

        /// <summary>Pairing code to enter on the phone (phone-number login).</summary>
        [DataMember]
        public string PairCode
        {
            get { return _pairCode; }
            set { _pairCode = value; OnPropertyChanged(); }
        }

        /// <summary>Login QR code encoded in base64 (PNG).</summary>
        [DataMember]
        public string QrImageData
        {
            get { return _qrImageData; }
            set { _qrImageData = value; OnPropertyChanged(); }
        }

        /// <summary>Validity of the QR code, in seconds.</summary>
        [DataMember]
        public int QrDuration
        {
            get { return _qrDuration; }
            set { _qrDuration = value; OnPropertyChanged(); }
        }

        /// <summary>JID of the linked WhatsApp account (e.g. 393401234567@s.whatsapp.net).</summary>
        [DataMember]
        public string AccountJid
        {
            get { return _accountJid; }
            set { _accountJid = value; OnPropertyChanged(); }
        }

        /// <summary>Call identifier as GOWA knows it.</summary>
        [DataMember]
        public string CallId
        {
            get { return _callId; }
            set { _callId = value; OnPropertyChanged(); }
        }

        /// <summary>Call outcome according to GOWA: "timeout", "reject", ... Empty if it does not say.</summary>
        [DataMember]
        public string CallReason
        {
            get { return _callReason; }
            set { _callReason = value; OnPropertyChanged(); }
        }

        /// <summary>Call duration in seconds. 0 means "we do not know".</summary>
        [DataMember]
        public int CallDurationSeconds
        {
            get { return _callDurationSeconds; }
            set { _callDurationSeconds = value; OnPropertyChanged(); }
        }

        /// <summary>True if the call was a video call.</summary>
        [DataMember]
        public bool CallIsVideo
        {
            get { return _callIsVideo; }
            set { _callIsVideo = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// The token of the shared service. It travels in the `hello` frame, like
        /// everything else: this protocol has no headers to put it in. On a private
        /// service it stays empty, and the server does not ask for it.
        /// </summary>
        [DataMember]
        public string Token
        {
            get { return _token; }
            set { _token = value; OnPropertyChanged(); }
        }

        /// <summary>Id of the message a revocation or edit frame refers to.</summary>
        [DataMember]
        public string RelatedMessageId
        {
            get { return _relatedMessageId; }
            set { _relatedMessageId = value; OnPropertyChanged(); }
        }

        /// <summary>Profile picture of the chat, in base64. Empty if it has none.</summary>
        [DataMember]
        public string AvatarData
        {
            get { return _avatarData; }
            set { _avatarData = value; OnPropertyChanged(); }
        }

        /// <summary>True for groups: GOWA has no personal avatar for them.</summary>
        [DataMember]
        public bool IsGroup
        {
            get { return _isGroup; }
            set { _isGroup = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// History: an old message, which the adapter sends when the chat is
        /// opened. It is a normal message and must be drawn as such, but it did not
        /// arrive now: it does not count as unread and raises no notification.
        /// </summary>
        [DataMember]
        public bool IsHistory
        {
            get { return _isHistory; }
            set { _isHistory = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// How many messages of this conversation are still unread. It is not data
        /// of the message: it is data of the chat-list row, and the adapter counts
        /// it because it is the only one that sees the messages that arrived while
        /// the phone was off.
        /// </summary>
        [DataMember]
        public int UnreadCount
        {
            get { return _unreadCount; }
            set { _unreadCount = value; OnPropertyChanged(); }
        }

        public string FormattedTime
        {
            get { return _formattedTime; }
            set { _formattedTime = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Picture decoded from MediaData. It is not a [DataMember]: it stays
        /// client-side only and serves the XAML binding of the bubble.
        /// </summary>
        public BitmapImage MediaImage
        {
            get { return _mediaImage; }
            set { _mediaImage = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// An image inside a bubble: the bubble is at most ~250 px wide, so 320
        /// covers the margin as well. Decoding the whole file to draw it at a third
        /// of the size is the heaviest item a conversation can keep in memory.
        /// </summary>
        private const int MediaDecodePixels = 320;

        /// <summary>
        /// Decodes the bitmap of this message, from base64 or the local file, at
        /// the requested width. The bubble, the full screen and the preview of a
        /// just-chosen attachment use it.
        /// </summary>
        public async Task<BitmapImage> LoadBitmapAsync(int decodePixelWidth)
        {
            if (!string.IsNullOrEmpty(MediaData))
                return await ImageHelper.FromBase64Async(MediaData, decodePixelWidth);
            if (!string.IsNullOrEmpty(MediaFilePath))
                return await ImageHelper.FromFileAsync(MediaFilePath, decodePixelWidth);
            return null;
        }

        /// <summary>
        /// Decodes MediaData (base64) or the local file into MediaImage. It must
        /// be awaited on the UI thread: the stream must stay open until
        /// SetSourceAsync has finished.
        /// </summary>
        public async Task LoadMediaImageAsync()
        {
            if (Type != MessageType.Image) return;

            // Already decoded (e.g. coming back to the page): doing it again would
            // waste CPU and memory for an identical result.
            if (MediaImage != null) return;

            // An image inside a bubble is the heaviest thing that can be decoded,
            // and under pressure it is postponed until the phone breathes: in the
            // meantime the placeholder stays.
            if (MemoryWatcher.Instance.IsUnderPressure) return;
            if (string.IsNullOrEmpty(MediaData) && string.IsNullOrEmpty(MediaFilePath)) return;

            try
            {
                MediaImage = await LoadBitmapAsync(MediaDecodePixels);
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatMessage.LoadMediaImageAsync", ex);
                MediaImage = null;
            }
        }

        // For XAML binding to determine bubble alignment
        public bool IsOutgoing
        {
            get { return !IsIncoming; }
        }

        /// <summary>
        /// This bubble is a video. It is true even before the bytes arrive: a
        /// history row gives the type in MediaType, and the box with the triangle
        /// is shown all the same, so the file can be requested.
        /// </summary>
        public bool IsVideo
        {
            get
            {
                return Type == MessageType.Video
                    || string.Equals(MediaType, "video", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// This bubble is a voice note or audio. It is true even before the bytes
        /// arrive: the history row declares MediaType "audio".
        /// </summary>
        public bool IsAudio
        {
            get
            {
                return Type == MessageType.Audio
                    || string.Equals(MediaType, "audio", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// This bubble is a document. There is no MessageType for a document: the
        /// type declared by the server (MediaType "document") is the only thing that
        /// tells it from a text message, and on the wire the text is already the
        /// file name.
        /// </summary>
        public bool IsDocument
        {
            get { return string.Equals(MediaType, "document", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// There is something to write under the media. The type placeholder
        /// ("[Video]", "[Image not downloaded]") is not a caption: for a video the
        /// box with the triangle says it, and repeating the word underneath serves
        /// no purpose. For everything else the text stays what it was, so a caption
        /// does not disappear.
        /// </summary>
        public bool ShowsText
        {
            get
            {
                if (string.IsNullOrEmpty(Text)) return false;
                if (IsVideo && IsMediaPlaceholder) return false;
                return true;
            }
        }

        /// <summary>The text is only the placeholder the adapter writes for a media.</summary>
        private bool IsMediaPlaceholder
        {
            get
            {
                return !string.IsNullOrEmpty(Text)
                    && Text.Length >= 2
                    && Text[0] == '['
                    && Text[Text.Length - 1] == ']';
            }
        }

        // Convenience property: Is this message a media type (image/audio/video)?
        public bool IsMedia
        {
            get
            {
                return Type == MessageType.Image
                    || Type == MessageType.Audio
                    || Type == MessageType.Video;
            }
        }

        // Short text for media messages shown without loading the full image
        public string MediaTypeText
        {
            get
            {
                if (Type == MessageType.Image) return Loc.Get("ChatMessage_Photo", "Photo");
                if (Type == MessageType.Audio) return Loc.Get("ChatMessage_Audio", "Audio");
                if (Type == MessageType.Video) return Loc.Get("ChatMessage_Video", "Video");
                return Loc.Get("ChatMessage_File", "File");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>The first instant of the epoch, in UTC: the base of the Microsoft format.</summary>
        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Milliseconds from the epoch, in the /Date(ms)/ shape the other end reads.</summary>
        private static string FormatWire(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            long milliseconds = (long)(utc - Epoch).TotalMilliseconds;
            return "/Date(" + milliseconds.ToString(CultureInfo.InvariantCulture) + ")/";
        }

        /// <summary>
        /// Interprets the Timestamp field of a frame.
        ///
        /// It accepts /Date(ms)/ with any number of backslashes before the slashes
        /// - an older adapter doubled them, and those backslashes are escapes of the
        /// JSON reader, not part of the value -, an ISO 8601 date with or without a
        /// zone, and raw milliseconds. Anything else becomes the current time, with
        /// the Diag line saying what was wrong: an unreadable field must not make
        /// the message disappear.
        /// </summary>
        private static DateTime ParseWire(string value)
        {
            if (string.IsNullOrEmpty(value)) return DateTime.Now;

            string text = value.Replace("\\", "").Trim();

            const string Prefix = "/Date(";
            if (text.StartsWith(Prefix, StringComparison.Ordinal)
                && text.EndsWith(")/", StringComparison.Ordinal))
            {
                string inner = text.Substring(Prefix.Length, text.Length - Prefix.Length - 2);
                long fromWire;
                if (long.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture, out fromWire))
                {
                    // FormatTime expects a local date: the wire carries UTC.
                    return Epoch.AddMilliseconds(fromWire).ToLocalTime();
                }
            }

            long raw;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw))
            {
                return Epoch.AddMilliseconds(raw).ToLocalTime();
            }

            DateTime parsed;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed.ToLocalTime();
            }

            Diag.Failed("ChatMessage/Timestamp",
                new FormatException("unrecognized date: " + text));
            return DateTime.Now;
        }

        private static string FormatTime(DateTime dt)
        {
            // The serializer reads /Date(ms)/ as UTC: without this conversion the
            // time shown is offset from the phone one.
            if (dt.Kind == DateTimeKind.Utc)
                dt = dt.ToLocalTime();

            var now = DateTime.Now;
            if (dt.Date == now.Date)
                return dt.ToString("HH:mm");
            if (dt.Date == now.Date.AddDays(-1))
                return Loc.Get("ChatMessage_Yesterday", "Yesterday");
            if (dt.Year == now.Year)
                return dt.ToString("dd/MM");
            return dt.ToString("dd/MM/yy");
        }

        public string ToJson()
        {
            using (var ms = new MemoryStream())
            {
                JsonSerializer.WriteObject(ms, this);
                return Encoding.UTF8.GetString(ms.ToArray(), 0, (int)ms.Length);
            }
        }

        public static ChatMessage FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return (ChatMessage)JsonSerializer.ReadObject(ms);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatMessage.FromJson", ex);
                return null;
            }
        }
    }
}

