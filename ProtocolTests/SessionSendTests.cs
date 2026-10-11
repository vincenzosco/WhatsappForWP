using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The two side channels of a conversation: the typing state this phone sends, and the
    /// receipt the server sends back about the messages it sent.
    ///
    /// The typing node's shape is the reference's own (`Socket/chats.js`): a `chatstate`
    /// carrying `from`, `to` and one child named after the state - not a `presence` with a
    /// type, which is what the online/offline node is and what the first guess would reach
    /// for.
    /// </summary>
    public static class SessionSendTests
    {
        [Test]
        public static void TheTypingNodeIsTheOneTheReferenceBuilds()
        {
            BinaryNode typing = Conversation.Typing("15559990000:3@s.whatsapp.net", "15551230000@s.whatsapp.net", Conversation.Composing);

            Assert.AreEqual("chatstate", typing.Tag);
            Assert.AreEqual("15559990000:3@s.whatsapp.net", typing.Attrs["from"], "the reference says who is composing");
            Assert.AreEqual("15551230000@s.whatsapp.net", typing.Attrs["to"]);

            var children = typing.Children();
            Assert.IsTrue(children != null && children.Count == 1, "one child, and it is the state");
            Assert.AreEqual("composing", children[0].Tag);
            Assert.IsTrue(children[0].Children() == null, "and the state carries nothing");
        }

        [Test]
        public static void StoppingTypingIsThePausedChild()
        {
            BinaryNode paused = Conversation.Typing("15559990000:3@s.whatsapp.net", "15551230000@s.whatsapp.net", Conversation.Paused);

            Assert.AreEqual("paused", paused.Children()[0].Tag);
            Assert.Throws<ProtocolException>(delegate
            {
                Conversation.Typing("1@s.whatsapp.net", "2@s.whatsapp.net", "available");
            });
        }

        [Test]
        public static void TheReadReceiptNamesEveryId()
        {
            var ids = new List<string>();
            ids.Add("3EB0AAAA0001");
            ids.Add("3EB0AAAA0002");
            ids.Add("3EB0AAAA0003");

            BinaryNode receipt = IncomingMessage.Receipt("15551230000@s.whatsapp.net", ids, "read", 1760000000);

            Assert.AreEqual("receipt", receipt.Tag);
            Assert.AreEqual("read", receipt.Attrs["type"]);
            Assert.AreEqual("15551230000@s.whatsapp.net", receipt.Attrs["to"]);
            Assert.AreEqual("3EB0AAAA0001", receipt.Attrs["id"], "the first id is the node's own");

            var answered = Conversation.Ids(receipt);
            Assert.AreEqual(3, answered.Count, "and the rest are in the list");
            Assert.AreEqual("3EB0AAAA0002", answered[1]);
            Assert.AreEqual("3EB0AAAA0003", answered[2]);
        }

        [Test]
        public static void AReceiptForOneMessageNamesThatOne()
        {
            var ids = new List<string>();
            ids.Add("3EB0BBBB0001");

            BinaryNode receipt = IncomingMessage.Receipt("15551230000@s.whatsapp.net", ids, "delivery", 1760000000);

            Assert.IsTrue(receipt.Child("list") == null, "a single message needs no list");
            Assert.AreEqual(1, Conversation.Ids(receipt).Count);
            Assert.IsFalse(Conversation.IsRead(receipt), "delivered is not read");
        }

        [Test]
        public static void TheReceiptNamesTheChatAndWhetherItWasRead()
        {
            var receipt = new BinaryNode("receipt");
            receipt.Attrs.Add("from", "15551230000@s.whatsapp.net");
            receipt.Attrs.Add("id", "3EB0CCCC0001");
            receipt.Attrs.Add("type", "read");

            Assert.AreEqual("15551230000@s.whatsapp.net", Conversation.From(receipt));
            Assert.IsTrue(Conversation.IsRead(receipt));

            var self = new BinaryNode("receipt");
            self.Attrs.Add("from", "15559990000@s.whatsapp.net");
            self.Attrs.Add("id", "3EB0CCCC0002");
            self.Attrs.Add("type", "read-self");

            Assert.IsTrue(Conversation.IsRead(self), "our own other device reading it is the same news");
        }

        [Test]
        public static void ANonReceiptIsNotOne()
        {
            var message = new BinaryNode("message");
            message.Attrs.Add("id", "3EB0DDDD");

            Assert.AreEqual(0, Conversation.Ids(message).Count, "a message is not a receipt");
            Assert.IsTrue(Conversation.From(message) == null);
            Assert.IsFalse(Conversation.IsRead(message));
        }
    }
}
