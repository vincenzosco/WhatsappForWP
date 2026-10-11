using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WaProto;
using ProtocolTests.Vectors;

namespace ProtocolTests
{
    /// <summary>
    /// The history payload, read against the reference's own bytes.
    ///
    /// The fixture carries the bootstrap the reference encoded, so every assertion below is
    /// about a value that came off WhatsApp's encoder rather than out of this client's
    /// opinion of the format - including the field numbers, which the fixture's own comment
    /// prints one by one.
    /// </summary>
    public static class HistorySyncTests
    {
        [Test]
        public static void TheReferenceBootstrapBecomesTwoConversations()
        {
            HistorySync sync = HistorySync.Decode(Hex(HistoryFixture.Plain));

            Assert.AreEqual(HistorySync.Full, sync.SyncType, "the fixture is a full sync");
            Assert.AreEqual(50, sync.Progress);
            Assert.AreEqual(2, sync.Conversations.Count);

            HistoryConversation chat = sync.Conversations[0];
            Assert.AreEqual("393000000000@s.whatsapp.net", chat.Jid);
            Assert.AreEqual("Marco", chat.Name);
            Assert.AreEqual(2, chat.UnreadCount, "two unread messages arrived with the chat");
            Assert.IsFalse(chat.IsGroup);
            Assert.AreEqual(1791600000L, chat.ConversationTimestamp);

            Assert.AreEqual(2, chat.Messages.Count, "both messages of the chat are there");
            Assert.AreEqual("3EB0AAAA0001", chat.Messages[0].Id, "oldest first");
            Assert.AreEqual("ci sei?", chat.Messages[0].Text);
            Assert.IsFalse(chat.Messages[0].IsFromMe);
            Assert.AreEqual("3EB0AAAA0002", chat.Messages[1].Id);
            Assert.AreEqual("sono qui", chat.Messages[1].Text);
            Assert.IsTrue(chat.Messages[1].IsFromMe, "the second one is ours");
            Assert.AreEqual(1791600000L, chat.Messages[0].Timestamp);
        }

        [Test]
        public static void TheSecondConversationIsTheGroup()
        {
            HistorySync sync = HistorySync.Decode(Hex(HistoryFixture.Plain));
            HistoryConversation group = sync.Conversations[1];

            Assert.AreEqual("120363000000000000@g.us", group.Jid);
            Assert.AreEqual("Gruppo", group.Name);
            Assert.IsTrue(group.IsGroup, "a @g.us jid is a group");
            Assert.AreEqual(1, group.Messages.Count);

            HistoryMessage message = group.Messages[0];
            Assert.AreEqual("buongiorno", message.Text);
            Assert.IsFalse(message.IsFromMe);
            Assert.AreEqual("393111111111@s.whatsapp.net", message.Sender, "a group message names who wrote it");
            Assert.AreEqual("Luca", message.SenderName);
            Assert.IsTrue(message.Encoded != null, "the encoded message is kept for the media path");
        }

        [Test]
        public static void ThePushNameArrivesWithTheHistory()
        {
            HistorySync sync = HistorySync.Decode(Hex(HistoryFixture.Plain));

            Assert.AreEqual(1, sync.PushNames.Count);
            Assert.AreEqual("393000000000@s.whatsapp.net", sync.PushNames[0].Jid);
            Assert.AreEqual("Marco Rossi", sync.PushNames[0].Name);
        }

        [Test]
        public static void TheInlineBootstrapInflatesToTheSameBytes()
        {
            byte[] plain = Zlib.Inflate(Hex(HistoryFixture.Inline));

            Assert.ByteArraysEqual(Hex(HistoryFixture.Plain), plain);

            // And the two agree on what the history is, which is the point of inflating it.
            Assert.AreEqual(2, HistorySync.Decode(plain).Conversations.Count);
        }

