using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// Status: the broadcast list, the list query and the view receipt.
    ///
    /// The one shape here that a person meets every day is the first: a status is not a chat,
    /// and a client that treated `status@broadcast` as one would put a chat row called
    /// "status" at the top of the list for every contact who posted that morning.
    /// </summary>
    public static class StatusFeedTests
    {
        [Test]
        public static void AStatusJidIsRecognised()
        {
            Assert.IsTrue(StatusFeed.IsStatus("status@broadcast"),
                "the list itself");
            Assert.IsTrue(StatusFeed.IsStatus("status:5@broadcast"),
                "and a device of it, which is how a status message's `from` is spelled");

            Assert.IsFalse(StatusFeed.IsStatus("15551230000@s.whatsapp.net"), "a contact is not a status");
            Assert.IsFalse(StatusFeed.IsStatus("120363000000000000@g.us"), "and neither is a group");
            Assert.IsFalse(StatusFeed.IsStatus("status@s.whatsapp.net"), "the user alone is not enough: the server has to be the broadcast one");
            Assert.IsFalse(StatusFeed.IsStatus(null));
        }

        [Test]
        public static void TheListQueryAsksForEveryJid()
        {
            var who = new List<string>();
            who.Add("15551230000@s.whatsapp.net");
            who.Add("391234567890@s.whatsapp.net");

            BinaryNode query = StatusFeed.ListQuery(who, new SignalSessions.CounterRandom());

            Assert.AreEqual("iq", query.Tag);
            Assert.AreEqual("usync", query.Attrs["xmlns"]);

            BinaryNode usync = query.Child("usync");
            Assert.IsTrue(usync != null, "one usync for the whole list");
            Assert.AreEqual("query", usync.Attrs["mode"]);

            IList<BinaryNode> children = usync.Children();
            BinaryNode list = null;
            for (int i = 0; i < children.Count; i++)
            {
                if (children[i].Tag == "list") list = children[i];
            }

            Assert.IsTrue(list != null, "with a list of who it asks about");
            Assert.IsTrue(list.Children().Count == 2, "one entry per JID");
            Assert.AreEqual(who[0], list.Children()[0].Attrs["jid"], "in the order they were given");
            Assert.AreEqual(who[1], list.Children()[1].Attrs["jid"]);

            Assert.Throws<ProtocolException>(delegate { StatusFeed.ListQuery(null, new SignalSessions.CounterRandom()); });
        }

        /// <summary>
        /// Where the statuses of the last day actually are.
        ///
        /// Not in a conversation: a status sync says so with its `syncType`
        /// (`InitialStatusV3`, 1) and its payload is `statusV3Messages` (field 3), a repeated
        /// `WebMessageInfo` with **no** `HistorySyncMsg` wrapper. A reader that only walked
        /// the conversations found nothing on a phone that had a day of statuses waiting.
        /// </summary>
        [Test]
        public static void AStatusSyncIsReadFromItsOwnList()
        {
            byte[] status = new ProtoWriter()
                .Bytes(1, new ProtoWriter()
                    .Text(1, StatusFeed.BroadcastJid)
                    .Text(3, "STATUS02")
                    .ToArray())
                .Bytes(2, new ProtoWriter().Text(1, "buongiorno").ToArray())
                .Varint(3, 1760000500)
                .Text(5, "15551230000:5@s.whatsapp.net")
                .ToArray();

            HistorySync sync = HistorySync.Decode(new ProtoWriter()
                .Varint(1, HistorySync.InitialStatusV3)
                .Bytes(3, status)
                .ToArray());

            IList<StatusPost> posts = StatusFeed.FromHistory(sync);

            Assert.IsTrue(posts.Count == 1, "one status in, one post out");
            Assert.AreEqual("STATUS02", posts[0].Id);
            // The author without their device: the feed shows a name, and the row it becomes
            // is keyed on the person, not on the phone they posted from.
            Assert.AreEqual("15551230000@s.whatsapp.net", posts[0].Author,
                "the participant is the author, not the list");
            Assert.AreEqual("buongiorno", posts[0].Text);
            Assert.AreEqual(1760000500u, posts[0].Timestamp);
        }

        /// <summary>A status this account posted is not somebody else's update.</summary>
        [Test]
        public static void OurOwnStatusIsNotInTheFeed()
        {
            byte[] status = new ProtoWriter()
                .Bytes(1, new ProtoWriter()
                    .Text(1, StatusFeed.BroadcastJid)
                    .Varint(2, 1)
                    .Text(3, "STATUS03")
                    .ToArray())
                .Bytes(2, new ProtoWriter().Text(1, "ciao").ToArray())
                .ToArray();

            HistorySync sync = HistorySync.Decode(new ProtoWriter().Bytes(3, status).ToArray());

            Assert.IsTrue(StatusFeed.FromHistory(sync).Count == 0,
                "a status this account wrote is not in the feed of other people's updates");
        }

        [Test]
        public static void AViewedReceiptNamesTheStatus()
        {
            BinaryNode receipt = StatusFeed.Viewed(StatusFeed.BroadcastJid, "STATUS01");

            Assert.AreEqual("receipt", receipt.Tag);
            Assert.AreEqual("STATUS01", receipt.Attrs["id"]);
            Assert.AreEqual(StatusFeed.BroadcastJid, receipt.Attrs["to"]);
            Assert.AreEqual("read", receipt.Attrs["type"], "a watched status is a read receipt");

            Assert.Throws<ProtocolException>(delegate { StatusFeed.Viewed(StatusFeed.BroadcastJid, null); });
        }
    }
}
