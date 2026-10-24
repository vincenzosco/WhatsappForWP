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
    /// A group message, both directions, with a real sender key and a real pairwise session
    /// underneath - nothing is mocked in the middle.
    ///
    /// The peer builds a sender key, sends it as the pairwise node the reference sends
    /// (`Socket/messages-send.js`, lines 559 to 585), and the group ciphertext alongside it;
    /// this device has to store the key and read the words. The second message then arrives
    /// with no key at all, which is the shape every message after the first has, and the one
    /// a client that never stored the first cannot read.
    /// </summary>
    public static class GroupSendTests
    {
        public const string GroupJid = "120363000000000000@g.us";
        public const string OtherGroup = "120363111111111111@g.us";
        public const string OurDevice = "15559990000:3@s.whatsapp.net";
        public const string PeerDevice = "15551230000:5@s.whatsapp.net";
        public const string PeerUser = "15551230000@s.whatsapp.net";

        [Test]
        public static void AGroupMessageIsReadWithItsParticipant()
        {
            var world = new World();
            BinaryNode stanza = world.PeerSendsGroup(GroupJid, "ciao gruppo", "G0000001", true);

            IncomingMessage incoming = IncomingMessage.Read(stanza, world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.IsTrue(incoming != null, "a group message arrives");
            Assert.AreEqual("ciao gruppo", incoming.Text);
            Assert.AreEqual(GroupJid, incoming.From, "the chat is the group");
            Assert.AreEqual(PeerDevice, incoming.Sender, "and the sender is the participant");
            Assert.IsFalse(incoming.IsOurEcho, "a member's message is not our own copy");

            Assert.IsTrue(world.Sessions.LoadSenderKey(GroupJid + "/" + PeerDevice) != null,
                "the sender key that came with it is stored, which is what makes the next message readable");
        }

        [Test]
        public static void TheSecondMessageArrivesWithoutTheKey()
        {
            var world = new World();

            IncomingMessage.Read(world.PeerSendsGroup(GroupJid, "primo", "G0000002", true), world.Us,
                world.Sessions, world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            IncomingMessage second = IncomingMessage.Read(world.PeerSendsGroup(GroupJid, "secondo", "G0000003", false),
                world.Us, world.Sessions, world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.AreEqual("secondo", second.Text,
                "no key in the stanza, and the chain stored from the first message reads it");
            Assert.AreEqual(PeerDevice, second.Sender);
        }

        [Test]
        public static void AMessageWithNoKeyAtAllSaysWhatIsMissing()
        {
            var world = new World();
            BinaryNode stanza = world.PeerSendsGroup(GroupJid, "ciao", "G0000004", false);

            string failed = Failed(delegate
            {
                IncomingMessage.Read(stanza, world.Us, world.Sessions, world.PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());
            });

            Assert.IsTrue(failed != null, "a group ciphertext with no key anywhere cannot be read");
            Assert.IsTrue(failed.Contains(PeerDevice) && failed.Contains(GroupJid),
                "and the refusal names both the group and the member: <" + failed + ">");
        }

        [Test]
        public static void AMessageThatLandsBeforeItsKeyIsReadWhenTheKeyArrives()
        {
            var world = new World();

            // The sender makes the key and then the message, as a phone does - so the key stanza
            // has the chain at iteration 0 and the message is encrypted with it - and the server
            // hands them over the other way round, which is the order this test is about.
            BinaryNode key = world.PeerSendsGroup(GroupJid, null, "G0000010", true);
            BinaryNode early = world.PeerSendsGroup(GroupJid, "ciao", "G0000009", false);

            Assert.Throws<SenderKeyMissingException>(delegate
            {
                IncomingMessage.Read(early, world.Us, world.Sessions, world.PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());
            }, "a key that is a moment late is not a message that failed");

            IncomingMessage.Read(key, world.Us,
                world.Sessions, world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            IncomingMessage late = IncomingMessage.Read(early, world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.AreEqual("ciao", late.Text, "the held stanza is read with the key that arrived");
            Assert.AreEqual(PeerDevice, late.Sender);
            Assert.AreEqual(GroupJid, late.Chat);
        }

        [Test]
        public static void TheConnectionSequenceHoldsTheMessageAndReadsItWhenTheKeyArrives()
        {
            // The exact order the connection uses, without the socket: read, keep what said it
            // was waiting, read the stanza that brings the key, drain, read again.
            var world = new World();
            var held = new HeldGroupMessages();

            BinaryNode key = world.PeerSendsGroup(GroupJid, null, "G0000013", true);
            BinaryNode early = world.PeerSendsGroup(GroupJid, "ciao", "G0000014", false);

            try
            {
                IncomingMessage.Read(early, world.Us, world.Sessions, world.PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());

                throw new AssertionException("a message with no key in the stanza is not read");
            }
            catch (SenderKeyMissingException missing)
            {
                Assert.IsTrue(held.Hold(missing.GroupJid, missing.Participant, missing.MessageId, early),
                    "the stanza is kept, not asked again");
            }

            IncomingMessage.Read(key, world.Us, world.Sessions, world.PreKeys,
                new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.IsTrue(world.Sessions.LoadSenderKey(GroupJid + "/" + PeerDevice) != null);

            System.Collections.Generic.IList<BinaryNode> waiting = held.Drain(GroupJid, PeerDevice);
            Assert.AreEqual(1, waiting.Count, "the held message is the one that comes back");

            IncomingMessage read = IncomingMessage.Read(waiting[0], world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.AreEqual("ciao", read.Text, "and it is read, with the key that arrived after it");
            Assert.AreEqual(0, held.Count, "nothing is left waiting");
        }

        [Test]
        public static void AMessageWaitingForItsKeySaysWhatItNeeds()
        {
            var world = new World();
            BinaryNode early = world.PeerSendsGroup(GroupJid, "ciao", "G0000011", false);

            try
            {
                IncomingMessage.Read(early, world.Us, world.Sessions, world.PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());
            }
            catch (SenderKeyMissingException missing)
            {
                Assert.AreEqual("G0000011", missing.MessageId, "the id the ack will answer");
                Assert.AreEqual(GroupJid, missing.GroupJid, "the group the key belongs to");
                Assert.AreEqual(PeerDevice, missing.Participant, "and the member it is waiting for");

                return;
            }

            throw new AssertionException("a message with no key in the stanza is not read");
        }

        [Test]
        public static void TheRefusalIsStillAProtocolException()
        {
            var world = new World();
            BinaryNode early = world.PeerSendsGroup(GroupJid, "ciao", "G0000012", false);

            // Every reader that treats "this stanza did not open" the same way has to keep
            // working: the new type refines the old one, it does not replace it.
            Assert.Throws<ProtocolException>(delegate
            {
                IncomingMessage.Read(early, world.Us, world.Sessions, world.PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());
            });
        }

        [Test]
        public static void AStanzaThatBringsOnlyTheKeyIsNotAMessage()
        {
            var world = new World();
            BinaryNode stanza = world.PeerSendsGroup(GroupJid, null, "G0000005", true);

            IncomingMessage incoming = IncomingMessage.Read(stanza, world.Us, world.Sessions,
                world.PreKeys, new SeenMessages(64), new SignalSessions.CounterRandom());

            Assert.IsTrue(incoming != null, "the stanza is still read: it carried a key");
            Assert.IsTrue(incoming.Text == null, "and there is nothing to show");
            Assert.AreEqual(GroupJid, incoming.From);
            Assert.IsTrue(world.Sessions.LoadSenderKey(GroupJid + "/" + PeerDevice) != null,
                "but the key was stored");
        }

        [Test]
        public static void ASenderKeyForAnotherGroupIsRefused()
        {
            var world = new World();
            // A key minted for another group, sent inside this one: exactly what a client
            // that mixed two groups' records would send.
            BinaryNode stanza = world.PeerSendsForeignKey(
                SenderKeyRecord.Create(OtherGroup, new SignalSessions.CounterRandom()));

            string failed = Failed(delegate
            {
                IncomingMessage.Read(stanza, world.Us, world.Sessions, world.PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());
            });

            Assert.IsTrue(failed != null, "a key minted for another group is not used here");
            Assert.IsTrue(failed.Contains(OtherGroup) && failed.Contains(GroupJid),
                "and the sentence names both groups: <" + failed + ">");
        }

        [Test]
        public static void TheGroupStanzaIsTheOneTheServerExpects()
        {
            var world = new World();
            BinaryNode stanza = world.PeerSendsGroup(GroupJid, "ciao", "G0000007", true);

            Assert.AreEqual("message", stanza.Tag);
            Assert.AreEqual(GroupJid, stanza.Attrs["to"]);
            Assert.AreEqual("text", stanza.Attrs["type"]);

            BinaryNode participants = stanza.Child("participants");
            Assert.IsTrue(participants != null, "the sender key travels in a participants node");
            Assert.IsTrue(participants.Children().Count == 1, "one entry per device that needed the key");
            Assert.AreEqual(OurDevice, participants.Children()[0].Attrs["jid"]);
            Assert.AreEqual("pkmsg", participants.Children()[0].Child("enc").Attrs["type"],
                "the first message on a new session is a `pkmsg`, and it becomes `msg` after that");

            BinaryNode cipher = stanza.Child("enc");
            Assert.IsTrue(cipher != null, "and the message itself is one enc");
            Assert.AreEqual("skmsg", cipher.Attrs["type"], "typed skmsg, which is what the group ciphertext is");
        }

        /// <summary>
        /// This device with its published pre-keys, and a member of a group who has a sender
        /// key for it. The member's side is built exactly as a phone's is: a session from this
        /// device's bundle, a sender key of its own, and the two nodes the reference sends.
        /// </summary>
        private sealed class World
        {
            public readonly Credentials Us = Credentials.Create(new SignalSessions.CounterRandom());
            public readonly Credentials Other = Credentials.Create(new SignalSessions.CounterRandom());
            public readonly PreKeyStore PreKeys = new PreKeyStore(new Memory());
            public readonly SessionStore Sessions = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            private readonly SessionStore _otherSessions = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            private readonly SenderKeyRecord _theirKey;
            private readonly SessionRecord _theirSession;

            public World()
            {
                Us.Jid = OurDevice;

                BinaryNode upload = PreKeyUpload.Node(Us, PreKeys, new SignalSessions.CounterRandom(), 5);
                BinaryNode oneTime = upload.Child("list").Children()[0];
                byte[] id = oneTime.Child("id").Bytes();
                byte[] value = oneTime.Child("value").Bytes();

                PreKeyBundle ours = DeviceDirectory.Bundles(BundleAnswer(id, value))[0].Value;
                _theirSession = DeviceDirectory.Session(OurDevice, ours, Other, _otherSessions,
                    new SignalSessions.CounterRandom());

                _theirKey = SenderKeyRecord.Create(GroupJid, new SignalSessions.CounterRandom());
            }

            /// <summary>
            /// A message from the member into the group. `body` null means the stanza carries
            /// nothing but the sender key, which is what a phone sends when it has just made
            /// one and has nothing to say yet.
            /// </summary>
            public BinaryNode PeerSendsGroup(string groupJid, string body, string id, bool withKey)
            {
                WhatsappApp.Protocol.WaProto.Message payload = body == null
                    ? null
                    : WhatsappApp.Protocol.WaProto.Message.Text(body);

                var content = new List<BinaryNode>();

                if (withKey)
                {
                    content.Add(KeyNode(groupJid, SenderKeys.DistributionMessage(groupJid, _theirKey)));
                }

                if (payload != null)
                {
                    BinaryNode group = new BinaryNode("enc");
                    group.Attrs.Add("v", "2");
                    group.Attrs.Add("type", "skmsg");
                    group.Content = SenderKeys.Ciphertext(groupJid, _theirKey, payload.Encode());
                    content.Add(group);
                }

                var stanza = new BinaryNode("message");
                stanza.Attrs.Add("from", groupJid);
                stanza.Attrs.Add("to", groupJid);
                stanza.Attrs.Add("participant", PeerDevice);
                stanza.Attrs.Add("id", id);
                stanza.Attrs.Add("t", "1760000000");
                stanza.Attrs.Add("type", "text");
                stanza.Content = content;

                return stanza;
            }

            /// <summary>The pairwise node: one `to[this device]` holding the encrypted sender key.</summary>
            private BinaryNode KeyNode(string groupJid, byte[] distribution)
            {
                BinaryNode enc = new BinaryNode("enc");
                enc.Attrs.Add("v", "2");
                enc.Attrs.Add("type", _theirSession.PreKeyPending ? "pkmsg" : "msg");
                enc.Content = _theirSession.Encrypt(distribution);

                BinaryNode device = new BinaryNode("to");
                device.Attrs.Add("jid", OurDevice);
                device.Content = new List<BinaryNode> { enc };

                var participants = new BinaryNode("participants");
                participants.Content = new List<BinaryNode> { device };

                return participants;
            }

            /// <summary>
            /// The same stanza, with a sender key minted for a **different** group: the
            /// mistake this client has to refuse rather than half-use.
            /// </summary>
            public BinaryNode PeerSendsForeignKey(SenderKeyRecord foreign)
            {
                byte[] distribution = foreign.Distribution(OtherGroup);

                BinaryNode enc = new BinaryNode("enc");
                enc.Attrs.Add("v", "2");
                enc.Attrs.Add("type", _theirSession.PreKeyPending ? "pkmsg" : "msg");
                enc.Content = _theirSession.Encrypt(new WhatsappApp.Protocol.WaProto.ProtoWriter()
                    .Bytes(SenderKeys.DistributionField, new WhatsappApp.Protocol.WaProto.ProtoWriter()
                        .Text(1, OtherGroup)
                        .Bytes(2, distribution)
                        .ToArray())
                    .ToArray());

                BinaryNode device = new BinaryNode("to");
                device.Attrs.Add("jid", OurDevice);
                device.Content = new List<BinaryNode> { enc };

                var participants = new BinaryNode("participants");
                participants.Content = new List<BinaryNode> { device };

                var stanza = new BinaryNode("message");
                stanza.Attrs.Add("from", GroupJid);
                stanza.Attrs.Add("to", GroupJid);
                stanza.Attrs.Add("participant", PeerDevice);
                stanza.Attrs.Add("id", "G0000008");
                stanza.Attrs.Add("t", "1760000000");
                stanza.Attrs.Add("type", "text");
                stanza.Content = new List<BinaryNode> { participants };

                return stanza;
            }

            private BinaryNode BundleAnswer(byte[] oneTimeId, byte[] oneTimeValue)
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
                var id = new BinaryNode("id");
                id.Content = oneTimeId;
                var value = new BinaryNode("value");
                value.Content = oneTimeValue;
                oneTime.Content = new List<BinaryNode> { id, value };

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
