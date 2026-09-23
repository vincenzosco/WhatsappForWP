using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.UI.Core;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    public class CommunicationService
    {
        private static CommunicationService _instance;
        public static CommunicationService Instance => _instance ?? (_instance = new CommunicationService());

        private StreamSocket _clientSocket;
        private StreamSocketListener _serverListener;
        private readonly List<StreamSocket> _serverClients = new List<StreamSocket>();
        private bool _isServerMode;
        private string _serverAddress;
        private int _serverPort;
        private string _myUserId;
        private string _myUsername;
        private DataWriter _writer;
        private DataReader _reader;
        private bool _isConnected = false;

        // Cached UI dispatcher for marshalling events to the UI thread
        private CoreDispatcher _uiDispatcher;

        // Events
        public event EventHandler<ChatMessage> MessageReceived;
        public event EventHandler<ChatMessage> ControlMessageReceived;
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;

        public bool IsConnected => _isConnected;
        public bool IsServerMode => _isServerMode;
        public string MyUserId => _myUserId;
        public string MyUsername => _myUsername;
        public string ServerAddress => _serverAddress;

        /// <summary>Stato della connessione WhatsApp: "disconnected", "waiting" o "connected".</summary>
        public string WhatsAppState { get; private set; } = "disconnected";

        /// <summary>JID dell'account WhatsApp collegato (vuoto se non connesso).</summary>
        public string AccountJid { get; private set; } = "";

        private CommunicationService() { }

        private CoreDispatcher GetUiDispatcher()
        {
            if (_uiDispatcher == null)
            {
                try
                {
                    _uiDispatcher = CoreApplication.GetCurrentView().CoreWindow.Dispatcher;
                }
                catch
                {
                    try
                    {
                        _uiDispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
                    }
                    catch { }
                }
            }
            return _uiDispatcher;
        }

        private async void DispatchOnUiThread(Action action)
        {
            var dispatcher = GetUiDispatcher();
            if (dispatcher != null)
            {
                try
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
                }
                catch
                {
                    action();
                }
            }
            else
            {
                action();
            }
        }

        /// <summary>
        /// Start the server mode - the phone hosts a TCP server
        /// </summary>
        public async Task StartServerAsync(string username, int port = 8585)
        {
            _isServerMode = true;
            _serverPort = port;
            _myUserId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _myUsername = username;
            _serverAddress = "localhost";

            try
            {
                _serverListener = new StreamSocketListener();
                _serverListener.ConnectionReceived += OnServerConnectionReceived;
                await _serverListener.BindServiceNameAsync(port.ToString());

                _isConnected = true;
                DispatchOnUiThread(() =>
                    ConnectionStatusChanged?.Invoke(this, $"Server avviato sulla porta {port}")
                );
            }
            catch (Exception ex)
            {
                _isConnected = false;
                DispatchOnUiThread(() =>
                    ErrorOccurred?.Invoke(this, $"Errore avvio server: {ex.Message}")
                );
            }
        }

        private async void OnServerConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var socket = args.Socket;
            lock (_serverClients)
            {
                _serverClients.Add(socket);
            }

            DispatchOnUiThread(() =>
                ConnectionStatusChanged?.Invoke(this, $"Nuovo client connesso ({_serverClients.Count} connessi)")
            );

            try
            {
                var reader = new DataReader(socket.InputStream);
                reader.InputStreamOptions = InputStreamOptions.Partial;

                while (_isConnected)
                {
                    // Read one encrypted frame
                    byte[] payload = await ReadFrameAsync(reader);
                    if (payload == null) break;

                    // Forward the encrypted frame to the other clients
                    // (same shared key, each frame carries its own IV)
                    await BroadcastToAllClientsAsync(payload, socket);

                    // Decrypt for the local UI
                    DispatchMessage(DecryptToMessage(payload));
                }
            }
            catch (Exception ex)
            {
                DispatchOnUiThread(() =>
                    ErrorOccurred?.Invoke(this, $"Client disconnesso: {ex.Message}")
                );
            }
            finally
            {
                lock (_serverClients)
                {
                    _serverClients.Remove(socket);
                }
                socket.Dispose();
                DispatchOnUiThread(() =>
                    ConnectionStatusChanged?.Invoke(this, $"Client rimosso ({_serverClients.Count} connessi)")
                );
            }
        }

        /// <summary>
        /// Connect to a PC server as a client
        /// </summary>
        public async Task<bool> ConnectToServerAsync(string address, int port, string username)
        {
            _isServerMode = false;
            _serverAddress = address;
            _serverPort = port;
            _myUserId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _myUsername = username;

            try
            {
                DispatchOnUiThread(() =>
                    ConnectionStatusChanged?.Invoke(this, "Connessione in corso...")
                );

                _clientSocket = new StreamSocket();
                var hostName = new HostName(address);
                await _clientSocket.ConnectAsync(hostName, port.ToString());

                _writer = new DataWriter(_clientSocket.OutputStream);
                _reader = new DataReader(_clientSocket.InputStream);
                _reader.InputStreamOptions = InputStreamOptions.Partial;

                _isConnected = true;

                // Send handshake with our identity (encrypted)
                var handshake = new ChatMessage
                {
                    Id = "handshake",
                    Text = username,
                    Command = "hello",
                    SenderId = _myUserId,
                    SenderName = username,
                    ChatId = "system",
                    Timestamp = DateTime.Now,
                    Type = MessageType.System,
                    IsIncoming = false
                };
                await SendFrameAsync(_clientSocket, Encoding.UTF8.GetBytes(handshake.ToJson()));

                DispatchOnUiThread(() =>
                    ConnectionStatusChanged?.Invoke(this, "Connesso al server")
                );

                // Start listening for incoming messages on a background thread
                _ = Task.Run(() => ListenForMessagesAsync());

                return true;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                DispatchOnUiThread(() =>
                    ErrorOccurred?.Invoke(this, $"Errore connessione: {ex.Message}")
                );
                return false;
            }
        }

        private async Task ListenForMessagesAsync()
        {
            try
            {
                while (_isConnected && _clientSocket != null && _reader != null)
                {
                    byte[] payload = await ReadFrameAsync(_reader);
                    if (payload == null) break;

                    DispatchMessage(DecryptToMessage(payload));
                }
            }
            catch (Exception ex)
            {
                if (_isConnected)
                {
                    DispatchOnUiThread(() =>
                        ErrorOccurred?.Invoke(this, $"Connessione persa: {ex.Message}")
                    );
                }
            }
            finally
            {
                _isConnected = false;
                DispatchOnUiThread(() =>
                    ConnectionStatusChanged?.Invoke(this, "Disconnesso")
                );
            }
        }

        /// <summary>
        /// Send a message to the connected peer(s)
        /// </summary>
        public async Task SendMessageAsync(ChatMessage message)
        {
            if (!_isConnected)
            {
                DispatchOnUiThread(() => ErrorOccurred?.Invoke(this, "Non connesso"));
                return;
            }

            try
            {
                string json = message.ToJson();
                byte[] jsonBytes = Encoding.UTF8.GetBytes(json);

                if (_isServerMode)
                {
                    // Encrypt and broadcast to all connected clients
                    byte[] payload = CryptoHelper.Encrypt(jsonBytes);
                    await BroadcastToAllClientsAsync(payload, null);
                }
                else if (_clientSocket != null)
                {
                    await SendFrameAsync(_clientSocket, jsonBytes);
                }
            }
            catch (Exception ex)
            {
                DispatchOnUiThread(() =>
                    ErrorOccurred?.Invoke(this, $"Errore invio: {ex.Message}")
                );
            }
        }

        /// <summary>
        /// Invia un frame di controllo all'adapter (stato, login QR/numero,
        /// contatti, logout). Il payload va in Text quando serve.
        /// </summary>
        public async Task SendControlAsync(string command, string payload = null)
        {
            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = command,
                Text = payload ?? "",
                SenderId = _myUserId ?? "me",
                SenderName = _myUsername ?? "Io",
                ChatId = "system",
                Timestamp = DateTime.Now,
                Type = MessageType.System,
                IsIncoming = false
            };
            await SendMessageAsync(message);
        }

        /// <summary>
        /// Instrada un messaggio decifrato: i frame di controllo (Type = System)
        /// vanno all'evento ControlMessageReceived, gli altri a MessageReceived.
        /// </summary>
        private void DispatchMessage(ChatMessage message)
        {
            if (message == null) return;

            if (message.Type == MessageType.System)
            {
                if (message.Command == "state")
                {
                    WhatsAppState = string.IsNullOrEmpty(message.State) ? "disconnected" : message.State;
                    AccountJid = message.AccountJid ?? "";
                }
                DispatchOnUiThread(() => ControlMessageReceived?.Invoke(this, message));
            }
            else
            {
                DispatchOnUiThread(() => MessageReceived?.Invoke(this, message));
            }
        }

        /// <summary>
        /// Broadcasts an already-encrypted payload to all connected clients
        /// (except the excluded one).
        /// </summary>
        private async Task BroadcastToAllClientsAsync(byte[] payload, StreamSocket excludeSocket)
        {
            // Snapshot the client list under lock, then release before async work
            List<StreamSocket> snapshot;
            lock (_serverClients)
            {
                snapshot = _serverClients.ToList();
            }

            var deadClients = new List<StreamSocket>();

            foreach (var client in snapshot)
            {
                if (client == excludeSocket) continue;

                try
                {
                    var writer = new DataWriter(client.OutputStream);
                    writer.WriteUInt32((uint)payload.Length);
                    writer.WriteBytes(payload);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }
                catch
                {
                    deadClients.Add(client);
                }
            }

            // Clean up dead clients under lock
            if (deadClients.Count > 0)
            {
                lock (_serverClients)
                {
                    foreach (var dead in deadClients)
                        _serverClients.Remove(dead);
                }
            }
        }

        /// <summary>
        /// Encrypts the JSON bytes and writes one frame:
        /// [4-byte UInt32LE payload length][encrypted payload].
        /// </summary>
        private async Task SendFrameAsync(StreamSocket socket, byte[] jsonBytes)
        {
            byte[] payload = CryptoHelper.Encrypt(jsonBytes);
            var writer = new DataWriter(socket.OutputStream);
            writer.WriteUInt32((uint)payload.Length);
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        /// <summary>
        /// Reads one complete frame: [4-byte length][payload].
        /// Returns null when the connection is closed or the frame is incomplete.
        /// </summary>
        private async Task<byte[]> ReadFrameAsync(DataReader reader)
        {
            uint sizeFieldCount = await reader.LoadAsync(4);
            if (sizeFieldCount < 4) return null;

            uint payloadLength = reader.ReadUInt32();
            uint actualLength = await reader.LoadAsync(payloadLength);
            if (actualLength < payloadLength) return null;

            byte[] payload = new byte[payloadLength];
            reader.ReadBytes(payload);
            return payload;
        }

        /// <summary>
        /// Decrypts a frame payload and parses it into a ChatMessage.
        /// Returns null on decrypt/parse failure (e.g. wrong key or tampering).
        /// </summary>
        private ChatMessage DecryptToMessage(byte[] payload)
        {
            try
            {
                byte[] jsonBytes = CryptoHelper.Decrypt(payload);
                string json = Encoding.UTF8.GetString(jsonBytes, 0, jsonBytes.Length);
                return ChatMessage.FromJson(json);
            }
            catch (Exception ex)
            {
                DispatchOnUiThread(() =>
                    ErrorOccurred?.Invoke(this, $"Errore decifratura messaggio: {ex.Message}")
                );
                return null;
            }
        }

        /// <summary>
        /// Disconnect and clean up
        /// </summary>
        public void Disconnect()
        {
            _isConnected = false;
            WhatsAppState = "disconnected";
            AccountJid = "";

            lock (_serverClients)
            {
                foreach (var client in _serverClients)
                {
                    try { client.Dispose(); } catch { }
                }
                _serverClients.Clear();
            }

            try
            {
                _writer?.Dispose();
                _reader?.Dispose();
                _clientSocket?.Dispose();
                _serverListener?.Dispose();
            }
            catch { }

            _writer = null;
            _reader = null;
            _clientSocket = null;
            _serverListener = null;
            _uiDispatcher = null;

            DispatchOnUiThread(() => ConnectionStatusChanged?.Invoke(this, "Disconnesso"));
        }
    }
}
