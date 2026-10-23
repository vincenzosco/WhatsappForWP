using System;
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

        /// <summary>
        /// Who it came from, as the stanza spells it: the device JID of the sender, or the
        /// **group** when the message is a group's. A group message's `from` is the group
        /// and the sender is a `participant` attribute, which is why both are here.
        ///
        /// It keeps the device on purpose - that is what a Signal session is stored under -
        /// so it is *not* the chat (`Chat` is).
        /// </summary>
        public string From;

        /// <summary>
        /// The chat this message belongs to: `From` without the device, which is what the
        /// chat list, the message rows and the status feed key on.
        ///
        /// The reference makes the same split (`whatsmeow`, `parseMessageSource`:
        /// `source.Chat = from.ToNonAD()`, `source.Sender = from`), and it is what keeps a
        /// contact who writes from a linked device from getting a second chat row named after
        /// that device. For a group or a status it is the list's own JID, which has no
        /// device to drop.
        /// </summary>
        public string Chat;

        /// <summary>
        /// The member who wrote it, for a group message; null for a 1:1 one, where `From`
        /// already names them.
        /// </summary>
        public string Sender;

        /// <summary>The words, or null when this message is not words this client shows.</summary>
        public string Text;

        /// <summary>The stanza's `t` in seconds.</summary>
        public uint Timestamp;

        /// <summary>
        /// The name the sender goes by on WhatsApp (`notify`), or null when the stanza does
        /// not carry one.
        ///
        /// It is not decoration and it is not the address book: it is what the person chose
        /// themselves, and without it a row for somebody who has written once still shows a
        /// phone number. A value made only of spaces or invisible fillers is not a name, so it
        /// is answered as null here rather than at every reader.
        /// </summary>
        public string PushName;

        /// <summary>
        /// The phone-number JID behind a `@lid` sender (`sender_pn` on a one-to-one message,
        /// `participant_pn` in a group), or null when the stanza does not say.
        ///
        /// Why the stanza and not a query: WhatsApp migrated chats to LIDs, and every message
        /// already carries the real number beside it. A client that ignores this attribute can
        /// only print the LID, which is not a number anybody can dial.
        /// </summary>
        public string PhoneNumber;

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

            // The id is remembered only once the stanza has been read. A delivery that
            // failed to open must not look like a duplicate when the server sends it
            // again - it re-delivers what nobody acknowledged - or one transient failure
            // would hide the message for the rest of the run, and from the log that is
            // supposed to say what happened.
            IncomingMessage incoming;
            try
            {
                incoming = ReadStanza(message, credentials, sessions, preKeys, random, id, from);
            }
            catch (Exception)
            {
                if (seen != null) seen.Forget(id);
                throw;
            }

            incoming.PushName = NameBook.Clean(message.Attrs["notify"]);
            incoming.PhoneNumber = Jid(message.Attrs["sender_pn"]);
            if (incoming.PhoneNumber == null) incoming.PhoneNumber = Jid(message.Attrs["participant_pn"]);

            return incoming;
        }

        /// <summary>The stanza's phone-number JID, or null when it names none.</summary>
        private static string Jid(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            return value.IndexOf("@lid", StringComparison.OrdinalIgnoreCase) >= 0 ? null : value;
        }

        /// <summary>
        /// The stanza after its identity and its id are established, and the one place the
        /// session source is chosen.
        ///
        /// `from` is the address the stanza carries. For a status that is the **broadcast
        /// list**, while the ciphertext was written by the person who posted it: the session
        /// is theirs, not the list's. Reading it with the list's name is what the device log
        /// of 2026-10-17 shows - `the message's identity key is not the session's peer` on
        /// one status, then a plaintext that is not even a protobuf on the next.
        /// </summary>
        private static IncomingMessage ReadStanza(BinaryNode message, Credentials credentials,
            SessionStore sessions, PreKeyStore preKeys, IRandomSource random, string id, string from)
        {
            string participant = message.Attrs["participant"];
            string source = StatusFeed.IsStatus(from) && !string.IsNullOrEmpty(participant)
                ? participant
                : from;

            // A group's message: one `skmsg` for everybody, and - while a member's key is
            // new to this phone - also one pairwise copy of that member's sender key. The
            // group ciphertext is the message; where it came from for the session's purposes
            // is the participant, not the group.
            if (GroupDirectory.IsGroup(from))
            {
                return ReadGroup(message, credentials, sessions, preKeys, random, id, from);
            }

            BinaryNode enc = EncFor(message, credentials);
            if (enc == null) throw new ProtocolException("no enc for this device in message " + id);

            bool preKey = enc.Attrs["type"] == OutgoingMessage.PreKeyType;
            byte[] wire = enc.Bytes();
            if (wire == null || wire.Length == 0) throw new ProtocolException("message " + id + " carries no ciphertext");

            byte[] plaintext;
            uint usedPreKeyId = 0;
            try
            {
                SessionRecord session = SessionFor(wire, preKey, source, credentials, sessions, preKeys, random, out usedPreKeyId);

                plaintext = session.Decrypt(wire, preKey);

                // The ratchet moved: what the next message needs is the session as it is now.
                sessions.Save(source, session);

                // And only now is the key spent: the message has opened with it, so a
                // retry of anything that failed before this point still finds it.
                if (usedPreKeyId != 0) preKeys.Consume(usedPreKeyId);
            }
            catch (ProtocolException failed)
            {
                // One sentence for everything that can go wrong in there, and it names the
                // message: a ciphertext with a byte moved corrupts the wrapper as often as
                // the message, and "the signed pre-key does not match" with no id attached
                // is a search, not a report.
                throw new ProtocolException("message " + id + " from " + source + " did not read: " + failed.Message);
            }

            // The plaintext is a protobuf with the WhatsApp padding behind it, not the bare
            // message: the reference strips it for every message it opens
            // (`decode-wa-message`, `unpadRandomMax16`), and reading it as bytes was the
            // `a protobuf field announces 12095 bytes with 52 left` on a status this phone had
            // decrypted correctly.
            Message read = Message.DecodePadded(plaintext);
            var incoming = new IncomingMessage();
            incoming.Id = id;
            incoming.From = from;
            incoming.Chat = JidUtils.Normalize(from);
            incoming.Timestamp = Seconds(message.Attrs["t"]);

            // A status arrives exactly like a group message: the stanza's `from` names the
            // list (`status@broadcast`) and the author is the `participant`. Without this
            // the feed would credit every status to "status@broadcast" and the page would
            // show one anonymous row per update (`StatusFeed.Post`).
            if (StatusFeed.IsStatus(from)) incoming.Sender = message.Attrs["participant"];

            if (read.DeviceSentMessage == null)
            {
                incoming.Content = read;
                incoming.Text = read.Body();
                return incoming;
            }

            incoming.IsOurEcho = IsMine(source, credentials);
            incoming.Content = Message.Inner(read.DeviceSentMessage);
            incoming.Text = incoming.Content.Body();

            return incoming;
        }

        /// <summary>
        /// A message in a group.
        ///
        /// Two encs can be in the stanza and the order they are read in is the whole thing:
        ///
        /// - the **pairwise** one (`pkmsg`/`msg`, at the top when the server narrowed it down
        ///   to this device and inside `to[jid]` otherwise) is the sender's **sender key**, if
        ///   this is the first message since they made one. It is decrypted first because the
        ///   group ciphertext cannot be opened without the key it carries;
        /// - the **`skmsg`** is the message itself, encrypted once for the whole group.
        ///
        /// What is read out of it is the same as any other message - the words, the id, the
        /// time - plus the participant, because the group's chat row has to say who wrote it.
        /// A stanza that carries nothing but a sender key has no words and no media: it is a
        /// protocol part, and the caller sees a message with a null `Text` and a `Content`
        /// that says so (`IncomingMessage.Content.SenderKeyDistributionMessage`).
        /// </summary>
        private static IncomingMessage ReadGroup(BinaryNode message, Credentials credentials,
            SessionStore sessions, PreKeyStore preKeys, IRandomSource random,
            string id, string groupJid)
        {
            string participant = message.Attrs["participant"];
            if (string.IsNullOrEmpty(participant))
            {
                throw new ProtocolException("the group message " + id + " names no participant");
            }

            BinaryNode distribution = PairwiseEnc(message);
            if (distribution != null) TakeSenderKey(distribution, participant, groupJid, credentials, preKeys, sessions, random);

            BinaryNode cipher = CipherEnc(message);
            if (cipher == null)
            {
                // A stanza that only brought a sender key: there is a message to hand back
                // (the key is stored) but there is nothing to show, and saying so is the
                // caller's job.
                var keyOnly = new IncomingMessage();
                keyOnly.Id = id;
                keyOnly.From = groupJid;
                keyOnly.Chat = groupJid;
                keyOnly.Sender = participant;
                keyOnly.Timestamp = Seconds(message.Attrs["t"]);

                return keyOnly;
            }

            byte[] wire = cipher.Bytes();
            if (wire == null || wire.Length == 0) throw new ProtocolException("message " + id + " carries no ciphertext");

            string name = groupJid + "/" + participant;
            Message read;

            try
            {
                SenderKeyRecord record = sessions.LoadSenderKey(name);
                if (record == null)
                {
                    throw new ProtocolException("this phone holds no sender key for " + participant
                        + " in " + groupJid + ": the message that carries one has not arrived");
                }

                read = SenderKeys.DecodePadded(record.Decrypt(groupJid, SenderKeyMessage.Parse(wire)));

                // The chain moved: what the next message from them needs is the record as it
                // is now, and an iteration that is not stored is an iteration that is lost.
                sessions.SaveSenderKey(name, record);
            }
            catch (ProtocolException failed)
            {
                throw new ProtocolException("message " + id + " in " + groupJid + " from " + participant
                    + " did not read: " + failed.Message);
            }

            var incoming = new IncomingMessage();
            incoming.Id = id;
            incoming.From = groupJid;
            incoming.Chat = groupJid;
            incoming.Sender = participant;
            incoming.Timestamp = Seconds(message.Attrs["t"]);
            incoming.IsOurEcho = IsMine(participant, credentials);

            if (read.DeviceSentMessage == null)
            {
                incoming.Content = read;
                incoming.Text = read.Body();

                return incoming;
            }

            incoming.Content = Message.Inner(read.DeviceSentMessage);
            incoming.Text = incoming.Content.Body();

            return incoming;
        }

        /// <summary>
        /// The sender key a member just handed this phone, decrypted over the session this
        /// device already has with them and stored under the group and their device.
        ///
        /// A pairwise `enc` that turns out to carry something else is left alone: a member
        /// writing to this phone in the same stanza as a group message is unusual but not an
        /// error, and refusing the stanza over it would lose the group message.
        /// </summary>
        private static void TakeSenderKey(BinaryNode enc, string participant, string groupJid,
            Credentials credentials, PreKeyStore preKeys, SessionStore sessions, IRandomSource random)
        {
            bool preKey = enc.Attrs["type"] == OutgoingMessage.PreKeyType;
            byte[] wire = enc.Bytes();
            if (wire == null || wire.Length == 0) return;

            Message read;

            try
            {
                uint usedPreKeyId = 0;
                SessionRecord session = SessionFor(wire, preKey, participant, credentials, sessions, preKeys, random, out usedPreKeyId);

                read = Message.DecodePadded(session.Decrypt(wire, preKey));
                sessions.Save(participant, session);
                if (usedPreKeyId != 0) preKeys.Consume(usedPreKeyId);
            }
            catch (ProtocolException)
            {
                // Nothing that came with a group message is worth losing the message for.
                return;
            }

            byte[] field = read.SenderKeyDistributionMessage;
            if (field == null) return;

            string group = SenderKeys.GroupIdOfField(field);
            if (!string.IsNullOrEmpty(group) && group != groupJid)
            {
                // The key was minted for another group: using it here would encrypt (or
                // decrypt) with the wrong chain, so it is refused where it is named.
                throw new ProtocolException("a sender key for " + group + " arrived in " + groupJid);
            }

            byte[] distribution = SenderKeys.DistributionOfField(field);
            if (distribution == null) return;

            sessions.SaveSenderKey(groupJid + "/" + participant,
                SenderKeyRecord.StartAsReceiver(groupJid, distribution));
        }

        /// <summary>
        /// The pairwise ciphertext of a group stanza: a top-level `enc` that is not an
        /// `skmsg`, a `to[jid]` child, or a `to[jid]` **inside a `participants` node**.
        ///
        /// All three shapes exist in the wild and both are the reference's: the newest clients
        /// wrap the per-device nodes in `participants` (`whatsmeow`, `send.go`,
        /// `sendGroupMessage`), older ones put them straight under the stanza
        /// (`Socket/messages-send.js`, where `binaryNodeContent` holds the `to` nodes and then
        /// the `skmsg`). A reader that knew only one of them would read every group message
        /// except the ones that carry a new member's key - which is exactly the bug this test
        /// caught.
        /// </summary>
        private static BinaryNode PairwiseEnc(BinaryNode message)
        {
            BinaryNode direct = message.Child("enc");
            if (direct != null && direct.Attrs["type"] != SenderKeys.CipherType) return direct;

            BinaryNode found = DeviceEnc(message.Children());
            if (found != null) return found;

            BinaryNode participants = message.Child("participants");

            return participants == null ? null : DeviceEnc(participants.Children());
        }

        /// <summary>The first `to[jid]` in a list, whatever the OS/device order is.</summary>
        private static BinaryNode DeviceEnc(IList<BinaryNode> children)
        {
            if (children == null) return null;

            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Tag != "to") continue;

                BinaryNode enc = children[i].Child("enc");
                if (enc != null) return enc;
            }

            return null;
        }

        /// <summary>The group ciphertext of the stanza: the `enc` whose type is `skmsg`.</summary>
        private static BinaryNode CipherEnc(BinaryNode message)
        {
            BinaryNode direct = message.Child("enc");
            if (direct != null && direct.Attrs["type"] == SenderKeys.CipherType) return direct;

            IList<BinaryNode> children = message.Children();
            if (children == null) return null;

            for (int i = 0; i < children.Count; i++)
            {
                BinaryNode enc = children[i].Tag == "enc" ? children[i] : null;
                if (enc != null && enc.Attrs["type"] == SenderKeys.CipherType) return enc;
            }

            return null;
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
        /// The answer to a stanza this device could not open: a `retry` receipt, which asks
        /// the sender to encrypt it again - with a fresh session, when that is what was
        /// missing - instead of the plain `ack` that tells the server the stanza was read.
        ///
        /// Why it matters: the acknowledgement is what stops the server re-sending, so
        /// acknowledging a message whose open threw turned one transient failure into a
        /// permanent loss. When the stanza was the history sync, the chat list never came
        /// back (the reference sends a retry receipt for exactly this: `whatsmeow`,
        /// `sendRetryReceipt`). The count is one because this client does not track the
        /// retries of a single id; the sender is the one that does.
        /// </summary>
        public static BinaryNode RetryReceipt(BinaryNode received, uint timestamp, uint registrationId)
        {
            if (received == null) throw new ProtocolException("there is nothing to ask again for");

            string id = received.Attrs["id"];
            string from = received.Attrs["from"];
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(from))
            {
                throw new ProtocolException("a retry receipt needs the message's id and its sender");
            }

            var retry = new BinaryNode("retry");
            retry.Attrs.Add("count", "1");
            retry.Attrs.Add("id", id);
            retry.Attrs.Add("t", timestamp.ToString());
            retry.Attrs.Add("v", "1");

            var receipt = new BinaryNode("receipt");
            receipt.Attrs.Add("id", id);
            receipt.Attrs.Add("to", from);
            receipt.Attrs.Add("type", "retry");

            string participant = received.Attrs["participant"];
            if (!string.IsNullOrEmpty(participant)) receipt.Attrs.Add("participant", participant);

            // The registration id is what makes the sender look at its own session: the
            // reference ships it in this node (`messages-recv`, `sendRetryRequest`) and the
            // sending side compares it with the id inside the session it holds - a mismatch is
            // "this device is not the one I was talking to" and the session is thrown away in
            // favour of a fresh pre-key message (`sendMessagesAgain`, `reg id mismatch on retry
            // without bundle, deleting session`). Without it a retry is only "send it again",
            // which for a session built against an identity this phone no longer has is the same
            // unreadable message over and over.
            var registration = new BinaryNode("registration");
            registration.Content = BigEndian(registrationId, 4);

            receipt.Content = new List<BinaryNode> { retry, registration };

            return receipt;
        }

        /// <summary>Four bytes, most significant first: the shape the reference writes a registration id in.</summary>
        private static byte[] BigEndian(uint value, int bytes)
        {
            var result = new byte[bytes];
            for (int i = 0; i < bytes; i++) result[bytes - 1 - i] = (byte)(value >> (8 * i));

            return result;
        }

        /// <summary>
        /// A receipt: `read` is what tells the other phone its ticks turn blue, and it
        /// carries the time it happened. The reference sends the first id as the receipt's
        /// own id and the rest in children, which is the shape this keeps.
        /// </summary>
        public static BinaryNode Receipt(string to, IList<string> ids, string type, uint timestamp)
        {
            return Receipt(to, ids, type, timestamp, null);
        }

        /// <summary>
        /// The same, with the `participant` a broadcast list needs: a status is not a chat,
        /// its message lives in `status@broadcast` and the person who posted it is an
        /// attribute, exactly as it is on the stanza that brought it (`IncomingMessage.Read`
        /// reads `participant` for the same reason). Without it the receipt names a status
        /// the list cannot place, and the author's ring does not fill.
        /// </summary>
        public static BinaryNode Receipt(string to, IList<string> ids, string type, uint timestamp, string participant)
        {
            if (string.IsNullOrEmpty(to)) throw new ProtocolException("a receipt needs somebody to go to");
            if (ids == null || ids.Count == 0) throw new ProtocolException("a receipt answers at least one message");

            var receipt = new BinaryNode("receipt");
            receipt.Attrs.Add("id", ids[0]);
            receipt.Attrs.Add("to", to);
            receipt.Attrs.Add("type", type);

            if (type == "read" || type == "read-self") receipt.Attrs.Add("t", timestamp.ToString());

            if (!string.IsNullOrEmpty(participant)) receipt.Attrs.Add("participant", participant);

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
        /// The session a stanza is opened with - and the one decision that decides whether a
        /// message can ever be read again.
        ///
        /// A pre-key message is not only how a conversation starts: it is how it *restarts*. A
        /// sender wraps one whenever it has no session it trusts (a re-pair here, a reset there,
        /// a retry receipt it answered by rebuilding), and the wrapper carries the base key of
        /// the agreement it ran. The reference keeps the sessions it has answered by that key
        /// (`libsignal`, `session_builder.initIncoming`: it returns early when
        /// `record.getSession(message.baseKey)` finds one, and otherwise closes the open session
        /// and sets the new one), so the question is never "is there a session" but "is there a
        /// session for *this* key".
        ///
        /// Reading it as "is there a session" is what the phone showed on 2026-10-18: a pairing
        /// minted a new identity, every stored session had been built with the old one, and the
        /// pre-key message that would have rebuilt them was opened with the stale record - so
        /// every message from every peer, the account's own phone included, failed with "the
        /// message's MAC does not verify", and no retry could ever succeed.
        ///
        /// A base key already answered is the re-delivery case, and it must keep the stored
        /// session: the one-time pre-key that started it has been spent, and re-running the
        /// agreement would be refused - correctly (`StartResponder`).
        /// </summary>
        private static SessionRecord SessionFor(byte[] wire, bool preKey, string from,
            Credentials credentials, SessionStore sessions, PreKeyStore preKeys,
            IRandomSource random, out uint usedPreKeyId)
        {
            usedPreKeyId = 0;

            SessionRecord stored = sessions.Load(from);

            if (!preKey)
            {
                if (stored == null || !stored.HasSession)
                {
                    throw new ProtocolException("it is not a pre-key message and there is no session with " + from);
                }

                return stored;
            }

            PreKeySignalMessage envelope = PreKeySignalMessage.Decode(SignalEnvelope.Unwrap(wire));
            if (stored != null && stored.HasSession && stored.KnowsTheirBaseKey(envelope.BaseKey))
            {
                return stored;
            }

            // A key this record has not answered: the peer started over, and the session it
            // started replaces the one on disk (the caller saves it once the message opens).
            return StartResponder(envelope, from, credentials, preKeys, random, out usedPreKeyId);
        }

        /// <summary>
        /// The responder session the first message of a conversation starts.
        ///
        /// The wrapper names the signed pre-key and, when the sender had one, a one-time
        /// pre-key. Both are looked up and **not spent**: a name is not authentication, and
        /// spending the key before the ciphertext has opened deletes the only key a retry
        /// could use. The caller spends it after the message has decrypted and the session
        /// has been saved (`usedPreKeyId` is that id, zero when none was named).
        ///
        /// A named pre-key this device does not hold is a refusal, never a session: X3DH
        /// without it is a different agreement - four Diffie-Hellmans instead of five - and
        /// the peer that computed five would fail the MAC on every message of the session.
        /// That is exactly what the phone logged on 2026-10-17, and the session was saved
        /// before anybody noticed.
        /// </summary>
        private static SessionRecord StartResponder(PreKeySignalMessage envelope, string from, Credentials credentials,
            PreKeyStore preKeys, IRandomSource random, out uint usedPreKeyId)
        {
            byte[] signedPrivate = preKeys.SignedPreKeyPrivate;
            if (signedPrivate == null || envelope.SignedPreKeyId != preKeys.SignedPreKeyId)
            {
                throw new ProtocolException("the message from " + from + " names signed pre-key "
                    + envelope.SignedPreKeyId + " and this device holds " + preKeys.SignedPreKeyId);
            }

            byte[] oneTime = null;
            if (envelope.HasPreKey)
            {
                oneTime = preKeys.Find(envelope.PreKeyId);
                if (oneTime == null)
                {
                    throw new ProtocolException("the message from " + from + " names one-time pre-key "
                        + envelope.PreKeyId + " and this device no longer holds it: the agreement "
                        + "it was part of cannot be repeated");
                }
            }

            usedPreKeyId = envelope.HasPreKey ? envelope.PreKeyId : 0;

            // Not saved here: a session is worth keeping only once it has opened the message
            // that created it. The caller saves it after `Decrypt`, which is the difference
            // between one unreadable delivery and a peer this phone can never read again.
            return SessionRecord.StartAsResponder(
                credentials.IdentityPrivateKey, signedPrivate, oneTime,
                credentials.RegistrationId, envelope, random);
        }

        /// <summary>
        /// Whether a JID is one of this account's own - the number or the LID, any of its
        /// devices, the device part dropped.
        ///
        /// Public because two callers ask it about a stanza and not only about a device-sent
        /// form: a message from another of this account's devices is our own copy and is not
        /// shown (that is the `IsOurEcho` above), and a **history sync** is applied only when it
        /// comes from here. The second one is the reference's own rule and it is not a detail:
        /// `historySyncNotification` is a self-only protocol message, and both references drop
        /// it when the stanza is not ours - "an attacker could otherwise spoof any of these to
        /// manipulate local state" (`Utils/process-message.js`, mirroring `whatsmeow`'s
        /// `handleProtocolMessage`). Applied from anybody, the same stanza lets a contact write
        /// this phone's chat list, its call log and its status feed.
        /// </summary>
        public static bool IsMine(string jid, Credentials credentials)
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

        /// <summary>
        /// Forgets an id. Called when the stanza it belongs to could not be read: seen is
        /// what stops a message being shown twice, and a message that was never shown must
        /// not be remembered as one.
        /// </summary>
        public void Forget(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            _ids.Remove(id);
        }
    }
}
