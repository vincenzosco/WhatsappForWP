using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The call node, both ways: built the way the app builds it and read the way the read
    /// loop reads it.
    ///
    /// What is pinned here is the contract the feature stands on: one node with one verb
    /// child, the id in every node of the same call, and a `Read` that answers null instead
    /// of throwing on anything that is not a call. The media is deliberately absent - see
    /// `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md` - so nothing here
    /// asserts a negotiation that does not exist yet.
    /// </summary>
    public static class CallSignalingTests
    {
        private const string Peer = "15551230000@s.whatsapp.net";
        private const string CallId = "CALLAAAA0001";

        [Test]
        public static void AnOfferIsACallNodeWithOneOfferChild()
        {
            BinaryNode offer = CallSignaling.Offer(CallId, Peer, false);

            Assert.AreEqual("call", offer.Tag);
            Assert.AreEqual(Peer, offer.Attrs["to"]);
            Assert.AreEqual(CallId, offer.Attrs["id"]);

            IList<BinaryNode> children = offer.Children();
            Assert.IsTrue(children != null && children.Count == 1, "one child, the verb");
            Assert.AreEqual("offer", children[0].Tag);
        }

        [Test]
        public static void AnOfferNamesItsMediaAsChildren()
        {
            BinaryNode voice = CallSignaling.Offer(CallId, Peer, false);
            BinaryNode video = CallSignaling.Offer(CallId, Peer, true);

            Assert.IsTrue(voice.Children()[0].Child("audio") != null, "a call always has audio");
            Assert.IsTrue(voice.Children()[0].Child("video") == null, "and video only when it is a video call");
            Assert.IsTrue(video.Children()[0].Child("video") != null, "a video offer says so");
        }

        [Test]
        public static void TheFourAnswersAreTheSameNodeWithAnotherVerb()
        {
            BinaryNode preAccept = CallSignaling.PreAccept(CallId, Peer);
            BinaryNode accept = CallSignaling.Accept(CallId, Peer);
            BinaryNode reject = CallSignaling.Reject(CallId, Peer, "busy");
            BinaryNode terminate = CallSignaling.Terminate(CallId, Peer, "timeout");

            Assert.AreEqual("preaccept", preAccept.Children()[0].Tag);
            Assert.AreEqual("accept", accept.Children()[0].Tag);
            Assert.AreEqual("reject", reject.Children()[0].Tag);
            Assert.AreEqual("terminate", terminate.Children()[0].Tag);

            Assert.AreEqual("busy", reject.Children()[0].Attrs["reason"], "a refusal says why");
            Assert.AreEqual("timeout", terminate.Children()[0].Attrs["reason"], "and so does an end");
        }

        [Test]
        public static void ARejectionWithNoReasonCarriesNoAttribute()
        {
            BinaryNode reject = CallSignaling.Reject(CallId, Peer, null);

            Assert.IsTrue(reject.Children()[0].Attrs["reason"] == null,
                "an empty reason is absent, not an empty attribute");
        }

        [Test]
        public static void ReadingAnOfferGivesTheCallItIsAbout()
        {
            CallSignal signal = CallSignaling.Read(CallSignaling.Offer(CallId, Peer, true));

            Assert.AreEqual(CallKind.Offer, signal.Kind);
            Assert.AreEqual(CallId, signal.CallId);
            Assert.AreEqual(Peer, signal.To);
            Assert.IsTrue(signal.IsVideo, "the video child is what makes it a video call");
            Assert.IsTrue(signal.Rings, "an offer is the one verb that rings");
        }

        [Test]
        public static void ReadingAVideoOfferByItsAttributeWorksToo()
        {
            // A sender that describes the media with an attribute instead of a child: the
            // parser reads both, because the exact shape of an offer is the part of this
            // protocol this client knows least about.
            var offer = new BinaryNode("offer");
            offer.Attrs.Add("media", "video");

            var node = new BinaryNode("call");
            node.Attrs.Add("id", CallId);
            node.Attrs.Add("to", Peer);
            node.Content = new List<BinaryNode> { offer };

            CallSignal signal = CallSignaling.Read(node);

            Assert.AreEqual(CallKind.Offer, signal.Kind);
            Assert.IsTrue(signal.IsVideo);
        }

        [Test]
        public static void TheEndOfACallNamesItsReason()
        {
            CallSignal signal = CallSignaling.Read(CallSignaling.Terminate(CallId, Peer, "timeout"));

            Assert.AreEqual(CallKind.Terminate, signal.Kind);
            Assert.AreEqual("timeout", signal.Reason);
            Assert.IsFalse(signal.Rings, "the end of a call does not ring");
        }

        [Test]
        public static void AVerticalThisClientHasNoAnswerForIsUnknownAndNotACall()
        {
            var node = new BinaryNode("call");
            node.Attrs.Add("id", CallId);
            node.Content = new List<BinaryNode> { new BinaryNode("transport") };

            Assert.AreEqual(CallKind.Transport, CallSignaling.Read(node).Kind,
                "a vertical that is named in the reference is named here");

            node.Children()[0].Tag = "rekey";
            Assert.AreEqual(CallKind.Unknown, CallSignaling.Read(node).Kind,
                "one that is not is Unknown, so the log can name it");
        }

        [Test]
        public static void ANonCallNodeIsNullAndNotAnException()
        {
            Assert.IsTrue(CallSignaling.Read(null) == null, "no node, no call");
            Assert.IsTrue(CallSignaling.Read(new BinaryNode("message")) == null,
                "a message is not a call, whatever its children say");
        }

        [Test]
        public static void ACallNodeWithNoVerbIsStillReadAsItsId()
        {
            var node = new BinaryNode("call");
            node.Attrs.Add("id", CallId);
            node.Attrs.Add("from", Peer);

            CallSignal signal = CallSignaling.Read(node);

            Assert.AreEqual(CallKind.Unknown, signal.Kind);
            Assert.AreEqual(CallId, signal.CallId, "the id is what an answer would need");
            Assert.AreEqual(Peer, signal.From);
        }

        [Test]
        public static void ACallIdInThePayloadIsRead()
        {
            // The shape the official client's own log describes: "no call-id in the payload
            // of incoming <call> node". An offer whose id is on the verb, not on the node.
            var offer = new BinaryNode("offer");
            offer.Attrs.Add("call-id", CallId);
            offer.Content = new List<BinaryNode> { new BinaryNode("audio") };

            var node = new BinaryNode("call");
            node.Attrs.Add("from", Peer);
            node.Content = new List<BinaryNode> { offer };

            CallSignal signal = CallSignaling.Read(node);

            Assert.AreEqual(CallId, signal.CallId, "the id is what an answer would need");
            Assert.AreEqual(CallKind.Offer, signal.Kind);
            Assert.AreEqual(Peer, signal.From);
            Assert.IsTrue(signal.Rings, "and an offer with its id is a call that rings");
        }

        [Test]
        public static void ACallIdOnTheNodeAsCallIdIsReadToo()
        {
            var node = new BinaryNode("call");
            node.Attrs.Add("call-id", CallId);
            node.Attrs.Add("to", Peer);
            node.Content = new List<BinaryNode> { new BinaryNode("terminate") };

            CallSignal signal = CallSignaling.Read(node);

            Assert.AreEqual(CallId, signal.CallId);
            Assert.AreEqual(CallKind.Terminate, signal.Kind);
        }

        [Test]
        public static void AnEmptyIdFallsThroughToTheSpellingThatHasOne()
        {
            var offer = new BinaryNode("offer");
            offer.Attrs.Add("call-id", CallId);

            var node = new BinaryNode("call");
            node.Attrs.Add("id", "");
            node.Content = new List<BinaryNode> { offer };

            Assert.AreEqual(CallId, CallSignaling.Read(node).CallId, "an empty attribute is not an id");
        }

        [Test]
        public static void TheNodesOwnIdWinsOverThePayload()
        {
            var offer = new BinaryNode("offer");
            offer.Attrs.Add("call-id", "SOMEONEELSES");

            var node = new BinaryNode("call");
            node.Attrs.Add("id", CallId);
            node.Content = new List<BinaryNode> { offer };

            Assert.AreEqual(CallId, CallSignaling.Read(node).CallId,
                "two ids in one node is a node to read by the outer one");
        }

        [Test]
        public static void BuildingACallWithNoIdOrNoPeerIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { CallSignaling.Offer(null, Peer, false); });
            Assert.Throws<ProtocolException>(delegate { CallSignaling.Offer(CallId, null, false); });
            Assert.Throws<ProtocolException>(delegate { CallSignaling.Accept(CallId, null); });
            Assert.Throws<ProtocolException>(delegate { CallSignaling.Terminate(null, Peer, "timeout"); });
        }

        [Test]
        public static void TheRejectTheClientAlreadySendsIsThisOne()
        {
            // `Calls.Reject` is the node that was shipped before the rest of the family
            // existed, and the two must not drift: it is built by this file now.
            BinaryNode old = Calls.Reject(CallId, Peer);
            BinaryNode now = CallSignaling.Reject(CallId, Peer, null);

            Assert.AreEqual("call", old.Tag);
            Assert.AreEqual(CallId, old.Attrs["id"]);
            Assert.AreEqual(Peer, old.Attrs["to"]);
            Assert.AreEqual(now.Children()[0].Tag, old.Children()[0].Tag, "the same verb");
            Assert.AreEqual(1, old.Children().Count, "and nothing else in it");
        }
    }
}
