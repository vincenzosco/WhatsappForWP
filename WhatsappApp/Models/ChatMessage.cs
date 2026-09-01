using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.IO;
using System.Text;

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

        [DataMember]
        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string SenderId
        {
            get => _senderId;
            set { _senderId = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string SenderName
        {
            get => _senderName;
            set { _senderName = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string ChatId
        {
            get => _chatId;
            set { _chatId = value; OnPropertyChanged(); }
        }

        [DataMember]
        public DateTime Timestamp
        {
            get => _timestamp;
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
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        [DataMember]
        public MessageType Type
        {
            get => _type;
            set { _type = value; OnPropertyChanged(); }
        }

        [DataMember]
        public bool IsIncoming
        {
            get => _isIncoming;
            set { _isIncoming = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string MediaData
        {
            get => _mediaData;
            set { _mediaData = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string MediaMimeType
        {
            get => _mediaMimeType;
            set { _mediaMimeType = value; OnPropertyChanged(); }
        }

        [DataMember]
        public string MediaFileName
        {
            get => _mediaFileName;
            set { _mediaFileName = value; OnPropertyChanged(); }
        }

        public string FormattedTime
        {
            get => _formattedTime;
            set { _formattedTime = value; OnPropertyChanged(); }
        }

        // For XAML binding to determine bubble alignment
        public bool IsOutgoing => !IsIncoming;

        // Convenience property: Is this message a media type (image/audio)?
        public bool IsMedia => Type == MessageType.Image || Type == MessageType.Audio;

        // Short text for media messages shown without loading the full image
        public string MediaTypeText
        {
            get
            {
                if (Type == MessageType.Image) return "Foto";
                if (Type == MessageType.Audio) return "Audio";
                return "File";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static string FormatTime(DateTime dt)
        {
            var now = DateTime.Now;
            if (dt.Date == now.Date)
                return dt.ToString("HH:mm");
            if (dt.Date == now.Date.AddDays(-1))
                return "Ieri";
            if (dt.Year == now.Year)
                return dt.ToString("dd/MM");
            return dt.ToString("dd/MM/yy");
        }

        public string ToJson()
        {
            using (var ms = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(ChatMessage));
                serializer.WriteObject(ms, this);
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
                    var serializer = new DataContractJsonSerializer(typeof(ChatMessage));
                    return (ChatMessage)serializer.ReadObject(ms);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}

