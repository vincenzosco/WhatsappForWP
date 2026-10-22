using System;
using System.Collections.Generic;
using System.Text;

namespace WhatsappApp.Protocol.WABinary
{
    /// <summary>
    /// Reads the WhatsApp binary format back into a <see cref="BinaryNode"/>.
    ///
    /// This is a transcription of the reference's `decodeDecompressedBinaryNode`.
    /// Its guards are kept as they are: a length that runs past the buffer is an
    /// exception and never a short read, because a short read desynchronizes the
    /// stream and every node after it is garbage that still parses.
    ///
    /// <see cref="Decode"/> takes a frame (with the compression prefix);
    /// <see cref="DecodePayload"/> takes the bytes after that prefix, which is what
    /// the transport uses once it has inflated a frame itself.
    /// </summary>
    public static class NodeReader
    {
        /// <summary>Reads a frame: strips the prefix, then reads the node.</summary>
        public static BinaryNode Decode(byte[] frame)
        {
            if (frame == null || frame.Length == 0)
            {
                throw new ProtocolException("an empty frame cannot be decoded");
            }

            byte[] payload = Slice(frame, 1, frame.Length - 1);

            // 0x02 says the rest is zlib, and WhatsApp uses it for the big nodes: the
            // history sync and the chat list that comes with it. Refusing it, which this
            // did, took the whole connection down on one compressed frame - and since the
            // sync restarts from the beginning when the socket is remade, the phone sat on
            // a chat list that never finished loading. The reference inflates and then
            // decodes (`decodeBinaryNode`, `inflateSync`), and so does this.
            if ((frame[0] & 2) != 0)
            {
                payload = Zlib.Inflate(payload);
            }

            return DecodePayload(payload);
        }

        /// <summary>Reads one node from bytes that already had their prefix removed.</summary>
        public static BinaryNode DecodePayload(byte[] payload)
        {
            var reader = new Cursor(payload);
            return reader.ReadNode();
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }

        /// <summary>The read position over one frame's bytes.</summary>
        private sealed class Cursor
        {
            private readonly byte[] _buffer;
            private int _index;

            public Cursor(byte[] buffer)
            {
                if (buffer == null) throw new ProtocolException("no bytes to decode");
                _buffer = buffer;
            }

            public BinaryNode ReadNode()
            {
                int listSize = ReadListSize(ReadByte());
                string header = ReadString(ReadByte());

                if (listSize == 0 || string.IsNullOrEmpty(header))
                {
                    throw new ProtocolException("invalid node at byte " + _index);
                }

                var node = new BinaryNode(header);

                int attributeCount = (listSize - 1) >> 1;
                for (int i = 0; i < attributeCount; i++)
                {
                    string key = ReadString(ReadByte());
                    string value = ReadString(ReadByte());
                    node.Attrs.Add(key, value);
                }

                if (listSize % 2 == 0)
                {
                    int tag = ReadByte();
                    if (IsListTag(tag))
                    {
                        node.Content = ReadList(tag);
                    }
                    else
                    {
                        switch (tag)
                        {
                            case Tags.Binary8:
                                node.Content = ReadBytes(ReadByte());
                                break;
                            case Tags.Binary20:
                                node.Content = ReadBytes(ReadInt20());
                                break;
                            case Tags.Binary32:
                                node.Content = ReadBytes(ReadInt(4));
                                break;
                            default:
                                node.Content = ReadString(tag);
                                break;
                        }
                    }
                }

                return node;
            }

            private List<BinaryNode> ReadList(int tag)
            {
                int size = ReadListSize(tag);
                var children = new List<BinaryNode>(size);
                for (int i = 0; i < size; i++) children.Add(ReadNode());
                return children;
            }

            private string ReadString(int tag)
            {
                // A tag below the table's length is a token. The dictionary selectors
                // are above it, which is the whole reason the table stops at 236.
                if (tag >= 1 && tag < Tokens.SingleByteTokens.Length)
                {
                    return Tokens.SingleByteTokens[tag];
                }

                switch (tag)
                {
                    case Tags.Dictionary0:
                    case Tags.Dictionary1:
                    case Tags.Dictionary2:
                    case Tags.Dictionary3:
                        return DoubleToken(tag - Tags.Dictionary0, ReadByte());
                    case Tags.ListEmpty:
                        return string.Empty;
                    case Tags.Binary8:
                        return ReadText(ReadByte());
                    case Tags.Binary20:
                        return ReadText(ReadInt20());
                    case Tags.Binary32:
                        return ReadText(ReadInt(4));
                    case Tags.JidPair:
                        return ReadJidPair();
                    case Tags.FbJid:
                        return ReadFbJid();
                    case Tags.InteropJid:
                        return ReadInteropJid();
                    case Tags.AdJid:
                        return ReadAdJid();
                    case Tags.Hex8:
                    case Tags.Nibble8:
                        return ReadPacked8(tag);
                    default:
                        throw new ProtocolException("invalid string tag: " + tag);
                }
            }

