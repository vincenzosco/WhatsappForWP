using System.Collections.Generic;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// A text on its way out: the `message` stanza the server forwards, with one ciphertext
    /// per device of the peer.
    ///
    /// The shape is the reference's (`@whiskeysockets/baileys`, `createParticipantNodes`):
    /// `message` with `to`, `type` `text`, an `id` and a `t`, and one `to[jid]` child per
    /// device, each holding one `enc` whose `v` is `2` and whose `type` is `pkmsg` while the
    /// device has not answered yet and `msg` after that.
    ///
    /// The words themselves are not sent bare: they go inside a `deviceSentMessage` naming
    /// the JID they are for, which is what makes a copy of our own message readable on our
    /// other devices and recognisable as ours when it comes back to us.
    ///
    /// `SessionRecord.Encrypt` hands back the whole message - sealed, or wrapped in the
    /// pre-key form while the session is pending - so nothing here frames anything twice.
    /// </summary>
    public static class OutgoingMessage
    {
        /// <summary>The `enc` version this client speaks.</summary>
        public const string KeyVersion = "2";

        /// <summary>The `enc` type of a message that carries the pre-keys of a new session.</summary>
        public const string PreKeyType = "pkmsg";

        /// <summary>The `enc` type of a message on a session that already exists.</summary>
        public const string ExistingType = "msg";

        /// <summary>The stanza's `type` for a message that is only words.</summary>
        public const string TextType = "text";

        /// <summary>The stanza's `type` for a message that carries a file.</summary>
        public const string MediaType = "media";

        /// <summary>How many random bytes a message id is: sixteen hex characters.</summary>
        public const int IdBytes = 8;

        /// <summary>A fresh message id, upper-case hex.</summary>
        public static string Id(IRandomSource random)
        {
            if (random == null) throw new ProtocolException("a message id needs randomness");

            byte[] bytes = random.Bytes(IdBytes);
            var text = new System.Text.StringBuilder(IdBytes * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));

            return text.ToString();
        }

        /// <summary>
        /// The stanza for one text, one ciphertext per device. `devices` is keyed by the
        /// device JID, which is what the `to` attribute names and what the session was
        /// stored under.
        /// </summary>
        public static BinaryNode Node(string to, string id, string body,
            IList<KeyValuePair<string, SessionRecord>> devices, uint timestamp)
        {
            if (body == null) throw new ProtocolException("a text message needs words");

            return Node(to, id, Message.Text(body), TextType, null, devices, timestamp);
        }

        /// <summary>
        /// The same stanza carrying a message of any shape, which is what a photo needs.
        ///
        /// Two attributes are the difference and both are the reference's
        /// (`Socket/messages-send.js`, `getMessageType` at 885 and `getMediaType` at 906):
        /// the stanza's `type` is **`media`** and not `text`, and each `enc` also carries
        /// `mediatype` (`image`, `video`, `ptt` for a voice note, `document`), which is what
        /// tells the receiving phone which player to prepare before it has decrypted
        /// anything.
        /// </summary>
        public static BinaryNode Node(string to, string id, Message message, string type, string mediaType,
            IList<KeyValuePair<string, SessionRecord>> devices, uint timestamp)
        {
            if (string.IsNullOrEmpty(to)) throw new ProtocolException("a message needs somebody to go to");
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a message needs an id: it is what a receipt answers");
            if (message == null) throw new ProtocolException("there is no message to send");
            if (devices == null || devices.Count == 0)
            {
                throw new ProtocolException("a message needs at least one device to encrypt for");
            }

            // The device-sent form, built once for every device: the same message, the same
            // id, the same JID, in every copy.
            byte[] plaintext = Message.DeviceSent(to, message).Encode();

            var content = new List<BinaryNode>();
            for (int i = 0; i < devices.Count; i++)
            {
                string deviceJid = devices[i].Key;
                SessionRecord session = devices[i].Value;

                if (string.IsNullOrEmpty(deviceJid)) throw new ProtocolException("a message goes to a device, not to nothing");
                if (session == null) throw new ProtocolException("the session for " + deviceJid + " is missing");

                BinaryNode enc = new BinaryNode("enc");
                enc.Attrs.Add("v", KeyVersion);
                enc.Attrs.Add("type", session.PreKeyPending ? PreKeyType : ExistingType);
                if (!string.IsNullOrEmpty(mediaType)) enc.Attrs.Add("mediatype", mediaType);
                enc.Content = session.Encrypt(plaintext);

                BinaryNode device = new BinaryNode("to");
                device.Attrs.Add("jid", deviceJid);
                device.Content = new List<BinaryNode> { enc };

                content.Add(device);
            }

            var stanza = new BinaryNode("message");
            stanza.Attrs.Add("to", to);
            stanza.Attrs.Add("type", string.IsNullOrEmpty(type) ? TextType : type);
            stanza.Attrs.Add("id", id);
            stanza.Attrs.Add("t", timestamp.ToString());
            stanza.Content = content;

            return stanza;
        }
    }
}
