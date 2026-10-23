using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>The JID helpers the readers and the fan-outs share.</summary>
    public static class JidUtilsTests
    {
        /// <summary>
        /// The addresses that are not conversations stay out of the chat list, and the ones
        /// that are stay in it.
        ///
        /// Why a test: a chat row is created from whatever a stanza names, and the phone of
        /// 2026-10-22 carried a row called "0" - `0@s.whatsapp.net`, the address an offline
        /// marker comes from - beside the conversations a person keeps. Statuses and channels
        /// were already excluded at the row; the placeholder and the empty address were not.
        /// </summary>
        [Test]
        public static void Only_a_conversation_becomes_a_chat_row()
        {
            Assert.IsTrue(JidUtils.IsConversation("393491234567@s.whatsapp.net"), "a person is a chat");
            Assert.IsTrue(JidUtils.IsConversation("150375613116620@lid"), "a LID is a person too");
            Assert.IsTrue(JidUtils.IsConversation("120363412599616890@g.us"), "a group is a chat");
            Assert.IsTrue(JidUtils.IsConversation("867051314767696@bot"), "the bot has a chat");

            Assert.IsFalse(JidUtils.IsConversation("0@s.whatsapp.net"), "the placeholder address is nobody");
            Assert.IsFalse(JidUtils.IsConversation("000@c.us"), "all zeros are still nobody");
            Assert.IsFalse(JidUtils.IsConversation("status@broadcast"), "statuses have their own feed");
            Assert.IsFalse(JidUtils.IsConversation("120363412599616890@newsletter"), "a channel is not a chat");
            Assert.IsFalse(JidUtils.IsConversation(""), "an empty address is not a chat");
            Assert.IsFalse(JidUtils.IsConversation(null), "no address at all is not a chat");
        }

        /// <summary>
        /// A LID is not a phone number, and its user part is a long run of digits: anything
        /// that tests "is this all digits and long enough" before it asks what the address is
        /// prints `+150375613116620`, a number nobody has. That is exactly what
        /// `DisplayNameForJid` did, because its `@lid` guard sat below the digits branch.
        /// The rule lives here so the three places that print a JID ask the same question.
        /// </summary>
        [Test]
        public static void ALidIsNotAPhoneNumber()
        {
            Assert.IsTrue(JidUtils.IsLid("150375613116620@lid"), "a LID is a LID");
            Assert.IsTrue(JidUtils.IsLid("150375613116620:5@lid"), "the device does not change it");
            Assert.IsTrue(JidUtils.IsLid("150375613116620@hosted.lid"), "a hosted LID too");

            Assert.IsFalse(JidUtils.IsLid("393491234567@s.whatsapp.net"), "a phone number is not a LID");
            Assert.IsFalse(JidUtils.IsLid("393491234567:12@s.whatsapp.net"), "nor with its device");
            Assert.IsFalse(JidUtils.IsLid("120363412599616890@g.us"), "a group is not a LID");
            Assert.IsFalse(JidUtils.IsLid("150375613116620@hosted"), "a hosted phone number is a number");
            Assert.IsFalse(JidUtils.IsLid("not a jid"), "not a JID, not a LID");
            Assert.IsFalse(JidUtils.IsLid(null), "no address at all, not a LID");
        }

        /// <summary>
        /// Device 99 is a placeholder the server puts in a device list, and the device sits
        /// inside the JID: a check on the front of the string tested the phone number and
        /// left the placeholder in every fan-out.
        /// </summary>
        [Test]
        public static void ThePlaceholderDeviceIsRecognisedInsideTheJid()
        {
            Assert.IsTrue(JidUtils.IsPlaceholderDevice("15551230000:99@s.whatsapp.net"));
            Assert.IsFalse(JidUtils.IsPlaceholderDevice("15551230000:5@s.whatsapp.net"));
            Assert.IsFalse(JidUtils.IsPlaceholderDevice("15551230000@s.whatsapp.net"));
            Assert.IsFalse(JidUtils.IsPlaceholderDevice(null));
        }

        /// <summary>A device of zero is not written out, so both sides name the same JID.</summary>
        [Test]
        public static void ADeviceOfZeroIsLeftOutOfTheJid()
        {
            Assert.AreEqual("15551230000@s.whatsapp.net",
                JidUtils.Encode("15551230000", "s.whatsapp.net", 0));
            Assert.AreEqual("15551230000:5@s.whatsapp.net",
                JidUtils.Encode("15551230000", "s.whatsapp.net", 5));
        }
    }

    /// <summary>
    /// The binary codec: the tables, the round trips and the edges that a hand-rolled
    /// implementation gets wrong (the 20-bit length, the packed forms, the device JID).
    ///
    /// The reference values are Baileys' `WABinary/constants.ts`: the token tables are
    /// pinned at a few known indices, and the length of every table is pinned, because a
    /// transcription that dropped or merged one entry shifts every later index and is
    /// invisible in a round trip (the encoder and the decoder shift together).
    /// </summary>
    public static class WABinaryTests
    {
        [Test]
        public static void The_token_tables_match_the_reference()
        {
            // 236, not 256: tags 236..239 are the dictionary selectors.
            Assert.AreEqual(236, Tokens.SingleByteTokens.Length);
            Assert.AreEqual("", Tokens.SingleByteTokens[0]);
            Assert.AreEqual("s.whatsapp.net", Tokens.SingleByteTokens[3]);
            Assert.AreEqual("type", Tokens.SingleByteTokens[4]);
            Assert.AreEqual("message", Tokens.SingleByteTokens[19]);
            Assert.AreEqual("iq", Tokens.SingleByteTokens[25]);
            Assert.AreEqual("screen_height", Tokens.SingleByteTokens[235]);

            Assert.AreEqual(4, Tokens.DoubleByteTokens.Length);
            Assert.AreEqual(256, Tokens.DoubleByteTokens[0].Length);
            Assert.AreEqual(256, Tokens.DoubleByteTokens[1].Length);
            Assert.AreEqual(256, Tokens.DoubleByteTokens[2].Length);
            Assert.AreEqual(256, Tokens.DoubleByteTokens[3].Length);

            Assert.AreEqual("read-self", Tokens.DoubleByteTokens[0][0]);
            Assert.AreEqual("mute_v2", Tokens.DoubleByteTokens[0][255]);
            Assert.AreEqual("reject", Tokens.DoubleByteTokens[1][0]);
            Assert.AreEqual("u_aud_limit_sil_restarts_test1", Tokens.DoubleByteTokens[1][255]);
            Assert.AreEqual("64", Tokens.DoubleByteTokens[2][0]);
            Assert.AreEqual("1537", Tokens.DoubleByteTokens[2][255]);
            Assert.AreEqual("1724", Tokens.DoubleByteTokens[3][0]);
            Assert.AreEqual("1961", Tokens.DoubleByteTokens[3][255]);
        }

        [Test]
        public static void A_frame_starts_with_the_not_compressed_prefix()
        {
            byte[] frame = NodeWriter.Encode(new BinaryNode("iq"));
            Assert.AreEqual(0, (int)frame[0]);

            BinaryNode back = NodeReader.Decode(frame);
            Assert.AreEqual("iq", back.Tag);
        }

        [Test]
        public static void A_node_round_trips()
        {
            var node = new BinaryNode("iq");
            node.Attrs.Add("type", "set");
            node.Attrs.Add("to", "s.whatsapp.net");
            node.Content = "ping";

            BinaryNode back = NodeReader.Decode(NodeWriter.Encode(node));

            Assert.AreEqual("iq", back.Tag);
            Assert.AreEqual("set", back.Attrs["type"]);
            Assert.AreEqual("s.whatsapp.net", back.Attrs["to"]);
            Assert.AreEqual("ping", back.Content);
        }

        [Test]
        public static void The_attribute_order_is_kept()
        {
            var node = new BinaryNode("iq");
            node.Attrs.Add("zzz", "1");
            node.Attrs.Add("aaa", "2");
            node.Attrs.Add("mmm", "3");

            BinaryNode back = NodeReader.Decode(NodeWriter.Encode(node));

            var order = new List<string>();
            foreach (KeyValuePair<string, string> attribute in back.Attrs) order.Add(attribute.Key);

            Assert.AreEqual("zzz,aaa,mmm", string.Join(",", order.ToArray()));
        }

        [Test]
        public static void A_children_list_round_trips()
        {
            var parent = new BinaryNode("ib");
            var child = new BinaryNode("item");
            child.Attrs.Add("v", "1");
            parent.Content = new List<BinaryNode> { child };

            BinaryNode back = NodeReader.Decode(NodeWriter.Encode(parent));
            IList<BinaryNode> children = back.Children();

            Assert.IsTrue(children != null);
            Assert.AreEqual(1, children.Count);
            Assert.AreEqual("item", children[0].Tag);
            Assert.AreEqual("1", children[0].Attrs["v"]);
            Assert.AreEqual("item", back.Child("item").Tag);
        }

        [Test]
        public static void A_binary_payload_round_trips_unchanged()
        {
            byte[] payload = new byte[] { 0x00, 0xff, 0x10, 0x00, 0x7f };
            var node = new BinaryNode("message");
            node.Content = payload;

            BinaryNode back = NodeReader.Decode(NodeWriter.Encode(node));
            Assert.ByteArraysEqual(payload, back.Bytes());
        }

        [Test]
        public static void A_jid_is_packed_as_a_pair_and_read_back()
        {
            var node = new BinaryNode("to");
            node.Attrs.Add("jid", "393401234567@s.whatsapp.net");

            byte[] bytes = NodeWriter.Encode(node);
            BinaryNode back = NodeReader.Decode(bytes);

            Assert.AreEqual("393401234567@s.whatsapp.net", back.Attrs["jid"]);

            // The user is digits only, so it goes as nibbles, not as raw text, and the
            // server is the token for s.whatsapp.net.
            Assert.IsTrue(Contains(bytes, (byte)Tags.Nibble8));
            Assert.IsTrue(Contains(bytes, (byte)Tags.JidPair));
        }

        [Test]
        public static void A_device_jid_keeps_its_device()
        {
            var node = new BinaryNode("to");
            node.Attrs.Add("jid", "393401234567:12@s.whatsapp.net");

            byte[] bytes = NodeWriter.Encode(node);
            BinaryNode back = NodeReader.Decode(bytes);

            Assert.AreEqual("393401234567:12@s.whatsapp.net", back.Attrs["jid"]);
            Assert.IsTrue(Contains(bytes, (byte)Tags.AdJid));
        }

        /// <summary>
        /// The chat's JID, which is the address without the device.
        ///
        /// Both references normalize before they decide which conversation a message
        /// belongs to (`whatsmeow`, `message.go`, `source.Chat = from.ToNonAD()`; Baileys'
        /// `jidNormalizedUser`), and without it a contact writing from a linked device gets a
        /// second chat row named after that device.
        /// </summary>
        [Test]
        public static void A_chat_jid_drops_the_device()
        {
            Assert.AreEqual("393401234567@s.whatsapp.net",
                JidUtils.Normalize("393401234567:12@s.whatsapp.net"));
            Assert.AreEqual("393401234567@s.whatsapp.net",
                JidUtils.Normalize("393401234567@s.whatsapp.net"),
                "a JID with no device is already a chat");
            Assert.AreEqual("393401234567@s.whatsapp.net",
                JidUtils.Normalize("393401234567:0@s.whatsapp.net"),
                "and device zero is no device");
            Assert.AreEqual("120363000000000000@g.us",
                JidUtils.Normalize("120363000000000000@g.us"),
                "a group has no device to drop");
            Assert.AreEqual("status@broadcast",
                JidUtils.Normalize("status:5@broadcast"),
                "and neither has a status, whose author is the device-qualified part");
            Assert.AreEqual("393401234567@s.whatsapp.net",
                JidUtils.Normalize("393401234567@c.us"),
                "`c.us` is the old spelling of the same server");
            Assert.AreEqual("", JidUtils.Normalize(""));
            Assert.AreEqual("not a jid", JidUtils.Normalize("not a jid"),
                "a string that is not a JID is left alone rather than guessed at");
        }

        [Test]
        public static void A_long_payload_uses_the_20_bit_length()
        {
            var payload = new byte[300];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xff);

            var node = new BinaryNode("body");
            node.Content = payload;

            byte[] bytes = NodeWriter.Encode(node);

            Assert.IsTrue(Contains(bytes, (byte)Tags.Binary20));
            Assert.ByteArraysEqual(payload, NodeReader.Decode(bytes).Bytes());
        }

        [Test]
        public static void Long_text_content_comes_back_as_its_utf8_bytes()
        {
            // A real asymmetry of the format, not a bug here: a long string is written
            // with a BINARY_20 length, and the decoder reads content under a BINARY_*
            // tag as bytes whatever it was. The reference does the same, so a caller
            // that wants the text back has to decode it - this test is the record of
            // that, so nobody "fixes" it into a stream WhatsApp does not read.
            string text = new string('a', 300);
            var node = new BinaryNode("body");
            node.Content = text;

            byte[] bytes = NodeWriter.Encode(node);
            BinaryNode back = NodeReader.Decode(bytes);

            Assert.IsTrue(Contains(bytes, (byte)Tags.Binary20));
            Assert.ByteArraysEqual(System.Text.Encoding.UTF8.GetBytes(text), back.Bytes());
        }

        [Test]
        public static void A_hex_string_is_packed_and_read_back()
        {
            var node = new BinaryNode("to");
            node.Attrs.Add("hash", "ABCDEF01");

            byte[] bytes = NodeWriter.Encode(node);
            BinaryNode back = NodeReader.Decode(bytes);

            Assert.AreEqual("ABCDEF01", back.Attrs["hash"]);
            Assert.IsTrue(Contains(bytes, (byte)Tags.Hex8));
        }

        [Test]
        public static void A_truncated_buffer_is_refused_not_read()
        {
            byte[] bytes = NodeWriter.Encode(new BinaryNode("iq"));
            Assert.Throws<ProtocolException>(delegate { NodeReader.Decode(Truncate(bytes, 3)); });
        }

        /// <summary>
        /// A packed string that announces zero bytes is an empty string, not a crash.
        ///
        /// `0x80` is a HEX_8 with a count of zero and the odd-count bit set, and the
        /// reference trims the padding nibble with a slice that is a no-op on an empty
        /// string. `value.Length -= 1` on an empty StringBuilder is not: it is an
        /// ArgumentOutOfRangeException out of the codec, so a malformed frame from the
        /// server ends the read with an error nobody can act on instead of being refused
        /// as the node it is not.
        /// </summary>
        [Test]
        public static void An_empty_packed_string_is_refused_not_thrown_over()
        {
            // [prefix][LIST_8, size 1][HEX_8][0x80]: a node whose header is an empty pack,
            // which ReadNode refuses because a node without a header is not a node.
            byte[] frame = new byte[] { 0x00, (byte)Tags.List8, 1, (byte)Tags.Hex8, 0x80 };

            Assert.Throws<ProtocolException>(delegate { NodeReader.Decode(frame); });
        }

        [Test]
        public static void A_compressed_frame_is_read()
        {
            // Why this replaced "a compressed frame is refused": WhatsApp sends the big
            // nodes - the history sync and its chat list - as 0x02 frames. Refusing one
            // ended the connection, and the sync started again from the beginning every
            // time, which on the phone is a chat list that never finishes loading (the
            // diag log of 2026-10-22 reads "compressed frames are not supported yet"
            // right before "retrying in 1s").
            var node = new BinaryNode("message");
            node.Attrs.Add("to", "393491234567@s.whatsapp.net");
            node.Attrs.Add("type", "text");
            node.Attrs.Add("id", "3AF1A2B3C4D5");
            node.Content = "ciao";

            byte[] frame = NodeWriter.Encode(node);
            byte[] payload = new byte[frame.Length - 1];
            System.Buffer.BlockCopy(frame, 1, payload, 0, payload.Length);

            byte[] compressed = CompressedFrame(payload);

            Assert.AreEqual((byte)0x02, compressed[0], "the frame says it is compressed");
            Assert.AreEqual((byte)0x78, compressed[1], "and carries a zlib header");

            BinaryNode back = NodeReader.Decode(compressed);

            Assert.AreEqual("message", back.Tag);
            Assert.AreEqual("393491234567@s.whatsapp.net", back.Attrs["to"]);
            Assert.AreEqual("3AF1A2B3C4D5", back.Attrs["id"]);
            // The decoder hands content back as bytes whatever tag carried it (the same
            // asymmetry `Long_text_content_comes_back_as_its_utf8_bytes` records): what
            // this asserts is that the words survived the inflate unchanged.
            Assert.ByteArraysEqual(System.Text.Encoding.UTF8.GetBytes("ciao"), back.Bytes());
        }

        [Test]
        public static void A_compressed_frame_that_is_not_zlib_is_still_refused()
        {
            // The flag says zlib; the bytes are not. That is a broken frame, and it has to
            // end as a named exception rather than as a node decoded from garbage.
            Assert.Throws<ProtocolException>(delegate { NodeReader.Decode(new byte[] { 0x02, 0x00 }); });
        }

        /// <summary>
        /// The bytes WhatsApp sends for a compressed frame: `0x02`, then the payload as a
        /// zlib stream - a two-byte header and raw deflate, which is what `zlib.deflate`
        /// writes and what <see cref="Zlib.Inflate"/> reads.
        /// </summary>
        private static byte[] CompressedFrame(byte[] payload)
        {
            using (var output = new System.IO.MemoryStream())
            {
                output.WriteByte(0x02);
                output.WriteByte(0x78);
                output.WriteByte(0x9c);

                using (var deflate = new System.IO.Compression.DeflateStream(output,
                    System.IO.Compression.CompressionMode.Compress, true))
                {
                    deflate.Write(payload, 0, payload.Length);
                }

                return output.ToArray();
            }
        }


        private static bool Contains(byte[] bytes, byte value)
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == value) return true;
            }

            return false;
        }

        private static byte[] Truncate(byte[] source, int length)
        {
            var copy = new byte[length];
            System.Buffer.BlockCopy(source, 0, copy, 0, length);
            return copy;
        }
    }
}
