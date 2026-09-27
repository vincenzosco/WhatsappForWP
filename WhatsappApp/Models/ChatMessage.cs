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

        // Il valore del campo Timestamp cosi' come e' arrivato. E' una stringa e
        // non un DateTime perche' la deserializzazione non deve poter fallire:
        // una data che il telefono non riconosce faceva cadere l'intero frame
        // ("String was not recognized as a valid DateTime", 0x8013150C) e il
        // messaggio spariva senza che l'utente vedesse niente.
        private string _timestampWire;
        private MessageStatus _status;
        private MessageType _type;
        private bool _isIncoming;
        private string _formattedTime;
        private string _mediaData;      // base64-encoded media content
        private string _mediaMimeType;  // e.g. "image/jpeg", "image/png"
        private string _mediaFileName;  // optional filename
        private string _mediaTransferId;   // id di un allegato che viaggia a pezzi
        private int _mediaChunkIndex;      // quale pezzo e' questo
        private int _mediaChunkTotal;      // quanti pezzi in tutto
        private string _mediaType;         // "image", "video": il tipo dichiarato dal server
        private string _command;        // control frame command (see adapter protocol)
        private string _state;          // "disconnected" | "waiting" | "connected"
        private string _pairCode;       // pairing code for phone-number login
        private string _qrImageData;    // base64 PNG of the login QR code
        private int _qrDuration;        // QR validity in seconds
        private string _avatarData;     // immagine del profilo, base64
        private bool _isGroup;          // la chat e' un gruppo
        private bool _isHistory;        // messaggio vecchio, mandato aprendo la chat
        private int _unreadCount;       // riga dell'elenco chat: quanti non letti
        private string _accountJid;     // WhatsApp JID of the logged-in account
        private string _callId;             // id della chiamata, da GOWA
        private string _callReason;         // esito riportato da GOWA (timeout, reject, ...)
        private int _callDurationSeconds;   // durata in secondi, 0 se sconosciuta
        private bool _callIsVideo;          // chiamata video
        private string _relatedMessageId;   // messaggio toccato da una revoca o una modifica
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

        /// <summary>
        /// Il campo che viaggia sul filo, cosi' com'e'. Quando il messaggio e'
        /// stato costruito qui (e non letto da un frame) e' vuoto, e il getter
        /// lo scrive dal DateTime: e' l'unica sorgente della forma /Date(ms)/.
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

        /// <summary>La stessa data come la usa l'app. Non e' un [DataMember]: sul filo va la stringa.</summary>
        public DateTime Timestamp
        {
            get { return _timestamp; }
            set
            {
                _timestamp = value;
                _timestampWire = null;   // si riscrive dal DateTime alla prossima serializzazione
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

        /// <summary>Identificativo di un allegato che viaggia a pezzi (media.begin/end).</summary>
        [DataMember]
        public string MediaTransferId
        {
            get { return _mediaTransferId; }
            set { _mediaTransferId = value; OnPropertyChanged(); }
        }

        /// <summary>Quale pezzo di un allegato e' questo frame.</summary>
        [DataMember]
        public int MediaChunkIndex
        {
            get { return _mediaChunkIndex; }
            set { _mediaChunkIndex = value; OnPropertyChanged(); }
        }

        /// <summary>Quanti pezzi ha in tutto l'allegato.</summary>
        [DataMember]
        public int MediaChunkTotal
        {
            get { return _mediaChunkTotal; }
            set { _mediaChunkTotal = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Il tipo di media come lo dichiara il server ("image", "video"). Non e'
        /// il tipo MIME: serve a sapere che una riga di cronologia *e'* un'immagine
        /// anche quando i byte non sono arrivati.
        /// </summary>
        [DataMember]
        public string MediaType
        {
            get { return _mediaType; }
            set { _mediaType = value; OnPropertyChanged(); }
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

        /// <summary>Identificativo della chiamata come lo conosce GOWA.</summary>
        [DataMember]
        public string CallId
        {
            get { return _callId; }
            set { _callId = value; OnPropertyChanged(); }
        }

        /// <summary>Esito della chiamata secondo GOWA: "timeout", "reject", ... Vuoto se non lo dice.</summary>
        [DataMember]
        public string CallReason
        {
            get { return _callReason; }
            set { _callReason = value; OnPropertyChanged(); }
        }

        /// <summary>Durata della chiamata in secondi. 0 significa "non lo sappiamo".</summary>
        [DataMember]
        public int CallDurationSeconds
        {
            get { return _callDurationSeconds; }
            set { _callDurationSeconds = value; OnPropertyChanged(); }
        }

        /// <summary>Vero se la chiamata era video.</summary>
        [DataMember]
        public bool CallIsVideo
        {
            get { return _callIsVideo; }
            set { _callIsVideo = value; OnPropertyChanged(); }
        }

        /// <summary>Id del messaggio a cui si riferisce un frame di revoca o modifica.</summary>
        [DataMember]
        public string RelatedMessageId
        {
            get { return _relatedMessageId; }
            set { _relatedMessageId = value; OnPropertyChanged(); }
        }

        /// <summary>Immagine del profilo della chat, in base64. Vuota se non ce l'ha.</summary>
        [DataMember]
        public string AvatarData
        {
            get { return _avatarData; }
            set { _avatarData = value; OnPropertyChanged(); }
        }

        /// <summary>Vero per i gruppi: GOWA non ha un avatar personale per loro.</summary>
        [DataMember]
        public bool IsGroup
        {
            get { return _isGroup; }
            set { _isGroup = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Cronologia: un messaggio vecchio, che l'adapter manda aprendo la chat.
        /// E' un messaggio normale e va disegnato come tale, ma non e' arrivato
        /// adesso: non conta come non letto e non alza nessun avviso.
        /// </summary>
        [DataMember]
        public bool IsHistory
        {
            get { return _isHistory; }
            set { _isHistory = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Quanti messaggi di questa conversazione non sono ancora stati letti.
        /// Non e' un dato del messaggio: e' un dato della riga dell'elenco chat,
        /// e l'adapter lo conta perche' e' l'unico che vede i messaggi arrivati
        /// mentre il telefono era spento.
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
        /// Immagine decodificata da MediaData. Non è un [DataMember]: resta
        /// solo lato client e serve al binding XAML della bolla.
        /// </summary>
        public BitmapImage MediaImage
        {
            get { return _mediaImage; }
            set { _mediaImage = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Un'immagine dentro un fumetto: la bolla e' larga al massimo ~250 px,
        /// quindi 320 copre anche il margine. Decodificare il file intero per
        /// disegnarlo a un terzo della misura e' la voce piu' pesante che una
        /// conversazione puo' tenere in memoria.
        /// </summary>
        private const int MediaDecodePixels = 320;

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

            // Un'immagine dentro un fumetto e' la cosa piu' pesante che si possa
            // decodificare, e sotto pressione si rimanda a quando il telefono
            // respira: nel frattempo resta il segnaposto.
            if (MemoryWatcher.Instance.IsUnderPressure) return;

            try
            {
                MediaImage = await ImageHelper.FromBase64Async(MediaData, MediaDecodePixels);
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

        /// <summary>Il primo istante dell'epoch, in UTC: la base del formato Microsoft.</summary>
        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Millisecondi dall'epoch, nella forma /Date(ms)/ che l'altro capo legge.</summary>
        private static string FormatWire(DateTime value)
        {
            DateTime utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            long milliseconds = (long)(utc - Epoch).TotalMilliseconds;
            return "/Date(" + milliseconds.ToString(CultureInfo.InvariantCulture) + ")/";
        }

        /// <summary>
        /// Interpreta il campo Timestamp di un frame.
        ///
        /// Accetta /Date(ms)/ con un numero qualsiasi di backslash davanti agli
        /// slash - un adattatore piu' vecchio li raddoppiava, e quei backslash
        /// sono escape del lettore JSON, non parte del valore -, una data ISO
        /// 8601 con o senza fuso, e i millisecondi nudi. Qualunque altra cosa
        /// diventa l'ora attuale, con la riga di Diag che dice cosa non andava:
        /// un campo illeggibile non deve far sparire il messaggio.
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
                    // FormatTime si aspetta una data locale: il filo porta UTC.
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
                new FormatException("data non riconosciuta: " + text));
            return DateTime.Now;
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