            private static string DoubleToken(int dictionary, int index)
            {
                if (dictionary < 0 || dictionary >= Tokens.DoubleByteTokens.Length)
                {
                    throw new ProtocolException("invalid dictionary: " + dictionary);
                }

                string[] entries = Tokens.DoubleByteTokens[dictionary];
                if (index < 0 || index >= entries.Length)
                {
                    throw new ProtocolException("invalid token " + index + " in dictionary " + dictionary);
                }

                return entries[index];
            }

            private string ReadJidPair()
            {
                string user = ReadString(ReadByte());
                string server = ReadString(ReadByte());

                if (string.IsNullOrEmpty(server))
                {
                    throw new ProtocolException("invalid jid pair: '" + user + "', '" + server + "'");
                }

                return (user ?? string.Empty) + "@" + server;
            }

            private string ReadAdJid()
            {
                int domainType = ReadByte();
                int device = ReadByte();
                string user = ReadString(ReadByte());

                return JidUtils.Encode(user, JidUtils.ServerForDomain(domainType), device);
            }

            private string ReadFbJid()
            {
                string user = ReadString(ReadByte());
                int device = ReadInt(2);
                string server = ReadString(ReadByte());

                return user + ":" + device.ToString() + "@" + server;
            }

            private string ReadInteropJid()
            {
                string user = ReadString(ReadByte());
                int device = ReadInt(2);
                int integrator = ReadInt(2);
                string server = "interop";

                // The server is optional in this one form: a frame that ends here is
                // legal, so the position is put back and "interop" kept.
                int beforeServer = _index;
                try
                {
                    server = ReadString(ReadByte());
                }
                catch (ProtocolException)
                {
                    _index = beforeServer;
                }

                return integrator.ToString() + "-" + user + ":" + device.ToString() + "@" + server;
            }

            private string ReadPacked8(int tag)
            {
                int startByte = ReadByte();
                var value = new StringBuilder(startByte & 127);

                for (int i = 0; i < (startByte & 127); i++)
                {
                    int current = ReadByte();
                    value.Append(Unpack(tag, (current & 0xf0) >> 4));
                    value.Append(Unpack(tag, current & 0x0f));
                }

                // The odd count's last nibble is padding. An empty pack (`0x80`) has no
                // byte to pad, and the reference's `str.slice(0, -1)` on an empty string is
                // a no-op - so this is a guard and not a trim: a length of -1 on an empty
                // StringBuilder is an ArgumentOutOfRangeException out of the codec, which is
                // neither the empty string the reference returns nor a refusal to read.
                if ((startByte >> 7) != 0 && value.Length > 0) value.Length -= 1;
                return value.ToString();
            }

            private static char Unpack(int tag, int value)
            {
                if (tag == Tags.Nibble8)
                {
                    if (value >= 0 && value <= 9) return (char)('0' + value);
                    if (value == 10) return '-';
                    if (value == 11) return '.';
                    if (value == 15) return '\0';
                    throw new ProtocolException("invalid nibble: " + value);
                }

                if (value >= 0 && value < 16)
                {
                    return value < 10 ? (char)('0' + value) : (char)('A' + value - 10);
                }

                throw new ProtocolException("invalid hex: " + value);
            }

            private static bool IsListTag(int tag)
            {
                return tag == Tags.ListEmpty || tag == Tags.List8 || tag == Tags.List16;
            }

            private int ReadListSize(int tag)
            {
                switch (tag)
                {
                    case Tags.ListEmpty:
                        return 0;
                    case Tags.List8:
                        return ReadByte();
                    case Tags.List16:
                        return ReadInt(2);
                    default:
                        throw new ProtocolException("invalid tag for a list size: " + tag);
                }
            }

            private string ReadText(int length)
            {
                return Encoding.UTF8.GetString(ReadBytes(length), 0, length);
            }

            private int ReadInt20()
            {
                CheckAvailable(3);
                return ((Next() & 15) << 16) + (Next() << 8) + Next();
            }

            /// <summary>Big-endian, matching the writer's `pushInt`.</summary>
            private int ReadInt(int count)
            {
                CheckAvailable(count);

                int value = 0;
                for (int i = 0; i < count; i++)
                {
                    int shift = count - 1 - i;
                    value |= Next() << (shift * 8);
                }

                return value;
            }

            private byte[] ReadBytes(int count)
            {
                if (count < 0) throw new ProtocolException("negative length at byte " + _index);

                CheckAvailable(count);

                var value = new byte[count];
                Buffer.BlockCopy(_buffer, _index, value, 0, count);
                _index += count;
                return value;
            }

            private int ReadByte()
            {
                CheckAvailable(1);
                return Next();
            }

            private int Next()
            {
                int value = _buffer[_index];
                _index++;
                return value;
            }

            private void CheckAvailable(int length)
            {
                if (_index + length > _buffer.Length)
                {
                    throw new ProtocolException("end of stream: " + length + " byte(s) requested at "
                        + _index + " of " + _buffer.Length);
                }
            }
        }
    }
}
