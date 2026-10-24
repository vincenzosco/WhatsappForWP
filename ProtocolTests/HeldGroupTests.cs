using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The store that keeps a group message until the sender key it needs arrives: what comes
    /// back, in which order, and what happens when the key never does.
    ///
    /// Why it is a test of its own and not a line in `GroupSendTests`: the reading is the
    /// protocol's, the waiting is the connection's, and this is the part that has a ceiling -
    /// a store that waits forever is the leak that looks like a feature.
    /// </summary>
    public static class HeldGroupTests
    {
        private const string Group = "120363000000000000@g.us";
        private const string OtherGroup = "120363111111111111@g.us";
        private const string Peer = "15551230000:5@s.whatsapp.net";
        private const string OtherPeer = "15557770000:2@s.whatsapp.net";

        [Test]
        public static void HeldMessagesComeBackOldestFirst()
        {
            var store = new HeldGroupMessages();

            store.Hold(Group, Peer, "M1", Stanza("M1"));
            store.Hold(Group, Peer, "M2", Stanza("M2"));

            Assert.AreEqual(2, store.Count);

            var read = store.Drain(Group, Peer);

            Assert.AreEqual(2, read.Count);
            Assert.AreEqual("M1", read[0].Attrs["id"], "the one that waited longest is read first");
            Assert.AreEqual("M2", read[1].Attrs["id"]);
            Assert.AreEqual(0, store.Count, "and it is no longer held");
            Assert.IsFalse(store.Has(Group, Peer));
        }

        [Test]
        public static void TheSameIdIsNotHeldTwice()
        {
            var store = new HeldGroupMessages();
            BinaryNode stanza = Stanza("M1");

            Assert.IsTrue(store.Hold(Group, Peer, "M1", stanza), "the first copy is kept");
            Assert.IsFalse(store.Hold(Group, Peer, "M1", Stanza("M1")),
                "the server re-delivers what nobody acknowledged: the second copy is not a second message");
            Assert.AreEqual(1, store.Count);
        }

        [Test]
        public static void TakingOneMemberLeavesTheOthersAlone()
        {
            var store = new HeldGroupMessages();

            store.Hold(Group, Peer, "M1", Stanza("M1"));
            store.Hold(Group, OtherPeer, "M2", Stanza("M2"));
            store.Hold(OtherGroup, Peer, "M3", Stanza("M3"));

            var read = store.Drain(Group, Peer);

            Assert.AreEqual(1, read.Count);
            Assert.AreEqual("M1", read[0].Attrs["id"]);
            Assert.IsTrue(store.Has(Group, OtherPeer), "another member of the same group still waits");
            Assert.IsTrue(store.Has(OtherGroup, Peer),
                "and the same member's key in another group is another key");
        }

        [Test]
        public static void AKeyForAnotherGroupDoesNotFreeTheMessage()
        {
            var store = new HeldGroupMessages();
            store.Hold(Group, Peer, "M1", Stanza("M1"));

            Assert.IsFalse(store.Has(OtherGroup, Peer),
                "a message held for one group is not answered by the key of another");
            Assert.AreEqual(1, store.Count);
        }

        [Test]
        public static void OneMemberCannotHoldTheWholePhone()
        {
            var store = new HeldGroupMessages();

            for (int i = 0; i < HeldGroupMessages.MaxPerSender + 5; i++)
            {
                store.Hold(Group, Peer, "M" + i, Stanza("M" + i));
            }

            Assert.AreEqual(HeldGroupMessages.MaxPerSender, store.Count,
                "one member whose key never comes cannot fill the store");
            Assert.AreEqual(5, store.Dropped, "and what it pushed out is counted");

            var read = store.Drain(Group, Peer);
            Assert.AreEqual("M5", read[0].Attrs["id"], "the oldest went first");
        }

        [Test]
        public static void TheStoreHasATotalCeiling()
        {
            var store = new HeldGroupMessages();

            // Four members, one cap each: the last twenty to arrive push the first twenty out.
            string[] members = { "A:1@s.whatsapp.net", "B:1@s.whatsapp.net", "C:1@s.whatsapp.net", "D:1@s.whatsapp.net" };
            for (int m = 0; m < members.Length; m++)
            {
                for (int i = 0; i < HeldGroupMessages.MaxPerSender; i++)
                {
                    store.Hold(Group, members[m], m + "-" + i, Stanza(m + "-" + i));
                }
            }

            Assert.AreEqual(HeldGroupMessages.MaxTotal, store.Count);
            Assert.AreEqual(HeldGroupMessages.MaxPerSender * members.Length - HeldGroupMessages.MaxTotal,
                store.Dropped);

            var first = store.Drain(Group, members[0]);
            Assert.AreEqual(0, first.Count, "the member who waited longest is the one that waits no longer");
            Assert.AreEqual(HeldGroupMessages.MaxPerSender, store.Drain(Group, members[3]).Count);
        }

        [Test]
        public static void HoldingSomethingThatIsNotAStanzaIsRefused()
        {
            var store = new HeldGroupMessages();

            Assert.Throws<ProtocolException>(delegate { store.Hold(Group, Peer, "M1", null); });
            Assert.Throws<ProtocolException>(delegate { store.Hold(Group, null, "M1", Stanza("M1")); });
            Assert.Throws<ProtocolException>(delegate { store.Hold(Group, Peer, null, Stanza("M1")); });
        }

        private static BinaryNode Stanza(string id)
        {
            var stanza = new BinaryNode("message");
            stanza.Attrs.Add("id", id);

            return stanza;
        }
    }
}
