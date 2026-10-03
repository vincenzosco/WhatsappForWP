using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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
        /// One write at a time on a socket. The DataWriter has a single buffer: two
        /// StoreAsync in flight together put the length prefix of one in front of
        /// the payload of the other, and the other end reads a frame that does not
        /// exist. It happened when sending a message while an attachment was still
        /// uploading.
        /// </summary>
        private readonly SerialQueue _writes = new SerialQueue();

        /// <summary>
        /// Connection attempt number. Every attempt increments it and publishes its
        /// objects only if it is still the most recent one; its reader keeps going
        /// only while the id stays that. Without this, the automatic startup and the
        /// page contended for `_reader`: a failed attempt closed the DataReader of
        /// the successful connection and two readers on the same DataReader
        /// misaligned it, which is the path OutOfMemoryException came through.
        /// </summary>
        private int _connectionId;

        /// <summary>
        /// Deadline of ConnectAsync, in milliseconds. StreamSocket accepts no timeout
        /// and has no CancellationToken: without a limit the phone freezes on an
        /// address that no longer answers until the TCP stack gives up. The socket is
        /// closed on expiry, which is the only way to cancel a connection still in
        /// flight.
        /// </summary>
        private const int ConnectDeadlineMs = 6000;

        // WinSock errors arrive as WinRT exceptions with FACILITY_WIN32: 0x8007xxxx.
        // WSAETIMEDOUT is the one seen on the device.
        private const int WsaETimedOut = unchecked((int)0x8007274C);
        private const int WsaEConnRefused = unchecked((int)0x8007274D);
        private const int WsaENetUnreachable = unchecked((int)0x80072743);
        private const int WsaEHostUnreachable = unchecked((int)0x80072751);

        // Cached UI dispatcher for marshalling events to the UI thread
        private CoreDispatcher _uiDispatcher;

        // True when none of the sources gave a dispatcher: without this flag every
        // received message repeated the three calls and their exceptions, for the
        // whole life of the connection.
        private bool _uiDispatcherFailed;

        // Events
        public event EventHandler<ChatMessage> MessageReceived;
        public event EventHandler<ChatMessage> ControlMessageReceived;
        public event EventHandler<string> ConnectionStatusChanged;
        public event EventHandler<string> ErrorOccurred;

        /// <summary>
        /// The server did not answer after the automatic attempts. It is distinct
        /// from ErrorOccurred because it is not a socket failure to explain: it is a
        /// state worth a single sentence, and pages must not infer it from the text
        /// (which changes with the language).
        /// </summary>
        public event EventHandler ServerUnavailable;

        /// <summary>
        /// Raised (on the UI thread) when the socket is ready. It replaces the check
        /// on the state text, which broke when the language changed.
        /// </summary>
        public event EventHandler ConnectionEstablished;

        public bool IsConnected
        {
            get { return _isConnected; }
        }

        /// <summary>
        /// When the last frame was read (UTC). It is not protocol data: only the
        /// watchdog reads it, because on WP8.1 the socket can die without anyone
        /// saying so - suspending the app closes it, and a network change leaves it
        /// there answering no more. The _isConnected flag does not notice: it stays
        /// true while the app is mute, which was exactly the case to tell apart.
        /// </summary>
        public DateTime LastInboundUtc { get; private set; }
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

        /// <summary>WhatsApp connection state: "disconnected", "waiting" or "connected".</summary>
        public string WhatsAppState { get; private set; }

        /// <summary>JID of the linked WhatsApp account (empty when not connected).</summary>
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

            // Two sources, from the most direct. From a background thread
            // GetCurrentView fails; MainView is the one that keeps answering. Each
            // logs its own failure once only. (Window.Current is not a source: from a
            // background thread it returns null, so "falling back" there would only
            // give one more NullReferenceException.)
            _uiDispatcher = TryGetDispatcher(
                delegate { return CoreApplication.GetCurrentView().CoreWindow.Dispatcher; },
                "GetUiDispatcher/GetCurrentView")
                ?? TryGetDispatcher(
                    delegate { return CoreApplication.MainView.CoreWindow.Dispatcher; },
                    "GetUiDispatcher/MainView");

            if (_uiDispatcher == null)
            {
                _uiDispatcherFailed = true;
                Diag.Failed("GetUiDispatcher", new InvalidOperationException("no CoreDispatcher available"));
            }
            return _uiDispatcher;
        }

        /// <summary>
        /// The automatic connector says it when it has run out of attempts without a
        /// connection. It goes through the dispatcher because the listener writes on
        /// the page, and the caller is a background thread.
        /// </summary>
        public void NotifyServerUnavailable()
        {
            DispatchOnUiThread(() =>
            {
                var handler = ServerUnavailable;
                if (handler != null) handler(this, EventArgs.Empty);
            });
        }

        /// <summary>
        /// Must be called once at startup, on the UI thread: it is the only moment
        /// the dispatcher is reliably available. Resolving it for the first time from
        /// a background thread is why this service stayed without a dispatcher and
        /// retried the two calls on every message.
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

            // The flag tells apart "the dispatcher ran nothing" (retry inline) from
            // "the action started but blew up" (do not run it again, otherwise the
            // handlers receive the event twice).
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
                var reader = FrameCodec.CreateFrameReader(socket.InputStream);

                while (_isConnected)
                {
                    // Read one encrypted frame
                    byte[] payload = await FrameCodec.ReadFrameAsync(reader);
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
            // Stable for the life of the install, not per connection: the server
            // derives this device's token from it, so a fresh id every time was a
            // new user of the same phone every time, with a new token each.
            _myUserId = SettingsService.DeviceId;
            _myUsername = username;

            // Objects of the attempt, not of the service: until it is published, this
            // connection does not exist for anyone else.
            StreamSocket socket = null;
            DataWriter writer = null;
            DataReader reader = null;

            try
            {
                DispatchOnUiThread(() =>
                    RaiseConnectionStatusChanged(Loc.Get("CommService_Connecting", "Connecting..."))
                );

                // Before opening a socket: an empty address or a port out of range is
                // not a network failure to explain, it is a value to correct.
                if (string.IsNullOrEmpty(address) || port < 1 || port > 65535)
                {
                    Diag.Failed("ConnectToServerAsync/address",
                        new ArgumentException("invalid address or port: " + Endpoint(address, port)));
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_InvalidAddress",
                                "Enter a valid address (host name or IP, port 1-65535): {0}"),
                            Endpoint(address, port))));
                    return false;
                }

                HostName hostName;
                try
                {
                    hostName = new HostName(address);
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConnectToServerAsync/hostname", ex);
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_InvalidAddress",
                                "Enter a valid address (host name or IP, port 1-65535): {0}"),
                            Endpoint(address, port))));
                    return false;
                }

                socket = new StreamSocket();
                await ConnectWithDeadlineAsync(socket, hostName, port);

                writer = FrameCodec.CreateFrameWriter(socket.OutputStream);
                reader = FrameCodec.CreateFrameReader(socket.InputStream);

                // A newer attempt has already taken this one place: what we opened is
                // closed and nothing shared is touched (this was how a timeout
                // cancelled the successful connection of the other attempt).
                if (attempt != _connectionId)
                {
                    DisposeSocket(socket, writer, reader);
                    return false;
                }

                DisposePublishedSocket();
                _clientSocket = socket;
                _writer = writer;
                _reader = reader;
                LastInboundUtc = DateTime.UtcNow;
                _isConnected = true;

                // Send handshake with our identity (encrypted)
                var handshake = new ChatMessage
                {
                    Id = "handshake",
                    Text = username,
                    // The token of the shared service, if there is one: it is what tells
                    // the server who this phone belongs to.
                    Token = SettingsService.Token,
                    Command = "hello",
                    SenderId = _myUserId,
                    SenderName = username,
                    ChatId = "system",
                    Timestamp = DateTime.Now,
                    Type = MessageType.System,
                    IsIncoming = false
                };
                // The first frame is also the first use of the cipher: if the cipher is
                // not there, the error must be said here, instead of coming out as
                // "operation not implemented" without saying which step.
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
                                ExplainConnectionFailure(ex, "handshake", Endpoint(address, port)))));
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

                // The reader carries the attempt id and its own reader with it: no
                // shared fields, no second reader on the same DataReader.
