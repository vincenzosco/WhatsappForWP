using System;
using System.Collections.Generic;
using System.Globalization;
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
        public static void AnEmptyWireProfileLeavesTheOfferAsItWas()
        {
            BinaryNode without = CallSignaling.Offer(CallId, Peer, "me@lid", null,
                OneAudio(), NoVideo(), new List<CallKeyPayload>(), NoCandidate());
            BinaryNode empty = CallSignaling.Offer(CallId, Peer, "me@lid", null,
                OneAudio(), NoVideo(), new List<CallKeyPayload>(), NoCandidate(), new CallWireProfile());

            Assert.AreEqual(CallSignaling.Dump(without), CallSignaling.Dump(empty));
        }

        [Test]
        public static void AWireProfileWritesTheCallerFieldsOnTheVerb()
        {
            var profile = new CallWireProfile();
            profile.CallerPn = "393892672185";
            profile.CallerCountryCode = "IT";
            profile.Joinable = true;

            BinaryNode offer = CallSignaling.Offer(CallId, Peer, "me@lid", null,
                OneAudio(), NoVideo(), new List<CallKeyPayload>(), NoCandidate(), profile);
            BinaryNode verb = offer.Children()[0];

            Assert.AreEqual("offer", verb.Tag);
            Assert.AreEqual("393892672185", verb.Attrs["caller_pn"]);
            Assert.AreEqual("IT", verb.Attrs["caller_country_code"]);
            Assert.AreEqual("true", verb.Attrs["joinable"]);
        }

        [Test]
        public static void AWireProfileReadsTheFieldsOfACapturedOffer()
        {
            var metadata = new BinaryNode("metadata");
            metadata.Content = new byte[] { 1, 2, 3 };

            var verb = new BinaryNode("offer");
            verb.Attrs.Add("call-creator", "231790946025638@lid");
            verb.Attrs.Add("call-id", CallId);
            verb.Attrs.Add("caller_pn", "393492556507");
            verb.Attrs.Add("caller_country_code", "IT");
            verb.Attrs.Add("joinable", "false");
            verb.Content = new List<BinaryNode> { metadata };

            var call = new BinaryNode("call");
            call.Attrs.Add("from", "231790946025638@lid");
            call.Content = new List<BinaryNode> { verb };

            CallWireProfile profile = CallWireProfile.FromCallNode(call);

            Assert.IsTrue(profile != null, "the fields of a captured offer are read back");
            Assert.AreEqual("393492556507", profile.CallerPn);
            Assert.AreEqual("IT", profile.CallerCountryCode);
            Assert.AreEqual("false", profile.Joinable.Value ? "true" : "false");
            Assert.IsTrue(profile.Metadata != null, "metadata comes back whole");
        }

        private static IList<CallAudio> OneAudio()
        {
            var audio = new List<CallAudio>();
            audio.Add(new CallAudio { Codec = "opus", Rate = CallSignaling.DefaultAudioRate });
            return audio;
        }

        private static IList<CallVideoDescription> NoVideo()
        {
            return new List<CallVideoDescription>();
        }

        private static IList<CallCandidate> NoCandidate()
        {
            return new List<CallCandidate>();
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

        /// <summary>
        /// The offer the official client sends, byte for byte in shape: media, then the
        /// encrypted key, then the candidates. This is the node a call needs to be answerable -
        /// without `enc` the other phone cannot decrypt the media, and without `te` it cannot
        /// reach this one.
        /// </summary>
        [Test]
        public static void AnOfferCarriesItsKeyItsCandidatesAndItsMedia()
        {
            var audio = new List<CallAudio> { new CallAudio { Codec = "opus", Rate = 16000 } };
            var video = new List<CallVideoDescription>
            {
                new CallVideoDescription
                {
                    Codec = "h.264", ScreenWidth = 480, ScreenHeight = 800, Orientation = 0
                }
            };
            var key = new CallKeyPayload { Type = "msg", Version = 2, Ciphertext = new byte[] { 1, 2, 3 } };
            var candidates = new List<CallCandidate>
            {
                new CallCandidate { Priority = 0, Data = new byte[] { 9, 8 } }
            };

            BinaryNode offer = CallSignaling.Offer(CallId, Peer, audio, video, key, candidates).Children()[0];

            Assert.AreEqual(CallId, offer.Attrs["call-id"], "the verb says which call it is about");
            Assert.AreEqual("opus", offer.Child("audio").Attrs["enc"]);
            Assert.AreEqual("16000", offer.Child("audio").Attrs["rate"]);
            Assert.AreEqual("h.264", offer.Child("video").Attrs["enc"]);
            Assert.AreEqual("480", offer.Child("video").Attrs["screen_width"]);

            BinaryNode enc = offer.Child("enc");
            Assert.AreEqual("msg", enc.Attrs["type"], "the ciphertext type is the Signal one");
            Assert.AreEqual("2", enc.Attrs["v"], "the call-key version the server expects");
            Assert.IsTrue(enc.Bytes() != null && enc.Bytes().Length == 3, "the ciphertext is the content");

            BinaryNode te = offer.Child("destination").Child("te");
            Assert.AreEqual("0", te.Attrs["priority"], "a candidate has its priority");
            Assert.IsTrue(te.Bytes() != null && te.Bytes().Length == 2, "and its bytes");
        }

        /// <summary>
        /// The offer the server routes: `call-creator` on the verb, and the children in the
        /// order the reconstructions pin - narrow audio, wide audio, net, capability,
        /// destination with its candidates, the keys, then `encopt`. A mis-ordered offer is
        /// refused outright (error 439), and a missing `call-creator` is not a call the server
        /// can attribute: both were true of the offer that made no phone ring.
        /// </summary>
        [Test]
        public static void AnOfferNamesItsCreatorAndTheChildrenInTheOrderTheServerReads()
        {
            const string Caller = "39389@s.whatsapp.net";

            var audio = new List<CallAudio>
            {
                new CallAudio { Codec = "opus", Rate = 8000 },
                new CallAudio { Codec = "opus", Rate = 16000 }
            };
            var keys = new List<CallKeyPayload>
            {
                new CallKeyPayload { Type = "msg", Version = 2, Ciphertext = new byte[] { 1 } }
            };
            var candidates = new List<CallCandidate>
            {
                new CallCandidate { Priority = 0, Data = new byte[] { 9 } }
            };

            BinaryNode offer = CallSignaling.Offer(CallId, Peer, Caller, audio, null, keys, candidates)
                .Children()[0];

            Assert.AreEqual(Caller, offer.Attrs["call-creator"], "the call says who created it");

            IList<BinaryNode> children = offer.Children();
            Assert.AreEqual(7, children.Count, "two audio, net, capability, destination, enc, encopt");

            Assert.AreEqual("audio", children[0].Tag);
            Assert.AreEqual("8000", children[0].Attrs["rate"], "the narrow stream comes first");
            Assert.AreEqual("16000", children[1].Attrs["rate"], "then the wide one");
            Assert.AreEqual("net", children[2].Tag);
            Assert.AreEqual("3", children[2].Attrs["medium"], "the medium the offer names");
            Assert.AreEqual("capability", children[3].Tag);
            Assert.AreEqual("1", children[3].Attrs["ver"]);
            Assert.AreEqual(7, children[3].Bytes().Length, "the blob both reconstructions agree on");
            Assert.AreEqual("destination", children[4].Tag, "the candidates live inside it");
            Assert.IsTrue(children[4].Child("te") != null, "and one is an endpoint");
            Assert.AreEqual("enc", children[5].Tag, "the key comes after the way back");
            Assert.AreEqual("encopt", children[6].Tag, "and the key schedule is last");
            Assert.AreEqual("2", children[6].Attrs["keygen"], "the v2 schedule");
        }

        /// <summary>
        /// An offer that opens a session says who it is, and says it after the keys.
        ///
        /// The rule is the message path's, and the offer was the one place it was missing: a
        /// recipient that has to verify a pre-key session is entitled to ignore the key when
        /// nothing proves the device that sent it - two offers on 4 November 2026, five
        /// pre-key keys each, and not one ack from the server.
        /// </summary>
        [Test]
        public static void AnOfferThatOpensASessionCarriesTheDeviceIdentityLast()
        {
            var keys = new List<CallKeyPayload>
            {
                new CallKeyPayload { Type = "pkmsg", Version = 2, Ciphertext = new byte[] { 1 } }
            };
            var identity = new byte[] { 7, 7, 9 };
            var audio = new List<CallAudio>
            {
                new CallAudio { Codec = "opus", Rate = 8000 },
                new CallAudio { Codec = "opus", Rate = 16000 }
            };
            var candidates = new List<CallCandidate>
            {
                new CallCandidate { Priority = 0, Data = new byte[] { 9 } }
            };

            BinaryNode offer = CallSignaling.Offer(CallId, Peer, "39389@s.whatsapp.net", identity,
                audio, null, keys, candidates).Children()[0];

            IList<BinaryNode> children = offer.Children();
            BinaryNode last = children[children.Count - 1];

            Assert.AreEqual(8, children.Count, "the seven of the offer and the identity");
            Assert.AreEqual("device-identity", last.Tag, "the identity a pre-key offer needs comes last");
            Assert.AreEqual(3, last.Bytes().Length, "and it is this account's signed identity");
            Assert.AreEqual((byte)7, last.Bytes()[0], "the first byte of it");
            Assert.AreEqual("encopt", children[6].Tag, "the key schedule stays before it");
        }

        /// <summary>
        /// An offer whose every key rides an established session carries no identity: there is
        /// nothing to verify, and a node the reference does not write is a node the server has
        /// no reason to have to read.
        /// </summary>
        [Test]
        public static void AnOfferWithSessionKeysOnlyCarriesNoIdentity()
        {
            var keys = new List<CallKeyPayload>
            {
                new CallKeyPayload { Type = "msg", Version = 2, Ciphertext = new byte[] { 1 } }
            };

            BinaryNode offer = CallSignaling.Offer(CallId, Peer, "39389@s.whatsapp.net",
                new byte[] { 7, 7, 9 }, new List<CallAudio>(), null, keys,
                new List<CallCandidate>()).Children()[0];

            IList<BinaryNode> children = offer.Children();
            Assert.AreEqual("encopt", children[children.Count - 1].Tag, "no identity to prove");
        }

        /// <summary>
        /// The call node names the caller, the callee and the stanza id: the three attributes
        /// the spec's own examples write and the ones the server routes by.
        /// </summary>
        [Test]
        public static void TheCallNodeNamesTheCallerAndTheCallee()
        {
            BinaryNode call = CallSignaling.Offer(CallId, Peer, "39389@s.whatsapp.net:4",
                new List<CallAudio>(), null, new List<CallKeyPayload>(), null);

            Assert.AreEqual("39389@s.whatsapp.net:4", call.Attrs["from"], "the caller is on the node");
            Assert.AreEqual(Peer, call.Attrs["to"], "and the callee");
            Assert.AreEqual(CallId, call.Attrs["id"], "and the id the ack will name");
            Assert.AreEqual(CallSignaling.ClientVersion, call.Attrs["version"],
                "the version the callee reads, as both captured offers carry it");
            Assert.AreEqual(CallSignaling.ClientPlatform, call.Attrs["platform"], "and the platform");
            Assert.AreEqual("0", call.Attrs["offline"], "an offer that is not an offline copy");
            Assert.IsTrue(!string.IsNullOrEmpty(call.Attrs["t"]), "and the moment it was made");
        }

        /// <summary>
        /// The line the log prints about an offer: tags and attribute names, never a key.
        /// </summary>
        /// <summary>
        /// An offer the server kept while the app was closed says how old it is, and a call that
        /// waited minutes is not a call to answer.
        ///
        /// The log of 4 November 2026: the same offer, `offline="1"`, thirteen minutes old, put
        /// on the screen at every start.
        /// </summary>
        [Test]
        public static void AnOfferThatWaitedInTheQueueIsNotACallToAnswer()
        {
            CallSignal signal = CallSignaling.Read(OfflineOffer("1", CallSignaling.UnixTime() - 780));

            Assert.IsTrue(signal.Offline, "the copy says it was kept");
            Assert.IsTrue(signal.AgeSeconds >= 779, "and how long it waited");
            Assert.IsTrue(CallSignaling.IsStale(signal), "thirteen minutes is not a call to answer");
        }

        /// <summary>A call that is happening is never stale, whatever its stamp says.</summary>
        [Test]
        public static void ACallThatIsLiveIsNeverStale()
        {
            CallSignal signal = CallSignaling.Read(OfflineOffer("0", CallSignaling.UnixTime() - 3600));

            Assert.IsTrue(!CallSignaling.IsStale(signal), "a live node is answered");
        }

        /// <summary>And neither is a copy the server handed over at once.</summary>
        [Test]
        public static void AnOfferFromASecondAgoIsNotStale()
        {
            CallSignal signal = CallSignaling.Read(OfflineOffer("1", CallSignaling.UnixTime()));

            Assert.IsTrue(!CallSignaling.IsStale(signal), "a call still ringing is answered");
        }

        /// <summary>
        /// The ack of a call: its own id back, to the JID it came from, with `call` as the
        /// class - the shape the message path already uses, which is how a stanza leaves the
        /// offline queue.
        /// </summary>
        [Test]
        public static void TheAckOfACallNamesTheStanzaAndItsClass()
        {
            BinaryNode call = new BinaryNode("call");
            call.Attrs.Add("from", Peer);
            call.Attrs.Add("id", CallId);

            BinaryNode ack = IncomingMessage.Ack(call);

            Assert.AreEqual("ack", ack.Tag);
            Assert.AreEqual(CallId, ack.Attrs["id"], "the server's own id back");
            Assert.AreEqual(Peer, ack.Attrs["to"], "to the JID it came from");
            Assert.AreEqual("call", ack.Attrs["class"], "with the tag as the class");
        }

        /// <summary>An offer node stamped as the log shows it, for the three tests above.</summary>
        private static BinaryNode OfflineOffer(string offline, long timestamp)
        {
            var call = new BinaryNode("call");
            call.Attrs.Add("from", Peer);
            call.Attrs.Add("to", "15550001111@s.whatsapp.net");
            call.Attrs.Add("id", CallId);
            call.Attrs.Add("offline", offline);
            call.Attrs.Add("t", timestamp.ToString(CultureInfo.InvariantCulture));

            var offer = new BinaryNode("offer");
            offer.Attrs.Add("call-id", CallId);
            offer.Attrs.Add("call-creator", Peer);
            call.Content = new List<BinaryNode> { offer };

            return call;
        }

        /// <summary>
        /// The dump: every field named with its value, and the key counted but never written.
        ///
        /// It is how a call that does not ring gets explained - the real offer of 4 November
        /// carries `device_class`, `joinable`, `metadata`, `rte`, `voip_settings` and this client
        /// carries none of them - without a key ever reaching the log file.
        /// </summary>
        [Test]
        public static void TheDumpOfAnOfferSaysEveryFieldAndWithholdsTheKey()
        {
            var offer = new BinaryNode("offer");
            offer.Attrs.Add("call-id", CallId);
            offer.Attrs.Add("device_class", "5513196007865");
            offer.Attrs.Add("joinable", "0");

            var audio = new BinaryNode("audio");
            audio.Attrs.Add("codec", "opus");
            audio.Attrs.Add("rate", "16000");

            var enc = new BinaryNode("enc");
            enc.Attrs.Add("v", "2");
            enc.Attrs.Add("type", "pkmsg");
            enc.Content = new byte[] { 0xAB, 0xCD, 0xEF };

            offer.Content = new List<BinaryNode> { audio, enc };

            string dump = CallSignaling.Dump(offer);

            Assert.IsTrue(dump.IndexOf("device_class=5513196007865") >= 0,
                "every attribute value is written: " + dump);
            Assert.IsTrue(dump.IndexOf("codec=opus") >= 0, "and every child's: " + dump);
            Assert.IsTrue(dump.IndexOf("enc[v=2, type=pkmsg]") >= 0, "the key keeps its shape: " + dump);
            Assert.IsTrue(dump.IndexOf("withheld, 3 byte(s)") >= 0, "and is counted, not written: " + dump);
            Assert.IsTrue(dump.IndexOf("ABCDEF") < 0, "the ciphertext never reaches the log: " + dump);
        }

        [Test]
        public static void TheShapeOfAnOfferIsDescribedWithoutItsKeys()
        {
            var keys = new List<CallKeyPayload>
            {
                new CallKeyPayload { Type = "pkmsg", Version = 2, Ciphertext = new byte[] { 4, 2 } }
            };

            BinaryNode call = CallSignaling.Offer(CallId, Peer, "39389@s.whatsapp.net",
                new byte[] { 7 }, new List<CallAudio>(), null, keys, new List<CallCandidate>());

            string shape = CallSignaling.Describe(call);

            Assert.IsTrue(shape.StartsWith("call[from,version,platform,to,id,offline,t]", StringComparison.Ordinal),
                "the node and its attributes, in the order a real offer writes them");
            Assert.IsTrue(shape.Contains("offer[call-id,call-creator] with "), "the verb and its attributes");
            Assert.IsTrue(shape.Contains("device-identity"), "and the identity is in the shape");
            Assert.IsTrue(!shape.Contains("4,2"), "and the ciphertext is not");
        }

        /// <summary>Everything an offer carried comes back out of `Read`, not just its verb.</summary>
        [Test]
        public static void ReadingAnOfferGivesBackEverythingItCarried()
        {
            var audio = new List<CallAudio>
            {
                new CallAudio { Codec = "opus", Rate = 48000 },
                new CallAudio { Codec = "opus", Rate = 16000 }
            };
            var video = new List<CallVideoDescription>
            {
                new CallVideoDescription { Codec = "vp8", ScreenWidth = 720, ScreenHeight = 1280, Orientation = 1 }
            };
            var key = new CallKeyPayload { Type = "pkmsg", Version = 2, RetryCount = 1, Ciphertext = new byte[] { 7 } };
            var candidates = new List<CallCandidate>
            {
                new CallCandidate { Priority = 1, Data = new byte[] { 4, 5, 6 } }
            };

            CallSignal signal = CallSignaling.Read(
                CallSignaling.Offer(CallId, Peer, audio, video, key, candidates));

            Assert.AreEqual(CallKind.Offer, signal.Kind);
            Assert.IsTrue(signal.IsVideo, "a video description is what makes it a video call");
            Assert.AreEqual(2, signal.Audio.Count, "both streams are read");
            Assert.AreEqual(48000, signal.Audio[0].Rate);
            Assert.AreEqual("vp8", signal.Video[0].Codec);
            Assert.AreEqual(720, signal.Video[0].ScreenWidth);
            Assert.AreEqual("pkmsg", signal.Key.Type);
            Assert.AreEqual(1u, signal.Key.RetryCount, "a retry is part of the key");
            Assert.AreEqual(1, signal.Key.Ciphertext.Length);
            Assert.AreEqual(1, signal.Candidates.Count);
            Assert.AreEqual(3, signal.Candidates[0].Data.Length);
        }

        /// <summary>
        /// The refusal of a call whose key this phone could not decrypt: the reason, the retry
        /// count and the registration id the other side rebuilds its session from.
        /// </summary>
        [Test]
        public static void ARejectForAnUndecryptableKeyCarriesTheRegistrationId()
        {
            BinaryNode node = CallSignaling.RejectUndecryptable(CallId, Peer, 2, 0x01020304u);
            BinaryNode reject = node.Children()[0];

            Assert.AreEqual("enc", reject.Attrs["reason"]);
            Assert.AreEqual("2", reject.Attrs["count"]);

            BinaryNode registration = reject.Child("registration");
            Assert.IsTrue(registration != null, "the registration id is a child, not an attribute");

            CallSignal signal = CallSignaling.Read(node);
            Assert.AreEqual(CallKind.Reject, signal.Kind);
            Assert.AreEqual(0x01020304u, signal.RegistrationId, "four bytes, big-endian, as the official writes them");
            Assert.IsTrue(signal.HasRegistration);
        }

        /// <summary>The end of a call that connected: how long it lasted, and the video's state.</summary>
        [Test]
        public static void ATerminateCarriesTheDurationAndTheVideoState()
        {
            CallSignal signal = CallSignaling.Read(
                CallSignaling.Terminate(CallId, Peer, "accepted", 75, 2));

            Assert.AreEqual(CallKind.Terminate, signal.Kind);
            Assert.AreEqual("accepted", signal.Reason);
            Assert.AreEqual(75, signal.DurationSeconds);
            Assert.AreEqual(2, signal.VideoState, "`videostate` is the video's state at the end");
        }

        /// <summary>
        /// The relay exchange: latencies out, an elected relay back. The token is bytes in the
        /// node's content, which is what the other side sends its media to.
        /// </summary>
        [Test]
        public static void TheRelayExchangeKeepsItsLatenciesAndItsToken()
        {
            var relays = new List<CallRelayLatency>
            {
                new CallRelayLatency { Latency = 42, Data = new byte[] { 1 } },
                new CallRelayLatency { Latency = 61, Data = new byte[] { 2 } }
            };

            CallSignal measured = CallSignaling.Read(CallSignaling.RelayLatency(CallId, Peer, relays));

            Assert.AreEqual(CallKind.RelayLatency, measured.Kind);
            Assert.AreEqual(2, measured.RelayLatency.Count, "one entry per relay");
            Assert.AreEqual(42, measured.RelayLatency[0].Latency);
            Assert.AreEqual(61, measured.RelayLatency[1].Latency);

            CallSignal elected = CallSignaling.Read(
                CallSignaling.RelayElection(CallId, Peer, 38, new byte[] { 3, 4 }));

            Assert.AreEqual(CallKind.RelayElection, elected.Kind);
            Assert.AreEqual(1, elected.RelayLatency.Count);
            Assert.AreEqual(38, elected.RelayLatency[0].Latency);
            Assert.AreEqual(2, elected.RelayLatency[0].Data.Length, "the token is the content");
        }

        /// <summary>Video on, off and turned is a verb of its own, with a number for each fact.</summary>
        [Test]
        public static void AVideoStateChangeIsItsOwnVerb()
        {
            CallSignal signal = CallSignaling.Read(CallSignaling.Video(CallId, Peer, 1, 3));

            Assert.AreEqual(CallKind.Video, signal.Kind, "the verb is `video`, not a description inside an offer");
            Assert.AreEqual(1, signal.VideoState);
            Assert.AreEqual(3, signal.Orientation);
        }

        /// <summary>The three small verbs: battery, hold, mute.</summary>
        [Test]
        public static void BatteryHoldAndMuteAreRead()
        {
            CallSignal notify = CallSignaling.Read(CallSignaling.Notify(CallId, Peer, 2));
            Assert.AreEqual(CallKind.Notify, notify.Kind);
            Assert.AreEqual(2, notify.BatteryState);

            CallSignal held = CallSignaling.Read(CallSignaling.Interruption(CallId, Peer, true));
            Assert.AreEqual(CallKind.Interruption, held.Kind);
            Assert.AreEqual("begin", held.State);

            CallSignal resumed = CallSignaling.Read(CallSignaling.Interruption(CallId, Peer, false));
            Assert.AreEqual("end", resumed.State);

            CallSignal muted = CallSignaling.Read(CallSignaling.Mute(CallId, Peer, true));
            Assert.AreEqual(CallKind.Mute, muted.Kind);
            Assert.AreEqual("begin", muted.State);
        }

        /// <summary>The family the connection sends is `Calls`', and it is `CallSignaling`'s.</summary>
        [Test]
        public static void TheCallFamilyIsBuiltByOneAuthor()
        {
            var candidates = new List<CallCandidate> { new CallCandidate { Priority = 0, Data = new byte[] { 1 } } };

            BinaryNode viaCalls = Calls.Transport(CallId, Peer, candidates);
            BinaryNode direct = CallSignaling.Transport(CallId, Peer, candidates);

            Assert.AreEqual(direct.Children()[0].Tag, viaCalls.Children()[0].Tag);
            Assert.AreEqual(direct.Children()[0].Attrs["call-id"], viaCalls.Children()[0].Attrs["call-id"]);
            Assert.AreEqual(direct.Children()[0].Children().Count, viaCalls.Children()[0].Children().Count);
        }
    }
}
