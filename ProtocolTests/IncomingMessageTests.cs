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
        /// The world one conversation needs: this device with its published pre-keys, and
        /// somebody on the other end who builds a session from them.
        /// </summary>
        private sealed class World
        {
            public readonly Credentials Us = Credentials.Create(new SignalSessions.CounterRandom());
            public readonly Credentials Other = Credentials.Create(new SignalSessions.CounterRandom());
            public readonly PreKeyStore PreKeys = new PreKeyStore(new Memory());
            public readonly SessionStore Sessions = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            public readonly SessionStore OtherSessions = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            private readonly uint _oneTimePreKeyId;
            private readonly byte[] _oneTimePreKeyPublic;

            public World()
            {
                Us.Jid = OurDevice;

                BinaryNode upload = PreKeyUpload.Node(Us, PreKeys, new SignalSessions.CounterRandom(), 5);
                BinaryNode oneTime = upload.Child("list").Children()[0];
                _oneTimePreKeyId = Number(oneTime.Child("id").Bytes());
                _oneTimePreKeyPublic = oneTime.Child("value").Bytes();
            }

            /// <summary>What the other phone sends to this one.</summary>
            public Sent PeerSends(string body, string id)
            {
                return SomeoneSends(OurDevice, PeerDevice, body, id);
            }

            /// <summary>The same, with the JIDs chosen: an echo test needs our own on both ends.</summary>
            public Sent SomeoneSends(string to, string from, string body, string id)
            {
                PreKeyBundle ours = DeviceDirectory.Bundles(OurBundleAnswer())[0].Value;

                SessionRecord theirSide = DeviceDirectory.Session(to, ours, Other,
                    OtherSessions, new SignalSessions.CounterRandom());

                var devices = new List<KeyValuePair<string, SessionRecord>>();
                devices.Add(new KeyValuePair<string, SessionRecord>(to, theirSide));

                BinaryNode node = OutgoingMessage.Node(to, id, body, devices, 1760000000);
                node.Attrs.Add("from", from);

                return new Sent(node, _oneTimePreKeyId);
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
                oneTimeId.Content = BigEndian(_oneTimePreKeyId, 3);
                var oneTimeValue = new BinaryNode("value");
                oneTimeValue.Content = _oneTimePreKeyPublic;
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
