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

        /// <summary>A group's JID, which is what decides the shape of a send.</summary>
        public static bool IsGroup(string jid)
        {
            return GroupDirectory.IsGroup(jid);
        }

        /// <summary>
        /// The group stanza: our sender key handed to the devices that do not have it, and
        /// **one** ciphertext for everybody.
        ///
        /// That asymmetry is the point of sender keys and it is worth saying plainly: a
        /// message into a group of twenty people is one group ciphertext plus one small
        /// per-device message carrying the key, not twenty copies of the message.
        ///
        /// The shape is the reference's (`Socket/messages-send.js`, lines 559 to 585, and
        /// `whatsmeow`, `send.go`, `sendGroupMessage`):
        ///
        ///     message to=&lt;group&gt; type=&lt;text|media&gt; id t [addressing_mode]
        ///       participants
        ///         to jid=&lt;device&gt;
        ///           enc v=2 type=pkmsg|msg      the sender key distribution, pairwise
        ///       enc v=2 type=skmsg              the group ciphertext
        ///
        /// The distribution goes to every device of every member - and to our own other
        /// devices, which need it to read what this phone writes - and its plaintext is the
        /// distribution message **and nothing else**: it carries no words, so a receiver that
        /// has our key never sees it as a message.
        ///
        /// The group ciphertext's plaintext is the message itself, with **no
        /// device-sent wrapper**: only a 1:1 message carries one, because a group has no
        /// single recipient to name (`whatsmeow`, `marshalMessage`).
        /// </summary>
        public static BinaryNode Group(string to, string id, Message message, string type, string mediaType,
            SenderKeyRecord record, string addressingMode,
            IList<KeyValuePair<string, SessionRecord>> distributions, uint timestamp)
        {
            if (!GroupDirectory.IsGroup(to)) throw new ProtocolException("not a group: " + to);
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a message needs an id: it is what a receipt answers");
            if (message == null) throw new ProtocolException("there is no message to send");
            if (record == null) throw new ProtocolException("a group message needs this device's sender key for " + to);

            byte[] distribution = SenderKeys.DistributionMessage(to, record);

            var content = new List<BinaryNode>();

            if (distributions != null && distributions.Count > 0)
            {
                var nodes = new List<BinaryNode>();

                for (int i = 0; i < distributions.Count; i++)
                {
                    string deviceJid = distributions[i].Key;
                    SessionRecord session = distributions[i].Value;

                    if (string.IsNullOrEmpty(deviceJid)) throw new ProtocolException("a message goes to a device, not to nothing");
                    if (session == null) throw new ProtocolException("the session for " + deviceJid + " is missing");

                    BinaryNode enc = new BinaryNode("enc");
                    enc.Attrs.Add("v", KeyVersion);
                    enc.Attrs.Add("type", session.PreKeyPending ? PreKeyType : ExistingType);
                    enc.Content = session.Encrypt(distribution);

                    BinaryNode device = new BinaryNode("to");
                    device.Attrs.Add("jid", deviceJid);
                    device.Content = new List<BinaryNode> { enc };

                    nodes.Add(device);
                }

                var participants = new BinaryNode("participants");
                participants.Content = nodes;
                content.Add(participants);
            }

            BinaryNode group = new BinaryNode("enc");
            group.Attrs.Add("v", KeyVersion);
            group.Attrs.Add("type", SenderKeys.CipherType);
            if (!string.IsNullOrEmpty(mediaType)) group.Attrs.Add("mediatype", mediaType);
            group.Content = SenderKeys.Ciphertext(to, record, message.Encode());

            content.Add(group);

            var stanza = new BinaryNode("message");
            stanza.Attrs.Add("to", to);
            stanza.Attrs.Add("type", string.IsNullOrEmpty(type) ? TextType : type);
            stanza.Attrs.Add("id", id);
            stanza.Attrs.Add("t", timestamp.ToString());
            if (!string.IsNullOrEmpty(addressingMode)) stanza.Attrs.Add("addressing_mode", addressingMode);
            stanza.Content = content;

            return stanza;
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
