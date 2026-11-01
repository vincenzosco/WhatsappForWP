using System;
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

        /// <summary>
        /// A plaintext as a phone sends it: the message's protobuf with 1 to 15 bytes of the
        /// WhatsApp padding behind it, the last of which is the count.
        ///
        /// Why our own messages need it even though nobody complains: the receiving phone strips
        /// a number of bytes read from the **last byte** of the decrypted plaintext
        /// (`baileys`, `unpadRandomMax16`), so a message whose protobuf happens to end in a byte
        /// between 1 and 15 - a text that ends in a newline is enough - loses that many bytes on
        /// the other side. Padding is what makes the last byte mean what it says.
        /// </summary>
        public static byte[] Pad(byte[] plaintext, IRandomSource random)
        {
            if (plaintext == null) throw new ProtocolException("there is nothing to pad");
            if (random == null) throw new ProtocolException("padding needs randomness for its bytes");

            int pad = 1 + (random.Bytes(1)[0] % Message.MaxPadding);

            var padded = new byte[plaintext.Length + pad];
            Buffer.BlockCopy(plaintext, 0, padded, 0, plaintext.Length);

            byte[] noise = random.Bytes(pad);
            Buffer.BlockCopy(noise, 0, padded, plaintext.Length, pad);
            padded[padded.Length - 1] = (byte)pad;

            return padded;
        }

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
            IList<KeyValuePair<string, SessionRecord>> devices, uint timestamp, IRandomSource random)
        {
            if (body == null) throw new ProtocolException("a text message needs words");

            return Node(to, id, Message.Text(body), TextType, null, devices, timestamp, random);
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
            IList<KeyValuePair<string, SessionRecord>> distributions, uint timestamp, IRandomSource random,
            byte[] deviceIdentity)
        {
            if (!GroupDirectory.IsGroup(to)) throw new ProtocolException("not a group: " + to);
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a message needs an id: it is what a receipt answers");
            if (message == null) throw new ProtocolException("there is no message to send");
            if (record == null) throw new ProtocolException("a group message needs this device's sender key for " + to);

            byte[] distribution = SenderKeys.DistributionMessage(to, record);

            var content = new List<BinaryNode>();
            bool preKey = false;

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
                    enc.Content = session.Encrypt(Pad(distribution, random));

                    if (session.PreKeyPending) preKey = true;

                    BinaryNode device = new BinaryNode("to");
                    device.Attrs.Add("jid", deviceJid);
                    device.Content = new List<BinaryNode> { enc };

                    nodes.Add(device);
                }

                var participants = new BinaryNode("participants");
                participants.Content = nodes;
                content.Add(participants);
            }

            // The identity belongs to the message, not to the group ciphertext: it is what
            // proves this device to the members that are being handed the sender key for the
            // first time, and the reference puts it after `participants` for both shapes
            // (ExWapp's protocol reference, "Group fanout").
            BinaryNode identity = preKey ? DeviceIdentityNode(deviceIdentity) : null;
            if (identity != null) content.Add(identity);

            BinaryNode group = new BinaryNode("enc");
            group.Attrs.Add("v", KeyVersion);
            group.Attrs.Add("type", SenderKeys.CipherType);
            if (!string.IsNullOrEmpty(mediaType)) group.Attrs.Add("mediatype", mediaType);
            group.Content = SenderKeys.Ciphertext(to, record, Pad(message.Encode(), random));

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
            IList<KeyValuePair<string, SessionRecord>> devices, uint timestamp, IRandomSource random)
        {
            return Node(to, id, message, type, mediaType, devices, timestamp, random, null, null);
        }

        /// <summary>
        /// The same stanza with an optional trusted-contact token.
        ///
        /// The token is **optional**: both reference clients ask the server for one and
        /// attach it when it answers, and neither makes a send wait for it
        /// (`Socket/messages-send.js`, `getPrivacyTokens`; `whatsmeow`, `tctoken`). A client
        /// that refused to send without one sent nothing at all: the server answers the
        /// privacy query with no token for a contact it has not issued one for, and this
        /// device saw that empty answer on every attempt.
        /// </summary>
        public static BinaryNode Node(string to, string id, Message message, string type, string mediaType,
            IList<KeyValuePair<string, SessionRecord>> devices, uint timestamp, IRandomSource random,
            PrivacyToken privacyToken)
        {
            return Node(to, id, message, type, mediaType, devices, timestamp, random, privacyToken, null);
        }

        /// <summary>
        /// The direct-message stanza: one `enc` per device, inside a `participants` node,
        /// and a `<tctoken>` when this phone has one for the chat.
        ///
        /// **Who gets which plaintext** is the thing this method had wrong. A one-to-one
        /// send has two payload forms (`Socket/messages-send.js`, `relayMessage`): the
        /// recipient's devices get the message as it was written, and this account's other
        /// devices get a `deviceSentMessage` wrapping it - the sender's own copy, which is
        /// what makes the words appear as sent on the phone. Every copy used to be the
        /// wrapper, so the one device that matters, the recipient's, received a copy of our
        /// own sent history instead of a message to it. The phone answers that with an
        /// `ack error="479"`, which is the refusal this app has been collecting on every
        /// direct send.
        ///
        /// `ownJids` is this account's JIDs (the number and the LID). When it is null no
        /// device in the list is ours, which is the ordinary case: the list comes from the
        /// recipient's `usync`.
        /// </summary>
        public static BinaryNode Node(string to, string id, Message message, string type, string mediaType,
            IList<KeyValuePair<string, SessionRecord>> devices, uint timestamp, IRandomSource random,
            PrivacyToken privacyToken, IList<string> ownJids)
        {
            return Node(to, id, message, type, mediaType, devices, timestamp, random, privacyToken,
                ownJids, null);
        }

        /// <summary>
        /// The same stanza, carrying the account's signature over this device when one of the
        /// ciphertexts opens a session.
        ///
        /// Why the node is not optional on a `pkmsg`: a device the recipient has never heard
        /// of has to be told, by the account, that the identity key inside the pre-key message
        /// is this device's - that signature is the whole content of `device-identity`. The
        /// reference attaches it the moment an `enc` is a `pkmsg` (`Socket/messages-send.js`,
        /// `shouldIncludeDeviceIdentity`), and a message that goes out without it is taken by
        /// the server and shown to nobody: `ack class="message"`, one tick, and no device of
        /// the recipient draws anything - the phone of 2026-11-01, twice, in both address
        /// families.
        ///
        /// An established session carries no node, and that is the reference's rule too: the
        /// identity is what opens a session, and a `msg` is already inside one.
        /// </summary>
        public static BinaryNode Node(string to, string id, Message message, string type, string mediaType,
            IList<KeyValuePair<string, SessionRecord>> devices, uint timestamp, IRandomSource random,
            PrivacyToken privacyToken, IList<string> ownJids, byte[] deviceIdentity)
        {
            if (string.IsNullOrEmpty(to)) throw new ProtocolException("a message needs somebody to go to");
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a message needs an id: it is what a receipt answers");
            if (message == null) throw new ProtocolException("there is no message to send");
            if (devices == null || devices.Count == 0)
            {
                throw new ProtocolException("a message needs at least one device to encrypt for");
            }

            // One padding per form, drawn when the first device of that kind needs it: the
            // padding is random bytes, so two plaintexts cannot share one envelope.
            byte[] forRecipient = null;
            byte[] forOurDevices = null;
            bool preKey = false;

            var participants = new List<BinaryNode>();
            for (int i = 0; i < devices.Count; i++)
            {
                string deviceJid = devices[i].Key;
                SessionRecord session = devices[i].Value;

                if (string.IsNullOrEmpty(deviceJid)) throw new ProtocolException("a message goes to a device, not to nothing");
                if (session == null) throw new ProtocolException("the session for " + deviceJid + " is missing");

                byte[] plaintext;
                if (IsOurs(deviceJid, ownJids))
                {
                    if (forOurDevices == null) forOurDevices = Pad(Message.DeviceSent(to, message).Encode(), random);
                    plaintext = forOurDevices;
                }
                else
                {
                    if (forRecipient == null) forRecipient = Pad(message.Encode(), random);
                    plaintext = forRecipient;
                }

                BinaryNode enc = new BinaryNode("enc");
                enc.Attrs.Add("v", KeyVersion);
                enc.Attrs.Add("type", session.PreKeyPending ? PreKeyType : ExistingType);
                if (!string.IsNullOrEmpty(mediaType)) enc.Attrs.Add("mediatype", mediaType);
                enc.Content = session.Encrypt(plaintext);

                if (session.PreKeyPending) preKey = true;

                BinaryNode device = new BinaryNode("to");
                device.Attrs.Add("jid", deviceJid);
                device.Content = new List<BinaryNode> { enc };

                participants.Add(device);
            }

            // The per-device nodes sit in `participants` for a direct message too: both
            // reference clients write it that way, and the reader here already accepts both
            // shapes on the way in (`IncomingMessage.PairwiseEnc`).
            var devicesNode = new BinaryNode("participants");
            devicesNode.Content = participants;

            var content = new List<BinaryNode>();
            content.Add(devicesNode);

            BinaryNode identity = preKey ? DeviceIdentityNode(deviceIdentity) : null;
            if (identity != null) content.Add(identity);

            if (privacyToken != null && privacyToken.Bytes != null && privacyToken.Bytes.Length > 0)
            {
                var token = new BinaryNode("tctoken");
                token.Attrs.Add("t", privacyToken.Timestamp);
                token.Content = privacyToken.Bytes;
                content.Add(token);
            }

            var stanza = new BinaryNode("message");
            stanza.Attrs.Add("to", to);
            stanza.Attrs.Add("type", string.IsNullOrEmpty(type) ? TextType : type);
            stanza.Attrs.Add("id", id);
            stanza.Attrs.Add("t", timestamp.ToString());
            stanza.Content = content;

            return stanza;
        }

        /// <summary>
        /// The `device-identity` child of a message that opens a session, or null when this
        /// account has no signed identity to show.
        ///
        /// Null is a possibility the caller cannot fix and the log has to name: an account
        /// linked by a build that did not keep the identity has to be linked again, and a
        /// send without the node is a message a recipient is entitled to ignore.
        /// </summary>
        public static BinaryNode DeviceIdentityNode(byte[] deviceIdentity)
        {
            if (deviceIdentity == null || deviceIdentity.Length == 0) return null;

            var node = new BinaryNode("device-identity");
            node.Content = deviceIdentity;

            return node;
        }

        /// <summary>
        /// Whether a device JID is this account's, by user part: the number and the LID are
        /// two names for the same account, and a device of it is one of ours whichever name
        /// the device list used.
        /// </summary>
        private static bool IsOurs(string deviceJid, IList<string> ownJids)
        {
            if (ownJids == null || ownJids.Count == 0) return false;

            DecodedJid device = JidUtils.Decode(deviceJid);
            if (!device.IsJid) return false;

            for (int i = 0; i < ownJids.Count; i++)
            {
                if (string.IsNullOrEmpty(ownJids[i])) continue;

                DecodedJid own = JidUtils.Decode(ownJids[i]);
                if (!own.IsJid) continue;
                if (string.Equals(own.User, device.User, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
    }
}
