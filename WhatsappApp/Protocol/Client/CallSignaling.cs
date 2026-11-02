using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// What one `call` node says.
    ///
    /// The node is one, and the verb is one of its children - the same shape
    /// `Calls.Reject` already sends (`<call to=… id=…><reject/></call>`). The names here
    /// are the ones the official client's own telemetry uses, read out of its APK on
    /// 2 November 2026 (`call_offer`, `call_terminate`, `preaccept`, `enc_key`,
    /// `candidates`, `relaylatency`) - see
    /// `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md`.
    /// </summary>
    public enum CallKind
    {
        /// <summary>A `call` node whose child this client does not know: named, not guessed at.</summary>
        Unknown,

        /// <summary>A call is being offered to this phone. This is the one that rings.</summary>
        Offer,

        /// <summary>The callee's phone has the offer and is preparing its side.</summary>
        PreAccept,

        /// <summary>The callee answered.</summary>
        Accept,

        /// <summary>The callee refused.</summary>
        Reject,

        /// <summary>The call is over, from whichever side.</summary>
        Terminate,

        /// <summary>The callee's transport candidates. Read and logged, not acted on yet.</summary>
        Transport,

        /// <summary>Latency to a relay. Read and logged, not acted on yet.</summary>
        RelayLatency
    }

    /// <summary>
    /// One call node, read: the verb, who it is about, and why it ended.
    ///
    /// It is a plain object with no WinRT in it, so the parser is a test in
    /// `ProtocolTests/CallSignalingTests.cs` rather than a trip to the phone.
    /// </summary>
    public sealed class CallSignal
    {
        public CallKind Kind;

        /// <summary>The call's id: the same value in every node of the same call.</summary>
        public string CallId;

        /// <summary>Who sent this node, as the stanza's `from`. Null on our own.</summary>
        public string From;

        /// <summary>Who it is addressed to, as the stanza's `to`.</summary>
        public string To;

        /// <summary>An offer that carries a video stream.</summary>
        public bool IsVideo;

        /// <summary>The `reason` of a `terminate` or a `reject`, or null.</summary>
        public string Reason;

        /// <summary>
        /// Whether this is a call a page should ring for: an offer, and nothing else. A
        /// `preaccept` of a call this phone is already placing is not an incoming call.
        /// </summary>
        public bool Rings
        {
            get { return Kind == CallKind.Offer; }
        }
    }

    /// <summary>
    /// The `call` node: how one is built, and how one that arrives is read.
    ///
    /// What is **not** here, and why: the media. The official client's offers carry the
    /// negotiation its native engine produces (`enc_key`, `candidates`, the `relaylatency`
    /// exchange), and none of that exists in this app - see the design document. What this
    /// file gives is the signalling seam those parts will attach to: a call can be
    /// offered, ring, be answered, be refused and be ended, and the words for the four
    /// steps are the ones the client's own log uses.
    /// </summary>
    public static class CallSignaling
    {
        /// <summary>The stanza's tag. `InboundRouter` classifies it as `InboundKind.Call`.</summary>
        public const string Tag = "call";

        /// <summary>
        /// A call this phone places: `<call to id><offer><audio/><video/></offer></call>`.
        ///
        /// The two children under `offer` are what the shape says a call carries, not a
        /// codec negotiation: `audio` is always there (a call with no stream is not a
        /// call) and `video` is what makes it a video call. This is the smallest offer
        /// that names the intent, which is the point - the media negotiation is the part
        /// that does not exist yet.
        /// </summary>
        public static BinaryNode Offer(string callId, string to, bool video)
        {
            Require(callId, to, "offered");

            var offer = new BinaryNode("offer");
            offer.Content = new List<BinaryNode> { new BinaryNode("audio") };
            if (video) offer.Children().Add(new BinaryNode("video"));

            return Wrap(callId, to, offer);
        }

        /// <summary>The callee's phone has the offer: `<call><preaccept/></call>`.</summary>
        public static BinaryNode PreAccept(string callId, string to)
        {
            Require(callId, to, "pre-accepted");

            return Wrap(callId, to, new BinaryNode("preaccept"));
        }

        /// <summary>The call is answered: `<call><accept/></call>`.</summary>
        public static BinaryNode Accept(string callId, string to)
        {
            Require(callId, to, "accepted");

            return Wrap(callId, to, new BinaryNode("accept"));
        }

        /// <summary>
        /// The call is refused: `<call><reject/></call>`, with a `reason` when there is one
        /// worth saying. `Calls.Reject` is this, without a reason.
        /// </summary>
        public static BinaryNode Reject(string callId, string to, string reason)
        {
            Require(callId, to, "refused");

            var reject = new BinaryNode("reject");
            if (!string.IsNullOrEmpty(reason)) reject.Attrs.Add("reason", reason);

            return Wrap(callId, to, reject);
        }

        /// <summary>
        /// The call is over: `<call><terminate reason=…/></call>`. The reason matters - the
        /// other side's log reads it to say "busy", "missed" or "ended", and a terminate
        /// with no reason is a call that hung up on itself.
        /// </summary>
        public static BinaryNode Terminate(string callId, string to, string reason)
        {
            Require(callId, to, "ended");

            var terminate = new BinaryNode("terminate");
            if (!string.IsNullOrEmpty(reason)) terminate.Attrs.Add("reason", reason);

            return Wrap(callId, to, terminate);
        }

        /// <summary>
        /// The node read, or null when it is not a call node at all.
        ///
        /// It never throws: this runs on the connection's read loop, and a malformed call
        /// node is a call this client does not answer - not a connection it tears down.
        /// The two attributes it insists on are the id and one of `from`/`to`, because a
        /// call with no id cannot be answered or ended, and one that names nobody cannot be
        /// shown.
        /// </summary>
        public static CallSignal Read(BinaryNode node)
        {
            if (node == null || node.Tag != Tag) return null;

            string callId = node.Attrs["id"];
            string from = node.Attrs["from"];
            string to = node.Attrs["to"];

            BinaryNode verb = FirstChild(node);

            var signal = new CallSignal();
            signal.CallId = callId;
            signal.From = from;
            signal.To = to;
            signal.Kind = verb == null ? CallKind.Unknown : KindOf(verb.Tag);

            if (verb != null)
            {
                signal.Reason = verb.Attrs["reason"];

                BinaryNode media = verb.Child("video");
                if (media != null) signal.IsVideo = true;

                // Some senders describe the media with an attribute instead of a child.
                string described = verb.Attrs["media"];
                if (!string.IsNullOrEmpty(described)
                    && described.ToLowerInvariant().IndexOf("video") >= 0) signal.IsVideo = true;
            }

            return signal;
        }

        /// <summary>A child's tag as the verb it is, or `Unknown`.</summary>
        private static CallKind KindOf(string tag)
        {
            if (tag == "offer") return CallKind.Offer;
            if (tag == "preaccept") return CallKind.PreAccept;
            if (tag == "accept") return CallKind.Accept;
            if (tag == "reject") return CallKind.Reject;
            if (tag == "terminate") return CallKind.Terminate;
            if (tag == "transport") return CallKind.Transport;
            if (tag == "relaylatency") return CallKind.RelayLatency;

            return CallKind.Unknown;
        }

        /// <summary>The first child, or null. A call node with two verbs is read by its first.</summary>
        private static BinaryNode FirstChild(BinaryNode node)
        {
            IList<BinaryNode> children = node.Children();
            if (children == null || children.Count == 0) return null;

            return children[0];
        }

        /// <summary>The node around one verb: the three attributes every call node carries.</summary>
        private static BinaryNode Wrap(string callId, string to, BinaryNode verb)
        {
            var node = new BinaryNode(Tag);
            node.Attrs.Add("to", to);
            node.Attrs.Add("id", callId);
            node.Content = new List<BinaryNode> { verb };

            return node;
        }

        /// <summary>
        /// The two things that make a call node meaningful. It throws where `Read` answers
        /// null: building a call with no id is a bug in this app, and a protocol exception
        /// here is a line in the log instead of a stanza nobody can answer.
        /// </summary>
        private static void Require(string callId, string to, string verb)
        {
            if (string.IsNullOrEmpty(callId))
            {
                throw new ProtocolException("a call is " + verb + " by its id");
            }

            if (string.IsNullOrEmpty(to))
            {
                throw new ProtocolException("a call is " + verb + " with somebody");
            }
        }
    }
}
