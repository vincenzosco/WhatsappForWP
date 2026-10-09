using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using WhatsappApp.Protocol.Transport;

namespace ProtocolTests
{
    /// <summary>
    /// An <see cref="ITransport"/> over `ClientWebSocket`, which exists on the desktop
    /// and not on the phone. It is the reason the protocol driver can be pointed at the
    /// real `wss://web.whatsapp.com/ws/chat` from this machine: the same
    /// <c>WhatsAppSocket</c> that will run on the Lumia runs here first, against the
    /// server it will actually talk to.
    ///
    /// One WebSocket message is one protocol payload, which is what `ws` - the client
    /// the reference uses - does: it sends the encoded node as the message body and
    /// hands a received message over whole.
    /// </summary>
    public sealed class DesktopTransport : ITransport
    {
        private readonly int _timeoutMilliseconds;
        private ClientWebSocket _socket;

        public DesktopTransport(int timeoutMilliseconds)
        {
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        public async Task ConnectAsync(string url)
        {
            _socket = new ClientWebSocket();
            _socket.Options.SetRequestHeader("Origin", "https://web.whatsapp.com");

            using (var deadline = new CancellationTokenSource(_timeoutMilliseconds))
            {
                await _socket.ConnectAsync(new Uri(url), deadline.Token);
            }
        }

        public async Task SendAsync(byte[] payload)
        {
            using (var deadline = new CancellationTokenSource(_timeoutMilliseconds))
            {
                await _socket.SendAsync(new ArraySegment<byte>(payload),
                    WebSocketMessageType.Binary, true, deadline.Token);
            }
        }

        public async Task<byte[]> ReceiveAsync()
        {
            using (var deadline = new CancellationTokenSource(_timeoutMilliseconds))
            {
                return await ReceiveRawAsync(deadline.Token);
            }
        }

        /// <summary>
        /// The raw bytes of one WebSocket message, with no interpretation. The live
        /// probe uses it to report what the server actually sent before deciding how
        /// a payload is framed.
        /// </summary>
        public async Task<byte[]> ReceiveRawAsync(CancellationToken token)
        {
            var buffer = new byte[8192];
            var total = new System.IO.MemoryStream();

            while (true)
            {
                WebSocketReceiveResult result = await _socket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                total.Write(buffer, 0, result.Count);
                if (result.EndOfMessage) break;
            }

            return total.ToArray();
        }

        public void Close()
        {
            if (_socket == null) return;

            try
            {
                _socket.Dispose();
            }
            catch (Exception)
            {
                // A socket that is already gone is exactly the state Close() wants.
            }

            _socket = null;
        }
    }
}
