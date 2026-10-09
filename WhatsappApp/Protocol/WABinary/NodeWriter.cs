using System;
using System.Collections.Generic;
using System.Text;

namespace WhatsappApp.Protocol.WABinary
{
    /// <summary>
    /// Writes a <see cref="BinaryNode"/> as the WhatsApp binary format.
    ///
    /// This is a transcription of the reference's `encodeBinaryNodeInner`, not a
    /// design of its own: every branch (the token lookup before the packing, the
    /// nibble form before the hex one, a JID before raw text, `LIST_EMPTY` for a
    /// missing string) decides the bytes WhatsApp reads, and a "simplification" here
    /// is read as a different stanza on the other side. Where it differs, the reason
    /// is in a comment.
    /// </summary>
    public static class NodeWriter
    {
        /// <summary>
        /// Encodes a node as a frame: the `0x00` "not compressed" prefix first, then
        /// the node. This is what is handed to the WebSocket.
        /// </summary>
        public static byte[] Encode(BinaryNode node)
        {
            var buffer = new List<byte>(256);
            buffer.Add(0); // not compressed; the reference writes this same byte
            WriteNode(node, buffer);
            return buffer.ToArray();
        }

        /// <summary>The node alone, with no frame prefix.</summary>
        public static byte[] EncodePayload(BinaryNode node)
        {
            var buffer = new List<byte>(256);
            WriteNode(node, buffer);
            return buffer.ToArray();
        }

        private static void WriteNode(BinaryNode node, List<byte> buffer)
        {
            if (node == null) throw new ProtocolException("a null node cannot be encoded");
            if (string.IsNullOrEmpty(node.Tag)) throw new ProtocolException("a node needs a tag");

            // A null value is not an attribute: the reference drops those from the
            // count and from the stream, and a mismatch between the two is a frame
            // whose list length lies about its own content.
            var attributes = new List<KeyValuePair<string, string>>(node.Attrs.Count);
            foreach (KeyValuePair<string, string> attribute in node.Attrs)
            {
                if (attribute.Value != null) attributes.Add(attribute);
            }

            bool hasContent = node.Content != null;
            WriteListStart(2 * attributes.Count + 1 + (hasContent ? 1 : 0), buffer);

            WriteString(node.Tag, buffer);

            for (int i = 0; i < attributes.Count; i++)
            {
                WriteString(attributes[i].Key, buffer);
                WriteString(attributes[i].Value, buffer);
            }

            if (!hasContent) return;

            string text = node.Content as string;
            if (text != null)
            {
                WriteString(text, buffer);
                return;
            }

            byte[] bytes = node.Content as byte[];
            if (bytes != null)
            {
                WriteByteLength(bytes.Length, buffer);
                buffer.AddRange(bytes);
                return;
            }

            IList<BinaryNode> children = node.Children();
            if (children != null)
            {
                var valid = new List<BinaryNode>(children.Count);
                for (int i = 0; i < children.Count; i++)
                {
                    BinaryNode child = children[i];
                    if (child != null && !string.IsNullOrEmpty(child.Tag)) valid.Add(child);
                }

                WriteListStart(valid.Count, buffer);
                for (int i = 0; i < valid.Count; i++) WriteNode(valid[i], buffer);
                return;
            }

            throw new ProtocolException("a node's content must be text, bytes or children, not "
                + node.Content.GetType().FullName);
        }

        /// <summary>
        /// Writes a string, choosing the shortest form that round-trips: a token from
        /// the tables, then a packed nibble/hex run, then a JID pair, then the bytes.
        /// </summary>
        private static void WriteString(string value, List<byte> buffer)
        {
            if (value == null)
            {
                PushByte(Tags.ListEmpty, buffer);
                return;
            }

            if (value.Length == 0)
            {
                WriteStringRaw(value, buffer);
                return;
            }

            Tokens.TokenRef token;
            if (Tokens.TryGetToken(value, out token))
            {
                if (token.Dictionary >= 0) PushByte(Tags.Dictionary0 + token.Dictionary, buffer);
                PushByte(token.Index, buffer);
                return;
            }

            // Digits and -/. go as nibbles, and only uppercase hex goes as hex: a
            // lowercase "ab" is NOT hex-packed, it is written raw. That asymmetry is
            // the reference's, and the reader accepts both.
            if (IsNibble(value))
            {
                WritePackedBytes(value, true, buffer);
                return;
            }

            if (IsHex(value))
            {
                WritePackedBytes(value, false, buffer);
                return;
            }

            DecodedJid jid = JidUtils.Decode(value);
            if (jid.IsJid)
            {
                WriteJid(jid, buffer);
                return;
            }

            WriteStringRaw(value, buffer);
        }

