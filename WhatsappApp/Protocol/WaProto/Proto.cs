using System;
using System.Collections.Generic;

namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// A minimal protobuf writer: the two wire types WhatsApp's handshake uses.
    ///
    /// The project has no protobuf library and cannot take one - the phone ships what
    /// this repository contains, and a generated code base for a thousand messages to
    /// read three of them is the wrong trade. What is here is varint and
    /// length-delimited only, which is all `HandshakeMessage`, `CertChain` and the
    /// login payload need; a field of any other shape is not silently skipped, it is
    /// refused.
    ///
    /// The field numbers go up to protobuf's own limit, not to 15: `ClientPayload`
    /// carries `pull` at 33, and 15 was a limit that only held until the first
    /// message past the handshake was written.
    /// </summary>
    public sealed class ProtoWriter
    {
        private readonly List<byte> _bytes = new List<byte>(64);

        /// <summary>Field 1..15, wire type 2: a length-delimited byte run.</summary>
        public ProtoWriter Bytes(int field, byte[] value)
        {
            if (value == null) return this;

            WriteTag(field, 2);
            WriteVarint((ulong)value.Length);
            _bytes.AddRange(value);
            return this;
        }

        /// <summary>Field 1..15, wire type 0: a varint.</summary>
        public ProtoWriter Varint(int field, ulong value)
        {
            WriteTag(field, 0);
            WriteVarint(value);
            return this;
        }

        /// <summary>The UTF-8 bytes of a string in a length-delimited field.</summary>
        public ProtoWriter Text(int field, string value)
        {
            if (value == null) return this;

            return Bytes(field, System.Text.Encoding.UTF8.GetBytes(value));
        }

        public byte[] ToArray()
        {
            return _bytes.ToArray();
        }

        private void WriteTag(int field, int wireType)
        {
            // 2^29 - 1 is protobuf's own ceiling: the tag is a varint whose low three
            // bits are the wire type.
            if (field < 1 || field > 536870911)
            {
                throw new ProtocolException("not a protobuf field number: " + field);
            }

            WriteVarint((ulong)((field << 3) | wireType));
        }

        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.Add((byte)((value & 0x7f) | 0x80));
                value >>= 7;
            }

            _bytes.Add((byte)value);
        }
    }

    /// <summary>
    /// A minimal protobuf reader, the counterpart of <see cref="ProtoWriter"/>.
    ///
    /// It walks whatever it is given field by field, because a peer is free to add
    /// fields and the unknown ones have to be skipped rather than treated as the end.
    /// A length that runs past the buffer, or a wire type it does not know, is a
    /// <see cref="ProtocolException"/>: a handshake parsed halfway is a handshake that
    /// fails later with a message nobody can act on.
    /// </summary>
    /// <summary>
    /// One field's tag: which field it is and how its bytes are laid out.
    ///
    /// A struct rather than a number and an `out` parameter because the C# 5 guard reads
    /// any declaration of the form `out <type> <name>` as C# 6's inline declaration and
    /// fails the build - a false positive, but the guard is what the WP8.1 toolchain can
    /// actually compile, and returning both values is clearer here anyway.
    /// </summary>
    public struct ProtoField
    {
        /// <summary>The field number, 1 and up.</summary>
        public int Number;

        /// <summary>0 for a varint, 1 for 64 bits, 2 for length-delimited, 5 for 32 bits.</summary>
        public int WireType;
    }

    public sealed class ProtoReader
    {
        private readonly byte[] _data;
        private int _index;

        public ProtoReader(byte[] data)
        {
            if (data == null) throw new ProtocolException("no protobuf bytes to read");

            _data = data;
        }

        public bool HasMore
        {
            get { return _index < _data.Length; }
        }

        /// <summary>The next field's number and wire type.</summary>
        public ProtoField ReadField()
        {
            ulong tag = ReadVarint();
            ulong number = tag >> 3;
            int wireType = (int)(tag & 7);

            // Refused instead of cast: a tag whose field number does not fit 29 bits is not
            // protobuf, and `(int)` truncated it to a small number that can coincide with a
            // real field - so a corrupt frame would be read as a valid one with a meaning
            // nobody sent. Wire types above 5 do not exist either.
            if (number == 0 || number > 536870911UL || wireType > 5)
            {
                throw new ProtocolException("a protobuf field is not one: number " + number
                    + ", wire type " + wireType);
            }

            var field = new ProtoField();
            field.Number = (int)number;
            field.WireType = wireType;

            return field;
        }

        public byte[] ReadBytes()
        {
            ulong length = ReadVarint();
            if (length > (ulong)(_data.Length - _index))
            {
                throw new ProtocolException("a protobuf field announces " + length
                    + " bytes with " + (_data.Length - _index) + " left");
            }

            var value = new byte[length];
            Buffer.BlockCopy(_data, _index, value, 0, (int)length);
            _index += (int)length;
            return value;
        }

        public ulong ReadVarint()
        {
            ulong value = 0;
            int shift = 0;

            while (true)
            {
                if (_index >= _data.Length) throw new ProtocolException("a varint runs past the buffer");

                byte current = _data[_index];
                _index++;

                value |= (ulong)(current & 0x7f) << shift;
                if ((current & 0x80) == 0) return value;

                shift += 7;
                if (shift > 63) throw new ProtocolException("a varint is longer than 64 bits");
            }
        }

        /// <summary>Steps over a field this reader does not know.</summary>
        public void SkipField(int wireType)
        {
            if (wireType == 0)
            {
                ReadVarint();
                return;
            }

            if (wireType == 2)
            {
                ReadBytes();
                return;
            }

            if (wireType == 1)
            {
                Advance(8);
                return;
            }

            if (wireType == 5)
            {
                Advance(4);
                return;
            }

            throw new ProtocolException("unsupported protobuf wire type " + wireType);
        }

        private void Advance(int count)
        {
            if (_index + count > _data.Length) throw new ProtocolException("a field runs past the buffer");
            _index += count;
        }
    }
}
