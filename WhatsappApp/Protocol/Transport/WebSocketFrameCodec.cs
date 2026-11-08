using System;

namespace WhatsappApp.Protocol.Transport
{
    public sealed class WebSocketFrame
    {
        public byte Opcode { get; private set; }
        public bool IsFinal { get; private set; }
        public byte[] Payload { get; private set; }

        internal WebSocketFrame(byte opcode, bool isFinal, byte[] payload)
        {
            Opcode = opcode;
            IsFinal = isFinal;
            Payload = payload;
        }
    }

    public static class WebSocketFrameCodec
    {
        private const int MaximumPayloadLength = 16 * 1024 * 1024;

        public static byte[] EncodeClient(byte opcode, byte[] payload, byte[] maskKey)
        {
            if (payload == null) throw new ProtocolException("a WebSocket frame needs a payload");
            if (maskKey == null || maskKey.Length != 4)
                throw new ProtocolException("a client WebSocket frame needs a four-byte mask");

            ValidateOpcode(opcode);
            ValidateControlFrame(opcode, true, (ulong)payload.Length);
            if (payload.Length > MaximumPayloadLength)
                throw new ProtocolException("a WebSocket frame exceeds the size limit");

            int extendedLength = payload.Length < 126 ? 0 : payload.Length <= ushort.MaxValue ? 2 : 8;
            int headerLength = 2 + extendedLength + maskKey.Length;
            var frame = new byte[headerLength + payload.Length];
            frame[0] = (byte)(0x80 | opcode);

            if (extendedLength == 0)
            {
                frame[1] = (byte)(0x80 | payload.Length);
            }
            else if (extendedLength == 2)
            {
                frame[1] = 0x80 | 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)payload.Length;
            }
            else
            {
                frame[1] = 0x80 | 127;
                ulong length = (ulong)payload.Length;
                for (int i = 0; i < 8; i++)
                    frame[2 + i] = (byte)(length >> (56 - i * 8));
            }

            int maskOffset = 2 + extendedLength;
            Buffer.BlockCopy(maskKey, 0, frame, maskOffset, maskKey.Length);
            int payloadOffset = headerLength;
            for (int i = 0; i < payload.Length; i++)
                frame[payloadOffset + i] = (byte)(payload[i] ^ maskKey[i & 3]);

            return frame;
        }

        public static int GetFrameLength(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ProtocolException("a WebSocket frame buffer is missing");
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ProtocolException("a WebSocket frame range is outside its buffer");
            if (count < 2) return -1;

            byte first = buffer[offset];
            byte second = buffer[offset + 1];
            bool isFinal = (first & 0x80) != 0;
            byte opcode = (byte)(first & 0x0f);

            if ((first & 0x70) != 0)
                throw new ProtocolException("a WebSocket frame uses an unnegotiated extension");
            ValidateOpcode(opcode);

            int lengthMarker = second & 0x7f;
            int extendedLength = lengthMarker == 126 ? 2 : lengthMarker == 127 ? 8 : 0;
            int maskLength = (second & 0x80) != 0 ? 4 : 0;
            int headerLength = 2 + extendedLength + maskLength;
            if (count < headerLength) return -1;

            ulong payloadLength;
            if (lengthMarker < 126)
            {
                payloadLength = (ulong)lengthMarker;
            }
            else if (lengthMarker == 126)
            {
                payloadLength = ((ulong)buffer[offset + 2] << 8) | buffer[offset + 3];
                if (payloadLength < 126)
                    throw new ProtocolException("a WebSocket frame uses a noncanonical payload length");
            }
            else
            {
                if ((buffer[offset + 2] & 0x80) != 0)
                    throw new ProtocolException("a WebSocket frame payload length has its reserved bit set");

                payloadLength = 0;
                for (int i = 0; i < 8; i++)
                    payloadLength = (payloadLength << 8) | buffer[offset + 2 + i];

                if (payloadLength <= ushort.MaxValue)
                    throw new ProtocolException("a WebSocket frame uses a noncanonical payload length");
            }

            ValidateControlFrame(opcode, isFinal, payloadLength);
            if (payloadLength > MaximumPayloadLength)
                throw new ProtocolException("a WebSocket frame exceeds the size limit");

            return headerLength + (int)payloadLength;
        }

        public static WebSocketFrame Decode(byte[] buffer)
        {
            if (buffer == null) throw new ProtocolException("a WebSocket frame is missing");

            int frameLength = GetFrameLength(buffer, 0, buffer.Length);
            if (frameLength < 0 || frameLength != buffer.Length)
                throw new ProtocolException("a WebSocket frame is incomplete or has trailing bytes");

            byte first = buffer[0];
            byte second = buffer[1];
            byte opcode = (byte)(first & 0x0f);
            bool isFinal = (first & 0x80) != 0;
            bool isMasked = (second & 0x80) != 0;
            int lengthMarker = second & 0x7f;
            int extendedLength = lengthMarker == 126 ? 2 : lengthMarker == 127 ? 8 : 0;
            int maskOffset = 2 + extendedLength;
            int payloadOffset = maskOffset + (isMasked ? 4 : 0);
            int payloadLength = buffer.Length - payloadOffset;
            var payload = new byte[payloadLength];

            if (isMasked)
            {
                for (int i = 0; i < payloadLength; i++)
                    payload[i] = (byte)(buffer[payloadOffset + i] ^ buffer[maskOffset + (i & 3)]);
            }
            else if (payloadLength > 0)
            {
                Buffer.BlockCopy(buffer, payloadOffset, payload, 0, payloadLength);
            }

            return new WebSocketFrame(opcode, isFinal, payload);
        }

        private static void ValidateOpcode(byte opcode)
        {
            if (opcode != 0 && opcode != 1 && opcode != 2
                && opcode != 8 && opcode != 9 && opcode != 10)
                throw new ProtocolException("a WebSocket frame has an unknown opcode");
        }

        private static void ValidateControlFrame(byte opcode, bool isFinal, ulong payloadLength)
        {
            if (opcode < 8) return;
            if (!isFinal) throw new ProtocolException("a WebSocket control frame is fragmented");
            if (payloadLength > 125) throw new ProtocolException("a WebSocket control frame is too long");
        }
    }
}
