using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using WhatsappApp.Services;

namespace WhatsappApp.Protocol.Transport
{
    /// <summary>
    /// The phone's half of <see cref="ITransport"/>: one WebSocket message is one
    /// protocol payload, over WinRT's `MessageWebSocket`.
    ///
    /// It is the counterpart of `ProtocolTests/DesktopTransport.cs`, and the two are
    /// deliberately the same shape - `ClientWebSocket` on the desktop, `MessageWebSocket`
    /// on the phone - so the driver that pairs this device is the driver that was already
    /// run against the real WhatsApp server from a machine.
    ///
    /// It is **not** under `Protocol/Transport` because the core is portable: this file is
    /// the only one there that names a WinRT type, and `ProtocolTests.csproj` excludes it
    /// by name, exactly so the desktop host can compile its siblings.
    ///
    /// Reading is pull-based - `ReceiveAsync` - because that is what the driver wants, but
    /// WinRT delivers messages by event. The bridge between the two is a one-slot handoff:
    /// a message that arrives with nobody waiting is queued, and a read with nothing queued
    /// waits for the next one. One reader at a time, which is what `WhatsAppSocket` is.
    /// </summary>
    public sealed class WebSocketTransport : ITransport
    {
        private readonly Queue<byte[]> _arrived = new Queue<byte[]>();

        /// <summary>Guards the queue, the waiter and the close reason: all three are touched from both the UI thread and the socket's.</summary>
        private readonly object _gate = new object();

        private TaskCompletionSource<byte[]> _waiting;
        private Exception _failure;
        private MessageWebSocket _socket;
        private DataWriter _writer;

        /// <summary>
        /// Opens the connection.
        ///
        /// The `Origin` header is not decoration: the server refuses a client that does
        /// not claim to be the web client, which is why the desktop transport sets it too.
        /// </summary>
        public async Task ConnectAsync(string url)
        {
            _socket = new MessageWebSocket();

            // Binary, and through `Control`: the WP8.1 projection does not expose a
            // `MessageType` on the socket itself (the ARM build answered CS1061), and the
            // default is text - which would put the protocol's bytes through a UTF-8
            // encoder and change every one of them above 0x7f.
            _socket.Control.MessageType = SocketMessageType.Binary;

            _socket.SetRequestHeader("Origin", "https://web.whatsapp.com");
            _socket.Closed += OnClosed;
            _socket.MessageReceived += OnMessageReceived;

            await _socket.ConnectAsync(new Uri(url));

            // One writer for the life of the connection: a message is sent when
            // `StoreAsync` is called, and the stream is left attached on purpose.
            _writer = new DataWriter(_socket.OutputStream);
            Diag.Ok("whatsapp: connected to " + url);
        }

        /// <summary>Sends one encoded node as one message.</summary>
        public async Task SendAsync(byte[] payload)
        {
            if (_writer == null) throw new ProtocolException("the connection is not open");

            _writer.WriteBytes(payload);
            await _writer.StoreAsync();
            await _writer.FlushAsync();
        }

        /// <summary>
        /// The next message, waiting for it if it has not arrived.
        ///
        /// Throws when the connection is closed or has failed: a read that returned an
        /// empty payload instead would be read as a node that decoded from nothing.
        /// </summary>
        public Task<byte[]> ReceiveAsync()
        {
            lock (_gate)
            {
                if (_arrived.Count > 0) return Task.FromResult(_arrived.Dequeue());
                if (_failure != null) return Failure(_failure);

                _waiting = new TaskCompletionSource<byte[]>();
                return _waiting.Task;
            }
        }

        /// <summary>Closes the connection. Safe to call more than once.</summary>
        public void Close()
        {
            MessageWebSocket socket;
            lock (_gate)
            {
                socket = _socket;
                if (_failure == null) _failure = new ProtocolException("the connection was closed");
                Release(null);
            }

            if (socket == null) return;

            try
            {
                socket.Dispose();
            }
            catch (Exception error)
            {
                // A socket that is already gone is the state Close() wants.
                Diag.Failed("WebSocketTransport/close", error);
            }
        }

        /// <summary>
        /// The message WinRT delivered. It is `async void` because that is the only
        /// shape an event handler has here, so nothing may escape it: a fault is written
        /// down and handed to the reader that is waiting, which is where it matters.
        /// </summary>
        private async void OnMessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                DataReader reader = args.GetDataReader();
                uint length = reader.UnconsumedBufferLength;

                await reader.LoadAsync(length);

                var payload = new byte[length];
                reader.ReadBytes(payload);

                lock (_gate) Release(payload);
            }
            catch (Exception error)
            {
                Diag.Failed("WebSocketTransport/receive", error);

                lock (_gate)
                {
                    if (_failure == null) _failure = error;
                    Release(null);
                }
            }
        }

        /// <summary>
        /// The peer hung up. The reader that is waiting gets the reason instead of
        /// waiting forever: a pairing session that is never answered is a QR on screen
        /// that nothing will ever scan into a session.
        ///
        /// The sender is `IWebSocket` and not `MessageWebSocket`: that is the delegate
        /// WP8.1 signs this event with (CS0123 otherwise).
        /// </summary>
        private void OnClosed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            Exception failure = new ProtocolException("the server closed the connection ("
                + args.Code + ": " + args.Reason + ")");

            bool first;
            lock (_gate)
            {
                first = _failure == null;
                if (first) _failure = failure;
                Release(null);
            }

            if (first) Diag.Failed("WebSocketTransport/closed", failure);
        }

        /// <summary>
        /// Hands a message to the waiter, or queues it. Called with `_gate` held.
        /// </summary>
        private void Release(byte[] payload)
        {
            if (_waiting == null)
            {
                if (payload != null) _arrived.Enqueue(payload);
                return;
            }

            TaskCompletionSource<byte[]> waiter = _waiting;
            _waiting = null;

            if (payload != null) waiter.SetResult(payload);
            else waiter.SetException(_failure == null
                ? new ProtocolException("the connection was closed")
                : _failure);
        }

        /// <summary>A read on a connection that is already gone: a faulted task, not a throw.</summary>
        private static Task<byte[]> Failure(Exception error)
        {
            var source = new TaskCompletionSource<byte[]>();
            source.SetException(error);
            return source.Task;
        }
    }
}
