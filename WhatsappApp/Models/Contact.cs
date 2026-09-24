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
            get { return _id; }
            set { _id = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get { return _name; }
            set { _name = value; OnPropertyChanged(); }
        }

        public string Status
        {
            get { return _status; }
            set { _status = value; OnPropertyChanged(); }
        }

        public string LastMessage
        {
            get { return _lastMessage; }
            set { _lastMessage = value; OnPropertyChanged(); }
        }

        public string LastMessageTime
        {
            get { return _lastMessageTime; }
            set { _lastMessageTime = value; OnPropertyChanged(); }
        }

        public string Initials
        {
            get { return _initials; }
            set { _initials = value; OnPropertyChanged(); }
        }

        public string AvatarColor
        {
            get { return _avatarColor; }
            set { _avatarColor = value; OnPropertyChanged(); }
        }

        public bool IsOnline
        {
            get { return _isOnline; }
            set { _isOnline = value; OnPropertyChanged(); }
        }

        public int UnreadCount
        {
            get { return _unreadCount; }
            set { _unreadCount = value; OnPropertyChanged(); }
        }

        public string AvatarUri
        {
            get { return _avatarUri; }
            set { _avatarUri = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
