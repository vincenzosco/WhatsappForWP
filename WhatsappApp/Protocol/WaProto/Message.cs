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

        /// <summary>The encoded `DeviceSentMessage`: a copy for our own other devices.</summary>
        public const int DeviceSentField = 31;

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

        /// <summary>A message that is only words.</summary>
        public static Message Text(string body)
        {
            if (body == null) throw new ProtocolException("a text message needs words");

            var message = new Message();
            message.Conversation = body;
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

        /// <summary>The message a device-sent form wraps.</summary>
        public static Message Inner(byte[] deviceSent)
        {
            Dictionary<int, byte[]> fields = Fields(deviceSent);
            if (!fields.ContainsKey(2))
            {
                throw new ProtocolException("a device-sent form with no message inside it");
            }

            return Decode(fields[2]);
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
                            byte[] words = reader.ReadBytes();
                            message.Conversation = System.Text.Encoding.UTF8.GetString(words, 0, words.Length);
                            break;
                        }

                    case SenderKeyDistributionField:
                        message.SenderKeyDistributionMessage = reader.ReadBytes();
                        break;

                    case ExtendedTextField:
                        message.ExtendedTextMessage = reader.ReadBytes();
                        break;

                    case ImageField:
                        message.ImageMessage = reader.ReadBytes();
                        break;

                    case DocumentField:
                        message.DocumentMessage = reader.ReadBytes();
                        break;

                    case AudioField:
                        message.AudioMessage = reader.ReadBytes();
                        break;

                    case VideoField:
                        message.VideoMessage = reader.ReadBytes();
                        break;

                    case ProtocolField:
                        message.ProtocolMessage = reader.ReadBytes();
                        break;

                    case DeviceSentField:
                        message.DeviceSentMessage = reader.ReadBytes();
                        break;

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return message;
        }

        /// <summary>
        /// The length-delimited fields of a sub-message, by field number. The sub-messages
        /// this class keeps encoded (`DeviceSentMessage`, `ExtendedText`) are read one or
        /// two fields at a time, and each of them has its own numbers.
        /// </summary>
        private static Dictionary<int, byte[]> Fields(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no message bytes to read");

            var fields = new Dictionary<int, byte[]>();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();
                if (field.WireType == 2) fields[field.Number] = reader.ReadBytes();
                else reader.SkipField(field.WireType);
            }

            return fields;
        }
    }
}
