using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Security.Cryptography;
using WhatsappApp.Services;

namespace WhatsappApp.Protocol.Transport
{
    public sealed class WebSocketTransport : ITransport
    {
        private const string Origin = "https://web.whatsapp.com";
        private const int MaximumHandshakeBytes = 16384;

        private readonly object _gate = new object();
        private readonly SemaphoreSlim _sending = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _receiving = new SemaphoreSlim(1, 1);

        private StreamSocket _socket;
        private Stream _input;
        private Stream _output;
        private bool _closed;

        public async Task ConnectAsync(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ProtocolException("a secure WebSocket URL is required");

            var socket = new StreamSocket();
            lock (_gate)
            {
                if (_closed || _socket != null)
                    throw new ProtocolException("the WebSocket transport is already closed or connected");
                _socket = socket;
            }

            Stream input = null;
            Stream output = null;
            Stream activeInput = null;
            Stream activeOutput = null;
            try
            {
                var host = new HostName(uri.DnsSafeHost);
                await socket.ConnectAsync(host, uri.Port.ToString(CultureInfo.InvariantCulture),
                    SocketProtectionLevel.Tls12);

                input = socket.InputStream.AsStreamForRead();
                output = socket.OutputStream.AsStreamForWrite();
                lock (_gate)
                {
                    if (_closed) throw new ProtocolException("the WebSocket transport was closed while connecting");
                    _input = input;
                    _output = output;
                    activeInput = input;
                    activeOutput = output;
                    input = null;
                    output = null;
                }

                byte[] nonce = new byte[16];
                CryptographicBuffer.CopyToByteArray(CryptographicBuffer.GenerateRandom(16), out nonce);
                string key = Convert.ToBase64String(nonce);

                byte[] request = WebSocketHandshake.BuildRequestBytes(uri, Origin, key);
                await activeOutput.WriteAsync(request, 0, request.Length).ConfigureAwait(false);
                await activeOutput.FlushAsync().ConfigureAwait(false);

                string response = await ReadResponseHeadersAsync(activeInput).ConfigureAwait(false);
                WebSocketHandshake.ValidateResponse(response, key);
                Diag.Ok("whatsapp: connected to " + url);
            }
            catch
            {
                DisposeStream(input, "WebSocketTransport/failed input");
                DisposeStream(output, "WebSocketTransport/failed output");
                Close();
                throw;
            }
        }

        public Task SendAsync(byte[] payload)
        {
            if (payload == null) throw new ProtocolException("a WebSocket message has no payload");
            return WriteFrameAsync(2, payload);
        }

        public async Task<byte[]> ReceiveAsync()
        {
            await _receiving.WaitAsync().ConfigureAwait(false);
            try
            {
                Stream input = Input();
                using (var assembler = new WebSocketMessageAssembler())
                {
                    while (true)
                    {
                        WebSocketFrame frame = await ReadFrameAsync(input).ConfigureAwait(false);

                        if (frame.Opcode == 8)
                        {
                            await WriteFrameAsync(8, frame.Payload).ConfigureAwait(false);
                            throw new ProtocolException("the server closed the WebSocket connection");
                        }

                        if (frame.Opcode == 9)
                        {
                            await WriteFrameAsync(10, frame.Payload).ConfigureAwait(false);
                            continue;
                        }

                        if (frame.Opcode == 10) continue;

                        byte[] message = assembler.Accept(frame);
                        if (message != null) return message;
                    }
                }
            }
            finally
            {
                _receiving.Release();
            }
        }

        public void Close()
        {
            StreamSocket socket;
            Stream input;
            Stream output;

            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                socket = _socket;
                input = _input;
                output = _output;
                _socket = null;
                _input = null;
                _output = null;
            }

            DisposeStream(input, "WebSocketTransport/close input");
            DisposeStream(output, "WebSocketTransport/close output");

            if (socket == null) return;
            try { socket.Dispose(); }
            catch (Exception error) { Diag.Failed("WebSocketTransport/close socket", error); }
        }

        private async Task WriteFrameAsync(byte opcode, byte[] payload)
        {
            await _sending.WaitAsync().ConfigureAwait(false);
            try
            {
                Stream output = Output();
                byte[] mask = new byte[4];
                CryptographicBuffer.CopyToByteArray(CryptographicBuffer.GenerateRandom(4), out mask);
                byte[] frame = WebSocketFrameCodec.EncodeClient(opcode, payload, mask);
                await output.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
                await output.FlushAsync().ConfigureAwait(false);
            }
            finally
            {
                _sending.Release();
            }
        }

        private static async Task<string> ReadResponseHeadersAsync(Stream input)
        {
            var bytes = new MemoryStream();
            var oneByte = new byte[1];

            while (bytes.Length < MaximumHandshakeBytes)
            {
                int read = await input.ReadAsync(oneByte, 0, 1).ConfigureAwait(false);
                if (read == 0) throw new ProtocolException("the server closed during the WebSocket upgrade");

                bytes.WriteByte(oneByte[0]);
                int count = (int)bytes.Length;
                if (count >= 4)
                {
                    byte[] current = bytes.GetBuffer();
                    if (current[count - 4] == 13 && current[count - 3] == 10
                        && current[count - 2] == 13 && current[count - 1] == 10)
                    {
                        var headerBytes = new byte[count];
                        Buffer.BlockCopy(current, 0, headerBytes, 0, count);
                        return WebSocketHandshake.DecodeResponseHeaders(headerBytes);
                    }
                }
            }

            throw new ProtocolException("the WebSocket upgrade headers exceeded the size limit");
        }

        private static async Task<WebSocketFrame> ReadFrameAsync(Stream input)
        {
            var header = new byte[14];
            await ReadExactAsync(input, header, 0, 2).ConfigureAwait(false);

            int headerLength = 2;
            int frameLength = WebSocketFrameCodec.GetFrameLength(header, 0, headerLength);
            while (frameLength < 0)
            {
                if (headerLength == header.Length)
                    throw new ProtocolException("the WebSocket frame header is too long");

                await ReadExactAsync(input, header, headerLength, 1).ConfigureAwait(false);
                headerLength++;
                frameLength = WebSocketFrameCodec.GetFrameLength(header, 0, headerLength);
            }

            if (frameLength < headerLength)
                throw new ProtocolException("the WebSocket frame length is smaller than its header");

            var bytes = new byte[frameLength];
            Buffer.BlockCopy(header, 0, bytes, 0, headerLength);
            await ReadExactAsync(input, bytes, headerLength, frameLength - headerLength).ConfigureAwait(false);
            return WebSocketFrameCodec.Decode(bytes);
        }

        private static async Task ReadExactAsync(Stream input, byte[] buffer, int offset, int count)
        {
            int read = 0;
            while (read < count)
            {
                int amount = await input.ReadAsync(buffer, offset + read, count - read).ConfigureAwait(false);
                if (amount == 0) throw new ProtocolException("the server closed inside a WebSocket frame");
                read += amount;
            }
        }

        private Stream Input()
        {
            lock (_gate)
            {
                if (_closed || _input == null)
                    throw new ProtocolException("the WebSocket connection is not open");
                return _input;
            }
        }

        private Stream Output()
        {
            lock (_gate)
            {
                if (_closed || _output == null)
                    throw new ProtocolException("the WebSocket connection is not open");
                return _output;
            }
        }

        private static void DisposeStream(Stream stream, string where)
        {
            if (stream == null) return;
            try { stream.Dispose(); }
            catch (Exception error) { Diag.Failed(where, error); }
        }
    }
}
