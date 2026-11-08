using System;
using System.IO;
using WhatsappApp.Protocol;

namespace WhatsappApp.Protocol.Transport
{
    public sealed class WebSocketMessageAssembler : IDisposable
    {
        private const int MaximumMessageLength = 16 * 1024 * 1024;
        private MemoryStream _fragments;

        public byte[] Accept(WebSocketFrame frame)
        {
            if (frame == null) throw new ProtocolException("a WebSocket frame is missing");

            if (frame.Opcode >= 8) return null;
            if (frame.Opcode == 1)
                throw new ProtocolException("a text WebSocket message cannot carry a protocol node");

            if (frame.Opcode == 2)
            {
                if (_fragments != null)
                    throw new ProtocolException("a WebSocket message started before its previous fragments ended");
                if (frame.IsFinal) return frame.Payload;

                _fragments = new MemoryStream();
                Append(frame.Payload);
                return null;
            }

            if (frame.Opcode != 0 || _fragments == null)
                throw new ProtocolException("a WebSocket continuation has no fragmented message");

            Append(frame.Payload);
            if (!frame.IsFinal) return null;

            byte[] message = _fragments.ToArray();
            _fragments.Dispose();
            _fragments = null;
            return message;
        }

        public void Dispose()
        {
            if (_fragments == null) return;
            _fragments.Dispose();
            _fragments = null;
        }

        private void Append(byte[] payload)
        {
            if (payload == null) throw new ProtocolException("a WebSocket frame has no payload");
            if (_fragments.Length > MaximumMessageLength - payload.Length)
                throw new ProtocolException("a fragmented WebSocket message exceeds the size limit");

            _fragments.Write(payload, 0, payload.Length);
        }
    }
}
