using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// A text on its way out, and the only thing that proves it is right: the device it was
    /// encrypted for can read it back.
    ///
    /// The peer's side is rebuilt here the way the real one would - a pre-key message opens
    /// a responder session, the sealed message inside it is opened, and the device-sent
    /// form inside that names its destination and carries the words.
    /// </summary>
    public static class OutgoingMessageTests
    {
        /// <summary>The source for everything a send draws: a ratchet key and the padding's bytes.</summary>
        private static readonly SignalSessions.CounterRandom Random = new SignalSessions.CounterRandom();

        /// <summary>This account's number, and a tablet of it.</summary>
        private const string OurPhone = "15559990000@s.whatsapp.net";
        private const string OurTablet = "15559990000:9@s.whatsapp.net";

        [Test]
        public static void TheNodeCarriesOneEncPerDevice()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            BinaryNode node = OutgoingMessage.Node(
                PeerBundleFixture.Peer,
                "0123456789ABCDEF",
                "Hello, WhatsApp!",
                TwoSessions(us, peer, store),
                1760000000, Random);

            Assert.AreEqual("message", node.Tag);
            Assert.AreEqual(PeerBundleFixture.Peer, node.Attrs["to"]);
            Assert.AreEqual("text", node.Attrs["type"]);
            Assert.AreEqual("0123456789ABCDEF", node.Attrs["id"]);
            Assert.AreEqual("1760000000", node.Attrs["t"]);

            // The per-device nodes sit in `participants`, as both reference clients write a
            // direct stanza (`Socket/messages-send.js`, `relayMessage`; `whatsmeow`, `send.go`).
            Assert.AreEqual(1, node.Children().Count, "the stanza carries one node: the participants");

            IList<BinaryNode> devices = node.Child("participants").Children();
            Assert.AreEqual(2, devices.Count);
            Assert.AreEqual(PeerBundleFixture.PeerPhone, devices[0].Attrs["jid"]);
            Assert.AreEqual(PeerBundleFixture.PeerTablet, devices[1].Attrs["jid"]);

            for (int i = 0; i < devices.Count; i++)
            {
                BinaryNode enc = devices[i].Child("enc");
                Assert.AreEqual(OutgoingMessage.KeyVersion, enc.Attrs["v"]);
                Assert.AreEqual(OutgoingMessage.PreKeyType, enc.Attrs["type"]);
                Assert.IsTrue(enc.Bytes().Length > 0, "an enc with no ciphertext in it is not a message");
            }
        }

        [Test]
        public static void TheRecipientReadsTheMessageItselfAndNotTheSendersCopy()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            BinaryNode node = OutgoingMessage.Node(
                PeerBundleFixture.Peer, "ABCDEF0123456789", "Hello, WhatsApp!",
                TwoSessions(us, peer, store), 1760000000, Random);

            IList<BinaryNode> devices = node.Child("participants").Children();
            for (int i = 0; i < devices.Count; i++)
            {
                string deviceJid = devices[i].Attrs["jid"];
                Message read = ReadAs(peer, devices[i].Child("enc"));

                // The device-sent form is the sender's own copy: ours other devices read the
                // words out of it, and the recipient's phone answers a copy it never asked
                // for with `ack error="479"` - which is what this stanza used to be.
                Assert.IsTrue(read.DeviceSentMessage == null,
                    deviceJid + " is the recipient's, so it has to read the message, not our sent copy");

                string words = read.Body();
                Assert.IsTrue("Hello, WhatsApp!" == words,
                    deviceJid + " has to read the words, and read " + words);
            }
        }

        [Test]
        public static void ThisAccountsOtherDevicesReadTheDeviceSentForm()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            us.Jid = OurPhone;
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            var sessions = new List<KeyValuePair<string, SessionRecord>>();
            sessions.Add(new KeyValuePair<string, SessionRecord>(PeerBundleFixture.PeerPhone,
                Session(us, peer, PeerBundleFixture.PeerPhone, store)));
            sessions.Add(new KeyValuePair<string, SessionRecord>(OurTablet,
                Session(us, us, OurTablet, store)));

            BinaryNode node = OutgoingMessage.Node(
                PeerBundleFixture.Peer, "ABCDEF0123456789", Message.Text("Hello, WhatsApp!"),
                OutgoingMessage.TextType, null, sessions, 1760000000, Random, null,
                new List<string> { us.Jid });

            IList<BinaryNode> devices = node.Child("participants").Children();

            Message onPeer = ReadAs(peer, devices[0].Child("enc"));
            Assert.IsTrue(onPeer.DeviceSentMessage == null, "the recipient's copy is the message");
            Assert.AreEqual("Hello, WhatsApp!", onPeer.Body());

            Message onOurTablet = ReadAs(us, devices[1].Child("enc"));
            Assert.IsTrue(onOurTablet.DeviceSentMessage != null,
                "our own device reads the copy we sent, wrapped so it can tell it from an incoming message");
            Assert.AreEqual(PeerBundleFixture.Peer, Message.Destination(onOurTablet.DeviceSentMessage));
            Assert.AreEqual("Hello, WhatsApp!", Message.Inner(onOurTablet.DeviceSentMessage).Body());
        }

        [Test]
        public static void TheIdIsSixteenUpperHexCharactersAndNotTwiceTheSame()
        {
            var random = new SignalSessions.CounterRandom();

            string first = OutgoingMessage.Id(random);
            string second = OutgoingMessage.Id(random);

            Assert.AreEqual(16, first.Length);
            Assert.AreEqual(16, second.Length);
            Assert.IsTrue(first != second, "two messages in the same second need two ids");
            Assert.IsTrue(first == first.ToUpperInvariant(),
                "the id is written in upper case, as the reference writes it");

            for (int i = 0; i < first.Length; i++)
            {
                char character = first[i];
                bool hex = (character >= '0' && character <= '9') || (character >= 'A' && character <= 'F');
                Assert.IsTrue(hex, "a message id is hex and nothing else, and this one has '" + character + "'");
            }
        }

        [Test]
        public static void ATextWithNoDeviceIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                OutgoingMessage.Node(PeerBundleFixture.Peer, "AABB", "hello",
                    new List<KeyValuePair<string, SessionRecord>>(), 1, Random);
            });
        }

        /// <summary>One session per device of the peer, the way a first send builds them.</summary>
        private static IList<KeyValuePair<string, SessionRecord>> TwoSessions(
            Credentials us, Credentials peer, SessionStore store)
        {
            var sessions = new List<KeyValuePair<string, SessionRecord>>();
            string[] devices = new string[] { PeerBundleFixture.PeerPhone, PeerBundleFixture.PeerTablet };

            for (int i = 0; i < devices.Length; i++)
            {
                sessions.Add(new KeyValuePair<string, SessionRecord>(devices[i],
                    Session(us, peer, devices[i], store)));
            }

            return sessions;
        }

        /// <summary>A session to one device, opened from the keys that device published.</summary>
        private static SessionRecord Session(Credentials us, Credentials owner, string deviceJid,
            SessionStore store)
        {
            return DeviceDirectory.Session(deviceJid,
                PeerBundleFixture.Bundle(owner, deviceJid, true),
                us, store, new SignalSessions.CounterRandom());
        }

        /// <summary>
        /// What the other side does with one `enc`: the pre-key wrapper is opened, the session
        /// it describes is started, and the sealed message inside is decrypted. `owner` holds
        /// the private pre-keys that wrapper was sealed to.
        /// </summary>
        private static Message ReadAs(Credentials owner, BinaryNode enc)
        {
            byte[] wrapper = SignalEnvelope.Unwrap(enc.Bytes());
            PreKeySignalMessage opened = PreKeySignalMessage.Decode(wrapper);

            SessionRecord responder = SessionRecord.StartAsResponder(
                owner.IdentityPrivateKey,
                owner.SignedPreKeyPrivate,
                PeerBundleFixture.OneTimePrivate(),
                owner.RegistrationId,
                opened,
                new SignalSessions.CounterRandom());

            // Decrypt takes the whole `enc`, not the sealed message inside it: the wrapper is
            // what it unwraps to find the fields the agreement was run on.
            byte[] plaintext = responder.Decrypt(enc.Bytes(), true);

            // The plaintext carries the WhatsApp padding the sender appended
            // (`OutgoingMessage.Pad`), so the receiver strips it the way a phone does.
            return Message.DecodePadded(plaintext);
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
