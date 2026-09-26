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

        /// <summary>L'immagine del profilo arrivata dall'adapter, ancora in base64.</summary>
        public string AvatarData
        {
            get { return _avatarData; }
            set { _avatarData = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// L'immagine decodificata. Non e' un dato che arriva dal filo: la
        /// costruisce LoadAvatarAsync, e la XAML la usa al posto delle iniziali.
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

        /// <summary>Vero quando c'e' un'immagine da mostrare al posto delle iniziali.</summary>
        public bool HasAvatar
        {
            get { return _avatar != null; }
        }

        /// <summary>
        /// Decodifica AvatarData una volta sola. Va atteso sul thread UI, come
        /// richiede ImageHelper: BitmapImage non e' agnostico rispetto alla view.
        /// </summary>
        public async Task LoadAvatarAsync()
        {
            if (_avatar != null || string.IsNullOrEmpty(_avatarData)) return;
            try
            {
                Avatar = await ImageHelper.FromBase64Async(_avatarData);
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
