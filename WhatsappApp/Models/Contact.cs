using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media.Imaging;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    public class Contact : INotifyPropertyChanged
    {
        private string _id;
        private string _name;
        private string _lastMessage;
        private string _lastMessageTime;
        private string _initials;
        private int _unreadCount;
        private bool _isPinned;
        private bool _isMuted;
        private string _avatarData;
        private BitmapImage _avatar;

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

        public int UnreadCount
        {
            get { return _unreadCount; }
            set { _unreadCount = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// At the top of the list. It is a decision of this phone
        /// (ChatPreferences): WhatsApp knows nothing of a pin made here, and the
        /// adapter sends no field for it.
        /// </summary>
        public bool IsPinned
        {
            get { return _isPinned; }
            set { _isPinned = value; OnPropertyChanged(); }
        }

        /// <summary>The messages of this chat raise no notification. The unread count stays.</summary>
        public bool IsMuted
        {
            get { return _isMuted; }
            set { _isMuted = value; OnPropertyChanged(); }
        }

        /// <summary>The profile picture that arrived from the adapter, still in base64.</summary>
        public string AvatarData
        {
            get { return _avatarData; }
            set { _avatarData = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// The decoded picture. It is not data that comes off the wire:
        /// LoadAvatarAsync builds it, and the XAML uses it in place of the
        /// initials.
        /// </summary>
        public BitmapImage Avatar
        {
            get { return _avatar; }
            set
            {
                _avatar = value;
                OnPropertyChanged();
                OnPropertyChanged("HasAvatar");
            }
        }

        /// <summary>True when there is a picture to show in place of the initials.</summary>
        public bool HasAvatar
        {
            get { return _avatar != null; }
        }

        /// <summary>
        /// The size at which the XAML draws the avatar: the row box is a 52 px
        /// square. Decoding any larger is wasted memory: what arrives from the
        /// server is a full-size image.
        /// </summary>
        public const int AvatarDecodePixels = 52;

        /// <summary>
        /// Decodes AvatarData once. It must be awaited on the UI thread, as
        /// ImageHelper requires: BitmapImage is not view-agnostic.
        /// </summary>
        public async Task LoadAvatarAsync()
        {
            if (_avatar != null || string.IsNullOrEmpty(_avatarData)) return;

            // Under pressure nothing is decoded: without this line the first
            // frame of conversations puts back everything just freed.
            if (MemoryWatcher.Instance.IsUnderPressure) return;

            try
            {
                Avatar = await ImageHelper.FromBase64Async(_avatarData, AvatarDecodePixels);
            }
            catch (Exception ex)
            {
                Diag.Failed("Contact.LoadAvatarAsync", ex);
                Avatar = null;
            }
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
