using System;
using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The read path, exercised the way a real one arrives: the sender builds a session from
    /// this device's published pre-keys, sends through `OutgoingMessage`, and the stanza is
    /// handed to `IncomingMessage.Read` with the `from` the server would have put on it.
    ///
    /// Nothing here is mocked in the middle. A test with a hand-built `enc` would pass with
    /// a read path that cannot open a real one.
    /// </summary>
    public static class IncomingMessageTests
    {
        public static readonly string OurDevice = "15559990000:3@s.whatsapp.net";
        public static readonly string OurUser = "15559990000@s.whatsapp.net";
        public static readonly string PeerDevice = "15551230000:5@s.whatsapp.net";
        public static readonly string PeerUser = "15551230000@s.whatsapp.net";

        [Test]
        public static void ATextFromAPeerIsReadBack()
        {
            var world = new World();
            Sent sent = world.PeerSends("Hello, WhatsApp!", "A1B2C3D4E5F60718");

            IncomingMessage incoming = IncomingMessage.Read(sent.Node, world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.IsTrue(incoming != null, "a message that arrived is read");
            Assert.IsTrue("Hello, WhatsApp!" == incoming.Text,
                "expected the words that were sent, and read <" + incoming.Text + ">");
            Assert.IsTrue(PeerDevice == incoming.From, "the sender is the device the stanza names");
            Assert.IsTrue("A1B2C3D4E5F60718" == incoming.Id, "the id is the stanza's own");
            Assert.IsFalse(incoming.IsOurEcho, "a peer's message is not our own copy");
            Assert.AreEqual((uint)1760000000, incoming.Timestamp);
        }

        /// <summary>
        /// The address it came from and the chat it belongs to are two different JIDs.
        ///
        /// A message written from a linked device arrives with the sender's device in `from`
        /// (`15551230000:5@s.whatsapp.net`), and the chat is the same person without it - the
        /// reference splits them exactly here (`whatsmeow`, `parseMessageSource`). A chat
        /// keyed on `From` gave that contact one chat row per device they write from.
        /// </summary>
        [Test]
        public static void TheChatIsTheSenderWithoutTheirDevice()
        {
            var world = new World();
            Sent sent = world.PeerSends("hello", "9988776655443322");

            IncomingMessage incoming = IncomingMessage.Read(sent.Node, world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.AreEqual(PeerDevice, incoming.From, "the session is still keyed on the device");
            Assert.AreEqual(PeerUser, incoming.Chat, "and the chat is not");
        }

        [Test]
        public static void TheDecodedMessageIsHandedToTheCallerAndNotOnlyItsWords()
        {
            var world = new World();
            Sent sent = world.PeerSends("Hello, WhatsApp!", "77AA88BB99CC0011");

            IncomingMessage incoming = IncomingMessage.Read(sent.Node, world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            // The words are not all a message carries: the history bootstrap arrives as a
            // protocol message inside an ordinary stanza, and a read path that threw the
            // decoded message away would leave the chat list unreachable.
            Assert.IsTrue(incoming.Content != null, "the caller gets the message, not only its text");
            Assert.IsTrue("Hello, WhatsApp!" == incoming.Content.Conversation,
                "and it is the message the peer wrote, inner form unwrapped: <"
                + incoming.Content.Conversation + ">");
            Assert.IsTrue(incoming.Content.ProtocolMessage == null,
                "an ordinary text carries no protocol message");
        }

        [Test]
        public static void TheFirstMessageFromANewPeerStartsTheSessionAndSpendsTheKey()
        {
            var world = new World();
            int before = world.PreKeys.Remaining;
            Sent sent = world.PeerSends("hello", "0011223344556677");

            Assert.IsTrue(world.Sessions.Load(PeerDevice) == null, "there is no session before the first message");

            IncomingMessage.Read(sent.Node, world.Us, world.Sessions, world.PreKeys,
                new SeenMessages(64), new SignalSessions.CounterRandom());

            SessionRecord stored = world.Sessions.Load(PeerDevice);
            Assert.IsTrue(stored != null, "the responder session is stored, so the next message opens with it");
            Assert.IsTrue(stored.HasSession);

            Assert.IsTrue(world.PreKeys.Remaining == before - 1,
                "the one-time pre-key the message named is spent, and "
                + world.PreKeys.Remaining + " of " + before + " are left");
            Assert.IsTrue(world.PreKeys.Consume(sent.OneTimePreKeyId) == null,
                "and it is not handed out a second time");
        }

        [Test]
        public static void TheSameIdTwiceIsOneMessage()
        {
            var world = new World();
            var seen = new SeenMessages(64);
            Sent sent = world.PeerSends("hello", "DECAFBAD12345678");

            IncomingMessage first = IncomingMessage.Read(sent.Node, world.Us, world.Sessions,
                world.PreKeys, seen, new SignalSessions.CounterRandom());

            Assert.IsTrue(first != null, "the first copy is the message");

            IncomingMessage second = IncomingMessage.Read(sent.Node, world.Us, world.Sessions,
                world.PreKeys, seen, new SignalSessions.CounterRandom());

            Assert.IsTrue(second == null, "the copy the server re-delivered is not a second message");
            Assert.IsFalse(seen.IsNew("DECAFBAD12345678"), "and the set of seen ids says so too");
        }

        [Test]
        public static void AMovedCiphertextByteIsANamedFailure()
        {
            var world = new World();
            Sent sent = world.PeerSends("hello", "0F0E0D0C0B0A0908");

            // The ciphertext is inside the `to` this stanza names, which is the shape the
            // server forwards and the shape this test therefore moves a byte in.
            byte[] enc = sent.Node.Child("to").Child("enc").Bytes();
            enc[enc.Length - 1] = (byte)(enc[enc.Length - 1] ^ 0x01);

            string failed = Failed(delegate
            {
                IncomingMessage.Read(sent.Node, world.Us, world.Sessions, world.PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());
            });

            Assert.IsTrue(failed != null, "a message whose ciphertext moved does not read");
            Assert.IsTrue(failed.Contains("0F0E0D0C0B0A0908"),
                "the refusal names the message it refused, and it said: " + failed);
        }

        [Test]
        public static void AStanzaThatIsNotAMessageIsRefusedByName()
        {
            var world = new World();

            var receipt = new BinaryNode("receipt");
            receipt.Attrs.Add("id", "A1B2");
            receipt.Attrs.Add("from", PeerDevice);

            string failed = Failed(delegate
            {
                IncomingMessage.Read(receipt, world.Us, world.Sessions, world.PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());
            });

            Assert.IsTrue(failed != null, "a receipt is not a message");
            Assert.IsTrue(failed.Contains("receipt"),
                "the refusal names what arrived, and it said: " + failed);
        }

        [Test]
        public static void TheAckNamesTheStanzaItAnswers()
        {
            var world = new World();
            Sent sent = world.PeerSends("hello", "1122334455667788");

            BinaryNode ack = IncomingMessage.Ack(sent.Node);

            Assert.AreEqual("ack", ack.Tag);
            Assert.AreEqual("1122334455667788", ack.Attrs["id"]);
            Assert.AreEqual(PeerDevice, ack.Attrs["to"]);
            Assert.AreEqual("message", ack.Attrs["class"]);
        }        /// <summary>
        /// A message that could not be opened is answered with a retry, not an ack.
        ///
        /// The two are not interchangeable: the ack tells the server the stanza arrived, and
        /// the server then stops sending it - so an ack on a failed open is the one line that
        /// makes a transient failure permanent, and it is how the initial history sync could
        /// be lost for good. The reference asks for the stanza again (`sendRetryReceipt`), and
        /// the shape is pinned here so a later tidy-up cannot turn it back into an ack.
        ///
        /// The `<registration>` child is the difference between "send it again" and "start
        /// over": the sending side compares it with the id inside the session it holds and
        /// throws that session away when the two disagree (`baileys`, `sendMessagesAgain`, `reg
        /// id mismatch on retry without bundle, deleting session`) - which is what a phone whose
        /// identity changed at pairing needs it to do.
        /// </summary>
        [Test]
        public static void AMessageThatDidNotReadIsAskedForAgain()
        {
            var world = new World();
            Sent sent = world.PeerSends("hello", "AABBCCDDEEFF0011");

            BinaryNode retry = IncomingMessage.RetryReceipt(sent.Node, 1760000000, 4321);

            Assert.AreEqual("receipt", retry.Tag);
            Assert.AreEqual("AABBCCDDEEFF0011", retry.Attrs["id"]);
            Assert.AreEqual(PeerDevice, retry.Attrs["to"]);
            Assert.AreEqual("retry", retry.Attrs["type"],
                "the receipt is a retry, not the ack that tells the server it was read");

            BinaryNode asked = retry.Child("retry");
            Assert.IsTrue(asked != null, "the retry travels as the receipt's child");

            Assert.AreEqual("AABBCCDDEEFF0011", asked.Attrs["id"]);
            Assert.AreEqual("1", asked.Attrs["count"]);
            Assert.AreEqual("1", asked.Attrs["v"]);
            Assert.AreEqual("1760000000", asked.Attrs["t"]);

            BinaryNode registration = retry.Child("registration");
            Assert.IsTrue(registration != null,
                "the retry carries this device's registration id, or the sender cannot tell that "
                + "the session it holds was built with an identity this phone no longer has");
            Assert.ByteArraysEqual(new byte[] { 0x00, 0x00, 0x10, 0xE1 }, registration.Bytes());
        }

        /// <summary>
        /// A pre-key message that starts a *new* session replaces the one on disk.
        ///
        /// This is the bug the run of 2026-10-18 logged: a pairing minted a new identity, the
        /// old sessions stayed, and because a session existed the peer's pre-key message was
        /// opened with it - so every message failed its MAC, and the message that would have
        /// rebuilt the session was the one that could not be read. The reference keeps the
        /// session per base key and replaces the open one for a key it has not answered
        /// (`libsignal`, `session_builder.initIncoming`).
        /// </summary>
        [Test]
        public static void AFreshPreKeyMessageReplacesTheSessionItFinds()
        {
            var world = new World();
            Sent first = world.PeerSends("primo", "1000000000000001");

            IncomingMessage.Read(first.Node, world.Us, world.Sessions, world.PreKeys,
                new SeenMessages(64), new SignalSessions.CounterRandom());

            SessionRecord before = world.Sessions.Load(PeerDevice);
            Assert.IsTrue(before != null, "the first message left a session behind");

            // The peer starts over with a new agreement - the base key is new, the identity
            // is the same, and the words travel in a pre-key message again.
            Sent second = world.PeerSends("secondo", "1000000000000002", true);

            IncomingMessage incoming = IncomingMessage.Read(second.Node, world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.IsTrue("secondo" == incoming.Text,
                "the words of a session that started over have to read, and it read <" + incoming.Text + ">");
            Assert.IsTrue(world.Sessions.Load(PeerDevice) != before,
                "and the record on disk is the new session, not the one it replaced");
        }

        /// <summary>
        /// A store whose signed pre-key has the account's **id** but another key cannot read,
        /// and adopting the account's key is what fixes it.
        ///
        /// This is the phone of 2026-10-18, in one test: signed pre-keys are numbered from one,
        /// so a store left by a previous pairing has the same id over a private key whose public
        /// half nobody has - and the responder path can only compare the id, because a pre-key
        /// wrapper carries nothing else. The agreement then runs on the wrong half, the root is
        /// not the sender's, and every message of the session fails `the message's MAC does not
        /// verify` - which is what the log said, for four senders and a hundred stanzas, with no
        /// line pointing at the store.
        /// </summary>
        [Test]
        public static void AStoreWithAnotherKeyUnderTheSameIdCannotReadUntilItIsAdopted()
        {
            var world = new World();
            Sent sent = world.PeerSends("ciao", "1000000000000004");

            // Precisely what survives from a previous identity: the account's own id, over
            // another key pair.
            world.PreKeys.SetSignedPreKey(world.Us.SignedPreKeyId,
                world.Other.SignedPreKeyPrivate, world.Other.SignedPreKeySignature);

            string failed = Failed(delegate { Read(world, sent); });
            Assert.IsTrue(failed != null && failed.Contains("MAC"),
                "the wrong half of the agreement is a MAC that does not verify, and it said: " + failed);

            Assert.IsTrue(PreKeyUpload.AdoptSignedPreKey(world.Us, world.PreKeys),
                "the account's own signed pre-key is the one that has to be in the store");

            IncomingMessage incoming = Read(world, sent);
            Assert.IsTrue("ciao" == incoming.Text,
                "and with it the same stanza reads, and read <" + incoming.Text + ">");
        }

        /// <summary>
        /// The record on disk remembers which agreement started it.
        ///
        /// That base key is the whole answer to "open this pre-key message with the session I
        /// have, or start a new one", so it has to survive `Encode`/`Decode`: a record that
        /// lost it would rebuild the session on every re-delivery - with a one-time pre-key
        /// that was spent the first time, so the rebuild is a refusal.
        /// </summary>
        [Test]
        public static void TheStoredSessionRemembersTheAgreementThatStartedIt()
        {
            var world = new World();
            Sent sent = world.PeerSends("ciao", "1000000000000003");

            IncomingMessage.Read(sent.Node, world.Us, world.Sessions, world.PreKeys,
                new SeenMessages(64), new SignalSessions.CounterRandom());

            BinaryNode enc = sent.Node.Child("to").Child("enc");
            Assert.AreEqual(OutgoingMessage.PreKeyType, enc.Attrs["type"],
                "the first message of a conversation is a pre-key message");

            PreKeySignalMessage wrapper = PreKeySignalMessage.Decode(SignalEnvelope.Unwrap(enc.Bytes()));
            SessionRecord stored = world.Sessions.Load(PeerDevice);

            Assert.IsTrue(stored.KnowsTheirBaseKey(wrapper.BaseKey),
                "the session read back from the store knows the agreement it was started by");
        }

        [Test]
        public static void AReadReceiptCarriesTheTimeAndAListIsAChild()
        {
            BinaryNode one = IncomingMessage.Receipt(PeerUser, new string[] { "AA" }, "read", 1760000000);
            Assert.AreEqual("receipt", one.Tag);
            Assert.AreEqual("AA", one.Attrs["id"]);
            Assert.AreEqual("read", one.Attrs["type"]);
            Assert.AreEqual("1760000000", one.Attrs["t"]);
            Assert.AreEqual(PeerUser, one.Attrs["to"]);

            BinaryNode many = IncomingMessage.Receipt(PeerUser, new string[] { "AA", "BB" }, "delivery", 0);
            Assert.IsTrue(many.Attrs["t"] == null, "only a read receipt carries a time");
            Assert.AreEqual("BB", many.Child("list").Children()[0].Attrs["id"]);
        }

        [Test]
        public static void OurOwnCopyIsNotAnIncomingMessage()
        {
            var world = new World();

            // Our other device sends to this one: the stanza's `to` is this device, and the
            // device-sent form inside names the account's own JID.
            Sent sent = world.SomeoneSends(OurUser, "15559990000:7@s.whatsapp.net",
                "written on the other phone", "99AABBCCDDEEFF00");

            IncomingMessage incoming = IncomingMessage.Read(sent.Node, world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.IsTrue(incoming != null, "our own copy is still readable");
            Assert.IsTrue(incoming.IsOurEcho, "a copy of our own message is recognised as ours");
            Assert.IsTrue("written on the other phone" == incoming.Text,
                "and its words come from inside the device-sent form");
        }

        /// <summary>
        /// A history sync is this account's own, or it is not applied at all.
        ///
        /// The sync is a peer-routed self stanza: it arrives with `from` set to one of this
        /// account's own devices, which is exactly the check the reference makes before it reads
        /// one (`historySyncNotification` is a self-only protocol message, and both references
        /// drop it when the stanza is not ours). `IncomingMessage.IsMine` is that check, and it
        /// has to hold for the number, for the LID, and with the device part on or off.
        /// </summary>
        [Test]
        public static void AHistorySyncIsOnlyThisAccountsOwn()
        {
            var world = new World();
            world.Us.Lid = "15559990000@lid";

            Assert.IsTrue(IncomingMessage.IsMine(OurDevice, world.Us), "our own device is ours");
            Assert.IsTrue(IncomingMessage.IsMine(OurUser, world.Us), "and the account itself");
            Assert.IsTrue(IncomingMessage.IsMine("15559990000:9@s.whatsapp.net", world.Us),
                "another device of this account is still ours: the sync comes from the phone");
            Assert.IsTrue(IncomingMessage.IsMine("15559990000:4@lid", world.Us),
                "the same over the LID, which is how a linked device is named");
            Assert.IsFalse(IncomingMessage.IsMine(PeerDevice, world.Us),
                "a contact's stanza must not write this phone's chat list");
            Assert.IsFalse(IncomingMessage.IsMine(null, world.Us), "and nothing is nobody");
        }

        /// <summary>
        /// The world one conversation needs: this device with its published pre-keys, and
        /// somebody on the other end who builds a session from them.
        /// </summary>
        private sealed class World
        {
            public readonly Credentials Us;
            public readonly Credentials Other;
            public readonly PreKeyStore PreKeys = new PreKeyStore(new Memory());
            public readonly SessionStore Sessions = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            public readonly SessionStore OtherSessions = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            private readonly IList<BinaryNode> _oneTimeKeys;
            private readonly IRandomSource _random = new SignalSessions.CounterRandom();

            /// <summary>
            /// The peer's source, kept across messages: a fresh one per send would draw the same
            /// bytes every time and hand the second agreement the first one's base key, which is
            /// exactly the thing a re-negotiation has to differ in.
            /// </summary>
            private readonly IRandomSource _peerRandom = new SignalSessions.CounterRandom();
            private int _nextKey;

            public World()
            {
                // One source for both, so the two identities are two: a fresh `CounterRandom`
                // per account hands out the same bytes twice, and an "other identity" whose
                // keys are this device's would make every stale-key test pass for the wrong
                // reason.
                Us = Credentials.Create(_random);
                Other = Credentials.Create(_random);

                Us.Jid = OurDevice;

                BinaryNode upload = PreKeyUpload.Node(Us, PreKeys, new SignalSessions.CounterRandom(), 5);
                _oneTimeKeys = upload.Child("list").Children();
            }

            /// <summary>The one-time pre-key the server hands out for the next agreement.</summary>
            private uint OneTimePreKeyId
            {
                get { return Number(_oneTimeKeys[_nextKey].Child("id").Bytes()); }
            }

            private byte[] OneTimePreKeyPublic
            {
                get { return _oneTimeKeys[_nextKey].Child("value").Bytes(); }
            }

            /// <summary>What the other phone sends to this one.</summary>
            public Sent PeerSends(string body, string id)
            {
                return SomeoneSends(OurDevice, PeerDevice, body, id);
            }

            /// <summary>
            /// The same, but a *new* agreement: the peer throws its session away first, which is
            /// what a peer does after a retry receipt it could not satisfy.
            /// </summary>
            public Sent PeerSends(string body, string id, bool newSession)
            {
                if (newSession)
                {
                    OtherSessions.Remove(OurDevice);

                    // And it asks the server for a bundle again, which hands out the next key.
                    _nextKey++;
                }

                return SomeoneSends(OurDevice, PeerDevice, body, id);
            }

            /// <summary>The same, with the JIDs chosen: an echo test needs our own on both ends.</summary>
            public Sent SomeoneSends(string to, string from, string body, string id)
            {
                PreKeyBundle ours = DeviceDirectory.Bundles(OurBundleAnswer())[0].Value;

                SessionRecord theirSide = DeviceDirectory.Session(to, ours, Other,
                    OtherSessions, _peerRandom);

                var devices = new List<KeyValuePair<string, SessionRecord>>();
                devices.Add(new KeyValuePair<string, SessionRecord>(to, theirSide));

                BinaryNode node = OutgoingMessage.Node(to, id, body, devices, 1760000000, _random);
                node.Attrs.Add("from", from);

                return new Sent(node, OneTimePreKeyId);
            }

            /// <summary>This device's bundle answer, built out of the keys it just published.</summary>
            private BinaryNode OurBundleAnswer()
            {
                var registration = new BinaryNode("registration");
                registration.Content = BigEndian(Us.RegistrationId, 4);

                var identity = new BinaryNode("identity");
                identity.Content = X3dh.Typed(Us.IdentityPublicKey);

                var signedPreKey = new BinaryNode("skey");
                var signedPreKeyId = new BinaryNode("id");
                signedPreKeyId.Content = BigEndian(Us.SignedPreKeyId, 3);
                var signedPreKeyValue = new BinaryNode("value");
                signedPreKeyValue.Content = X3dh.Typed(Us.SignedPreKeyPublic);
                var signature = new BinaryNode("signature");
                signature.Content = Us.SignedPreKeySignature;
                signedPreKey.Content = new List<BinaryNode> { signedPreKeyId, signedPreKeyValue, signature };

                var oneTime = new BinaryNode("key");
                var oneTimeId = new BinaryNode("id");
                oneTimeId.Content = BigEndian(OneTimePreKeyId, 3);
                var oneTimeValue = new BinaryNode("value");
                oneTimeValue.Content = OneTimePreKeyPublic;
                oneTime.Content = new List<BinaryNode> { oneTimeId, oneTimeValue };

                var user = new BinaryNode("user");
                user.Attrs.Add("jid", OurDevice);
                user.Content = new List<BinaryNode> { registration, identity, signedPreKey, oneTime };

                var list = new BinaryNode("list");
                list.Content = new List<BinaryNode> { user };

                var iq = new BinaryNode("iq");
                iq.Content = new List<BinaryNode> { list };
                return iq;
            }

            private static byte[] BigEndian(uint value, int bytes)
            {
                var result = new byte[bytes];
                for (int i = 0; i < bytes; i++) result[bytes - 1 - i] = (byte)(value >> (8 * i));

                return result;
            }
        }

        /// <summary>A stanza on its way in, and the pre-key it named.</summary>
        private struct Sent
        {
            public readonly BinaryNode Node;
            public readonly uint OneTimePreKeyId;

            public Sent(BinaryNode node, uint oneTimePreKeyId)
            {
                Node = node;
                OneTimePreKeyId = oneTimePreKeyId;
            }
        }

        /// <summary>One read with this test's stores and a fresh set of seen ids.</summary>
        private static IncomingMessage Read(World world, Sent sent)
        {
            return IncomingMessage.Read(sent.Node, world.Us, world.Sessions, world.PreKeys,
                new SeenMessages(64), new SignalSessions.CounterRandom());
        }

        private static uint Number(byte[] bigEndian)
        {
            uint value = 0;
            for (int i = 0; i < bigEndian.Length; i++) value = (value << 8) | bigEndian[i];

            return value;
        }

        private static string Failed(Action action)
        {
            try
            {
                action();
            }
            catch (ProtocolException refused)
            {
                return refused.Message;
            }

            return null;
        }

        private sealed class Memory : ISessionStorage
        {
            private readonly Dictionary<string, byte[]> _blobs = new Dictionary<string, byte[]>();

            public bool TryRead(string key, out byte[] value)
            {
                return _blobs.TryGetValue(key, out value);
            }

            public void Write(string key, byte[] value)
            {
                _blobs[key] = value;
            }

            public void Remove(string key)
            {
                _blobs.Remove(key);
            }
        }
    }
}
