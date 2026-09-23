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
    /// I contatti arrivano dall'adapter (GOWA), non più da dati di esempio.
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

            // Wire up to receive network messages and adapter control frames
            CommunicationService.Instance.MessageReceived += OnNetworkMessageReceived;
            CommunicationService.Instance.ControlMessageReceived += OnControlMessageReceived;
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
                string name = string.IsNullOrEmpty(message.SenderName)
                    ? DisplayNameForJid(message.ChatId)
                    : message.SenderName;

                contact = new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    LastMessage = message.Text,
                    LastMessageTime = message.FormattedTime,
                    Initials = InitialsFor(name),
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

        /// <summary>
        /// Gestisce i frame di controllo in arrivo dall'adapter: per ora la
        /// sincronizzazione dei contatti ("contact").
        /// </summary>
        private void OnControlMessageReceived(object sender, ChatMessage message)
        {
            if (message == null || message.Command != "contact" || string.IsNullOrEmpty(message.ChatId))
                return;

            var contact = _contacts.FirstOrDefault(c => c.Id == message.ChatId);
            string name = string.IsNullOrEmpty(message.SenderName)
                ? DisplayNameForJid(message.ChatId)
                : message.SenderName;

            if (contact == null)
            {
                _contacts.Add(new Contact
                {
                    Id = message.ChatId,
                    Name = name,
                    Status = "",
                    Initials = InitialsFor(name),
                    AvatarColor = "#FF075E54",
                    IsOnline = false,
                    UnreadCount = 0
                });
            }
            else
            {
                contact.Name = name;
                contact.Initials = InitialsFor(name);
            }
        }

        /// <summary>Nome mostrato per un JID quando non ne conosciamo il nome.</summary>
        public static string DisplayNameForJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "?";
            string user = jid.Split('@')[0];
            if (jid.EndsWith("@g.us")) return "Gruppo " + user;
            if (user.Length >= 8 && user.All(char.IsDigit)) return "+" + user;
            return string.IsNullOrEmpty(user) ? "?" : user;
        }

        private static string InitialsFor(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            string trimmed = name.Trim();
            if (trimmed.StartsWith("+") && trimmed.Length > 1)
                return trimmed.Substring(1, Math.Min(2, trimmed.Length - 1)).ToUpper();
            return trimmed.Substring(0, 1).ToUpper();
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
