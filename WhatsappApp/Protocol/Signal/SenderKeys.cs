using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Signal
{
    /// <summary>
    /// The two places a sender key meets a `Message`: field 2, where the distribution
    /// message travels, and the `enc` with `type="skmsg"` whose bytes are the group
    /// ciphertext.
    ///
    /// The shapes are the reference's (`@whiskeysockets/baileys`, `Socket/messages-send.js`,
    /// lines 559 to 585): the distribution is sent as a message of its own, whose only field
    /// is `senderKeyDistributionMessage` - a `groupId` and the sender key bytes - and the
    /// group traffic is one `enc` per stanza, not one per recipient, because the sender key
    /// is what makes one ciphertext readable by everybody.
    ///
    /// The `groupId` field inside the distribution message is redundant with the JID the
    /// stanza goes to, and it is written anyway: it is what the recipient checks the
    /// distribution against, and a group id that disagreed with the chat would be a
    /// distribution out of another conversation.
    /// </summary>
    public static class SenderKeys
    {
        /// <summary>`senderKeyDistributionMessage`, field 2 of `Message`.</summary>
        public const int DistributionField = 2;

        /// <summary>The `enc` type of group traffic.</summary>
        public const string CipherType = "skmsg";

        /// <summary>
        /// The distribution message as a `Message`: `groupId` field 1 and the sender key
        /// bytes field 2, kept in field 2 of the message that carries them.
        /// </summary>
        public static byte[] DistributionMessage(string groupId, SenderKeyRecord record)
        {
            if (string.IsNullOrEmpty(groupId)) throw new ProtocolException("a sender key belongs to a group");
            if (record == null) throw new ProtocolException("there is no sender key to hand out for " + groupId);

            byte[] distribution = record.Distribution(groupId);

            byte[] inner = new ProtoWriter()
                .Text(1, groupId)
                .Bytes(2, distribution)
                .ToArray();

            return new ProtoWriter().Bytes(DistributionField, inner).ToArray();
        }

        /// <summary>The group ciphertext for one message, as the `skmsg` `enc` carries it.</summary>
        public static byte[] Ciphertext(string groupId, SenderKeyRecord record, byte[] plaintext)
        {
            if (record == null) throw new ProtocolException("there is no sender key for " + groupId);

            return record.Encrypt(groupId, plaintext);
        }

        /// <summary>
        /// The message inside an `skmsg`, and the one place a WhatsApp group message is not
        /// quite a protobuf.
        ///
        /// Every Signal client pads the plaintext before encrypting it - whatsmeow appends
        /// between 1 and 15 bytes whose value is their own count (`message.go`,
        /// `padMessage`) - and the receiver strips that padding. Our sender does not add it
        /// (`SenderKeyRecord.Encrypt` relies on the cipher's own block padding, which is what
        /// the same receiver strips), but a phone's message arrives with it, and a protobuf
        /// reader that stopped at the trailing bytes would refuse the whole message.
        ///
        /// So: decode, and if that fails, drop one byte off the end and try again, up to the
        /// longest pad there can be. A message that is genuinely damaged fails all sixteen
        /// and keeps its failure.
        /// </summary>
        public static Message DecodePadded(byte[] plaintext)
        {
            if (plaintext == null) throw new ProtocolException("a group message carries no bytes");

            ProtocolException failed = null;

            for (int trimmed = 0; trimmed <= 15 && trimmed < plaintext.Length; trimmed++)
            {
                byte[] candidate = trimmed == 0 ? plaintext : Head(plaintext, plaintext.Length - trimmed);

                try
                {
                    return Message.Decode(candidate);
                }
                catch (ProtocolException error)
                {
                    if (failed == null) failed = error;
                }
            }

            throw new ProtocolException("the group message does not read as a protobuf: "
                + (failed == null ? "it is empty" : failed.Message));
        }

        /// <summary>
        /// The group id inside a distribution `Message`, or null when the message carries
        /// none. A distribution for another group is refused by
        /// <see cref="SenderKeyRecord.StartAsReceiver"/> against this.
        /// </summary>
        public static string GroupIdOf(byte[] distributionMessage)
        {
            if (distributionMessage == null) return null;

            return GroupIdOfField(Message.Decode(distributionMessage).SenderKeyDistributionMessage);
        }

        /// <summary>The sender key bytes inside a distribution `Message`.</summary>
        public static byte[] DistributionOf(byte[] distributionMessage)
        {
            if (distributionMessage == null) return null;

            return DistributionOfField(Message.Decode(distributionMessage).SenderKeyDistributionMessage);
        }

        /// <summary>
        /// The same two readers for the field itself, without the message around it - which
        /// is what a reader that has already decoded the message holds.
        /// </summary>
        public static string GroupIdOfField(byte[] field)
        {
            if (field == null) return null;

            var reader = new ProtoReader(field);

            while (reader.HasMore)
            {
                ProtoField one = reader.ReadField();

                if (one.Number == 1) return Utf8(reader.ReadBytes());

                reader.SkipField(one.WireType);
            }

            return null;
        }

        /// <summary>The `axolotlSenderKeyDistributionMessage` inside the field.</summary>
        public static byte[] DistributionOfField(byte[] field)
        {
            if (field == null) return null;

            var reader = new ProtoReader(field);

            while (reader.HasMore)
            {
                ProtoField one = reader.ReadField();

                if (one.Number == 2) return reader.ReadBytes();

                reader.SkipField(one.WireType);
            }

            return null;
        }

        private static string Utf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        private static byte[] Head(byte[] source, int count)
        {
            var head = new byte[count];
            System.Buffer.BlockCopy(source, 0, head, 0, count);

            return head;
        }
    }
}
