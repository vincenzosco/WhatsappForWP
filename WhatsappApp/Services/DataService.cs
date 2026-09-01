using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Central data service that holds contacts and messages, 
    /// bridges the communication service with the UI.
    /// </summary>
    public class DataService : INotifyPropertyChanged
    {
        private static DataService _instance;
        public static DataService Instance => _instance ?? (_instance = new DataService());

        private readonly ObservableCollection<Contact> _contacts;
        private readonly Dictionary<string, ObservableCollection<ChatMessage>> _chatMessages;
        private Contact _selectedContact;
        private string _connectionStatus;
        private bool _isServerRunning;

        public ObservableCollection<Contact> Contacts => _contacts;
        public Contact SelectedContact
        {
            get => _selectedContact;
            set { _selectedContact = value; OnPropertyChanged(); }
        }
        public string ConnectionStatus
        {
            get => _connectionStatus;
            set { _connectionStatus = value; OnPropertyChanged(); }
        }
        public bool IsServerRunning
        {
            get => _isServerRunning;
            set { _isServerRunning = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private DataService()
        {
            _contacts = new ObservableCollection<Contact>();
            _chatMessages = new Dictionary<string, ObservableCollection<ChatMessage>>();
            LoadSampleData();

            // Wire up to receive network messages
            CommunicationService.Instance.MessageReceived += OnNetworkMessageReceived;
        }

        private void OnNetworkMessageReceived(object sender, ChatMessage message)
        {
            // Ignore system/handshake messages
            if (message.Type == MessageType.System) return;

            // Add to the appropriate chat's message list
            if (!_chatMessages.ContainsKey(message.ChatId))
            {
                _chatMessages[message.ChatId] = new ObservableCollection<ChatMessage>();
            }
            _chatMessages[message.ChatId].Add(message);

            // Find or create contact for this chat
            var contact = _contacts.FirstOrDefault(c => c.Id == message.ChatId);
            if (contact == null)
            {
                contact = new Contact
                {
                    Id = message.ChatId,
                    Name = message.SenderName,
                    LastMessage = message.Text,
                    LastMessageTime = message.FormattedTime,
                    Initials = message.SenderName.Length > 0 ? message.SenderName.Substring(0, 1).ToUpper() : "?",
                    AvatarColor = "#FF075E54",
                    IsOnline = true,
                    UnreadCount = 0
                };
                _contacts.Insert(0, contact);
            }
            else
            {
                // Update the contact preview and move to top
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                if (message.IsIncoming)
                    contact.UnreadCount++;

                var idx = _contacts.IndexOf(contact);
                if (idx > 0)
                    _contacts.Move(idx, 0);
            }
        }

        private void LoadSampleData()
        {
            var contacts = new List<Contact>
            {
                new Contact { Id = "1", Name = "Comunità WhatsApp", Status = "Benvenuto nella community!", LastMessage = "Ciao a tutti! Benvenuti nell'app community.", LastMessageTime = "10:30", Initials = "CW", AvatarColor = "#FF075E54", IsOnline = true, UnreadCount = 2 },
                new Contact { Id = "2", Name = "Mamma", Status = "Online", LastMessage = "Va tutto bene. A dopo!", LastMessageTime = "09:15", Initials = "M", AvatarColor = "#FF128C7E", IsOnline = true, UnreadCount = 0 },
                new Contact { Id = "3", Name = "Papa", Status = "Al lavoro", LastMessage = "Arrivo tra 10 minuti", LastMessageTime = "Ieri", Initials = "P", AvatarColor = "#FF25D366", IsOnline = false, UnreadCount = 0 },
                new Contact { Id = "4", Name = "Anna", Status = "Occupata", LastMessage = "Ci sentiamo domani", LastMessageTime = "Ieri", Initials = "A", AvatarColor = "#FFE91E63", IsOnline = false, UnreadCount = 1 },
                new Contact { Id = "5", Name = "Marco", Status = "in vacanza", LastMessage = "Che bella la spiaggia!", LastMessageTime = "Lun", Initials = "M", AvatarColor = "#FF9C27B0", IsOnline = false, UnreadCount = 0 },
                new Contact { Id = "6", Name = "Giulia", Status = "Ascoltando musica", LastMessage = "Hai visto quel film?", LastMessageTime = "Lun", Initials = "G", AvatarColor = "#FFFF5722", IsOnline = true, UnreadCount = 3 },
                new Contact { Id = "7", Name = "Lorenzo", Status = "Disponibile", LastMessage = "Grazie mille!", LastMessageTime = "Dom", Initials = "L", AvatarColor = "#FF00BCD4", IsOnline = false, UnreadCount = 0 },
                new Contact { Id = "8", Name = "Sophia", Status = "In riunione...", LastMessage = "Perfetto, ci vediamo lì", LastMessageTime = "Sab", Initials = "S", AvatarColor = "#FF4CAF50", IsOnline = true, UnreadCount = 0 },
                new Contact { Id = "9", Name = "Gruppo Amici", Status = "35 partecipanti", LastMessage = "Simone: Chi viene stasera?", LastMessageTime = "Ven", Initials = "GA", AvatarColor = "#FF607D8B", IsOnline = false, UnreadCount = 5 },
                new Contact { Id = "10", Name = "Nonno", Status = "Ultimo accesso oggi 08:30", LastMessage = "Ti voglio bene", LastMessageTime = "10/03", Initials = "NN", AvatarColor = "#FF795548", IsOnline = false, UnreadCount = 0 },
            };

            foreach (var c in contacts)
                _contacts.Add(c);

            // Add sample messages for the first contact
            var communityMessages = new ObservableCollection<ChatMessage>
            {
                new ChatMessage { Id = "c1", Text = "Ciao a tutti! Benvenuti nella nuova app WhatsApp Community!", SenderId = "sys", SenderName = "Sistema", ChatId = "1", Timestamp = DateTime.Now.AddHours(-2), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "c2", Text = "Questa app è stata creata dalla community per la community", SenderId = "dev", SenderName = "Sviluppatore", ChatId = "1", Timestamp = DateTime.Now.AddHours(-1).AddMinutes(-30), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "c3", Text = "Fantastico! Funziona benissimo sul mio Lumia", SenderId = "user1", SenderName = "Mario", ChatId = "1", Timestamp = DateTime.Now.AddHours(-1), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "c4", Text = "Grazie a tutti per il supporto! Continuate a testare l'app e segnalate bug!", SenderId = "dev", SenderName = "Sviluppatore", ChatId = "1", Timestamp = DateTime.Now.AddHours(-1).AddMinutes(-15), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "c5", Text = "Assolutamente! Ottimo lavoro!", SenderId = "user2", SenderName = "Luigi", ChatId = "1", Timestamp = DateTime.Now.AddHours(-1).AddMinutes(-10), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "c6", Text = "Ragazzi, sapete se funziona anche in Italia?", SenderId = "user3", SenderName = "Chiara", ChatId = "1", Timestamp = DateTime.Now.AddHours(-1).AddMinutes(-5), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "c7", Text = "Certo! Funziona ovunque ci sia una connessione internet!", SenderId = "dev", SenderName = "Sviluppatore", ChatId = "1", Timestamp = DateTime.Now.AddHours(-1).AddMinutes(-2), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
            };
            _chatMessages["1"] = communityMessages;

            // Messages for mamma
            var mammaMessages = new ObservableCollection<ChatMessage>
            {
                new ChatMessage { Id = "m1", Text = "Buongiorno! Come stai oggi?", SenderId = "mom", SenderName = "Mamma", ChatId = "2", Timestamp = DateTime.Now.AddHours(-3), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "m2", Text = "Bene grazie mamma! Tutto ok da voi?", SenderId = "me", SenderName = "Io", ChatId = "2", Timestamp = DateTime.Now.AddHours(-2).AddMinutes(-45), Type = MessageType.Text, IsIncoming = false, Status = MessageStatus.Read },
                new ChatMessage { Id = "m3", Text = "Sì, tutto bene! Tuo papà è uscito a fare la spesa", SenderId = "mom", SenderName = "Mamma", ChatId = "2", Timestamp = DateTime.Now.AddHours(-2).AddMinutes(-30), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "m4", Text = "Bene dai, ci vediamo stasera?", SenderId = "me", SenderName = "Io", ChatId = "2", Timestamp = DateTime.Now.AddHours(-2).AddMinutes(-15), Type = MessageType.Text, IsIncoming = false, Status = MessageStatus.Read },
                new ChatMessage { Id = "m5", Text = "Certo! Ti aspetto per cena", SenderId = "mom", SenderName = "Mamma", ChatId = "2", Timestamp = DateTime.Now.AddHours(-2).AddMinutes(-10), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "m6", Text = "Va tutto bene. A dopo!", SenderId = "mom", SenderName = "Mamma", ChatId = "2", Timestamp = DateTime.Now.AddHours(-1), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
            };
            _chatMessages["2"] = mammaMessages;

            // Messages for Anna
            var annaMessages = new ObservableCollection<ChatMessage>
            {
                new ChatMessage { Id = "a1", Text = "Ehi! Come va?", SenderId = "anna", SenderName = "Anna", ChatId = "4", Timestamp = DateTime.Now.AddDays(-1).AddHours(-5), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "a2", Text = "Ciao! Tutto bene, tu?", SenderId = "me", SenderName = "Io", ChatId = "4", Timestamp = DateTime.Now.AddDays(-1).AddHours(-4).AddMinutes(-30), Type = MessageType.Text, IsIncoming = false, Status = MessageStatus.Read },
                new ChatMessage { Id = "a3", Text = "Sì, tutto ok! Volevo chiederti una cosa...", SenderId = "anna", SenderName = "Anna", ChatId = "4", Timestamp = DateTime.Now.AddDays(-1).AddHours(-4), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "a4", Text = "Dimmi pure!", SenderId = "me", SenderName = "Io", ChatId = "4", Timestamp = DateTime.Now.AddDays(-1).AddHours(-3).AddMinutes(-45), Type = MessageType.Text, IsIncoming = false, Status = MessageStatus.Read },
                new ChatMessage { Id = "a5", Text = "Ci sentiamo domani", SenderId = "anna", SenderName = "Anna", ChatId = "4", Timestamp = DateTime.Now.AddDays(-1).AddHours(-3), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
            };
            _chatMessages["4"] = annaMessages;

            // Messages for Giulia
            var giuliaMessages = new ObservableCollection<ChatMessage>
            {
                new ChatMessage { Id = "g1", Text = "Ciao! Sei andato a vedere quel nuovo film?", SenderId = "giulia", SenderName = "Giulia", ChatId = "6", Timestamp = DateTime.Now.AddDays(-3), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "g2", Text = "Non ancora! Mi dicevi che è bello?", SenderId = "me", SenderName = "Io", ChatId = "6", Timestamp = DateTime.Now.AddDays(-3), Type = MessageType.Text, IsIncoming = false, Status = MessageStatus.Read },
                new ChatMessage { Id = "g3", Text = "Sì, bellissimo! Dovresti andare assolutamente", SenderId = "giulia", SenderName = "Giulia", ChatId = "6", Timestamp = DateTime.Now.AddDays(-3), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
                new ChatMessage { Id = "g4", Text = "Hai visto quel film?", SenderId = "giulia", SenderName = "Giulia", ChatId = "6", Timestamp = DateTime.Now.AddDays(-1), Type = MessageType.Text, IsIncoming = true, Status = MessageStatus.Read },
            };
            _chatMessages["6"] = giuliaMessages;
        }

        public ObservableCollection<ChatMessage> GetMessages(string chatId)
        {
            if (!_chatMessages.ContainsKey(chatId))
            {
                _chatMessages[chatId] = new ObservableCollection<ChatMessage>();
            }
            return _chatMessages[chatId];
        }

        public void AddMessage(string chatId, ChatMessage message)
        {
            if (!_chatMessages.ContainsKey(chatId))
            {
                _chatMessages[chatId] = new ObservableCollection<ChatMessage>();
            }
            _chatMessages[chatId].Add(message);

            // Update the contact's last message
            var contact = _contacts.FirstOrDefault(c => c.Id == chatId);
            if (contact != null)
            {
                contact.LastMessage = message.Text;
                contact.LastMessageTime = message.FormattedTime;
                if (message.IsIncoming)
                    contact.UnreadCount++;

                // Move contact to top
                _contacts.Move(_contacts.IndexOf(contact), 0);
            }
        }

        public void ClearUnread(string chatId)
        {
            var contact = _contacts.FirstOrDefault(c => c.Id == chatId);
            if (contact != null)
                contact.UnreadCount = 0;
        }

        public void AddContact(Contact contact)
        {
            _contacts.Insert(0, contact);
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
