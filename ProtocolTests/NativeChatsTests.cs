using System.Collections.Generic;
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

        /// <summary>
        /// The chunk answers the messages the app draws, and nothing else.
        ///
        /// A sync that filled the rows and handed no message over was the list of chats that all
        /// open empty: the rows are what the person scrolls, the messages are what they read.
        /// </summary>
        [Test]
        public static void TheMessagesAChunkBroughtAreTheOnesTheAppDraws()
        {
            var chats = new NativeChats();
            IList<HistoryMessage> applied = chats.ApplyHistory(HistorySync.Decode(Hex(HistoryFixture.Plain)));

            Assert.AreEqual(3, applied.Count, "the person's two messages and the group's one");
            Assert.AreEqual("ci sei?", applied[0].Text);
            Assert.AreEqual("393000000000@s.whatsapp.net", applied[0].Jid,
                "the chat travels with the message, and the row's own JID is what the chat is called");
            Assert.IsTrue(applied[0].IsHistory,
                "and it is marked as history: nothing in here arrived now");
            Assert.AreEqual("buongiorno", applied[2].Text, "the group's message is in the same answer");
            Assert.IsTrue(chats.Rows[0].Messages[0].IsHistory,
                "the flag is on the row's own copy too, not only on the answer");
        }

        [Test]
        public static void AChunkDeliveredTwiceHandsTheAppNothingTwice()
        {
            var chats = new NativeChats();
            HistorySync sync = HistorySync.Decode(Hex(HistoryFixture.Plain));

            Assert.AreEqual(3, chats.ApplyHistory(sync).Count, "the first time it is the whole chunk");

            var again = new HistorySync();
            again.Conversations.Add(sync.Conversations[0]);

            Assert.AreEqual(0, chats.ApplyHistory(again).Count,
                "a re-delivered chunk adds no message, so it hands none over");
        }

        [Test]
        public static void AMessageThisClientCannotDrawIsNotHandedToTheApp()
        {
            // A message whose payload is a protocol message: no words and no media, which is
            // what a sticker or a reaction looks like to this client. It stays in the row - the
            // order, the preview and the dedupe are the row's - and it is not a bubble.
            byte[] inner = new ProtoWriter().Bytes(WhatsappApp.Protocol.WaProto.Message.ProtocolField,
                new ProtoWriter().Varint(2, 5).ToArray()).ToArray();
            byte[] key = new ProtoWriter().Text(1, "393444444444@s.whatsapp.net")
                .Text(3, "3EB0NNNN0001").ToArray();
            byte[] web = new ProtoWriter().Bytes(1, key).Bytes(2, inner).Varint(3, 1791610500).ToArray();
            byte[] conversation = new ProtoWriter().Text(1, "393444444444@s.whatsapp.net")
                .Bytes(2, new ProtoWriter().Bytes(1, web).ToArray()).ToArray();
            HistorySync sync = HistorySync.Decode(new ProtoWriter()
                .Varint(1, HistorySync.Recent).Bytes(2, conversation).ToArray());

            var chats = new NativeChats();
            IList<HistoryMessage> applied = chats.ApplyHistory(sync);

            Assert.AreEqual(0, applied.Count, "a message with nothing to draw is not an empty bubble");
            Assert.AreEqual(1, chats.Rows[0].Messages.Count, "and it is still a message of the chat");
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

        /// <summary>
        /// A chat the reader has opened does not come back with the messages it already
        /// read: the count is cleared here, and a chunk WhatsApp re-delivers carries the old
        /// number - it must not be applied when it brought nothing new (the failure the
        /// copy on disk documents: "re-applying it brought back a count the reader had
        /// already cleared").
        /// </summary>
        [Test]
        public static void AReadChatDoesNotCountItsOldMessagesAgain()
        {
            var chats = new NativeChats();
            HistorySync sync = HistorySync.Decode(Hex(HistoryFixture.Plain));
            chats.ApplyHistory(sync);

            string jid = chats.Rows[0].Jid;
            Assert.AreEqual(2, chats.Rows[0].UnreadCount, "the server's own count");

            chats.MarkRead(jid);
            Assert.AreEqual(0, chats.UnreadFor(jid), "the reader has seen them");

            var again = new HistorySync();
            again.Conversations.Add(sync.Conversations[0]);
            chats.ApplyHistory(again);

            Assert.AreEqual(0, chats.Rows[0].UnreadCount,
                "a chunk whose messages are all already here does not put the count back");

            Assert.IsTrue(chats.AddIncoming(Message(jid, "3EB0ABAB0001", "di nuovo", 1791630000L)));
            Assert.AreEqual(1, chats.UnreadFor(jid), "and the message that arrives next is one");
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