        [Test]
        public static void TheNotificationCarriesTheMediaItIsHiddenBehind()
        {
            HistorySyncNotification notification = HistorySyncNotification.Decode(Hex(HistoryFixture.Notification));

            // The bytes are compared as bytes: `Assert.AreEqual` boxes its arguments, and a
            // boxed int 2 is not a boxed byte 2 - it fails with "expected <2> but was <2>",
            // which is how the M4 notes learned the same lesson.
            Assert.AreEqual(32, notification.MediaKey.Length);
            Assert.AreEqual((byte)2, notification.MediaKey[0]);
            Assert.AreEqual(32, notification.FileSha256.Length);
            Assert.AreEqual((byte)1, notification.FileSha256[0]);
            Assert.AreEqual(32, notification.FileEncSha256.Length);
            Assert.AreEqual((byte)3, notification.FileEncSha256[0]);
            Assert.AreEqual("/v/t62/x", notification.DirectPath);
            Assert.AreEqual(1234L, notification.FileLength);
            Assert.AreEqual(HistorySync.Full, notification.SyncType);
            Assert.AreEqual(1, notification.ChunkOrder);
            Assert.AreEqual("3EB0CCCC", notification.OriginalMessageId);
        }

        [Test]
        public static void TheNotificationIsFoundInsideTheMessageThatCarriesIt()
        {
            // The envelope the server sends: a Message whose protocolMessage is the
            // notification. The protocol message's own number for it is 6 - encoding a
            // ProtocolMessage with only a notification starts with 0x32, and 0x32 >> 3 is 6 -
            // which is why this test builds the wrapper the way the server does.
            byte[] notification = Hex(HistoryFixture.Notification);
            byte[] protocol = new ProtoWriter().Bytes(HistorySyncNotification.ProtocolField, notification).ToArray();
            byte[] message = new ProtoWriter().Bytes(Message.ProtocolField, protocol).ToArray();

            HistorySyncNotification found = HistorySyncNotification.From(Message.Decode(message));

            Assert.IsTrue(found != null, "a history sync arrives as a message");
            Assert.AreEqual("/v/t62/x", found.DirectPath);
        }

        [Test]
        public static void AMessageWithNoWordsKeepsItsBytes()
        {
            // What a photo or a voice note looks like in the history: a message with no
            // conversation text at all. It has to survive the read - `Encoded` is the only
            // way back to the media - and it must not be turned into an empty bubble.
            byte[] encoded = new ProtoWriter().Bytes(31, new byte[] { 9, 9, 9 }).ToArray();
            byte[] web = new ProtoWriter()
                .Bytes(1, new ProtoWriter().Text(1, "393000000000@s.whatsapp.net").Text(3, "3EB0DDDD").ToArray())
                .Bytes(2, encoded)
                .Varint(3, 1791600000)
                .ToArray();
            byte[] conversation = new ProtoWriter()
                .Text(1, "393000000000@s.whatsapp.net")
                .Bytes(2, new ProtoWriter().Bytes(1, web).Varint(2, 1).ToArray())
                .ToArray();
            byte[] sync = new ProtoWriter().Varint(1, HistorySync.Full).Bytes(2, conversation).ToArray();

            HistoryMessage message = HistorySync.Decode(sync).Conversations[0].Messages[0];

            Assert.IsTrue(message.Text == null, "a message with no words has no words, and that is not an error");
            Assert.IsTrue(message.Encoded != null, "the bytes are kept: they are the only way to the media");
            Assert.AreEqual("3EB0DDDD", message.Id);
            Assert.AreEqual("393000000000@s.whatsapp.net", message.Jid);
            Assert.AreEqual(1791600000L, message.Timestamp);
        }

        [Test]
        public static void AnUnknownFieldCostsNothing()
        {
            byte[] plain = Hex(HistoryFixture.Plain);
            var longer = new List<byte>(plain);
            longer.AddRange(new ProtoWriter().Text(99, "something this client has never heard of").ToArray());

            HistorySync sync = HistorySync.Decode(longer.ToArray());

            Assert.AreEqual(2, sync.Conversations.Count, "a field nobody knows is skipped, not fatal");
            Assert.AreEqual("ci sei?", sync.Conversations[0].Messages[0].Text);
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