#pragma warning disable 4014
                Task.Run(() => ListenForMessagesAsync(attempt, reader));
#pragma warning restore 4014

                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("ConnectToServerAsync", ex);

                // Only the still-valid attempt can declare the failure: if a newer one
                // has started meanwhile, this is noise and its objects are closed
                // without touching the winning connection.
                DisposeSocket(socket, writer, reader);
                if (attempt == _connectionId)
                {
                    _isConnected = false;
                    DisposePublishedSocket();
                    DispatchOnUiThread(() =>
                        RaiseErrorOccurred(string.Format(
                            Loc.Get("CommService_ConnectError", "Connection error: {0}"),
                            ExplainConnectionFailure(ex, "socket", Endpoint(address, port)))));
                }
                return false;
            }
        }

        /// <summary>
        /// Closes the published connection, whatever it is, without touching the
        /// attempt id. Idempotent: the handshake failure branch, the outer catch and
        /// the reader that finishes call it.
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
        /// Closes the objects of an attempt. It does not touch the fields: it does
        /// not know whether that connection was ever published, and that is exactly
        /// why it exists.
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
        /// ConnectAsync with a deadline. On expiry the socket is closed, which is the
        /// only way to cancel a connection in flight, and a TimeoutException is
        /// thrown: the explanation to the user is written by
        /// ExplainConnectionFailure.
        /// </summary>
        private static async Task ConnectWithDeadlineAsync(StreamSocket socket, HostName hostName, int port)
        {
            Task connecting = socket.ConnectAsync(hostName, port.ToString()).AsTask();
            Task deadline = Task.Delay(ConnectDeadlineMs);

            if (await Task.WhenAny(connecting, deadline) != connecting)
            {
                try { socket.Dispose(); }
                catch (Exception ex) { Diag.Failed("ConnectWithDeadlineAsync/cancel", ex); }

                // The abandoned ConnectAsync will fail with "operation cancelled": it
                // is observed, otherwise it stays an exception with no reader. The
                // result is assigned because a Continuation left as a statement in an
                // async method is a CS4014.
                Task observed = connecting.ContinueWith(
                    delegate(Task t) { AggregateException ignored = t.Exception; },
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

                throw new TimeoutException(string.Format(
                    "no answer from {0}:{1} within {2} ms",
                    hostName.RawName, port, ConnectDeadlineMs));
            }

            await connecting;   // propagates the real failure (refusal, unreachable host, ...)
        }

        /// <summary>"address:port", the form in which the user wrote the value.</summary>
        private static string Endpoint(string address, int port)
        {
            return string.Format("{0}:{1}", address, port);
        }

        /// <summary>
        /// Turns the failure into an understandable line. "The method or operation is
        /// not implemented" does not tell the user that a piece of platform is
        /// missing; the WinSock English text says neither which address nor what to
        /// do.
        /// </summary>
        private static string ExplainConnectionFailure(Exception ex, string stage, string endpoint)
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

            if (ex is TimeoutException || ex.HResult == WsaETimedOut)
            {
                return string.Format(
                    Loc.Get("CommService_ConnectTimeout",
                        "The server at {0} did not answer. Check that the PC is on, on the same network, and that the port is open."),
                    endpoint);
            }

            if (ex.HResult == WsaEConnRefused)
            {
                return string.Format(
                    Loc.Get("CommService_ConnectRefused",
                        "The server at {0} refused the connection. Check that the adapter is running."),
                    endpoint);
            }

            if (ex.HResult == WsaENetUnreachable || ex.HResult == WsaEHostUnreachable)
            {
                return string.Format(
                    Loc.Get("CommService_ConnectUnreachable",
                        "The server at {0} is not reachable on this network."),
                    endpoint);
            }

            return ex.Message;
        }

        /// <summary>
        /// Reads the frames of the <paramref name="attempt"/> connection while it is
        /// the published one. The reader arrives as a parameter: taking it from
        /// `_reader` meant reading the reader of another connection as soon as a new
        /// one started.
        /// </summary>
        private async Task ListenForMessagesAsync(int attempt, DataReader reader)
        {
            try
            {
                while (_isConnected && attempt == _connectionId)
                {
                    byte[] payload = await FrameCodec.ReadFrameAsync(reader);
                    if (payload == null) break;

                    // Proof of life for the watchdog: a frame read just now.
                    LastInboundUtc = DateTime.UtcNow;
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
                // A superseded reader must not declare disconnected the connection
                // that replaced it.
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

            // The other half of the frame log: what this phone asked for, in the
            // order it asked. It sits here, on the single send path, so a control
            // frame cannot be added without being seen; `media.chunk` is filtered
            // inside Diag.Frame.
            if (message.Type == MessageType.System)
            {
                Diag.Frame("out", message.Command, message.Text);
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
        /// Sends a control frame to the adapter (state, QR/number login, contacts,
        /// logout). The payload goes in Text when needed.
        /// </summary>
        public async Task SendControlAsync(string command, string payload = null)
        {
            await SendControlAsync(command, payload, null);
        }

        /// <summary>
        /// The same frame with the third field some commands need (`State`, for the
        /// typing indicator): `Text` carries the chat, `State` carries what is being
        /// said about it.
        /// </summary>
        public async Task SendControlAsync(string command, string payload, string state)
        {
            var message = NewControlFrame(command);
            message.Text = payload ?? "";
            message.State = state;
            await SendMessageAsync(message);
        }

        /// <summary>
        /// Tells the adapter whether this phone is still watching the account.
        ///
        /// WP8.1 freezes the process on suspension without closing the socket, so
        /// from the outside a backgrounded app looks exactly like a connected one:
        /// the server would keep counting it and WhatsApp would keep showing the
        /// account online. This frame is the only thing that turns away from the
        /// foreground into "last seen" for the contacts. The state travels in
        /// `State`, as for `typing`.
        ///
        /// Silent when there is no socket: its closure already says the same
        /// thing, and an error line on suspend would only be noise.
        /// </summary>
        public async Task SendWatchingAsync(bool watching)
        {
            if (!_isConnected) return;

            var frame = NewControlFrame("presence");
            frame.State = watching ? "active" : "paused";
            await SendMessageAsync(frame);
        }

        /// <summary>
        /// The skeleton of a control frame. SendControlAsync used to build it for
        /// every command: here it is one place only, because the commands of an
        /// attachment (media.begin/chunk/end) are control frames too.
        /// </summary>
        private ChatMessage NewControlFrame(string command)
        {
            return new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Command = command,
                SenderId = _myUserId ?? "me",
                SenderName = _myUsername ?? Loc.Get("CommService_Me", "Me"),
                ChatId = "system",
                Timestamp = DateTime.Now,
                Type = MessageType.System,
                IsIncoming = false
            };
        }

        /// <summary>
        /// An attachment begins. The content is not here: it is in the pieces. The
        /// chat travels in Text, the file and its type in the fields that already
        /// carry that name.
        /// </summary>
        public async Task SendMediaBeginAsync(string chatId, string transferId,
            string fileName, string mimeType, int totalChunks)
        {
            var frame = NewControlFrame("media.begin");
            frame.Text = chatId;
            frame.MediaTransferId = transferId;
            frame.MediaFileName = fileName;
            frame.MediaMimeType = mimeType;
            frame.MediaChunkTotal = totalChunks;
            await SendMessageAsync(frame);
        }

        /// <summary>
        /// One piece of the attachment, already base64. The length is a multiple of 4
        /// characters, so the pieces can be concatenated without decoding them.
        /// </summary>
        public async Task SendMediaChunkAsync(string transferId, int index, string base64)
        {
            var frame = NewControlFrame("media.chunk");
            frame.MediaTransferId = transferId;
            frame.MediaChunkIndex = index;
            frame.MediaData = base64;
            await SendMessageAsync(frame);
        }

        /// <summary>The last piece is through: the adapter can send the file.</summary>
        public async Task SendMediaEndAsync(string transferId, string caption)
        {
            var frame = NewControlFrame("media.end");
            frame.MediaTransferId = transferId;
            frame.Text = caption ?? "";
            await SendMessageAsync(frame);
        }

        /// <summary>
        /// Requests the media bytes of a message the app already has. The chat
        /// travels in Text, the message in RelatedMessageId.
        /// </summary>
        public async Task RequestMediaAsync(string chatId, string messageId)
        {
            if (string.IsNullOrEmpty(chatId) || string.IsNullOrEmpty(messageId)) return;

            var frame = NewControlFrame("media.get");
            frame.Text = chatId;
            frame.RelatedMessageId = messageId;
            await SendMessageAsync(frame);
        }

        /// <summary>
        /// Routes a decrypted message: the control frames (Type = System) go to the
        /// ControlMessageReceived event, the others to MessageReceived.
        /// </summary>
        private void DispatchMessage(ChatMessage message)
        {
            if (message == null) return;

            if (message.Type == MessageType.System)
            {
                // Every control frame the adapter sends, in order, so the
                // diagnostics page can show whether the answer ever arrived.
                Diag.Frame("in ", message.Command, message.Text);

                if (message.Command == "state")
                {
                    WhatsAppState = string.IsNullOrEmpty(message.State) ? "disconnected" : message.State;
                    AccountJid = message.AccountJid ?? "";
                }

                // The shared service created this phone's token on its first
                // connection (see the `hello` case in the adapter), and this frame
                // is the only copy that will ever exist: it is written down before
                // the frame goes anywhere else.
                if (message.Command == "registered" && !string.IsNullOrEmpty(message.Token))
                {
                    string created = message.Token;
                    DispatchOnUiThread(() =>
                    {
                        SettingsService.Token = created;
                        RaiseConnectionStatusChanged(Loc.Get("CommService_Registered",
                            "This device is now registered on the server."));
                    });
                }
                else if (message.Command == "unauthorized")
                {
                    // The service hands its tokens out by hand (registration is
                    // off) and this phone has none that it accepts.
                    DispatchOnUiThread(() => RaiseConnectionStatusChanged(Loc.Get("CommService_NotAuthorized",
                        "This server did not recognize this device. Check the token in the settings.")));
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

                StreamSocket target = client;
                try
                {
                    // A whole transmission is one piece of the queue: two transmissions
                    // in flight on the same OutputStream would interleave like two
                    // StoreAsync on the same buffer.
                    await _writes.RunAsync(delegate
                    {
                        return FrameCodec.WriteFrameAsync(
                            FrameCodec.CreateFrameWriter(target.OutputStream), payload);
                    });
                }
                catch (Exception ex)
                {
                    Diag.Failed("BroadcastToAllClientsAsync", ex);
                    deadClients.Add(target);
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
        /// The writer arrives from outside because it is the socket one, created once
        /// in ConnectToServerAsync: before, a new one was created - and never closed
        /// - for every frame sent.
        /// The payload is encrypted now, with the writer of now: what enters the
        /// queue is an already-decided write, not a promise to write.
        /// </summary>
        private Task SendFrameAsync(DataWriter writer, byte[] jsonBytes)
        {
            byte[] payload = CryptoHelper.Encrypt(jsonBytes);
            return _writes.RunAsync(delegate { return FrameCodec.WriteFrameAsync(writer, payload); });
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
            // Stops a reader still running before closing its objects: it is what
            // tells apart a deliberate disconnection from a network failure to report.
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

            // _uiDispatcher is not cleared: it is not tied to the socket, and clearing
            // it forced GetUiDispatcher to redo the three calls (and log their
            // failures again) on the next line.
            DispatchOnUiThread(() =>
                RaiseConnectionStatusChanged(Loc.Get("CommService_Disconnected", "Disconnected")));
        }
    }
}
