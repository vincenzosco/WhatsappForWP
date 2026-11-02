using System;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// One RTP packet (RFC 3550) carrying Opus (RFC 7587).
    ///
    /// Why it is written here: a voice call sends sixty of these a second, and Windows Phone
    /// 8.1 has no RTP stack of any kind. What this file does is the header and nothing else:
    /// no jitter buffer, no NACK, no RTCP. Those are the next layer, and each one is easier
    /// to get right once the packet under it is a value that round-trips.
    ///
    /// The clock is the format's: Opus over RTP always counts time at **48 kHz**, whatever
    /// rate the samples were encoded at, so a 20 ms frame advances the timestamp by 960 even
    /// when the encoder was asked for 16 kHz (see `VoiceNoteEncoder`).
    /// </summary>
    public sealed class RtpPacket
    {
        /// <summary>The dynamic payload type WhatsApp's media uses for Opus.</summary>
        public const int OpusPayloadType = 111;

        /// <summary>Opus' RTP clock rate, in Hz (RFC 7587, section 4.1).</summary>
        public const int OpusClockRate = 48000;

        /// <summary>One frame, in milliseconds: what the recorder and the note writer use.</summary>
        public const int OpusFrameMilliseconds = 20;

        /// <summary>How much the timestamp moves for one frame at the RTP clock.</summary>
        public const int OpusFrameTicks = OpusClockRate / 1000 * OpusFrameMilliseconds;

        private const int HeaderBytes = 12;

        public int PayloadType { get; set; }

        /// <summary>The sequence number, modulo 65536: what orders the packets on arrival.</summary>
        public int SequenceNumber { get; set; }

        public uint Timestamp { get; set; }

        public uint Ssrc { get; set; }

        /// <summary>The payload, which for Opus is one encoded frame.</summary>
        public byte[] Payload { get; set; }

        /// <summary>One Opus frame, ready to send.</summary>
        public static RtpPacket Opus(int sequenceNumber, uint timestamp, uint ssrc, byte[] payload)
        {
            if (payload == null) throw new ProtocolException("an RTP packet carries a payload");

            var packet = new RtpPacket();
            packet.PayloadType = OpusPayloadType;
            packet.SequenceNumber = sequenceNumber & 0xffff;
            packet.Timestamp = timestamp;
            packet.Ssrc = ssrc;
            packet.Payload = payload;

            return packet;
        }

        /// <summary>The twelve-byte header and the payload.</summary>
        public byte[] ToBytes()
        {
            byte[] payload = Payload ?? new byte[0];
            var bytes = new byte[HeaderBytes + payload.Length];

            // V=2, P=0, X=0, CC=0.
            bytes[0] = 0x80;
            // M=0 and the payload type.
            bytes[1] = (byte)(PayloadType & 0x7f);

            bytes[2] = (byte)((SequenceNumber >> 8) & 0xff);
            bytes[3] = (byte)(SequenceNumber & 0xff);

            bytes[4] = (byte)(Timestamp >> 24);
            bytes[5] = (byte)(Timestamp >> 16);
            bytes[6] = (byte)(Timestamp >> 8);
            bytes[7] = (byte)Timestamp;

            bytes[8] = (byte)(Ssrc >> 24);
            bytes[9] = (byte)(Ssrc >> 16);
            bytes[10] = (byte)(Ssrc >> 8);
            bytes[11] = (byte)Ssrc;

            Buffer.BlockCopy(payload, 0, bytes, HeaderBytes, payload.Length);
            return bytes;
        }

        /// <summary>
        /// A packet read back, or null when the bytes are not a version 2 RTP packet this
        /// code can trust. It never throws: this runs on whatever arrives on a UDP socket,
        /// and a malformed packet is one packet to drop.
        ///
        /// The CSRC list, the header extension and the padding bit are honoured - a peer
        /// that sets them is still readable - but nothing of them is kept: only the payload
        /// matters to the decoder.
        /// </summary>
        public static RtpPacket FromBytes(byte[] bytes, int length)
        {
            if (bytes == null) return null;
            if (length <= 0) length = bytes.Length;
            if (length < HeaderBytes || length > bytes.Length) return null;

            int version = (bytes[0] >> 6) & 0x03;
            if (version != 2) return null;

            bool padding = (bytes[0] & 0x20) != 0;
            bool extension = (bytes[0] & 0x10) != 0;
            int csrcCount = bytes[0] & 0x0f;

            int at = HeaderBytes + csrcCount * 4;
            if (at > length) return null;

            var packet = new RtpPacket();
            packet.PayloadType = bytes[1] & 0x7f;
            packet.SequenceNumber = (bytes[2] << 8) | bytes[3];
            packet.Timestamp = ((uint)bytes[4] << 24) | ((uint)bytes[5] << 16)
                | ((uint)bytes[6] << 8) | bytes[7];
            packet.Ssrc = ((uint)bytes[8] << 24) | ((uint)bytes[9] << 16)
                | ((uint)bytes[10] << 8) | bytes[11];

            if (extension)
            {
                if (at + 4 > length) return null;

                int words = (bytes[at + 2] << 8) | bytes[at + 3];
                at += 4 + words * 4;
                if (at > length) return null;
            }

            int end = length;
            if (padding)
            {
                int pad = bytes[length - 1];
                if (pad == 0 || pad > end - at) return null;
                end -= pad;
            }

            packet.Payload = new byte[end - at];
            Buffer.BlockCopy(bytes, at, packet.Payload, 0, packet.Payload.Length);

            return packet;
        }
    }
}
