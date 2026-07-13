using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WhatsappApp.Models
{
    public class Contact : INotifyPropertyChanged
    {
        private string _id;
        private string _name;
        private string _status;
        private string _lastMessage;
        private string _lastMessageTime;
        private string _initials;
        private string _avatarColor;
        private bool _isOnline;
        private int _unreadCount;
        private string _avatarUri;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        public string LastMessage
        {
            get => _lastMessage;
            set { _lastMessage = value; OnPropertyChanged(); }
        }

        public string LastMessageTime
        {
            get => _lastMessageTime;
            set { _lastMessageTime = value; OnPropertyChanged(); }
        }

        public string Initials
        {
            get => _initials;
            set { _initials = value; OnPropertyChanged(); }
        }

        public string AvatarColor
        {
            get => _avatarColor;
            set { _avatarColor = value; OnPropertyChanged(); }
        }

        public bool IsOnline
        {
            get => _isOnline;
            set { _isOnline = value; OnPropertyChanged(); }
        }

        public int UnreadCount
        {
            get => _unreadCount;
            set { _unreadCount = value; OnPropertyChanged(); }
        }

        public string AvatarUri
        {
            get => _avatarUri;
            set { _avatarUri = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
