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
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;

        public bool IsConnected => _isConnected;
        public bool IsServerMode => _isServerMode;
        public string MyUserId => _myUserId;
        public string MyUsername => _myUsername;
        public string ServerAddress => _serverAddress;

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
                    uint sizeFieldCount = await reader.LoadAsync(4);
                    if (sizeFieldCount < 4) break;

                    uint messageLength = reader.ReadUInt32();
                    uint actualLength = await reader.LoadAsync(messageLength);
                    if (actualLength < messageLength) break;

                    byte[] messageData = new byte[messageLength];
                    reader.ReadBytes(messageData);
                    string json = Encoding.UTF8.GetString(messageData, 0, messageData.Length);

                    var message = ChatMessage.FromJson(json);
                    if (message != null)
                    {
                        // Broadcast to other clients and dispatch to UI
                        await BroadcastToAllClientsAsync(json, socket);
                        DispatchOnUiThread(() => MessageReceived?.Invoke(this, message));
                    }
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

                // Send handshake with our identity
                var handshake = new ChatMessage
                {
                    Id = "handshake",
                    Text = username,
                    SenderId = _myUserId,
                    SenderName = username,
                    ChatId = "system",
                    Timestamp = DateTime.Now,
                    Type = MessageType.System,
                    IsIncoming = false
                };
                await SendMessageAsync(handshake);

                _isConnected = true;
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
                    uint sizeFieldCount = await _reader.LoadAsync(4);
                    if (sizeFieldCount < 4) break;

                    uint messageLength = _reader.ReadUInt32();
                    uint actualLength = await _reader.LoadAsync(messageLength);
                    if (actualLength < messageLength) break;

                    byte[] messageData = new byte[messageLength];
                    _reader.ReadBytes(messageData);
                    string json = Encoding.UTF8.GetString(messageData, 0, messageData.Length);

                    var message = ChatMessage.FromJson(json);
                    if (message != null)
                    {
                        DispatchOnUiThread(() => MessageReceived?.Invoke(this, message));
                    }
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
                    // Broadcast to all connected clients
                    await BroadcastToAllClientsAsync(json, null);
                }
                else if (_clientSocket != null)
                {
                    await SendDataAsync(_clientSocket, jsonBytes);
                }
            }
            catch (Exception ex)
            {
                DispatchOnUiThread(() =>
                    ErrorOccurred?.Invoke(this, $"Errore invio: {ex.Message}")
                );
            }
        }

        private async Task BroadcastToAllClientsAsync(string json, StreamSocket excludeSocket)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);

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
                    writer.WriteUInt32((uint)data.Length);
                    writer.WriteBytes(data);
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

        private async Task SendDataAsync(StreamSocket socket, byte[] data)
        {
            var writer = new DataWriter(socket.OutputStream);
            writer.WriteUInt32((uint)data.Length);
            writer.WriteBytes(data);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        /// <summary>
        /// Disconnect and clean up
        /// </summary>
        public void Disconnect()
        {
            _isConnected = false;

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