        private static void WriteJid(DecodedJid jid, List<byte> buffer)
        {
            // A device makes it an "AD JID": domain, device, then the user. Without
            // one it is a pair of strings, and an empty user is `LIST_EMPTY` rather
            // than an empty string - two different bytes.
            if (jid.HasDevice)
            {
                PushByte(Tags.AdJid, buffer);
                PushByte(jid.DomainType, buffer);
                PushByte(jid.Device, buffer);
                WriteString(jid.User, buffer);
                return;
            }

            PushByte(Tags.JidPair, buffer);
            if (!string.IsNullOrEmpty(jid.User)) WriteString(jid.User, buffer);
            else PushByte(Tags.ListEmpty, buffer);
            WriteString(jid.Server, buffer);
        }

        private static void WritePackedBytes(string value, bool nibble, List<byte> buffer)
        {
            if (value.Length > Tags.PackedMax)
            {
                throw new ProtocolException("too many bytes to pack: " + value.Length);
            }

            PushByte(nibble ? Tags.Nibble8 : Tags.Hex8, buffer);

            // The high bit of the length says the count is odd, and the last nibble
            // is then padding the reader drops.
            int roundedLength = (value.Length + 1) / 2;
            if (value.Length % 2 != 0) roundedLength |= 128;
            PushByte(roundedLength, buffer);

            int pairs = value.Length / 2;
            for (int i = 0; i < pairs; i++)
            {
                PushByte((Pack(nibble, value[2 * i]) << 4) | Pack(nibble, value[2 * i + 1]), buffer);
            }

            if (value.Length % 2 != 0)
            {
                PushByte((Pack(nibble, value[value.Length - 1]) << 4) | Pack(nibble, '\0'), buffer);
            }
        }

        private static void WriteStringRaw(string value, List<byte> buffer)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteByteLength(bytes.Length, buffer);
            buffer.AddRange(bytes);
        }

        private static void WriteByteLength(int length, List<byte> buffer)
        {
            if (length >= 1 << 20)
            {
                PushByte(Tags.Binary32, buffer);
                PushInt(length, 4, buffer);
            }
            else if (length >= 256)
            {
                PushByte(Tags.Binary20, buffer);
                PushInt20(length, buffer);
            }
            else
            {
                PushByte(Tags.Binary8, buffer);
                PushByte(length, buffer);
            }
        }

        private static void WriteListStart(int size, List<byte> buffer)
        {
            if (size == 0)
            {
                PushByte(Tags.ListEmpty, buffer);
            }
            else if (size < 256)
            {
                PushByte(Tags.List8, buffer);
                PushByte(size, buffer);
            }
            else
            {
                PushByte(Tags.List16, buffer);
                PushInt(size, 2, buffer);
            }
        }

        private static void PushByte(int value, List<byte> buffer)
        {
            buffer.Add((byte)(value & 0xff));
        }

        /// <summary>Big-endian, as the reference writes it (its `pushInt` default).</summary>
        private static void PushInt(int value, int count, List<byte> buffer)
        {
            for (int i = 0; i < count; i++)
            {
                int shift = count - 1 - i;
                buffer.Add((byte)((value >> (shift * 8)) & 0xff));
            }
        }

        /// <summary>Three bytes: the top nibble, then two full bytes.</summary>
        private static void PushInt20(int value, List<byte> buffer)
        {
            buffer.Add((byte)((value >> 16) & 0x0f));
            buffer.Add((byte)((value >> 8) & 0xff));
            buffer.Add((byte)(value & 0xff));
        }

        private static int Pack(bool nibble, char character)
        {
            if (character >= '0' && character <= '9') return character - '0';

            if (nibble)
            {
                if (character == '-') return 10;
                if (character == '.') return 11;
                if (character == '\0') return 15;

                throw new ProtocolException("invalid character for nibble: '" + character + "'");
            }

            if (character >= 'A' && character <= 'F') return 10 + character - 'A';
            if (character == '\0') return 15;

            throw new ProtocolException("invalid character for hex: '" + character + "'");
        }

        private static bool IsNibble(string value)
        {
            if (value.Length > Tags.PackedMax) return false;

            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                bool digit = character >= '0' && character <= '9';
                if (!digit && character != '-' && character != '.') return false;
            }

            return true;
        }

        private static bool IsHex(string value)
        {
            if (value.Length > Tags.PackedMax) return false;

            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                bool digit = character >= '0' && character <= '9';
                bool upperHex = character >= 'A' && character <= 'F';
                if (!digit && !upperHex) return false;
            }

            return true;
        }
    }
}
