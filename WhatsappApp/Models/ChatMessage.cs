using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
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
        System
    }

    [DataContract]
    public class ChatMessage : INotifyPropertyChanged
    {
        private string _id;
        private string _text;
        private string _senderId;
        private string _senderName;
        private string _chatId;
        private DateTime _timestamp;
        private MessageStatus _status;
        private MessageType _type;
        private bool _isIncoming;
        private string _formattedTime;
        private string _mediaData;      // base64-encoded media content
        private string _mediaMimeType;  // e.g. "image/jpeg", "image/png"
        private string _mediaFileName;  // optional filename
        private string _command;        // control frame command (see adapter protocol)
        private string _state;          // "disconnected" | "waiting" | "connected"
        private string _pairCode;       // pairing code for phone-number login
        private string _qrImageData;    // base64 PNG of the login QR code
        private int _qrDuration;        // QR validity in seconds
        private string _accountJid;     // WhatsApp JID of the logged-in account
        private BitmapImage _mediaImage; // decoded MediaData, for the XAML image binding

        // Un serializer per tipo, non uno per messaggio: DataContractJsonSerializer
        // costruisce internamente il grafo del contratto a ogni istanza.
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
            set { _text = value; OnPropertyChanged(); }
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

        [DataMember]
        public DateTime Timestamp
        {
            get { return _timestamp; }
            set
            {
                _timestamp = value;
                FormattedTime = FormatTime(value);
                OnPropertyChanged();
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
            set { _type = value; OnPropertyChanged(); }
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

        /// <summary>Comando dei frame di controllo inviati/ricevuti dall'adapter (Type = System).</summary>
        [DataMember]
        public string Command
        {
            get { return _command; }
            set { _command = value; OnPropertyChanged(); }
        }

        /// <summary>Stato della connessione WhatsApp: "disconnected", "waiting" o "connected".</summary>
        [DataMember]
        public string State
        {
            get { return _state; }
            set { _state = value; OnPropertyChanged(); }
        }

        /// <summary>Codice di abbinamento da inserire sul telefono (login via numero).</summary>
        [DataMember]
        public string PairCode
        {
            get { return _pairCode; }
            set { _pairCode = value; OnPropertyChanged(); }
        }

        /// <summary>QR code di login codificato in base64 (PNG).</summary>
        [DataMember]
        public string QrImageData
        {
            get { return _qrImageData; }
            set { _qrImageData = value; OnPropertyChanged(); }
        }

        /// <summary>Durata di validità del QR code, in secondi.</summary>
        [DataMember]
        public int QrDuration
        {
            get { return _qrDuration; }
            set { _qrDuration = value; OnPropertyChanged(); }
        }

        /// <summary>JID dell'account WhatsApp collegato (es. 393401234567@s.whatsapp.net).</summary>
        [DataMember]
        public string AccountJid
        {
            get { return _accountJid; }
            set { _accountJid = value; OnPropertyChanged(); }
        }

        public string FormattedTime
        {
            get { return _formattedTime; }
            set { _formattedTime = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Immagine decodificata da MediaData. Non è un [DataMember]: resta
        /// solo lato client e serve al binding XAML della bolla.
        /// </summary>
        public BitmapImage MediaImage
        {
            get { return _mediaImage; }
            set { _mediaImage = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Decodifica MediaData (base64) in MediaImage. Va atteso sul thread UI:
        /// il flusso deve restare aperto finché SetSourceAsync non ha finito.
        /// </summary>
        public async Task LoadMediaImageAsync()
        {
            if (Type != MessageType.Image || string.IsNullOrEmpty(MediaData)) return;

            // Gia' decodificata (es. si torna sulla pagina): rifarlo sprecherebbe
            // CPU e memoria per un risultato identico.
            if (MediaImage != null) return;

            try
            {
                MediaImage = await ImageHelper.FromBase64Async(MediaData);
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

        // Convenience property: Is this message a media type (image/audio)?
        public bool IsMedia
        {
            get { return Type == MessageType.Image || Type == MessageType.Audio; }
        }

        // Short text for media messages shown without loading the full image
        public string MediaTypeText
        {
            get
            {
                if (Type == MessageType.Image) return Loc.Get("ChatMessage_Photo", "Photo");
                if (Type == MessageType.Audio) return Loc.Get("ChatMessage_Audio", "Audio");
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

        private static string FormatTime(DateTime dt)
        {
            // Il serializer legge /Date(ms)/ come UTC: senza questa conversione
            // l'orario mostrato è sfasato rispetto a quello del telefono.
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

