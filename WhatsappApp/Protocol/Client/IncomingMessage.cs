using System.Collections.Generic;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// A `message` stanza read: which device's copy to open, the session it needs - started
    /// from the wrapper when it is the first one - the words inside, and nothing at all when
    /// it is a copy already seen.
    ///
    /// Two shapes of stanza arrive here and both are the reference's:
    ///
    /// - the `enc` is at the top when the server has already narrowed the copies down to
    ///   this device (the ordinary case), and inside a `to[jid]` when it has not;
    /// - a pre-key message (`enc type="pkmsg"`) carries everything a responder session
    ///   needs, and a plain one (`type="msg"`) is opened with the session already stored.
    ///
    /// A duplicate is not an error: the server re-delivers a message that was not
    /// acknowledged, and showing it twice is the bug. That is what <see cref="SeenMessages"/>
    /// is for, and it is bounded, because a set that grows for the life of the app is a
    /// leak with a good excuse.
    /// </summary>
    public sealed class IncomingMessage
    {
        /// <summary>The stanza's id, which is what a receipt answers.</summary>
        public string Id;

        /// <summary>Who it came from, the device JID as the stanza spells it.</summary>
        public string From;

        /// <summary>The words, or null when this message is not words this client shows.</summary>
        public string Text;

        /// <summary>The stanza's `t` in seconds.</summary>
        public uint Timestamp;

        /// <summary>
        /// True when the copy is our own, sent by another device of this account: the
        /// stanza comes from one of our own JIDs and carries a device-sent form, and a chat
        /// that showed it would show every message a person sends from their own phone.
        ///
        /// The sender is what decides it, not the form's destination: every message this
        /// client sends carries a device-sent form, the peer's included, and the
        /// destination of a message arriving here is this device by definition.
        /// </summary>
        public bool IsOurEcho;

        /// <summary>
        /// The decoded message itself, or null when there was nothing to decode.
        ///
        /// It is here because the words are not all a message carries: the history
        /// bootstrap arrives as a `protocolMessage` inside an ordinary message stanza (the
        /// reference reads it out of `protocolMessage.historySyncNotification`), and a caller
        /// that only ever saw `Text` could not tell that the chat list was in the same
        /// stanza. For a device-sent form this is the *inner* message, which is the one whose
        /// protocol message is meant for this device.
        /// </summary>
        public Message Content;

        /// <summary>
        /// One message, or null when this id has already been read. Throws, with the id in
        /// the sentence, when the stanza cannot be opened at all.
        /// </summary>
        public static IncomingMessage Read(BinaryNode message, Credentials credentials,
            SessionStore sessions, PreKeyStore preKeys, SeenMessages seen, IRandomSource random)
        {
            if (message == null) throw new ProtocolException("there is no message to read");
            if (message.Tag != "message") throw new ProtocolException("not a message node: it is a " + message.Tag);

            string id = message.Attrs["id"];
            string from = message.Attrs["from"];
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a message with no id cannot be acknowledged");
            if (string.IsNullOrEmpty(from)) throw new ProtocolException("message " + id + " names nobody as its sender");

            if (seen != null && !seen.IsNew(id)) return null;

            BinaryNode enc = EncFor(message, credentials);
            if (enc == null) throw new ProtocolException("no enc for this device in message " + id);

            bool preKey = enc.Attrs["type"] == OutgoingMessage.PreKeyType;
            byte[] wire = enc.Bytes();
            if (wire == null || wire.Length == 0) throw new ProtocolException("message " + id + " carries no ciphertext");

            byte[] plaintext;
            try
            {
                SessionRecord session = sessions.Load(from);
                if (session == null || !session.HasSession)
                {
                    if (!preKey)
                    {
                        throw new ProtocolException("it is not a pre-key message and there is no session with " + from);
                    }

                    session = StartResponder(wire, from, credentials, preKeys, sessions, random);
                }

                plaintext = session.Decrypt(wire, preKey);

                // The ratchet moved: what the next message needs is the session as it is now.
                sessions.Save(from, session);
            }
            catch (ProtocolException failed)
            {
                // One sentence for everything that can go wrong in there, and it names the
                // message: a ciphertext with a byte moved corrupts the wrapper as often as
                // the message, and "the signed pre-key does not match" with no id attached
                // is a search, not a report.
                throw new ProtocolException("message " + id + " from " + from + " did not read: " + failed.Message);
            }

            Message read = Message.Decode(plaintext);
            var incoming = new IncomingMessage();
            incoming.Id = id;
            incoming.From = from;
            incoming.Timestamp = Seconds(message.Attrs["t"]);

            if (read.DeviceSentMessage == null)
            {
                incoming.Content = read;
                incoming.Text = read.Body();
                return incoming;
            }

            incoming.IsOurEcho = IsMine(from, credentials);
            incoming.Content = Message.Inner(read.DeviceSentMessage);
            incoming.Text = incoming.Content.Body();

            return incoming;
        }

        /// <summary>
        /// The acknowledgement the server waits for before it stops re-sending: its own id
        /// back, to the JID it came from, with the stanza's tag as the class.
        /// </summary>
        public static BinaryNode Ack(BinaryNode received)
        {
            if (received == null) throw new ProtocolException("there is nothing to acknowledge");

            var ack = new BinaryNode("ack");
            ack.Attrs.Add("id", received.Attrs["id"]);
            ack.Attrs.Add("to", received.Attrs["from"]);
            ack.Attrs.Add("class", received.Tag);

            string participant = received.Attrs["participant"];
            if (!string.IsNullOrEmpty(participant)) ack.Attrs.Add("participant", participant);

            string type = received.Attrs["type"];
            if (!string.IsNullOrEmpty(type)) ack.Attrs.Add("type", type);

            return ack;
        }

        /// <summary>
        /// A receipt: `read` is what tells the other phone its ticks turn blue, and it
        /// carries the time it happened. The reference sends the first id as the receipt's
        /// own id and the rest in children, which is the shape this keeps.
        /// </summary>
        public static BinaryNode Receipt(string to, IList<string> ids, string type, uint timestamp)
        {
            if (string.IsNullOrEmpty(to)) throw new ProtocolException("a receipt needs somebody to go to");
            if (ids == null || ids.Count == 0) throw new ProtocolException("a receipt answers at least one message");

            var receipt = new BinaryNode("receipt");
            receipt.Attrs.Add("id", ids[0]);
            receipt.Attrs.Add("to", to);
            receipt.Attrs.Add("type", type);

            if (type == "read" || type == "read-self") receipt.Attrs.Add("t", timestamp.ToString());

            if (ids.Count == 1) return receipt;

            var list = new BinaryNode("list");
            var answered = new List<BinaryNode>();
            for (int i = 1; i < ids.Count; i++)
            {
                var one = new BinaryNode("item");
                one.Attrs.Add("id", ids[i]);
                answered.Add(one);
            }

            list.Content = answered;
            receipt.Content = new List<BinaryNode> { list };

            return receipt;
        }

        /// <summary>
        /// The ciphertext for this device: the `enc` at the top of the stanza, or the one
        /// inside the `to` that names us.
        /// </summary>
        private static BinaryNode EncFor(BinaryNode message, Credentials credentials)
        {
            BinaryNode direct = message.Child("enc");
            if (direct != null) return direct;

            IList<BinaryNode> children = message.Children();
            if (children == null) return null;

            string mine = User(credentials == null ? null : credentials.Jid);

            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Tag != "to") continue;

                string jid = children[i].Attrs["jid"];
                if (mine != null && User(jid) != mine) continue;

                BinaryNode enc = children[i].Child("enc");
                if (enc != null) return enc;
            }

            return null;
        }

        /// <summary>
        /// The responder session the first message of a conversation starts: the wrapper
        /// names the signed pre-key and, when the sender had one, a one-time pre-key -
        /// which is spent here, so the same message cannot be opened twice with it.
        /// </summary>
        private static SessionRecord StartResponder(byte[] wire, string from, Credentials credentials,
            PreKeyStore preKeys, SessionStore sessions, IRandomSource random)
        {
            PreKeySignalMessage envelope = PreKeySignalMessage.Decode(SignalEnvelope.Unwrap(wire));

            byte[] signedPrivate = preKeys.SignedPreKeyPrivate;
            if (signedPrivate == null || envelope.SignedPreKeyId != preKeys.SignedPreKeyId)
            {
                throw new ProtocolException("the message from " + from + " names signed pre-key "
                    + envelope.SignedPreKeyId + " and this device holds " + preKeys.SignedPreKeyId);
            }

            byte[] oneTime = envelope.HasPreKey ? preKeys.Consume(envelope.PreKeyId) : null;

            SessionRecord started = SessionRecord.StartAsResponder(
                credentials.IdentityPrivateKey, signedPrivate, oneTime,
                credentials.RegistrationId, envelope, random);

            sessions.Save(from, started);

            return sessions.Load(from);
        }

        /// <summary>Whether a device-sent form names one of this account's own JIDs.</summary>
        private static bool IsMine(string jid, Credentials credentials)
        {
            if (string.IsNullOrEmpty(jid) || credentials == null) return false;

            return User(jid) == User(credentials.Jid) || User(jid) == User(credentials.Lid);
        }

        private static uint Seconds(string text)
        {
            uint seconds;
            if (string.IsNullOrEmpty(text) || !uint.TryParse(text, out seconds)) return 0;

            return seconds;
        }

        private static string User(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;

            int at = jid.IndexOf('@');
            string user = at < 0 ? jid : jid.Substring(0, at);

            int colon = user.IndexOf(':');
            return colon < 0 ? user : user.Substring(0, colon);
        }
    }

    /// <summary>
    /// The ids read in this run, oldest first, with a ceiling: a message the server
    /// re-delivers is dropped, and the memory it costs stays a few kilobytes.
    /// </summary>
    public sealed class SeenMessages
    {
        private readonly int _limit;
        private readonly List<string> _ids = new List<string>();

        public SeenMessages(int limit)
        {
            if (limit < 1) throw new ProtocolException("a set of seen ids holds at least one id");

            _limit = limit;
        }

        /// <summary>True the first time an id is seen, false every time after.</summary>
        public bool IsNew(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("an id is what tells one message from another");
            if (_ids.Contains(id)) return false;

            _ids.Add(id);
            if (_ids.Count > _limit) _ids.RemoveAt(0);

            return true;
        }
    }
}
