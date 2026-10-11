using System.Collections.Generic;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// What each node the server sends *is*, decided once.
    ///
    /// The tag list is not a guess: it is the reference's own registration table
    /// (`/tmp/m4ref/node_modules/@whiskeysockets/baileys/lib/Socket/messages-recv.js`,
    /// `ws.on('CB:<tag>')` for `ack`, `call`, `chatstate`, `failure`, `ib`, `iq`,
    /// `message`, `notification`, `presence`, `receipt`, `stream:error`, `success`,
    /// `xmlstreamend`), and the `ib` children are its `switch (child.tag)` at line 279 -
    /// which is a *tag* and not a `type` attribute, so `NotificationType` reads the child's
    /// tag.
    ///
    /// One thing the table settles that the plan had wrong: the history bootstrap does not
    /// arrive as an `ib` child. The reference reads it out of a **message**
    /// (`Utils/history.js:131`, `protocolMessage?.historySyncNotification`), so an envelope
    /// carrying it is a `Message` here and the protocol-message path is where it is found.
    /// </summary>
    public static class InboundRouterTests
    {
        [Test]
        public static void EachTagBecomesItsOwnKind()
        {
            Assert.AreEqual(InboundKind.Message, InboundRouter.Classify(Node("message")));
            Assert.AreEqual(InboundKind.Receipt, InboundRouter.Classify(Node("receipt")));
            Assert.AreEqual(InboundKind.ChatState, InboundRouter.Classify(Node("chatstate")));
            Assert.AreEqual(InboundKind.Presence, InboundRouter.Classify(Node("presence")));
            Assert.AreEqual(InboundKind.Call, InboundRouter.Classify(Node("call")));
            Assert.AreEqual(InboundKind.Ack, InboundRouter.Classify(Node("ack")));
            Assert.AreEqual(InboundKind.Success, InboundRouter.Classify(Node("success")));
            Assert.AreEqual(InboundKind.Notification, InboundRouter.Classify(Node("ib")));
            Assert.AreEqual(InboundKind.Notification, InboundRouter.Classify(Node("notification")));
            Assert.AreEqual(InboundKind.Ignored, InboundRouter.Classify(Node("iq")),
                "an iq that is not the liveness probe is answered by whoever asked, not here");
        }

        [Test]
        public static void TheServersLivenessProbeIsAProbe()
        {
            var attributeForm = Node("iq");
            attributeForm.Attrs.Add("xmlns", SessionKeepAlive.ProbeNamespace);

            var childForm = Node("iq");
            var ping = Node("ping");
            ping.Attrs.Add("xmlns", SessionKeepAlive.ProbeNamespace);
            childForm.Content = new List<BinaryNode> { ping };

            Assert.AreEqual(InboundKind.Probe, InboundRouter.Classify(attributeForm),
                "the shape the phone saw on 2026-10-10");
            Assert.AreEqual(InboundKind.Probe, InboundRouter.Classify(childForm));
        }

        [Test]
        public static void AFailureAndAStreamErrorAreRefusals()
        {
            Assert.AreEqual(InboundKind.Refusal, InboundRouter.Classify(Node("failure")));
            Assert.AreEqual(InboundKind.Refusal, InboundRouter.Classify(Node("stream:error")));
            Assert.AreEqual(InboundKind.Refusal, InboundRouter.Classify(Node("xmlstreamend")),
                "the server closing the stream is the connection being over, whatever it is called");
        }

        [Test]
        public static void AnIbCarriesItsNotificationTag()
        {
            Assert.AreEqual("encrypt", InboundRouter.NotificationType(Ib("encrypt")));
            Assert.AreEqual("mediaretry", InboundRouter.NotificationType(Ib("mediaretry")));
            Assert.AreEqual("server_sync", InboundRouter.NotificationType(Ib("server_sync")));
            Assert.AreEqual("w:gp2", InboundRouter.NotificationType(Ib("w:gp2")));
            Assert.IsTrue(InboundRouter.NotificationType(Node("ib")) == null,
                "an ib with nothing in it names no notification");
            Assert.IsTrue(InboundRouter.NotificationType(Node("message")) == null,
                "a node that is not an ib has no notification tag");
        }

        [Test]
        public static void AMessageForAnotherDeviceIsStillAMessage()
        {
            var node = Node("message");
            node.Attrs.Add("to", "393000000000:2@s.whatsapp.net");

            Assert.AreEqual(InboundKind.Message, InboundRouter.Classify(node),
                "the classifier answers what the node is, not who it is for");
        }

        [Test]
        public static void NothingElseIsGuessed()
        {
            Assert.AreEqual(InboundKind.Ignored, InboundRouter.Classify(Node("xyz")),
                "a tag nobody knows is not dressed up as one that is known");
            Assert.AreEqual(InboundKind.Ignored, InboundRouter.Classify(null));
        }

        private static BinaryNode Node(string tag)
        {
            return new BinaryNode(tag);
        }

        private static BinaryNode Ib(string childTag)
        {
            var node = Node("ib");
            node.Content = new List<BinaryNode> { Node(childTag) };

            return node;
        }
    }
}
