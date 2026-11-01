using System;
using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// A text to one person, end to end: the two answers a send asks for (who the devices
    /// are, and the keys each of them answers with), the sessions built from them, the
    /// stanza that goes out, and the peer reading its own copy of it. Nothing in the middle
    /// is mocked - the sessions, the padding, the protobuf and the ciphertext are the ones
    /// the phone uses.
    ///
    /// Why it exists: the device list and the bundle answer are two different shapes, and
    /// the one-to-one send used to hand the first to the parser of the second, so every text
    /// to a single person died before a stanza existed. That failure lived in
    /// `NativeConnection`, which this desktop runner cannot compile; the rule lives in
    /// `DirectSend` now, and this is the test that keeps the two answers apart.
    /// </summary>
    public static class DirectSendTests
    {
        public const string OurJid = "15559990000:3@s.whatsapp.net";
        public const string OurUser = "15559990000@s.whatsapp.net";
        public const string PeerUser = "15551230000@s.whatsapp.net";

        /// <summary>Device 0 is the phone, and WhatsApp spells it as the bare user address.</summary>
        public const string PeerPhone = PeerUser;
        public const string PeerTablet = "15551230000:5@s.whatsapp.net";
        public const string PeerOldPhone = "15551230000:7@s.whatsapp.net";

        /// <summary>The same person, as WhatsApp now addresses them: by a LID.</summary>
        public const string PeerLid = "179585517920463@lid";
        public const string PeerLidTablet = "179585517920463:5@lid";

        /// <summary>This account's own LID, and this phone's device under it.</summary>
        public const string OurLid = "150375613116620@lid";
        public const string OurLidDevice = "150375613116620:32@lid";

        /// <summary>
        /// A chat whose number this phone also knows by its `@lid` is sent **by the LID**.
        ///
        /// Why: on 2026-11-01 a text reached `393492556507@s.whatsapp.net` - spellt right,
        /// device 0 bare - and the server took it (`ack class="message"`) while the other
        /// phone showed nothing: no delivery receipt, no retry receipt. The person writes
        /// from their LID and every receipt the server sends names devices in `@lid`, so a
        /// device list for the number is a list of devices nobody reads. The row stays keyed
        /// on the number; the address on the wire is the LID.
        /// </summary>
        [Test]
        public static void AChatWithAKnownLidIsAddressedByIt()
        {
            Assert.AreEqual(PeerLid, DirectSend.WireAddress(PeerUser, PeerLid),
                "the pair is one account, and the LID is the address the server uses");
            Assert.AreEqual(PeerUser, DirectSend.WireAddress(PeerUser, null), "no pair, no switch");
            Assert.AreEqual(PeerUser, DirectSend.WireAddress(PeerUser, "179585517920463@s.whatsapp.net"),
                "only an @lid switches the family: a number is not an address to switch to");
        }

        /// <summary>
        /// And the stanza that comes out of it names the LID, seals for the LID's devices, and
        /// is still read by the peer on the other side - the whole path, not just the label.
        /// </summary>
        [Test]
        public static void ALidSendSealsForTheLidDevicesAndNamesTheLid()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            us.Jid = OurJid;
            us.Lid = OurLidDevice;

            var phone = new Device(PeerLid);
            var tablet = new Device(PeerLidTablet);

            IList<string> targets = DirectSend.Targets(LidAnswer("0", "5"), OurJid, OurLidDevice);

            Assert.AreEqual(2, targets.Count, "the peer's two devices, in the LID family");
            Assert.AreEqual(PeerLid, targets[0], "device 0 is the bare lid, exactly as for a number");
            Assert.AreEqual(PeerLidTablet, targets[1]);

            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            DirectSendPlan plan = DirectSend.Plan(targets, BundleAnswer(phone, tablet), us, store,
                new SignalSessions.CounterRandom());

            BinaryNode stanza = OutgoingMessage.Node(PeerLid, "3EB0LID000001", "ciao", plan.Devices,
                1760000000, new SignalSessions.CounterRandom());
            stanza.Attrs.Add("from", OurJid);

            Assert.AreEqual(PeerLid, stanza.Attrs["to"], "the stanza names the lid and nothing else");
            Assert.AreEqual(PeerLid, stanza.Child("participants").Children()[0].Attrs["jid"],
                "and the device nodes are in the same family as the address");
            Assert.AreEqual("ciao", phone.Read(stanza).Text, "the other phone reads its copy");
        }

        [Test]
        public static void AOneToOneTextReachesEveryDeviceOfThePeer()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            us.Jid = OurJid;

            var phone = new Device(PeerPhone);
            var tablet = new Device(PeerTablet);

            // Two answers, in the order a send asks them: who the devices are, then what
            // each one's keys are. The device list names this phone as well, which is what
            // the real one does and what a send has to leave out.
            BinaryNode devices = DeviceAnswer("0", "5");
            IList<string> targets = DirectSend.Targets(devices, OurJid, null);

            Assert.AreEqual(2, targets.Count, "only the peer's two devices are written to");
            Assert.AreEqual(PeerPhone, targets[0]);
            Assert.AreEqual(PeerTablet, targets[1]);

            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            Assert.AreEqual(2, DirectSend.WithoutSession(targets, store).Count,
                "nothing is known about either device yet, so both need a bundle");

            DirectSendPlan plan = DirectSend.Plan(targets, BundleAnswer(phone, tablet),
                us, store, new SignalSessions.CounterRandom());

            Assert.AreEqual(2, plan.Devices.Count);
            Assert.AreEqual(0, plan.Skipped.Count);
            Assert.IsTrue(store.Load(PeerPhone) != null && store.Load(PeerTablet) != null,
                "both sessions are saved before a message uses them");

            BinaryNode stanza = Text(plan, "3EB0D1EC70001", "ciao");

            Assert.AreEqual("message", stanza.Tag);
            Assert.AreEqual(PeerUser, stanza.Attrs["to"]);
            Assert.AreEqual("text", stanza.Attrs["type"]);
            Assert.AreEqual("3EB0D1EC70001", stanza.Attrs["id"]);

            BinaryNode participants = stanza.Child("participants");
            Assert.IsTrue(participants != null, "the per-device nodes travel inside `participants`");

            var to = participants.Children();
            Assert.AreEqual(2, to.Count, "one `to` per device of the peer");
            Assert.AreEqual(PeerPhone, to[0].Attrs["jid"]);
            Assert.AreEqual(PeerTablet, to[1].Attrs["jid"]);
            Assert.AreEqual(OutgoingMessage.PreKeyType, to[0].Child("enc").Attrs["type"],
                "a first message on a new session is a `pkmsg`");

            IncomingMessage onPhone = phone.Read(stanza);
            Assert.IsTrue(onPhone != null, "the peer's phone reads its copy");
            Assert.AreEqual("ciao", onPhone.Text);
            Assert.AreEqual(OurJid, onPhone.From, "and it is a message from this phone");

            IncomingMessage onTablet = tablet.Read(stanza);
            Assert.IsTrue(onTablet != null, "and so does the tablet, from the same stanza");
            Assert.AreEqual("ciao", onTablet.Text);
        }

        [Test]
        public static void ThisPhoneIsNotWrittenTo()
        {
            BinaryNode devices = DeviceAnswer("0", "5");
            IList<string> targets = DirectSend.Targets(devices, OurJid, null);

            Assert.AreEqual(2, targets.Count, "this phone is in the answer and is not a target");
            for (int i = 0; i < targets.Count; i++)
            {
                Assert.IsTrue(targets[i] != OurJid, "this phone is in the answer and is not a target");
            }

            string failed = Failed(delegate
            {
                // An answer that lists only this phone: there is nobody to write to, and
                // that is a sentence, not an empty stanza.
                DirectSend.Targets(OnlyOurDevice(), OurJid, null);
            });

            Assert.IsTrue(failed != null, "a list of nothing but this phone cannot be sent to");
        }

        /// <summary>
        /// The device list is asked for both parties, so it names this account's other
        /// devices too: they are targets - the device-sent copy is what makes the words
        /// appear as sent on the phone - and this phone alone is not.
        /// </summary>
        [Test]
        public static void OurOwnOtherDevicesAreWrittenToAndThisPhoneIsNot()
        {
            IList<string> targets = DirectSend.Targets(BothPartiesAnswer(), OurJid, null);

            Assert.AreEqual(3, targets.Count, "our other device and the peer's two");
            Assert.AreEqual(OurUser, targets[0],
                "the phone gets the copy that shows the message as sent, and device 0 has no suffix");
            Assert.AreEqual(PeerPhone, targets[1]);
            Assert.AreEqual(PeerTablet, targets[2]);
        }

        [Test]
        public static void TheSecondTextUsesTheEstablishedSession()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            us.Jid = OurJid;

            var phone = new Device(PeerPhone);
            var tablet = new Device(PeerTablet);
            IList<string> targets = DirectSend.Targets(DeviceAnswer("0", "5"), OurJid, null);
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

            DirectSendPlan plan = DirectSend.Plan(targets, BundleAnswer(phone, tablet),
                us, store, new SignalSessions.CounterRandom());

            BinaryNode first = Text(plan, "3EB0D1EC70002", "primo");
            Assert.AreEqual("primo", phone.Read(first).Text);
            Assert.AreEqual("primo", tablet.Read(first).Text);

            // The peer has not answered yet, so the pre-key wrapper still travels: that is
            // the state the reference calls `pendingPreKey`, and a `msg` here would be a
            // stanza the peer cannot open with the session it has.
            BinaryNode second = Text(plan, "3EB0D1EC70003", "secondo");
            Assert.AreEqual(OutgoingMessage.PreKeyType,
                second.Child("participants").Children()[0].Child("enc").Attrs["type"],
                "a session nobody has answered on is still a `pkmsg`");
            Assert.AreEqual("secondo", phone.Read(second).Text);

            // The answer is what establishes the session on this side.
            SessionRecord theirSide = phone.Sessions.Load(OurJid);
            Assert.IsTrue(theirSide != null, "the peer's own session is stored under this phone's device");

            BinaryNode answer = OutgoingMessage.Node(OurUser, "3EB0D1EC70004", "ricevuto",
                new List<KeyValuePair<string, SessionRecord>>
                {
                    new KeyValuePair<string, SessionRecord>(OurJid, theirSide)
                },
                1760000001, new SignalSessions.CounterRandom());
            answer.Attrs.Add("from", PeerPhone);

            IncomingMessage reply = IncomingMessage.Read(answer, us, store,
                new PreKeyStore(new Memory()), new SeenMessages(64), new SignalSessions.CounterRandom());
            Assert.AreEqual("ricevuto", reply.Text, "this side reads the answer on the session it started");

            // Answered: the next send asks again, as every send does, and this time the
            // sessions come out of the store with no pre-key wrapper left.
            IList<string> again = DirectSend.Targets(DeviceAnswer("0", "5"), OurJid, null);
            Assert.AreEqual(0, DirectSend.WithoutSession(again, store).Count,
                "and neither device needs a bundle any more");

            DirectSendPlan answered = DirectSend.Plan(again, null, us, store,
                new SignalSessions.CounterRandom());
            Assert.AreEqual(2, answered.Devices.Count, "the stored sessions are enough");

            BinaryNode third = Text(answered, "3EB0D1EC70005", "terzo");
            Assert.AreEqual(OutgoingMessage.ExistingType,
                third.Child("participants").Children()[0].Child("enc").Attrs["type"],
                "once the peer has answered, the stanza is a `msg`");
            Assert.AreEqual("terzo", phone.Read(third).Text);
        }

        [Test]
        public static void ADeviceTheServerHasNoKeyForIsSkipped()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            us.Jid = OurJid;

            var phone = new Device(PeerPhone);
            var tablet = new Device(PeerTablet);
            IList<string> targets = DirectSend.Targets(
                DeviceAnswer("0", "5", "7"), OurJid, null);

            Assert.AreEqual(3, targets.Count, "the third device is in the list");

            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            DirectSendPlan plan = DirectSend.Plan(targets, BundleAnswer(phone, tablet),
                us, store, new SignalSessions.CounterRandom());

            Assert.AreEqual(2, plan.Devices.Count, "the two devices with a key are written to");
            Assert.AreEqual(1, plan.Skipped.Count, "and the one without is named instead");
            Assert.AreEqual(PeerOldPhone, plan.Skipped[0]);
        }

        [Test]
        public static void ABundleCanUseDeviceZeroOrTheCorrespondingLid()
        {
            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            us.Jid = OurJid;

            IList<string> targets = new List<string> { PeerPhone };

            var zeroStore = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            DirectSendPlan zero = DirectSend.Plan(targets,
                BundleAnswer(new Device(PeerUser.Replace("@s.whatsapp.net", "@lid"))),
                us, zeroStore, new SignalSessions.CounterRandom());

            Assert.AreEqual(1, zero.Devices.Count,
                "a device-less per-user bundle is device zero");

            var lidStore = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            DirectSendPlan lid = DirectSend.Plan(targets,
                BundleAnswer(new Device(PeerPhone.Replace("@s.whatsapp.net", "@lid"))),
                us, lidStore, new SignalSessions.CounterRandom());

            Assert.AreEqual(1, lid.Devices.Count,
                "a bundle for the corresponding LID device still belongs to the target");
        }

        [Test]
        public static void TheDeviceListIsNotABundleAnswer()
        {
            // The mistake that made the one-to-one send impossible: the usync answer has
            // `user > devices`, no `registration`, and reading it as a bundle answer has to
            // be refused with the node it could not find - not the null dereference it used
            // to be.
            string failed = Failed(delegate
            {
                DeviceDirectory.Bundles(DeviceAnswer("0"));
            });

            Assert.IsTrue(failed != null, "a device list is not a bundle answer");
            Assert.IsTrue(failed.Contains("registration"),
                "the refusal names what was missing, and it said: " + failed);
            Assert.IsTrue(failed.Contains(OurUser) || failed.Contains(PeerUser),
                "and the device it was reading, and it said: " + failed);
        }

        /// <summary>The stanza a send builds, with the `from` the server stamps on it.</summary>
        private static BinaryNode Text(DirectSendPlan plan, string id, string body)
        {
            BinaryNode stanza = OutgoingMessage.Node(PeerUser, id, body, plan.Devices,
                1760000000, new SignalSessions.CounterRandom());

            // A stanza leaves this phone without `from`: the server adds it before the
            // peer reads it, and the peer's reader requires it.
            stanza.Attrs.Add("from", OurJid);

            return stanza;
        }

        /// <summary>
        /// The `usync` answer a device query gets: this phone's own device under our
        /// number, and the given devices under the peer's.
        /// </summary>
        private static BinaryNode DeviceAnswer(params string[] peerDeviceIds)
        {
            return Answer(UserDevices(OurUser, "3"), UserDevices(PeerUser, peerDeviceIds));
        }

        /// <summary>The same answer in the LID family, which is what the server sends now.</summary>
        private static BinaryNode LidAnswer(params string[] peerDeviceIds)
        {
            return Answer(UserDevices(OurLid, "32"), UserDevices(PeerLid, peerDeviceIds));
        }

        private static BinaryNode Answer(params BinaryNode[] users)
        {
            var list = new BinaryNode("list");
            list.Content = new List<BinaryNode>(users);

            var usync = new BinaryNode("usync");
            usync.Content = new List<BinaryNode> { list };

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("type", "result");
            iq.Content = new List<BinaryNode> { usync };

            return iq;
        }

        /// <summary>The answer to a query that named both parties: this account and the peer.</summary>
        private static BinaryNode BothPartiesAnswer()
        {
            var users = new List<BinaryNode>();
            users.Add(UserDevices(OurUser, "3", "0"));
            users.Add(UserDevices(PeerUser, "0", "5"));

            var list = new BinaryNode("list");
            list.Content = users;

            var usync = new BinaryNode("usync");
            usync.Content = new List<BinaryNode> { list };

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("type", "result");
            iq.Content = new List<BinaryNode> { usync };

            return iq;
        }

        private static BinaryNode OnlyOurDevice()
        {
            var list = new BinaryNode("list");
            list.Content = new List<BinaryNode> { UserDevices(OurUser, "3") };

            var usync = new BinaryNode("usync");
            usync.Content = new List<BinaryNode> { list };

            var iq = new BinaryNode("iq");
            iq.Content = new List<BinaryNode> { usync };

            return iq;
        }

        private static BinaryNode UserDevices(string userJid, params string[] devices)
        {
            var deviceList = new BinaryNode("device-list");
            var entries = new List<BinaryNode>();

            for (int i = 0; i < devices.Length; i++)
            {
                var device = new BinaryNode("device");
                device.Attrs.Add("id", devices[i]);
                entries.Add(device);
            }

            deviceList.Content = entries;

            var devicesNode = new BinaryNode("devices");
            devicesNode.Content = new List<BinaryNode> { deviceList };

            var user = new BinaryNode("user");
            user.Attrs.Add("jid", userJid);
            user.Content = new List<BinaryNode> { devicesNode };

            return user;
        }

        /// <summary>
        /// The bundle answer for the devices named: one `user` per device, with the keys that
        /// device's own pre-key upload published - which is what makes the peer able to read
        /// what this side sealed for it.
        /// </summary>
        private static BinaryNode BundleAnswer(params Device[] devices)
        {
            var users = new List<BinaryNode>();
            for (int i = 0; i < devices.Length; i++) users.Add(devices[i].BundleUser());

            var list = new BinaryNode("list");
            list.Content = users;

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("type", "result");
            iq.Content = new List<BinaryNode> { list };

            return iq;
        }

        /// <summary>
        /// One device of the peer: its credentials, the private pre-keys it published, and
        /// the sessions it reads with. Built from its own upload, so the bundle this side
        /// reads describes keys the peer really holds.
        /// </summary>
        private sealed class Device
        {
            public readonly Credentials Credentials;
            public readonly PreKeyStore PreKeys;
            public readonly SessionStore Sessions;

            private readonly BinaryNode _upload;

            public Device(string deviceJid)
            {
                Credentials = Credentials.Create(new SignalSessions.CounterRandom());
                Credentials.Jid = deviceJid;

                PreKeys = new PreKeyStore(new Memory());
                Sessions = new SessionStore(new Memory(), new SignalSessions.CounterRandom());

                _upload = PreKeyUpload.Node(Credentials, PreKeys,
                    new SignalSessions.CounterRandom(), 5);
            }

            /// <summary>The `user` node this device's keys go into a bundle answer as.</summary>
            public BinaryNode BundleUser()
            {
                var children = new List<BinaryNode>();
                children.Add(_upload.Child("registration"));
                children.Add(_upload.Child("identity"));
                children.Add(_upload.Child("skey"));

                // The first one-time pre-key the upload published, which is the one this
                // side is handed and the one the peer still has the private half of.
                children.Add(_upload.Child("list").Children()[0]);

                var user = new BinaryNode("user");
                user.Attrs.Add("jid", Credentials.Jid);
                user.Content = children;

                return user;
            }

            public IncomingMessage Read(BinaryNode stanza)
            {
                return IncomingMessage.Read(stanza, Credentials, Sessions, PreKeys,
                    new SeenMessages(64), new SignalSessions.CounterRandom());
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
