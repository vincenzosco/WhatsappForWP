using System;
using System.Collections.Generic;
using System.IO;
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
        public static CommunicationService Instance
        {
            get
            {
                if (_instance == null) _instance = new CommunicationService();
                return _instance;
            }
        }

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

        /// <summary>
        /// Frame piu' grande che accettiamo. Il prefisso di 4 byte e' l'unica
        /// cosa che l'altro capo controlla: se il lettore e' disallineato quella
        /// lunghezza e' un pezzo di JSON, cioe' un numero enorme. Senza un
        /// limite LoadAsync lo usava come dimensione e l'app finiva in
        /// OutOfMemoryException (0x8007000E) invece di chiudere la connessione.
        /// Otto mebibyte lasciano passare un'immagine in base64 e restano
        /// lontani dalla memoria di un telefono WP8.1.
        /// </summary>
        private const uint MaxFrameLength = 8 * 1024 * 1024;

        /// <summary>
        /// Numero del tentativo di connessione. Ogni tentativo lo incrementa e
        /// pubblica i propri oggetti solo se e' ancora quello piu' recente; il
        /// suo lettore continua solo finche' l'id resta quello. Senza questo,
        /// l'avvio automatico e la pagina si contendevano `_reader`: un
        /// tentativo fallito chiudeva il DataReader della connessione riuscita e
        /// due lettori sullo stesso DataReader lo disallineavano, il che e' la
        /// strada da cui e' arrivato OutOfMemoryException.
        /// </summary>
        private int _connectionId;

        // Cached UI dispatcher for marshalling events to the UI thread
        private CoreDispatcher _uiDispatcher;

        // Vero quando nessuna delle sorgenti ha dato un dispatcher: senza questo
        // flag ogni messaggio ricevuto ripeteva le tre chiamate e le loro
        // eccezioni, per tutta la durata della connessione.
        private bool _uiDispatcherFailed;

        // Events
        public event EventHandler<ChatMessage> MessageReceived;
        public event EventHandler<ChatMessage> ControlMessageReceived;
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;

        /// <summary>
        /// Sollevato (sul thread UI) quando il socket e' pronto. Sostituisce il
        /// controllo sul testo dello stato, che si rompeva cambiando lingua.
        /// </summary>
        public event EventHandler ConnectionEstablished;

        public bool IsConnected
        {
            get { return _isConnected; }
        }
        public bool IsServerMode
        {
            get { return _isServerMode; }
        }
        public string MyUserId
        {
            get { return _myUserId; }
        }
        public string MyUsername
        {
            get { return _myUsername; }
        }
        public string ServerAddress
        {
            get { return _serverAddress; }
        }

        /// <summary>Stato della connessione WhatsApp: "disconnected", "waiting" o "connected".</summary>
        public string WhatsAppState { get; private set; }

        /// <summary>JID dell'account WhatsApp collegato (vuoto se non connesso).</summary>
        public string AccountJid { get; private set; }

        private CommunicationService()
        {
            WhatsAppState = "disconnected";
            AccountJid = "";
        }

        private void RaiseConnectionStatusChanged(string status)
        {
            var handler = ConnectionStatusChanged;
            if (handler != null) handler(this, status);
        }

        private void RaiseConnectionEstablished()
        {
            var handler = ConnectionEstablished;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void RaiseErrorOccurred(string error)
        {
            var handler = ErrorOccurred;
            if (handler != null) handler(this, error);
        }

        private void RaiseMessageReceived(ChatMessage message)
        {
            var handler = MessageReceived;
            if (handler != null) handler(this, message);
        }

        private void RaiseControlMessageReceived(ChatMessage message)
        {
            var handler = ControlMessageReceived;
            if (handler != null) handler(this, message);
        }

        private CoreDispatcher GetUiDispatcher()
        {
            if (_uiDispatcher != null || _uiDispatcherFailed) return _uiDispatcher;

            // Due sorgenti, dalla piu' diretta. Da un thread di background
            // GetCurrentView fallisce; MainView e' quella che continua a
            // rispondere. Ognuna registra il proprio fallimento una volta sola.
            // (Window.Current non e' una sorgente: da un thread di background
            // restituisce null, quindi "ripiegare" li' darebbe solo un
            // NullReferenceException in piu'.)
            _uiDispatcher = TryGetDispatcher(
                delegate { return CoreApplication.GetCurrentView().CoreWindow.Dispatcher; },
                "GetUiDispatcher/GetCurrentView")
                ?? TryGetDispatcher(
                    delegate { return CoreApplication.MainView.CoreWindow.Dispatcher; },
                    "GetUiDispatcher/MainView");

            if (_uiDispatcher == null)
            {
                _uiDispatcherFailed = true;
                Diag.Failed("GetUiDispatcher", new InvalidOperationException("nessun CoreDispatcher disponibile"));
            }
            return _uiDispatcher;
        }

        /// <summary>
        /// Va chiamato una volta all'avvio, sul thread UI: e' l'unico momento in
        /// cui il dispatcher si trova di sicuro. Risolverlo la prima volta da un
        /// thread di background e' il motivo per cui questo servizio restava
        /// senza dispatcher e riprovava le due chiamate a ogni messaggio.
        /// </summary>
        public void Prewarm()
        {
            GetUiDispatcher();
        }

        private static CoreDispatcher TryGetDispatcher(Func<CoreDispatcher> source, string where)
        {
            try
            {
                return source();
            }
            catch (Exception ex)
            {
                Diag.Failed(where, ex);
                return null;
            }
        }

        private async void DispatchOnUiThread(Action action)
        {
            var dispatcher = GetUiDispatcher();
            if (dispatcher == null)
            {
                action();
                return;
            }

            // Il flag distingue "il dispatcher non ha eseguito nulla" (si
            // riprova in linea) da "l'azione è partita ma è esplosa" (non va
            // rieseguita, altrimenti gli handler ricevono l'evento due volte).
            bool dispatched = false;
            try
            {
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    dispatched = true;
                    action();
                });
            }
            catch (Exception ex)
            {
                Diag.Failed("DispatchOnUiThread", ex);
                if (!dispatched) action();
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
                {
                    RaiseConnectionStatusChanged(string.Format(
                        Loc.Get("CommService_ServerStarted", "Server started on port {0}"), port));
                    RaiseConnectionEstablished();
                });
            }
            catch (Exception ex)
            {
                Diag.Failed("StartServerAsync", ex);
                _isConnected = false;
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_ServerStartError", "Server start error: {0}"), ex.Message))
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
                RaiseConnectionStatusChanged(string.Format(
                    Loc.Get("CommService_ClientConnected", "New client connected ({0} online)"),
                    _serverClients.Count))
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
                Diag.Failed("OnServerConnectionReceived", ex);
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_ClientDisconnected", "Client disconnected: {0}"), ex.Message))
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
                    RaiseConnectionStatusChanged(string.Format(
                        Loc.Get("CommService_ClientRemoved", "Client removed ({0} online)"),
                        _serverClients.Count))
                );
            }
        }

        /// <summary>
        /// Connect to a PC server as a client
        /// </summary>
        public async Task<bool> ConnectToServerAsync(string address, int port, string username)
        {
            int attempt = ++_connectionId;

            _isServerMode = false;
            _serverAddress = address;
            _serverPort = port;
            _myUserId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _myUsername = username;

            // Oggetti del tentativo, non del servizio: finche' non e' pubblicata,
            // questa connessione non esiste per nessun altro.
            StreamSocket socket = null;
            DataWriter writer = null;
            DataReader reader = null;

            try
            {
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Connecting", "Connecting..."))
                );

                socket = new StreamSocket();
                var hostName = new HostName(address);
                await socket.ConnectAsync(hostName, port.ToString());

                writer = new DataWriter(socket.OutputStream);
                reader = new DataReader(socket.InputStream);
                reader.InputStreamOptions = InputStreamOptions.Partial;

                // Un tentativo piu' nuovo ha gia' preso il posto di questo:
                // si chiude quello che abbiamo aperto e non si tocca niente di
                // condiviso (era il modo in cui un timeout cancellava la
                // connessione riuscita dell'altro tentativo).
                if (attempt != _connectionId)
                {
                    DisposeSocket(socket, writer, reader);
                    return false;
                }

                DisposePublishedSocket();
                _clientSocket = socket;
                _writer = writer;
                _reader = reader;
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
                // Il primo frame e' anche il primo uso del cifrario: se il
                // cifrario non c'e' l'errore va detto qui, invece di uscire
                // come "operazione non implementata" senza dire quale passo.
                try
                {
                    await SendFrameAsync(writer, Encoding.UTF8.GetBytes(handshake.ToJson()));
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConnectToServerAsync/handshake", ex);
                    if (attempt == _connectionId)
                    {
                        _isConnected = false;
                        DisposePublishedSocket();
                        DispatchOnUiThread(() =>
                            RaiseErrorOccurred(string.Format(
                                Loc.Get("CommService_ConnectError", "Connection error: {0}"),
                                ExplainConnectionFailure(ex, "handshake"))));
                    }
                    else
                    {
                        DisposeSocket(socket, writer, reader);
                    }
                    return false;
                }

                DispatchOnUiThread(() =>
                {
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Connected", "Connected to the server"));
                    RaiseConnectionEstablished();
                });

                // Il lettore porta con se' l'id del tentativo e il suo reader:
                // niente campi condivisi, niente secondo lettore sullo stesso
                // DataReader.
#pragma warning disable 4014
                Task.Run(() => ListenForMessagesAsync(attempt, reader));
#pragma warning restore 4014

                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("ConnectToServerAsync", ex);

                // Solo il tentativo ancora valido puo' dichiarare il guasto: se
                // nel frattempo ne e' partito uno piu' nuovo, questo e' rumore e
                // i suoi oggetti si chiudono senza toccare la connessione
                // vincente.
                DisposeSocket(socket, writer, reader);
                if (attempt == _connectionId)
                {
                    _isConnected = false;
                    DisposePublishedSocket();
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_ConnectError", "Connection error: {0}"),
                            ExplainConnectionFailure(ex, "socket"))));
                }
                return false;
            }
        }

        /// <summary>
        /// Chiude la connessione pubblicata, qualunque essa sia, senza toccare
        /// l'id dei tentativi. Idempotente: la chiamano il ramo di fallimento
        /// del handshake, il catch esterno e il lettore che finisce.
        /// </summary>
        private void DisposePublishedSocket()
        {
            DataWriter writer = _writer;
            DataReader reader = _reader;
            StreamSocket socket = _clientSocket;

            _writer = null;
            _reader = null;
            _clientSocket = null;

            DisposeSocket(socket, writer, reader);
        }

        /// <summary>
        /// Chiude gli oggetti di un tentativo. Non tocca i campi: non sa se
        /// quella connessione e' mai stata pubblicata, ed e' esattamente il
        /// motivo per cui esiste.
        /// </summary>
        private static void DisposeSocket(StreamSocket socket, DataWriter writer, DataReader reader)
        {
            try { if (writer != null) writer.Dispose(); }
            catch (Exception ex) { Diag.Failed("DisposeSocket/writer", ex); }
            try { if (reader != null) reader.Dispose(); }
            catch (Exception ex) { Diag.Failed("DisposeSocket/reader", ex); }
            try { if (socket != null) socket.Dispose(); }
            catch (Exception ex) { Diag.Failed("DisposeSocket/socket", ex); }
        }

        /// <summary>
        /// Traduce il guasto in una riga comprensibile. "The method or operation
        /// is not implemented" non dice all'utente che manca un pezzo di
        /// piattaforma, ne' quale passo della connessione e' caduto.
        /// </summary>
        private static string ExplainConnectionFailure(Exception ex, string stage)
        {
            bool platformMissing = ex is NotImplementedException
                || ex is PlatformNotSupportedException
                || ex.HResult == unchecked((int)0x80004001);

            if (platformMissing)
            {
                return string.Format(
                    Loc.Get("CommService_PlatformMissing",
                        "This phone does not implement a required Windows feature ({0}: {1})"),
                    stage, ex.Message);
            }

            return ex.Message;
        }

        /// <summary>
        /// Legge i frame della connessione <paramref name="attempt"/> finche' e'
        /// quella pubblicata. Il reader arriva come parametro: prenderlo da
        /// `_reader` significava leggere il reader di un'altra connessione non
        /// appena ne partiva una nuova.
        /// </summary>
        private async Task ListenForMessagesAsync(int attempt, DataReader reader)
        {
            try
            {
                while (_isConnected && attempt == _connectionId)
                {
                    byte[] payload = await ReadFrameAsync(reader);
                    if (payload == null) break;

                    DispatchMessage(DecryptToMessage(payload));
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("ListenForMessagesAsync", ex);
                if (_isConnected && attempt == _connectionId)
                {
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_ConnectionLost", "Connection lost: {0}"), ex.Message))
                    );
                }
            }
            finally
            {
                // Un lettore superato non deve dichiarare disconnessa la
                // connessione che l'ha sostituito.
                if (attempt == _connectionId)
                {
                    _isConnected = false;
                    DisposePublishedSocket();
                    DispatchOnUiThread(() =>
                        RaiseConnectionStatusChanged(Loc.Get("CommService_Disconnected", "Disconnected"))
                    );
                }
            }
        }

        /// <summary>
        /// Send a message to the connected peer(s)
        /// </summary>
        public async Task SendMessageAsync(ChatMessage message)
        {
            if (!_isConnected)
            {
                DispatchOnUiThread(() => RaiseErrorOccurred(Loc.Get("CommService_NotConnected", "Not connected")));
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
                    await SendFrameAsync(_writer, jsonBytes);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("SendMessageAsync", ex);
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_SendError", "Send error: {0}"), ex.Message))
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
                SenderName = _myUsername ?? Loc.Get("CommService_Me", "Me"),
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
                DispatchOnUiThread(() => RaiseControlMessageReceived(message));
            }
            else
            {
                DispatchOnUiThread(() => RaiseMessageReceived(message));
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
                catch (Exception ex)
                {
                    Diag.Failed("BroadcastToAllClientsAsync", ex);
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
        /// Encrypts the JSON bytes and writes one frame on the given writer:
        /// [4-byte UInt32LE payload length][encrypted payload].
        /// Il writer arriva da fuori perche' e' quello del socket, creato una
        /// volta in ConnectToServerAsync: prima ne veniva creato — e mai
        /// chiuso — uno nuovo per ogni frame inviato.
        /// </summary>
        private async Task SendFrameAsync(DataWriter writer, byte[] jsonBytes)
        {
            byte[] payload = CryptoHelper.Encrypt(jsonBytes);
            writer.WriteUInt32((uint)payload.Length);
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        /// <summary>
        /// Reads one complete frame: [4-byte length][payload].
        /// Returns null when the connection is closed or the frame is not
        /// acceptable (a length outside 1..MaxFrameLength is a fault, not a
        /// payload: it is recorded and the connection is dropped).
        /// </summary>
        private async Task<byte[]> ReadFrameAsync(DataReader reader)
        {
            if (!await LoadAtLeastAsync(reader, 4)) return null;

            uint payloadLength = reader.ReadUInt32();

            if (payloadLength == 0 || payloadLength > MaxFrameLength)
            {
                Diag.Failed("ReadFrameAsync/length",
                    new InvalidDataException("lunghezza frame fuori intervallo: " + payloadLength));
                return null;
            }

            if (!await LoadAtLeastAsync(reader, payloadLength)) return null;

            byte[] payload = new byte[payloadLength];
            reader.ReadBytes(payload);
            return payload;
        }

        /// <summary>
        /// Riempie il buffer del reader finche' non ha almeno <paramref name="count"/>
        /// byte non consumati. Con InputStreamOptions.Partial una LoadAsync puo'
        /// restituirne meno del richiesto: il prefisso di lunghezza letto con una
        /// sola LoadAsync(4) veniva spezzato a meta' frame e la connessione
        /// cadeva su un frame che era solo arrivato in due pezzi.
        /// </summary>
        private static async Task<bool> LoadAtLeastAsync(DataReader reader, uint count)
        {
            while (reader.UnconsumedBufferLength < count)
            {
                uint loaded = await reader.LoadAsync(count - reader.UnconsumedBufferLength);
                if (loaded == 0) return false;   // flusso chiuso dall'altro capo
            }
            return true;
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
                Diag.Failed("DecryptToMessage", ex);
                DispatchOnUiThread(() =>
                    RaiseErrorOccurred(string.Format(
                        Loc.Get("CommService_DecryptError", "Message decryption error: {0}"), ex.Message))
                );
                return null;
            }
        }

        /// <summary>
        /// Disconnect and clean up
        /// </summary>
        public void Disconnect()
        {
            // Ferma un lettore ancora in esecuzione prima di chiudere i suoi
            // oggetti: e' quello che distingue una disconnessione voluta da un
            // guasto di rete da segnalare.
            _connectionId++;
            _isConnected = false;
            WhatsAppState = "disconnected";
            AccountJid = "";

            lock (_serverClients)
            {
                foreach (var client in _serverClients)
                {
                    try { client.Dispose(); }
                    catch (Exception ex) { Diag.Failed("Disconnect/client", ex); }
                }
                _serverClients.Clear();
            }

            try
            {
                if (_serverListener != null) _serverListener.Dispose();
            }
            catch (Exception ex)
            {
                Diag.Failed("Disconnect", ex);
            }

            DisposePublishedSocket();
            _serverListener = null;

            // _uiDispatcher non si azzera: non e' legato al socket, e azzerarlo
            // costringeva GetUiDispatcher a rifare le tre chiamate (e a
            // registrarne di nuovo i guasti) sulla riga successiva.
            DispatchOnUiThread(() =>
                RaiseConnectionStatusChanged(Loc.Get("CommService_Disconnected", "Disconnected")));
        }
    }
}
