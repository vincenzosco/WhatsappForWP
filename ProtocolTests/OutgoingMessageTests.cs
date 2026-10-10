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
                1760000000);

            Assert.AreEqual("message", node.Tag);
            Assert.AreEqual(PeerBundleFixture.Peer, node.Attrs["to"]);
            Assert.AreEqual("text", node.Attrs["type"]);
            Assert.AreEqual("0123456789ABCDEF", node.Attrs["id"]);
            Assert.AreEqual("1760000000", node.Attrs["t"]);

            IList<BinaryNode> devices = node.Children();
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
        public static void EachDeviceReadsBackTheWordsInsideTheDeviceSentForm()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            BinaryNode node = OutgoingMessage.Node(
                PeerBundleFixture.Peer, "ABCDEF0123456789", "Hello, WhatsApp!",
                TwoSessions(us, peer, store), 1760000000);

            IList<BinaryNode> devices = node.Children();
            for (int i = 0; i < devices.Count; i++)
            {
                string deviceJid = devices[i].Attrs["jid"];
                byte[] enc = devices[i].Child("enc").Bytes();

                // What the other side does with it: the pre-key wrapper is opened, the
                // session it describes is started, and the sealed message inside comes out
                // as the device-sent form.
                byte[] wrapper = SignalEnvelope.Unwrap(enc);
                PreKeySignalMessage opened = PreKeySignalMessage.Decode(wrapper);

                SessionRecord responder = SessionRecord.StartAsResponder(
                    peer.IdentityPrivateKey,
                    peer.SignedPreKeyPrivate,
                    PeerBundleFixture.OneTimePrivate(),
                    peer.RegistrationId,
                    opened,
                    new SignalSessions.CounterRandom());

                // Decrypt takes the whole `enc`, not the sealed message inside it: the
                // wrapper is what it unwraps to find the fields the agreement was run on.
                byte[] plaintext = responder.Decrypt(enc, true);
                Message read = Message.Decode(plaintext);

                string destination = Message.Destination(read.DeviceSentMessage);
                Assert.IsTrue(PeerBundleFixture.Peer == destination,
                    deviceJid + " has to see which JID the copy was for, and saw " + destination);

                string words = Message.Inner(read.DeviceSentMessage).Body();
                Assert.IsTrue("Hello, WhatsApp!" == words,
                    deviceJid + " has to read the words, and read " + words);
            }
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
                    new List<KeyValuePair<string, SessionRecord>>(), 1);
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
                SessionRecord session = DeviceDirectory.Session(devices[i],
                    PeerBundleFixture.Bundle(peer, devices[i], true),
                    us, store, new SignalSessions.CounterRandom());

                sessions.Add(new KeyValuePair<string, SessionRecord>(devices[i], session));
            }

            return sessions;
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
