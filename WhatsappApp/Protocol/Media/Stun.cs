using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>The two bits of the message type that say what a message is for.</summary>
    public enum StunClass
    {
        Request = 0,
        Indication = 1,
        SuccessResponse = 2,
        ErrorResponse = 3
    }

    /// <summary>
    /// The methods this client can name (RFC 5389, section 6, and RFC 5766). Only Binding is
    /// answered here; the rest are decoded so a peer's Allocate or ChannelBind is recognisable
    /// instead of looking like a corrupted Binding.
    /// </summary>
    public enum StunMethod
    {
        Binding = 0x001,
        Allocate = 0x003,
        Refresh = 0x004,
        Send = 0x006,
        Data = 0x007,
        CreatePermission = 0x008,
        ChannelBind = 0x009,
        Other = 0x3FF
    }

    /// <summary>
    /// One STUN message: the 20-byte header, the attributes, and the two hashes the traversal
    /// depends on - MESSAGE-INTEGRITY (HMAC-SHA1, RFC 5389 section 15.4) and FINGERPRINT
    /// (CRC-32, section 15.5).
    ///
    /// Why it is here: a WhatsApp call reaches the other phone over ICE, and ICE is STUN with
    /// a username and a password. This is the half that can be written and pinned against the
    /// RFC's own vectors (RFC 5769); what it is **not** is a full ICE stack - the controlling
    /// agent, the TURN allocation and WhatsApp's relay are the layers above it.
    ///
    /// The two hashes are computed over the message **as it will be on the wire**: both cover
    /// the header's length field, so the length has to be the final one before either is
    /// calculated. That is why `Seal` does both at once and no caller can add an attribute
    /// afterwards.
    /// </summary>
    public sealed class StunMessage
    {
        /// <summary>Every STUN message after RFC 5389 starts with this.</summary>
        public const uint MagicCookie = 0x2112A442;

        public const int HeaderLength = 20;

        public const int TransactionIdLength = 12;

        private const int AttributeHeaderLength = 4;

        private const int IntegrityLength = 24;

        private const int FingerprintLength = 8;

        /// <summary>The constant FINGERPRINT is xored with (RFC 5389, section 15.5).</summary>
        private const uint FingerprintXor = 0x5354554E;

        private readonly List<StunAttribute> _attributes = new List<StunAttribute>();

        // The datagram this message was parsed from, kept because verification hashes the
        // bytes the peer sent and not a re-serialization of them: padding may be any value,
        // and a rebuild that pads with zeros would not verify against a peer that padded
        // with spaces.
        private byte[] _raw;
        private int _integrityAt = -1;
        private int _fingerprintAt = -1;

        public StunMessage(StunMethod method, StunClass cls, byte[] transactionId)
        {
            if (transactionId == null || transactionId.Length != TransactionIdLength)
                throw new ProtocolException("STUN needs a 12-byte transaction id");

            Method = method;
            Class = cls;
            TransactionId = new byte[TransactionIdLength];
            Buffer.BlockCopy(transactionId, 0, TransactionId, 0, TransactionIdLength);
        }

        public StunMethod Method { get; private set; }

        public StunClass Class { get; private set; }

        public byte[] TransactionId { get; private set; }

        public IList<StunAttribute> Attributes
        {
            get { return _attributes; }
        }

        /// <summary>Whether this datagram is a STUN message at all, without parsing it.</summary>
        public static bool Looks(byte[] data, int offset, int count)
        {
            if (data == null || count < HeaderLength || offset < 0 || offset + count > data.Length)
                return false;
            if ((data[offset] & 0xC0) != 0) return false;

            return ReadUInt32(data, offset + 4) == MagicCookie;
        }

        public StunAttribute Find(ushort type)
        {
            for (int i = 0; i < _attributes.Count; i++)
            {
                if (_attributes[i].Type == type) return _attributes[i];
            }

            return null;
        }

        public void Add(StunAttribute attribute)
        {
            if (attribute == null) throw new ProtocolException("STUN cannot carry a null attribute");
            _attributes.Add(attribute);
        }

        public void AddText(ushort type, string value)
        {
            Add(StunAttribute.FromText(type, value));
        }

        public void AddUInt32(ushort type, uint value)
        {
            Add(StunAttribute.UInt32(type, value));
        }

        /// <summary>A XOR-MAPPED-ADDRESS for one endpoint, in this message's transaction.</summary>
        public void AddXorAddress(StunEndpoint endpoint)
        {
            Add(StunAttribute.XorAddress(StunAttributeType.XorMappedAddress, endpoint, TransactionId));
        }

        /// <summary>An ERROR-CODE: the class (3..6) and number (RFC 5389, section 15.6).</summary>
        public void AddError(int code, string reason)
        {
            Add(StunAttribute.Error(code, reason));
        }

        /// <summary>
        /// The message on the wire as it stands: no MESSAGE-INTEGRITY, no FINGERPRINT. A
        /// request that goes out is always `Seal`ed, because ICE checks that are not
        /// authenticated are dropped by every agent.
        /// </summary>
        public byte[] ToBytes()
        {
            return Serialize(null, false);
        }

        /// <summary>
        /// The message as it goes out: MESSAGE-INTEGRITY with `key`, then FINGERPRINT. Both
        /// are appended in this order, which is the order RFC 5389 requires and the order
        /// every peer expects.
        /// </summary>
        public byte[] Seal(byte[] key)
        {
            if (key == null) throw new ProtocolException("a sealed STUN message needs a key");

            return Serialize(key, true);
        }

        private byte[] Serialize(byte[] key, bool fingerprint)
        {
            int body = 0;
            for (int i = 0; i < _attributes.Count; i++) body += _attributes[i].WireLength;

            int extra = (key != null ? IntegrityLength : 0) + (fingerprint ? FingerprintLength : 0);
            var buffer = new byte[HeaderLength + body + extra];

            WriteHeader(buffer, body + extra);

            int at = HeaderLength;
            for (int i = 0; i < _attributes.Count; i++)
            {
                at += _attributes[i].Write(buffer, at);
            }

            if (key != null)
            {
                int end = at + IntegrityLength;

                // The length the peer will check against: up to the end of MESSAGE-INTEGRITY,
                // and not one byte of FINGERPRINT (RFC 5389, section 15.4). `buffer` already
                // carries the final length, which includes FINGERPRINT - so the two bytes are
                // patched for the hash and patched back after it.
                //
                // The hash covers the message **up to but not including** this attribute's
                // own header, while the patched length counts it: the attribute announces
                // itself in the length and then signs the bytes before it. RFC 5769's first
                // vector is what proved the difference - hashing the header too yields a
                // different MAC that no peer accepts.
                WriteLength(buffer, end - HeaderLength);
                byte[] mac = Hmac.ComputeSha1(key, Slice(buffer, 0, at));
                WriteLength(buffer, body + extra);

                WriteUInt16(buffer, at, StunAttributeType.MessageIntegrity);
                WriteUInt16(buffer, at + 2, 20);
                Buffer.BlockCopy(mac, 0, buffer, at + AttributeHeaderLength, mac.Length);
                at = end;
            }

            if (fingerprint)
            {
                uint crc = Crc32(buffer, 0, at) ^ FingerprintXor;

                WriteUInt16(buffer, at, StunAttributeType.Fingerprint);
                WriteUInt16(buffer, at + 2, 4);
                WriteUInt32(buffer, at + AttributeHeaderLength, crc);
            }

            return buffer;
        }

        /// <summary>
        /// Parses one datagram. Every length is checked against the buffer: a truncated
        /// attribute is a `ProtocolException` and not a shorter message, because the bytes
        /// after it would be read as a different attribute altogether.
        /// </summary>
        public static StunMessage Parse(byte[] data)
        {
            if (data == null) throw new ProtocolException("STUN needs bytes");
            if (data.Length < HeaderLength)
                throw new ProtocolException("a STUN message is at least 20 bytes");
            if ((data[0] & 0xC0) != 0)
                throw new ProtocolException("not a STUN message: the top two bits are not zero");
            if (ReadUInt32(data, 4) != MagicCookie)
                throw new ProtocolException("not a STUN message: the magic cookie is not there");

            int length = ReadUInt16(data, 2);
            if ((length & 3) != 0)
                throw new ProtocolException("a STUN body is a multiple of four bytes");
            if (HeaderLength + length > data.Length)
                throw new ProtocolException("a STUN attribute runs past the datagram");

            var transaction = new byte[TransactionIdLength];
            Buffer.BlockCopy(data, 8, transaction, 0, TransactionIdLength);

            int type = ReadUInt16(data, 0);
            var message = new StunMessage(DecodeMethod(type), DecodeClass(type), transaction);

            // The copy is what verification hashes later, so it must be the datagram as it
            // arrived and not a view over a buffer the socket is about to reuse.
            message._raw = Slice(data, 0, HeaderLength + length);

            int at = HeaderLength;
            int end = HeaderLength + length;
            while (at + AttributeHeaderLength <= end)
            {
                ushort attributeType = ReadUInt16(data, at);
                int valueLength = ReadUInt16(data, at + 2);
                int padded = (valueLength + 3) & ~3;

                if (at + AttributeHeaderLength + padded > end)
                    throw new ProtocolException("a STUN attribute announces more bytes than it has");

                var value = new byte[valueLength];
                Buffer.BlockCopy(data, at + AttributeHeaderLength, value, 0, valueLength);
                message.Add(new StunAttribute(attributeType, value));

                if (attributeType == StunAttributeType.MessageIntegrity && message._integrityAt < 0)
                    message._integrityAt = at;
                if (attributeType == StunAttributeType.Fingerprint && message._fingerprintAt < 0)
                    message._fingerprintAt = at;

                at += AttributeHeaderLength + padded;
            }

            return message;
        }

        /// <summary>
        /// Whether the MESSAGE-INTEGRITY this message carries was made with `key`. A message
        /// without the attribute answers false: an unauthenticated check is not a check.
        ///
        /// `key` is the key itself, not a password: for ICE's short-term credentials that is
        /// the password, while long-term credentials use MD5(username:realm:password)
        /// (RFC 5389, section 15.4) and the caller derives it. ICE never uses the long-term
        /// form; the RFC 5769 vector that does is here to prove the HMAC, not the credentials.
        /// </summary>
        public bool VerifyIntegrity(byte[] key)
        {
            if (key == null) throw new ProtocolException("verifying a STUN message needs a key");
            if (_raw == null || _integrityAt < 0) return false;

            int end = _integrityAt + IntegrityLength;
            if (end > _raw.Length) return false;

            // The same patch the sender made: the length the hash covers ends at
            // MESSAGE-INTEGRITY, whatever FINGERPRINT after it says, while the bytes hashed
            // stop before the attribute's own header.
            var input = Slice(_raw, 0, _integrityAt);
            WriteLength(input, end - HeaderLength);

            byte[] mac = Hmac.ComputeSha1(key, input);
            return FixedEquals(mac, Slice(_raw, _integrityAt + AttributeHeaderLength, mac.Length));
        }

        /// <summary>Whether the FINGERPRINT this message carries matches its bytes.</summary>
        public bool VerifyFingerprint()
        {
            if (_raw == null || _fingerprintAt < 0) return false;
            if (_fingerprintAt + FingerprintLength > _raw.Length) return false;

            uint crc = Crc32(_raw, 0, _fingerprintAt) ^ FingerprintXor;
            return crc == ReadUInt32(_raw, _fingerprintAt + AttributeHeaderLength);
        }

        /// <summary>The USERNAME the check carried, or null.</summary>
        public string UserName
        {
            get
            {
                StunAttribute attribute = Find(StunAttributeType.UserName);
                return attribute == null ? null : attribute.Text;
            }
        }

        private void WriteHeader(byte[] buffer, int length)
        {
            WriteUInt16(buffer, 0, (ushort)EncodeType(Method, Class));
            WriteUInt16(buffer, 2, (ushort)length);
            WriteUInt32(buffer, 4, MagicCookie);
            Buffer.BlockCopy(TransactionId, 0, buffer, 8, TransactionIdLength);
        }

        private static int EncodeType(StunMethod method, StunClass cls)
        {
            int m = (int)method;
            int c = (int)cls;

            // RFC 5389, section 6, figure 3: method bits 0-3 stay, 4-6 and 7-11 move up, and
            // the two class bits land in the holes left between them.
            return (m & 0x000F)
                | ((m & 0x0070) << 1)
                | ((m & 0x0F80) << 2)
                | ((c & 0x01) << 4)
                | ((c & 0x02) << 7);
        }

        public static StunMethod DecodeMethod(int type)
        {
            int method = (type & 0x000F) | ((type & 0x00E0) >> 1) | ((type & 0x3E00) >> 2);

            switch (method)
            {
                case 0x001: return StunMethod.Binding;
                case 0x003: return StunMethod.Allocate;
                case 0x004: return StunMethod.Refresh;
                case 0x006: return StunMethod.Send;
                case 0x007: return StunMethod.Data;
                case 0x008: return StunMethod.CreatePermission;
                case 0x009: return StunMethod.ChannelBind;
                default: return StunMethod.Other;
            }
        }

        public static StunClass DecodeClass(int type)
        {
            return (StunClass)(((type >> 4) & 0x01) | ((type >> 7) & 0x02));
        }

        internal static byte[] Slice(byte[] data, int offset, int count)
        {
            var slice = new byte[count];
            Buffer.BlockCopy(data, offset, slice, 0, count);
            return slice;
        }

        internal static ushort ReadUInt16(byte[] data, int at)
        {
            return (ushort)((data[at] << 8) | data[at + 1]);
        }

        internal static uint ReadUInt32(byte[] data, int at)
        {
            return ((uint)data[at] << 24)
                | ((uint)data[at + 1] << 16)
                | ((uint)data[at + 2] << 8)
                | data[at + 3];
        }

        internal static void WriteUInt16(byte[] data, int at, ushort value)
        {
            data[at] = (byte)(value >> 8);
            data[at + 1] = (byte)value;
        }

        internal static void WriteUInt32(byte[] data, int at, uint value)
        {
            data[at] = (byte)(value >> 24);
            data[at + 1] = (byte)(value >> 16);
            data[at + 2] = (byte)(value >> 8);
            data[at + 3] = (byte)value;
        }

        private static void WriteLength(byte[] data, int length)
        {
            WriteUInt16(data, 2, (ushort)length);
        }

        /// <summary>
        /// CRC-32 as FINGERPRINT uses it: the reflected polynomial 0xEDB88320, an all-ones
        /// register and an all-ones final xor (RFC 5389, section 15.5).
        /// </summary>
        internal static uint Crc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[offset + i];
                for (int bit = 0; bit < 8; bit++)
                {
                    uint mask = (uint)(-(int)(crc & 1));
                    crc = (crc >> 1) ^ (0xEDB88320 & mask);
                }
            }

            return ~crc;
        }

        /// <summary>
        /// Compares two byte ranges in the time the difference needs and not in the time the
        /// first differing byte needs: these two are a MAC and a transaction id.
        /// </summary>
        internal static bool FixedEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;

            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];

            return difference == 0;
        }
    }

    /// <summary>The attribute types this client reads or writes (RFC 5389, section 18.2).</summary>
    public static class StunAttributeType
    {
        public const ushort MappedAddress = 0x0001;
        public const ushort UserName = 0x0006;
        public const ushort MessageIntegrity = 0x0008;
        public const ushort ErrorCode = 0x0009;
        public const ushort UnknownAttributes = 0x000A;
        public const ushort Realm = 0x0014;
        public const ushort Nonce = 0x0015;
        public const ushort XorMappedAddress = 0x0020;
        public const ushort Priority = 0x0024;
        public const ushort UseCandidate = 0x0025;
        public const ushort Software = 0x8022;
        public const ushort Fingerprint = 0x8028;
        public const ushort IceControlled = 0x8029;
        public const ushort IceControlling = 0x802A;
    }

    /// <summary>One attribute: a type, and the value exactly as the wire carries it.</summary>
    public sealed class StunAttribute
    {
        public StunAttribute(ushort type, byte[] value)
        {
            if (value == null) throw new ProtocolException("a STUN attribute needs a value");

            Type = type;
            Value = value;
        }

        public ushort Type { get; private set; }

        public byte[] Value { get; private set; }

        /// <summary>The four-byte header, the value, and the padding to a multiple of four.</summary>
        public int WireLength
        {
            get { return 4 + ((Value.Length + 3) & ~3); }
        }

        public string Text
        {
            get { return Encoding.UTF8.GetString(Value, 0, Value.Length); }
        }

        public uint AsUInt32
        {
            get
            {
                if (Value.Length != 4)
                    throw new ProtocolException("a 32-bit STUN attribute is four bytes, not " + Value.Length);

                return StunMessage.ReadUInt32(Value, 0);
            }
        }

        public static StunAttribute UInt32(ushort type, uint value)
        {
            var bytes = new byte[4];
            StunMessage.WriteUInt32(bytes, 0, value);
            return new StunAttribute(type, bytes);
        }

        /// <summary>
        /// A 64-bit attribute: ICE-CONTROLLING and ICE-CONTROLLED carry a tie breaker this
        /// wide (RFC 8445, section 7.1.2), and a four-byte one is a check no peer accepts.
        /// </summary>
        public static StunAttribute UInt64(ushort type, ulong value)
        {
            var bytes = new byte[8];
            StunMessage.WriteUInt32(bytes, 0, (uint)(value >> 32));
            StunMessage.WriteUInt32(bytes, 4, (uint)value);
            return new StunAttribute(type, bytes);
        }

        public ulong AsUInt64
        {
            get
            {
                if (Value.Length != 8)
                    throw new ProtocolException("a 64-bit STUN attribute is eight bytes, not " + Value.Length);

                return ((ulong)StunMessage.ReadUInt32(Value, 0) << 32)
                    | StunMessage.ReadUInt32(Value, 4);
            }
        }

        public static StunAttribute FromText(ushort type, string value)
        {
            if (value == null) throw new ProtocolException("a text STUN attribute needs text");

            return new StunAttribute(type, Encoding.UTF8.GetBytes(value));
        }

        public static StunAttribute Flag(ushort type)
        {
            return new StunAttribute(type, new byte[0]);
        }

        /// <summary>
        /// A mapped address, xored the way XOR-MAPPED-ADDRESS requires (RFC 5389, section
        /// 15.2): the port against the top half of the magic cookie, and the address against
        /// the cookie itself - four bytes for IPv4, the cookie and the transaction id for
        /// IPv6.
        /// </summary>
        public static StunAttribute XorAddress(ushort type, StunEndpoint endpoint, byte[] transactionId)
        {
            if (endpoint == null) throw new ProtocolException("an address attribute needs an endpoint");
            if (transactionId == null || transactionId.Length != StunMessage.TransactionIdLength)
                throw new ProtocolException("an address attribute needs the message's transaction id");

            byte[] address = endpoint.Address;
            if (address.Length != 4 && address.Length != 16)
                throw new ProtocolException("a mapped address is IPv4 or IPv6");

            var value = new byte[4 + address.Length];
            value[0] = 0;
            value[1] = (byte)(address.Length == 4 ? 0x01 : 0x02);
            StunMessage.WriteUInt16(value, 2, (ushort)(endpoint.Port ^ (int)(StunMessage.MagicCookie >> 16)));

            var cookie = new byte[16];
            StunMessage.WriteUInt32(cookie, 0, StunMessage.MagicCookie);
            Buffer.BlockCopy(transactionId, 0, cookie, 4, StunMessage.TransactionIdLength);

            for (int i = 0; i < address.Length; i++) value[4 + i] = (byte)(address[i] ^ cookie[i]);

            return new StunAttribute(type, value);
        }

        /// <summary>The endpoint a MAPPED-ADDRESS or XOR-MAPPED-ADDRESS names.</summary>
        public StunEndpoint ReadAddress(byte[] transactionId)
        {
            if (Value.Length < 8) throw new ProtocolException("an address attribute is at least 8 bytes");
            if (Value[0] != 0) throw new ProtocolException("an address attribute starts with a zero byte");

            int family = Value[1];
            if (family != 0x01 && family != 0x02)
                throw new ProtocolException("an address attribute is IPv4 or IPv6, not family " + family);

            int port = StunMessage.ReadUInt16(Value, 2);
            if (Type == StunAttributeType.XorMappedAddress)
                port ^= (int)(StunMessage.MagicCookie >> 16);

            int length = family == 0x01 ? 4 : 16;
            if (Value.Length != 4 + length)
                throw new ProtocolException("an address attribute for family " + family + " is "
                    + (4 + length) + " bytes, not " + Value.Length);

            var address = new byte[length];
            Buffer.BlockCopy(Value, 4, address, 0, length);

            if (Type == StunAttributeType.XorMappedAddress)
            {
                if (transactionId == null || transactionId.Length != StunMessage.TransactionIdLength)
                    throw new ProtocolException("an xored address needs the message's transaction id");

                var cookie = new byte[16];
                StunMessage.WriteUInt32(cookie, 0, StunMessage.MagicCookie);
                Buffer.BlockCopy(transactionId, 0, cookie, 4, StunMessage.TransactionIdLength);

                for (int i = 0; i < length; i++) address[i] = (byte)(address[i] ^ cookie[i]);
            }

            return new StunEndpoint(address, port);
        }

        /// <summary>ERROR-CODE: two zeros, a class, a number, then a reason in UTF-8.</summary>
        public static StunAttribute Error(int code, string reason)
        {
            if (code < 300 || code > 699) throw new ProtocolException("a STUN error code is 300..699");
            if (reason == null) reason = "";

            byte[] text = Encoding.UTF8.GetBytes(reason);
            var value = new byte[4 + text.Length];
            value[2] = (byte)(code / 100);
            value[3] = (byte)(code % 100);
            Buffer.BlockCopy(text, 0, value, 4, text.Length);

            return new StunAttribute(StunAttributeType.ErrorCode, value);
        }

        /// <summary>The code an ERROR-CODE carries, or 0 when this is not one.</summary>
        public int ErrorCode
        {
            get
            {
                if (Type != StunAttributeType.ErrorCode || Value.Length < 4) return 0;

                return Value[2] * 100 + Value[3];
            }
        }

        /// <summary>Writes this attribute at `at` and answers how many bytes it took.</summary>
        public int Write(byte[] destination, int at)
        {
            StunMessage.WriteUInt16(destination, at, Type);
            StunMessage.WriteUInt16(destination, at + 2, (ushort)Value.Length);
            Buffer.BlockCopy(Value, 0, destination, at + 4, Value.Length);

            return WireLength;
        }
    }
    /// <summary>
    /// An address on the wire: four or sixteen bytes and a port.
    ///
    /// Why not `IPEndPoint`: the protocol core is compiled twice, into the phone and into the
    /// desktop test host, and the WP8.1 profile this app builds against does not carry
    /// `System.Net.IPEndPoint` at all. A call's endpoints arrive from a `DatagramSocket` as a
    /// `HostName` plus a port, so the app converts once at the socket and everything under it
    /// stays in bytes - which is also the form STUN's attributes are written in.
    /// </summary>
    public sealed class StunEndpoint
    {
        public StunEndpoint(byte[] address, int port)
        {
            if (address == null || (address.Length != 4 && address.Length != 16))
                throw new ProtocolException("an endpoint is an IPv4 or IPv6 address");
            if (port < 0 || port > 65535)
                throw new ProtocolException("a port is 0..65535, not " + port);

            Address = new byte[address.Length];
            Buffer.BlockCopy(address, 0, Address, 0, address.Length);
            Port = port;
        }

        /// <summary>The four or sixteen bytes, in network order.</summary>
        public byte[] Address { get; private set; }

        public int Port { get; private set; }

        public bool IsIPv6
        {
            get { return Address.Length == 16; }
        }

        /// <summary>
        /// The address as text: dotted quad for IPv4, and eight groups of hexadecimal for
        /// IPv6 without the zero compression. Uncompressed on purpose - a `HostName` takes
        /// either form, and a formatter that compresses has to be read twice to be trusted.
        /// </summary>
        public string Text
        {
            get
            {
                if (!IsIPv6)
                {
                    return Address[0] + "." + Address[1] + "." + Address[2] + "." + Address[3];
                }

                var text = new System.Text.StringBuilder();
                for (int i = 0; i < 8; i++)
                {
                    if (i > 0) text.Append(':');
                    int group = (Address[i * 2] << 8) | Address[i * 2 + 1];
                    text.Append(group.ToString("x"));
                }

                return text.ToString();
            }
        }

        /// <summary>
        /// Reads a dotted quad or an IPv6 address. Deliberately literal: no DNS, no zone
        /// index and no IPv4-mapped IPv6, because a STUN attribute carries bytes and this is
        /// the other way in - from a `HostName` the app already has.
        /// </summary>
        public static StunEndpoint Parse(string text, int port)
        {
            if (text == null) throw new ProtocolException("an endpoint needs an address");
            text = text.Trim();

            return new StunEndpoint(text.IndexOf(':') < 0 ? ParseIPv4(text) : ParseIPv6(text), port);
        }

        private static byte[] ParseIPv4(string text)
        {
            string[] parts = text.Split('.');
            if (parts.Length != 4) throw new ProtocolException("'" + text + "' is not an IPv4 address");

            var address = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                string part = parts[i];
                if (part.Length == 0 || part.Length > 3)
                    throw new ProtocolException("'" + text + "' is not an IPv4 address");

                for (int c = 0; c < part.Length; c++)
                {
                    if (part[c] < '0' || part[c] > '9')
                        throw new ProtocolException("'" + text + "' is not an IPv4 address");
                }

                int value = int.Parse(part);
                if (value > 255) throw new ProtocolException("'" + text + "' is not an IPv4 address");
                address[i] = (byte)value;
            }

            return address;
        }

        private static byte[] ParseIPv6(string text)
        {
            int gap = text.IndexOf("::", StringComparison.Ordinal);
            if (gap >= 0 && text.IndexOf("::", gap + 1, StringComparison.Ordinal) >= 0)
                throw new ProtocolException("'" + text + "' has more than one '::'");

            string head = gap < 0 ? text : text.Substring(0, gap);
            string tail = gap < 0 ? "" : text.Substring(gap + 2);

            ushort[] headGroups = Groups(head);
            ushort[] tailGroups = Groups(tail);

            int total = headGroups.Length + tailGroups.Length;
            if (gap < 0)
            {
                if (total != 8) throw new ProtocolException("'" + text + "' is not an IPv6 address");
            }
            else if (total > 7)
            {
                throw new ProtocolException("'" + text + "' is not an IPv6 address");
            }

            var address = new byte[16];
            int at = 0;

            for (int i = 0; i < headGroups.Length; i++, at += 2)
            {
                address[at] = (byte)(headGroups[i] >> 8);
                address[at + 1] = (byte)headGroups[i];
            }

            at = 16 - tailGroups.Length * 2;
            for (int i = 0; i < tailGroups.Length; i++, at += 2)
            {
                address[at] = (byte)(tailGroups[i] >> 8);
                address[at + 1] = (byte)tailGroups[i];
            }

            return address;
        }

        private static ushort[] Groups(string part)
        {
            if (part.Length == 0) return new ushort[0];

            string[] pieces = part.Split(':');
            var groups = new ushort[pieces.Length];

            for (int i = 0; i < pieces.Length; i++)
            {
                string piece = pieces[i];
                if (piece.Length == 0 || piece.Length > 4)
                    throw new ProtocolException("'" + part + "' is not an IPv6 group list");

                for (int c = 0; c < piece.Length; c++)
                {
                    char hex = piece[c];
                    bool digit = hex >= '0' && hex <= '9';
                    bool lower = hex >= 'a' && hex <= 'f';
                    bool upper = hex >= 'A' && hex <= 'F';
                    if (!digit && !lower && !upper)
                        throw new ProtocolException("'" + part + "' is not an IPv6 group list");
                }

                groups[i] = ushort.Parse(piece, System.Globalization.NumberStyles.HexNumber);
            }

            return groups;
        }

        public override bool Equals(object other)
        {
            var endpoint = other as StunEndpoint;
            if (endpoint == null) return false;
            if (endpoint.Port != Port || endpoint.Address.Length != Address.Length) return false;

            for (int i = 0; i < Address.Length; i++)
            {
                if (endpoint.Address[i] != Address[i]) return false;
            }

            return true;
        }

        public override int GetHashCode()
        {
            int hash = Port;
            for (int i = 0; i < Address.Length; i++) hash = (hash * 31) + Address[i];
            return hash;
        }

        public override string ToString()
        {
            return Text + ":" + Port;
        }
    }

}
