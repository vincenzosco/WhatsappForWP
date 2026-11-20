using System.Collections.Generic;

namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// `Message` - the protobuf behind every chat message, and the five fields of it this
    /// milestone reads or writes: the plain words, the sender-key distribution a group
    /// message carries, the extended text a phone sends with a quote or a link preview,
    /// the protocol message that revokes or edits, and the device-sent wrapper one of
    /// our own copies travels in.
    ///
    /// Only those fields are named; every other one is skipped, because a peer on a newer
    /// build is free to add fields and a decoder that stopped at the first unknown one
    /// would read last year's messages and drop today's.
    ///
    /// The field numbers are the reference's, not anyone's memory of them:
    /// `ProtocolTests/Vectors/MessageFixture.cs` holds the bytes printed from the
    /// published definition and the command that prints them again.
    /// </summary>
    public sealed class Message
    {
        /// <summary>The words, on the wire since the first version of WhatsApp.</summary>
        public const int ConversationField = 1;

        /// <summary>The encoded `SenderKeyDistributionMessage`: group traffic.</summary>
        public const int SenderKeyDistributionField = 2;

        /// <summary>The encoded `ExtendedText`: a quote, a link preview, a thumbnail.</summary>
        public const int ExtendedTextField = 6;

        /// <summary>`ExtendedTextMessage.contextInfo`, and inside it `mentionedJid`.</summary>
        public const int ContextInfoField = 17;
        public const int MentionedJidField = 15;

        /// <summary>The encoded `ImageMessage`: a photo.</summary>
        public const int ImageField = 3;

        /// <summary>The encoded `DocumentMessage`: an attachment.</summary>
        public const int DocumentField = 7;

        /// <summary>The encoded `AudioMessage`: a voice note or a track.</summary>
        public const int AudioField = 8;

        /// <summary>The encoded `VideoMessage`: a video.</summary>
        public const int VideoField = 9;

        /// <summary>The encoded `ProtocolMessage`: revoke, ephemeral, edit.</summary>
        public const int ProtocolField = 12;

        /// <summary>
        /// The encoded `Call` sub-message: the 32-byte media key of a call, field 10
        /// (`Message.Call.encode`, `writer.uint32(/* id 10 */ 82)`). It travels as the
        /// plaintext of each `<enc>` child of a call offer, one per recipient device.
        /// </summary>
        public const int CallField = 10;

        /// <summary>The `callKey` inside `Call`, field 1, as the reference writes it.</summary>
        public const int CallKeyField = 1;

        /// <summary>The encoded `DeviceSentMessage`: a copy for our own other devices.</summary>
        public const int DeviceSentField = 31;

        /// <summary>
        /// The `FutureProofMessage` wrappers. Each of these fields is a wrapper that carries
        /// `message = 1` around the real content - a photo that expires, one seen once, a
        /// document with a caption, an edited row, a status of a group - and it is the wrapper
        /// that travels in the `Message`. The reference client looks inside every one of them
        /// before it reads anything (`normalizeMessageContent`, `src/Utils/messages.ts`); a
        /// decoder that reads only the flat fields finds no media behind them, which was a
        /// status carrying a photo showing a screen with nothing on it.
        /// </summary>
        public const int ViewOnceField = 37;
        public const int EphemeralField = 40;
        public const int DocumentWithCaptionField = 53;
        public const int ViewOnceV2Field = 55;
        public const int EditedField = 58;
        public const int ViewOnceV2ExtensionField = 59;
        public const int AssociatedChildField = 91;
        public const int GroupStatusField = 96;
        public const int GroupStatusV2Field = 103;

        /// <summary>
        /// The longest padding a phone appends to a message's plaintext before encrypting it.
        ///
        /// Not the cipher's block padding - that one is the cipher's own business - but the
        /// WhatsApp layer's: between one and **sixteen** bytes whose **last byte is their own
        /// count**, appended to the serialized `Message` (`baileys`, `writeRandomPadMax16` /
        /// `unpadRandomMax16` - the name says the block; `whatsmeow`, `padMessage` /
        /// `unpadMessage`). Every message a phone sends carries it, pairwise as well as group,
        /// which is why a reader that strips it only for groups cannot read a `status` or a
        /// `msg` it has decrypted correctly.
        ///
        /// Sixteen and not fifteen, and the difference is a message lost: the padding is a
        /// whole block, so a count of sixteen is ordinary, and a walk that searched fifteen
        /// tails answered `a protobuf field announces 16 bytes with 14 left` for the copy the
        /// re-link received (`ProtoReader.MaxPadding` mirrors this).
        /// </summary>
        public const int MaxPadding = 16;

        /// <summary>`conversation`, field 1.</summary>
        public string Conversation;

        /// <summary>`senderKeyDistributionMessage`, field 2, kept encoded.</summary>
        public byte[] SenderKeyDistributionMessage;

        /// <summary>`extendedTextMessage`, field 6, kept encoded.</summary>
        public byte[] ExtendedTextMessage;

        /// <summary>
        /// The four media, as they arrived.
        ///
        /// Kept encoded and not decoded here, because the four have four different field
        /// tables and only the media path needs them (`MediaMessages` holds the tables). The
        /// important part is that they are kept at all: this decoder used to skip them, which
        /// turned every photo a contact sent into an empty bubble.
        /// </summary>
        public byte[] ImageMessage;
        public byte[] DocumentMessage;
        public byte[] AudioMessage;
        public byte[] VideoMessage;

        /// <summary>`protocolMessage`, field 12, kept encoded.</summary>
        public byte[] ProtocolMessage;

        /// <summary>`deviceSentMessage`, field 31, kept encoded.</summary>
        public byte[] DeviceSentMessage;

        /// <summary>`call`, field 10, kept encoded: a call offer's media key.</summary>
        public byte[] Call;

        /// <summary>
        /// The message a call offer's `<enc>` carries: `Message.call.callKey`, and nothing
        /// else. It is the plaintext that goes through the Signal session to one device - so
        /// each device gets its own ciphertext of the same 32 bytes.
        ///
        /// Why a `Message` and not the bare key: the reference's own offer encrypts
        /// `Message.CreateForCall(key)`, and a receiving phone reads it as a message before
        /// it reads the key out of it. Sending the raw 32 bytes would let the MAC verify and
        /// then fail at the first field read.
        /// </summary>
        public static Message ForCall(byte[] callKey)
        {
            if (callKey == null || callKey.Length == 0)
            {
                throw new ProtocolException("a call key needs bytes");
            }

            var message = new Message();
            message.Call = new ProtoWriter().Bytes(CallKeyField, callKey).ToArray();
            return message;
        }

        /// <summary>
        /// The call key inside a decrypted `Message`, or null when the message carries no
        /// call at all - which is what every ordinary message answers.
        /// </summary>
        public byte[] CallKey()
        {
            if (Call == null) return null;

            Dictionary<int, byte[]> call = Fields(Call);
            if (!call.ContainsKey(CallKeyField)) return null;

            return call[CallKeyField];
        }

        /// <summary>A message that is only words.</summary>
        public static Message Text(string body)
        {
            if (body == null) throw new ProtocolException("a text message needs words");

            var message = new Message();
            message.Conversation = body;
            return message;
        }

        /// <summary>
        /// A message taken back: the `ProtocolMessage` of type revoke and nothing else.
        ///
        /// It travels as a message like any other, because that is what a revoke is on this
        /// protocol: the other side's phone reads it and replaces the bubble it names with
        /// "this message was deleted" instead of drawing this one.
        /// </summary>
        public static Message Revoke(string jid, string messageId)
        {
            var message = new Message();
            message.ProtocolMessage = MessageProtocol.RevokeMessage(jid, messageId);
            return message;
        }

        /// <summary>
        /// A message that carries only a `ProtocolMessage` of another kind: the peer-data
        /// request that asks this account's own devices for history (`MessageProtocol`).
        /// </summary>
        public static Message Carrying(byte[] protocolMessage)
        {
            if (protocolMessage == null || protocolMessage.Length == 0)
            {
                throw new ProtocolException("a protocol message needs its bytes");
            }

            var message = new Message();
            message.ProtocolMessage = protocolMessage;
            return message;
        }

        /// <summary>
        /// The device-sent wrapper: `destinationJid` field 1 and the wrapped message
        /// field 2, which is the form our own other devices read the words out of.
        /// </summary>
        public static Message DeviceSent(string destinationJid, Message inner)
        {
            if (string.IsNullOrEmpty(destinationJid))
            {
                throw new ProtocolException("a device-sent message needs the JID it is for");
            }

            if (inner == null) throw new ProtocolException("a device-sent message needs the message it carries");

            var message = new Message();
            message.DeviceSentMessage = new ProtoWriter()
                .Text(1, destinationJid)
                .Bytes(2, inner.Encode())
                .ToArray();

            return message;
        }

        /// <summary>The `destinationJid` inside a device-sent form.</summary>
        public static string Destination(byte[] deviceSent)
        {
            Dictionary<int, byte[]> fields = Fields(deviceSent);
            if (!fields.ContainsKey(1)) return null;

            // The two-argument overload is the one the WP8.1 projection has: the app
            // build answered CS1501 for the single-argument call, and the desktop test
            // host never would have.
            return System.Text.Encoding.UTF8.GetString(fields[1], 0, fields[1].Length);
        }

        /// <summary>
        /// The message a device-sent form wraps, read **as a plaintext**: WhatsApp's own
        /// clients pad the copy they put inside this field, and reading it with `Decode` was
        /// the refusal.
        ///
        /// Why the padding is here and not only behind the stanza: the sender writes the
        /// wrapper, then pads the wrapper; a client that pads what it puts *inside* the field
        /// is equally real - the nine bytes of the phone's copy are `09` nine times, which
        /// `Decode` reads as field 1 with a length of nine while seven are left (`a protobuf
        /// field announces 9 bytes with 7 left`), and the answer to that was a `retry` for
        /// bytes that were never damaged. The words of a message sent from the account's own
        /// phone were lost that way, once per message.
        /// </summary>
        public static Message Inner(byte[] deviceSent)
        {
            Dictionary<int, byte[]> fields = Fields(deviceSent);
            if (!fields.ContainsKey(2))
            {
                throw new ProtocolException("a device-sent form with no message inside it");
            }

            return DecodePadded(fields[2]);
        }

        /// <summary>
        /// The words: `conversation` when there are any, otherwise the text inside an
        /// extended text, otherwise nothing - and nothing is a message this client does
        /// not display, not an error.
        /// </summary>
        public string Body()
        {
            if (Conversation != null) return Conversation;
            if (ExtendedTextMessage == null) return null;

            Dictionary<int, byte[]> extended = Fields(ExtendedTextMessage);
            if (!extended.ContainsKey(1)) return null;

            return System.Text.Encoding.UTF8.GetString(extended[1], 0, extended[1].Length);
        }

        /// <summary>
        /// A message with words that name people: an extended text whose `contextInfo`
        /// (field 17) lists each mentioned JID as `mentionedJid` (field 15). Without any
        /// mention it is a plain `conversation`, as `Text` makes it.
        /// </summary>
        public static Message TextMentioning(string body, IList<string> mentionedJids)
        {
            if (body == null) throw new ProtocolException("a text message needs words");
            if (mentionedJids == null || mentionedJids.Count == 0) return Text(body);

            var context = new ProtoWriter();
            for (int i = 0; i < mentionedJids.Count; i++)
            {
                context.Text(MentionedJidField, mentionedJids[i]);
            }

            var message = new Message();
            message.ExtendedTextMessage = new ProtoWriter()
                .Text(1, body)
                .Bytes(ContextInfoField, context.ToArray())
                .ToArray();
            return message;
        }

        /// <summary>
        /// The JIDs this message names, in the order they were written. Empty when the
        /// message names nobody - a plain conversation has no mentions at all.
        /// </summary>
        public IList<string> Mentions()
        {
            var jids = new List<string>();
            if (ExtendedTextMessage == null) return jids;

            byte[] context = null;
            var reader = new ProtoReader(ExtendedTextMessage);
            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();
                if (field.Number == ContextInfoField && field.WireType == 2) context = reader.ReadBytes(field.WireType);
                else reader.SkipField(field.WireType);
            }

            if (context == null) return jids;

            reader = new ProtoReader(context);
            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();
                if (field.Number == MentionedJidField && field.WireType == 2)
                {
                    byte[] jid = reader.ReadBytes(field.WireType);
                    jids.Add(System.Text.Encoding.UTF8.GetString(jid, 0, jid.Length));
                }
                else reader.SkipField(field.WireType);
            }

            return jids;
        }

        public byte[] Encode()
        {
            return new ProtoWriter()
                .Text(ConversationField, Conversation)
                .Bytes(SenderKeyDistributionField, SenderKeyDistributionMessage)
                .Bytes(ImageField, ImageMessage)
                .Bytes(ExtendedTextField, ExtendedTextMessage)
                .Bytes(DocumentField, DocumentMessage)
                .Bytes(AudioField, AudioMessage)
                .Bytes(VideoField, VideoMessage)
                .Bytes(ProtocolField, ProtocolMessage)
                .Bytes(CallField, Call)
                .Bytes(DeviceSentField, DeviceSentMessage)
                .ToArray();
        }

        public static Message Decode(byte[] bytes)
        {
            var message = new Message();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case ConversationField:
                        {
                            byte[] words = ReadBytesLaidOutAsBytes(reader, field);
                            message.Conversation = System.Text.Encoding.UTF8.GetString(words, 0, words.Length);
                            break;
                        }

                    case SenderKeyDistributionField:
                        message.SenderKeyDistributionMessage = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case ExtendedTextField:
                        message.ExtendedTextMessage = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case ImageField:
                        message.ImageMessage = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case DocumentField:
                        message.DocumentMessage = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case AudioField:
                        message.AudioMessage = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case VideoField:
                        message.VideoMessage = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case ProtocolField:
                        message.ProtocolMessage = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case CallField:
                        message.Call = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case DeviceSentField:
                        message.DeviceSentMessage = ReadBytesLaidOutAsBytes(reader, field);
                        break;

                    case ViewOnceField:
                    case EphemeralField:
                    case DocumentWithCaptionField:
                    case ViewOnceV2Field:
                    case EditedField:
                    case ViewOnceV2ExtensionField:
                    case AssociatedChildField:
                    case GroupStatusField:
                    case GroupStatusV2Field:
                        {
                            // The wrapper carries `message = 1` and the content is inside it,
                            // possibly wrapped again. What the message *is* is the content, so
                            // that is what is returned - the same replacement the reference
                            // makes before it reads a single field. A wrapper with no message
                            // in it is no content and the read goes on.
                            Message inner = Unwrap(ReadBytesLaidOutAsBytes(reader, field));
                            if (inner != null) return inner;
                            break;
                        }

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return message;
        }

        /// <summary>
        /// The message inside a `FutureProofMessage` wrapper, and null when the wrapper carries
        /// none. The inner one is decoded in full, so a wrapper around a wrapper around the
        /// content still ends in the content.
        /// </summary>
        private static Message Unwrap(byte[] wrapper)
        {
            Dictionary<int, byte[]> fields = Fields(wrapper);
            if (!fields.ContainsKey(1)) return null;

            return Decode(fields[1]);
        }

        /// <summary>
        /// The field's bytes, read with the layout its number gives it. A tag that names a
        /// field of this message and lays it out otherwise is refused: by the read first,
        /// which is where the padded copies of the echo and of the re-link are pinned to
        /// trip (`a protobuf field announces 9 bytes with 7 left` names the padding), and
        /// then explicitly, because a read that lands on the wrong layout is never a value
        /// this message accepts - the misparse a conformant parser would not make must not
        /// become a silently wrong field either.
        /// </summary>
        private static byte[] ReadBytesLaidOutAsBytes(ProtoReader reader, ProtoField field)
        {
            byte[] value = reader.ReadBytes();

            if (field.WireType != 2)
            {
                throw new ProtocolException("a protobuf field is laid out as wire type "
                    + field.WireType + " and is read as the bytes of field " + field.Number);
            }

            return value;
        }

        /// <summary>
        /// The message inside a decrypted plaintext, which is a protobuf with the WhatsApp
        /// padding behind it.
        ///
        /// The rule is the reference's and it comes first: the last byte is how many bytes were
        /// appended, so that many are dropped before the protobuf is read (`baileys`,
        /// `unpadRandomMax16`). Then the case the reference's rule does not cover, a plaintext
        /// that carries no padding at all - the rule is a rule, not a promise, and a plaintext
        /// that ends in a byte between 1 and 15 is exactly what a sender that skips the padding
        /// produces.
        ///
        /// Neither of the two is decided by parsing and catching any more. `ProtoReader.Measure`
        /// says where the protobuf ends without throwing, so the ordinary cases cost no
        /// exception at all; a plaintext searcher that threw its way through the sixteen
        /// possible trims was seventeen first-chance exceptions per message that carried no
        /// padding, which is the row of `Eccezione generata` the run left in
        /// the debugger.
        ///
        /// The plaintext itself is read last, so a message that really is damaged reports the
        /// failure its bytes produce instead of one that names a size nobody sent - and that
        /// report is a single `ProtocolException`, which is the one that should be seen.
        /// </summary>
        public static Message DecodePadded(byte[] plaintext)
        {
            if (plaintext == null || plaintext.Length == 0)
            {
                throw new ProtocolException("a message carries no bytes; there is nothing to read");
            }

            // Asked, not assumed: the walk says which tail of this plaintext leaves a whole
            // message, and the last byte - the count - is asked about first. It has to be: the
            // padding WhatsApp writes is the count repeated, so the padded buffer also walks as
            // a whole protobuf, and asking the buffer first read the padding as a field of the
            // message (`a protobuf field announces 9 bytes with 7 left`, the echo of 1 November
            // 2026). The other fifteen tails are tried before the message is refused - each one
            // a walk, never a parse that throws.
            int ended = ProtoReader.EndOfPadded(plaintext, plaintext[plaintext.Length - 1]);

            if (ended > 0) return Decode(Head(plaintext, ended));

            return Decode(plaintext);
        }

        /// <summary>
        /// The bytes of a sub-message without the padding that may follow them.
        ///
        /// The same walk `DecodePadded` makes, in the one form this reader needs: the answer is
        /// the bytes themselves when nothing is behind them, so an unpadded sub-message costs
        /// no copy and no search.
        /// </summary>
        private static byte[] TrimPadding(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return bytes;

            int whole = ProtoReader.Measure(bytes);
            if (whole == bytes.Length) return bytes;

            int ended = ProtoReader.EndOfPadded(bytes, bytes[bytes.Length - 1]);
            return ended > 0 ? Head(bytes, ended) : bytes;
        }

        private static byte[] Head(byte[] bytes, int length)
        {
            var head = new byte[length];
            System.Buffer.BlockCopy(bytes, 0, head, 0, length);

            return head;
        }

        /// <summary>
        /// The length-delimited fields of a sub-message, by field number. The sub-messages
        /// this class keeps encoded (`DeviceSentMessage`, `ExtendedText`) are read one or
        /// two fields at a time, and each of them has its own numbers.
        /// </summary>
        private static Dictionary<int, byte[]> Fields(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no message bytes to read");

            // The sub-message is a plaintext, and a plaintext may carry the padding behind it:
            // cut it off before the walk, or the tail reads as a field of its own.
            bytes = TrimPadding(bytes);

            var fields = new Dictionary<int, byte[]>();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();
                if (field.WireType == 2) fields[field.Number] = reader.ReadBytes(field.WireType);
                else reader.SkipField(field.WireType);
            }

            return fields;
        }
    }
}
