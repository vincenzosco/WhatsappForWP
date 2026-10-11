using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WaProto;
using ProtocolTests.Vectors;

namespace ProtocolTests
{
    /// <summary>
    /// The chat list as a rule, over the reference's own history bytes.
    ///
    /// What these tests are really about is what the pages will show: the order of the rows,
    /// the order of the messages inside them, the unread count, and - the one that bites -
    /// that the same message twice is one message. WhatsApp re-delivers chunks and messages
    /// after every reconnect, so "twice" is not an edge case here, it is Tuesday.
    /// </summary>
    public static class NativeChatsTests
    {
        [Test]
        public static void AHistorySyncBecomesRowsInItsOwnOrder()
        {
            var chats = new NativeChats();
            chats.ApplyHistory(HistorySync.Decode(Hex(HistoryFixture.Plain)));

            Assert.AreEqual(2, chats.Rows.Count);
            Assert.AreEqual("393000000000@s.whatsapp.net", chats.Rows[0].Jid, "the newest conversation is first");
            Assert.AreEqual("120363000000000000@g.us", chats.Rows[1].Jid);
            Assert.AreEqual(2, chats.Rows[0].UnreadCount, "the server's own count");
            Assert.AreEqual(0, chats.Rows[1].UnreadCount);
            Assert.IsTrue(chats.Rows[1].IsGroup);

            Assert.AreEqual(2, chats.Rows[0].Messages.Count, "oldest first, as the payload has them");
            Assert.AreEqual("ci sei?", chats.Rows[0].Messages[0].Text);
            Assert.AreEqual("sono qui", chats.Rows[0].Messages[1].Text);
        }

        [Test]
        public static void TheSameIdTwiceBecomesOneMessage()
        {
            var chats = new NativeChats();
            HistorySync sync = HistorySync.Decode(Hex(HistoryFixture.Plain));
            chats.ApplyHistory(sync);

            var again = new HistorySync();
            again.Conversations.Add(sync.Conversations[0]);

            chats.ApplyHistory(again);

            Assert.AreEqual(2, chats.Rows[0].Messages.Count, "a re-delivered chunk adds no second copy");
            Assert.AreEqual(2, chats.Rows.Count, "and no second chat");
        }

        [Test]
        public static void AnIncomingMessageForAnUnknownChatCreatesTheRow()
        {
            var chats = new NativeChats();
            chats.ApplyHistory(HistorySync.Decode(Hex(HistoryFixture.Plain)));

            bool added = chats.AddIncoming(Message("393222222222@s.whatsapp.net", "3EB0EEEE0001", "ciao", 1791610000L));

            Assert.IsTrue(added, "a message from someone new is not dropped");
            Assert.AreEqual(3, chats.Rows.Count);
            Assert.AreEqual("393222222222@s.whatsapp.net", chats.Rows[0].Jid, "and it is the newest row");
            Assert.AreEqual(1, chats.Rows[0].UnreadCount);
            Assert.AreEqual(1, chats.Rows[0].Messages.Count);
            Assert.AreEqual("ciao", chats.Rows[0].Messages[0].Text);
        }

        [Test]
        public static void AnIncomingMessageIsCountedOnce()
        {
            var chats = new NativeChats();
            HistoryMessage message = Message("393000000000@s.whatsapp.net", "3EB0FFFF0001", "ehi", 1791610000L);

            Assert.IsTrue(chats.AddIncoming(message), "the first time it is new");
            Assert.IsFalse(chats.AddIncoming(message), "and the second time it is not");
            Assert.AreEqual(1, chats.Rows[0].UnreadCount);
            Assert.AreEqual(1, chats.Rows[0].Messages.Count);
        }

        [Test]
        public static void AnOutgoingMessageDoesNotCountAsUnread()
        {
            var chats = new NativeChats();
            chats.ApplyHistory(HistorySync.Decode(Hex(HistoryFixture.Plain)));
            HistoryConversation group = chats.Rows[1];

            chats.AddOutgoing(group.Jid, "3EB0AAAA0003", "ci sono", 1791620000L);

            Assert.AreEqual(0, group.UnreadCount, "writing does not make a chat unread");
            Assert.AreEqual(2, group.Messages.Count);
            Assert.IsTrue(group.Messages[1].IsFromMe);
            Assert.AreEqual(group.Jid, chats.Rows[0].Jid, "and it moves the chat to the top");
        }

        [Test]
        public static void ASyncWithProgressOneHundredIsTheEndOfTheHistory()
        {
            var chats = new NativeChats();
            var partial = new HistorySync();
            partial.SyncType = HistorySync.Recent;
            partial.Progress = 83;

            chats.ApplyHistory(partial);
            Assert.IsFalse(chats.IsSyncComplete, "83 per cent of the way is not the end");

            var last = new HistorySync();
            last.SyncType = HistorySync.Recent;
            last.Progress = 100;

            chats.ApplyHistory(last);
            Assert.IsTrue(chats.IsSyncComplete);
        }

        [Test]
        public static void AnInitialBootstrapCompletesTheSyncByItself()
        {
            var chats = new NativeChats();
            var bootstrap = new HistorySync();
            bootstrap.SyncType = HistorySync.InitialBootstrap;
            bootstrap.Progress = 0;

            chats.ApplyHistory(bootstrap);

            Assert.IsTrue(chats.IsSyncComplete, "the reference fires the list on a bootstrap without waiting for progress");
        }

        private static HistoryMessage Message(string jid, string id, string text, long timestamp)
        {
            var message = new HistoryMessage();
            message.Jid = jid;
            message.Id = id;
            message.Text = text;
            message.Timestamp = timestamp;

            return message;
        }

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)System.Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }
    }
}
