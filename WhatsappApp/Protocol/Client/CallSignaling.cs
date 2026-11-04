using System.Collections.Generic;
using System.Globalization;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// What one `call` node says.
    ///
    /// The node is one, and the verb is one of its children. Every verb below was read out of
    /// the official client's own signalling, decompiled from
    /// `C:\Users\Vincenzo\Documents\WhatsApp-2.17.52.0-CSharp\src\WhatsAppCommon\WhatsApp\VoipSignaling.cs`
    /// (`WriteVoipNode`, the `switch (messageType - 1)`): `offer`, `accept`, `preaccept`,
    /// `reject`, `terminate`, `transport`, `relaylatency`, `relayelection`, `video`, `notify`,
    /// plus the two the same file sends on their own, `interruption` and `mute`.
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

        /// <summary>The callee's transport candidates, after the offer.</summary>
        Transport,

        /// <summary>Latency measured to a relay.</summary>
        RelayLatency,

        /// <summary>The relay chosen for this call (`relayelection`, with its `te` child).</summary>
        RelayElection,

        /// <summary>The video of a call went on, off, or turned: `video` with `state`.</summary>
        Video,

        /// <summary>The other phone's battery: `notify` with `batterystate`.</summary>
        Notify,

        /// <summary>The call was put on hold and picked up again (`interruption`).</summary>
        Interruption,

        /// <summary>The call was muted or unmuted (`mute`).</summary>
        Mute
    }

    /// <summary>One audio stream an offer or an accept names: `<audio enc="opus" rate="16000"/>`.</summary>
    public sealed class CallAudio
    {
        /// <summary>The codec, as the node spells it. `opus` is the only one WhatsApp uses.</summary>
        public string Codec;

        /// <summary>The sample rate in hertz.</summary>
        public int Rate;
    }

    /// <summary>
    /// One video stream's description inside an `offer`/`accept`/`preaccept`:
    /// `<video enc="h.264" screen_width="480" screen_height="800" orientation="0"/>`.
    ///
    /// The same tag carries a *state* change as a verb of its own (`CallKind.Video`); the two
    /// are told apart by where they sit, never by guessing from the attributes.
    /// </summary>
    public sealed class CallVideoDescription
    {
        /// <summary>`h.264` or `vp8`, the two the official enum names.</summary>
        public string Codec;

        public int ScreenWidth;
        public int ScreenHeight;

        /// <summary>The rotation the sender's screen is in.</summary>
        public int Orientation;
    }

    /// <summary>A transport candidate: `<te priority="0">bytes</te>`.</summary>
    public sealed class CallCandidate
    {
        public int Priority;
        public byte[] Data;
    }

    /// <summary>A relay's measured latency: `<te latency="42">bytes</te>`.</summary>
    public sealed class CallRelayLatency
    {
        public int Latency;
        public byte[] Data;
    }

    /// <summary>
    /// The encrypted call key: `<enc type="msg" v="2" count="1">ciphertext</enc>`.
    ///
    /// `type` is the Signal ciphertext type the official client writes with
    /// `Axolotl.CiphertextTypeString` (`msg` or `pkmsg`), `v` is the call-key version the
    /// server expects (2), and `count` is the retry a decryption failure asked for. What the
    /// ciphertext decrypts to is a 32-byte `Message.callKey`, which `CallKeys.Expand` turns
    /// into the 46 bytes of media key material.
    /// </summary>
    public sealed class CallKeyPayload
    {
        public string Type;
        public int Version;
        public uint RetryCount;
        public byte[] Ciphertext;
    }

    /// <summary>
    /// One call node, read: the verb, who it is about, why it ended, and the media the official
    /// node carries.
    ///
    /// It is a plain object with no WinRT in it, so the parser is a test in
    /// `ProtocolTests/CallSignalingTests.cs` rather than a trip to the phone. Every field the
    /// official `WriteVoipNode` writes has somewhere to land here; a verb this client does not
    /// handle is `Unknown` and named in the log, never guessed at.
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

        /// <summary>The `reason` of a `terminate`, a `reject`, or a `video` state.</summary>
        public string Reason;

        /// <summary>The encrypted call key of an offer, or null.</summary>
        public CallKeyPayload Key;

        /// <summary>
        /// Every `enc` an offer carried, in the order they came: one per recipient device.
        /// `Key` is the first of them, for the callers that only need a yes/no.
        /// </summary>
        public List<CallKeyPayload> Keys;

        /// <summary>The audio streams an offer/accept/preaccept names, or null.</summary>
        public List<CallAudio> Audio;

        /// <summary>The video descriptions an offer/accept/preaccept names, or null.</summary>
        public List<CallVideoDescription> Video;

        /// <summary>The transport candidates of an offer/accept/transport, or null.</summary>
        public List<CallCandidate> Candidates;

        /// <summary>The relay latencies of a `relaylatency`, or null.</summary>
        public List<CallRelayLatency> RelayLatency;

        /// <summary>The video state of a `video` verb, or -1 when the node carries none.</summary>
        public int VideoState = -1;

        /// <summary>The orientation of a `video` verb, or -1.</summary>
        public int Orientation = -1;

        /// <summary>The other phone's battery state from a `notify`, or -1.</summary>
        public int BatteryState = -1;

        /// <summary>`begin` or `end` for an `interruption`/`mute`, or null.</summary>
        public string State;

        /// <summary>The duration a `terminate` reports, or -1.</summary>
        public int DurationSeconds = -1;

        /// <summary>The registration id a `reject reason="enc"` carries, or 0.</summary>
        public uint RegistrationId;

        /// <summary>Whether the reject carried a `registration` child.</summary>
        public bool HasRegistration;

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
    /// The shapes are the official client's, verb by verb, and the two details that make the
    /// difference between a call that connects and one that does not are both here:
    ///
    ///  - the id travels twice - `id` on the `call` node (so the stanza can be
    ///    acknowledged) and `call-id` on the verb (so the other phone knows which call the
    ///    payload is about);
    ///  - an offer carries the encrypted call key (`enc`) and the transport candidates (`te`)
    ///    beside the media. Without them the other phone rings and can neither decrypt the
    ///    media nor reach this one.
    ///
    /// What is **not** here: the Signal encryption itself. `CallKeyPayload.Ciphertext` is
    /// bytes the caller has already encrypted to the peer's devices; the seam is
    /// `NativeConnection`, where the session lives.
    /// </summary>
    public static class CallSignaling
    {
        /// <summary>The stanza's tag. `InboundRouter` classifies it as `InboundKind.Call`.</summary>
        public const string Tag = "call";

        /// <summary>The Opus stream this client offers: the rate its own encoder produces.</summary>
        public const int DefaultAudioRate = 16000;

        /// <summary>The narrow-band Opus stream, which an offer names before the wide one.</summary>
        public const int NarrowAudioRate = 8000;

        /// <summary>The `medium` an offer names: 3 in both reconstructions.</summary>
        private const int NetworkMedium = 3;

        /// <summary>`encopt keygen="2"`: the second SRTP key schedule, which is the one a phone uses.</summary>
        private const int KeyGeneration = 2;

        /// <summary>
        /// The capability blob every offer carries: seven bytes with `ver="1"`, the one value
        /// the reconstructions agree on byte for byte. A real phone's blob says what its build
        /// supports; the peer reads it as capabilities and not as anything that authenticates.
        /// </summary>
        private static readonly byte[] CapabilityBlob = new byte[]
        {
            0x01, 0x05, 0xF7, 0x09, 0xE4, 0xBB, 0x13
        };

        /// <summary>
        /// A call this phone places: `<call to id><offer><audio/><video/></offer></call>`.
        ///
        /// The smallest offer that names the intent, kept for the callers that have no media
        /// keys yet (`NativeConnection.OfferCallAsync`). An offer that can actually be
        /// answered is built by the overload below.
        /// </summary>
        public static BinaryNode Offer(string callId, string to, bool video)
        {
            Require(callId, to, "offered");

            var audio = new List<CallAudio>();
            audio.Add(new CallAudio { Codec = "opus", Rate = DefaultAudioRate });

            var videos = new List<CallVideoDescription>();
            if (video) videos.Add(new CallVideoDescription { Codec = "h.264" });

            // An empty list, not null: `null` reads as either a key or a list of keys, and
            // the two overloads make it ambiguous (CS0121).
            return Offer(callId, to, audio, videos, new List<CallKeyPayload>(), null);
        }

        /// <summary>
        /// The offer the official client sends: the audio streams it can decode, one video
        /// description per video stream, the encrypted call key and the transport candidates.
        ///
        /// Order matters, because the other phone reads the children in the order they come:
        /// media first, then `enc`, then `te` - the order `WriteVoipNode` writes them in.
        /// </summary>
        public static BinaryNode Offer(
            string callId,
            string to,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            CallKeyPayload key,
            IList<CallCandidate> candidates)
        {
            var keys = new List<CallKeyPayload>();
            if (key != null) keys.Add(key);

            return Offer(callId, to, audio, video, keys, candidates);
        }

        /// <summary>
        /// The offer with one `<enc>` per recipient device, which is what a multi-device
        /// account needs: a Signal ciphertext opens on one session, so a companion phone with
        /// no ciphertext of its own cannot read the call key.
        /// </summary>
        public static BinaryNode Offer(
            string callId,
            string to,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallKeyPayload> keys,
            IList<CallCandidate> candidates)
        {
            return Offer(callId, to, null, audio, video, keys, candidates);
        }

        /// <summary>
        /// The offer with the caller named, and the children in the order the server reads them.
        ///
        /// Why this overload exists: the server routes a `call` node only when the offer has the
        /// shape the official client sends, and a mis-ordered one is refused outright (`error
        /// 439` in the reconstructions). The offer this app sent until 4 November 2026 had no
        /// `call-creator`, one audio stream, no `net`, no `capability`, no `encopt`, and its
        /// `te` were children of the offer instead of children of a `destination` - and the
        /// other phone never rang for a call the log said was offered.
        ///
        /// The order, from the two independent reconstructions: audio (narrow first), `net`,
        /// `capability`, `destination` with its `te`, one `enc` per recipient device, `encopt`.
        /// </summary>
        public static BinaryNode Offer(
            string callId,
            string to,
            string caller,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallKeyPayload> keys,
            IList<CallCandidate> candidates)
        {
            Require(callId, to, "offered");

            var children = new List<BinaryNode>();
            AddAudio(children, audio);
            AddVideo(children, video);
            AddNetwork(children);
            AddCapability(children);
            AddDestination(children, candidates);
            AddKeys(children, keys);
            AddEncryptionOptions(children);

            return Wrap(callId, to, Verb("offer", callId, caller, null, children));
        }

        /// <summary>The same offer with one key instead of a list of them.</summary>
        public static BinaryNode Offer(
            string callId,
            string to,
            string caller,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            CallKeyPayload key,
            IList<CallCandidate> candidates)
        {
            var keys = new List<CallKeyPayload>();
            if (key != null) keys.Add(key);

            return Offer(callId, to, caller, audio, video, keys, candidates);
        }

        /// <summary>The callee's phone has the offer: `<call><preaccept call-id/></call>`.</summary>
        public static BinaryNode PreAccept(string callId, string to)
        {
            Require(callId, to, "pre-accepted");

            return Wrap(callId, to, Verb("preaccept", callId, null, null));
        }

        /// <summary>
        /// A pre-accept with the media it will use: `<preaccept call-id><audio/><video/>...`.
        /// The official client sends the whole audio list here and only the first in `accept`.
        /// </summary>
        public static BinaryNode PreAccept(
            string callId, string to, IList<CallAudio> audio, IList<CallVideoDescription> video)
        {
            Require(callId, to, "pre-accepted");

            var children = new List<BinaryNode>();
            AddAudio(children, audio);
            AddVideo(children, video);

            return Wrap(callId, to, Verb("preaccept", callId, null, children));
        }

        /// <summary>The call is answered: `<call><accept call-id/></call>`.</summary>
        public static BinaryNode Accept(string callId, string to)
        {
            Require(callId, to, "accepted");

            return Wrap(callId, to, Verb("accept", callId, null, null));
        }

        /// <summary>
        /// The answer, with the one audio stream and the candidates that reach this phone.
        /// The official client takes the first audio node only (`GetAudioNodes(...).Take(1)`):
        /// the stream is negotiated by then, and a list here is a list nobody reads.
        /// </summary>
        public static BinaryNode Accept(
            string callId,
            string to,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallCandidate> candidates)
        {
            Require(callId, to, "accepted");

            var children = new List<BinaryNode>();
            if (audio != null && audio.Count > 0) AddAudio(children, new List<CallAudio> { audio[0] });
            AddVideo(children, video);
            AddDestination(children, candidates);

            return Wrap(callId, to, Verb("accept", callId, null, children));
        }

        /// <summary>
        /// The call is refused: `<call><reject call-id reason/></call>`, with a `reason` when
        /// there is one worth saying. `Calls.Reject` is this, without a reason.
        /// </summary>
        public static BinaryNode Reject(string callId, string to, string reason)
        {
            Require(callId, to, "refused");

            var attrs = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(reason)) attrs.Add(Key("reason", reason));

            return Wrap(callId, to, Verb("reject", callId, attrs, null));
        }

        /// <summary>
        /// The refusal that says the key could not be decrypted: `reason="enc"`, the retry
        /// count, and this phone's registration id inside `<registration>`.
        ///
        /// Why the registration id: the caller's engine rebuilds the Signal session from it
        /// before sending the call key again. A reject without it is a call that fails twice.
        /// </summary>
        public static BinaryNode RejectUndecryptable(
            string callId, string to, uint retryCount, uint registrationId)
        {
            Require(callId, to, "refused");

            var attrs = new List<KeyValuePair<string, string>>();
            attrs.Add(Key("reason", "enc"));
            if (retryCount != 0) attrs.Add(Key("count", Number(retryCount)));

            var registration = new BinaryNode("registration");
            registration.Content = UInt32(registrationId);

            var children = new List<BinaryNode> { registration };
            return Wrap(callId, to, Verb("reject", callId, attrs, children));
        }

        /// <summary>
        /// The call is over: `<call><terminate call-id reason/></call>`. The reason matters -
        /// the other side's log reads it to say "busy", "missed" or "ended" - and so do the
        /// two the official client adds when it has them: how long the call lasted and what
        /// the video was doing when it ended.
        /// </summary>
        public static BinaryNode Terminate(string callId, string to, string reason)
        {
            Require(callId, to, "ended");

            return Terminate(callId, to, reason, -1, -1);
        }

        /// <summary>A terminate with the duration and the video state the official node carries.</summary>
        public static BinaryNode Terminate(
            string callId, string to, string reason, int durationSeconds, int videoState)
        {
            Require(callId, to, "ended");

            var attrs = new List<KeyValuePair<string, string>>();
            if (!string.IsNullOrEmpty(reason)) attrs.Add(Key("reason", reason));
            if (durationSeconds >= 0) attrs.Add(Key("duration", Number(durationSeconds)));
            if (videoState != 0) attrs.Add(Key("videostate", Number(videoState)));

            return Wrap(callId, to, Verb("terminate", callId, attrs, null));
        }

        /// <summary>The candidates of a later measurement: `<transport call-id><te/></transport>`.</summary>
        public static BinaryNode Transport(string callId, string to, IList<CallCandidate> candidates)
        {
            Require(callId, to, "sent its transport for");

            var children = new List<BinaryNode>();
            AddDestination(children, candidates);

            return Wrap(callId, to, Verb("transport", callId, null, children));
        }

        /// <summary>What each relay answered: `<relaylatency call-id><te latency/></relaylatency>`.</summary>
        public static BinaryNode RelayLatency(string callId, string to, IList<CallRelayLatency> relays)
        {
            Require(callId, to, "measured a relay for");

            var children = new List<BinaryNode>();
            if (relays != null)
            {
                for (int i = 0; i < relays.Count; i++)
                {
                    var te = new BinaryNode("te");
                    te.Attrs.Add("latency", Number(relays[i].Latency));
                    te.Content = relays[i].Data;
                    children.Add(te);
                }
            }

            return Wrap(callId, to, Verb("relaylatency", callId, null, children));
        }

        /// <summary>
        /// The relay this side elects: `<relayelection call-id><te latency>token</te>`.
        ///
        /// The `data` is the relay's own token, not a candidate: it is what the other phone
        /// sends its media to, which is why the node carries it as content.
        /// </summary>
        public static BinaryNode RelayElection(string callId, string to, int latency, byte[] token)
        {
            Require(callId, to, "elected a relay for");

            var te = new BinaryNode("te");
            te.Attrs.Add("latency", Number(latency));
            te.Content = token;

            return Wrap(callId, to, Verb("relayelection", callId, null, new List<BinaryNode> { te }));
        }

        /// <summary>The video of a call started, stopped or turned: `<video call-id state orientation/>`.</summary>
        public static BinaryNode Video(string callId, string to, int state, int orientation)
        {
            Require(callId, to, "changed video for");

            var attrs = new List<KeyValuePair<string, string>>();
            attrs.Add(Key("state", Number(state)));
            attrs.Add(Key("orientation", Number(orientation)));

            // The id, the state and the orientation, in the official order: the verb's own
            // `call-id` comes first (`WriteVoipNode`, message type 13).
            var video = new BinaryNode("video");
            video.Attrs.Add("call-id", callId);
            for (int i = 0; i < attrs.Count; i++) video.Attrs.Add(attrs[i].Key, attrs[i].Value);

            return Wrap(callId, to, video);
        }

        /// <summary>The other phone's battery: `<notify call-id batterystate=N/>`.</summary>
        public static BinaryNode Notify(string callId, string to, int batteryState)
        {
            Require(callId, to, "notified");

            var notify = new BinaryNode("notify");
            notify.Attrs.Add("call-id", callId);
            notify.Attrs.Add("batterystate", Number(batteryState));

            return Wrap(callId, to, notify);
        }

        /// <summary>The call was put on hold (`begin`) or picked up again (`end`).</summary>
        public static BinaryNode Interruption(string callId, string to, bool begin)
        {
            Require(callId, to, "interrupted");

            return Wrap(callId, to, StateVerb("interruption", callId, begin));
        }

        /// <summary>The call was muted (`begin`) or unmuted (`end`).</summary>
        public static BinaryNode Mute(string callId, string to, bool muted)
        {
            Require(callId, to, "muted");

            return Wrap(callId, to, StateVerb("mute", callId, muted));
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

            BinaryNode verb = FirstChild(node);

            var signal = new CallSignal();
            signal.CallId = IdOf(node, verb);
            signal.From = node.Attrs["from"];
            signal.To = node.Attrs["to"];
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

                ReadMedia(verb, signal);
                ReadNumbers(verb, signal);
            }

            return signal;
        }

        /// <summary>The media children of a verb: audio, video descriptions, key, candidates.</summary>
        private static void ReadMedia(BinaryNode verb, CallSignal signal)
        {
            IList<BinaryNode> children = verb.Children();
            if (children == null) return;

            for (int i = 0; i < children.Count; i++)
            {
                BinaryNode child = children[i];
                if (child == null) continue;

                // The candidates an offer carries travel inside `destination`, so the loop
                // descends into it: its children are the `te` nodes the branch below reads.
                if (child.Tag == "destination")
                {
                    ReadMedia(child, signal);
                    continue;
                }

                if (child.Tag == "audio")
                {
                    if (signal.Audio == null) signal.Audio = new List<CallAudio>();

                    var audio = new CallAudio();
                    audio.Codec = child.Attrs["enc"];
                    audio.Rate = Int(child.Attrs["rate"], 0);
                    signal.Audio.Add(audio);
                    continue;
                }

                // A `video` child of an offer is a description (it has `enc`); a `video` child
                // of a `video` verb would be a description of nothing, and is left alone.
                if (child.Tag == "video" && child.Attrs["enc"] != null)
                {
                    if (signal.Video == null) signal.Video = new List<CallVideoDescription>();

                    var video = new CallVideoDescription();
                    video.Codec = child.Attrs["enc"];
                    video.ScreenWidth = Int(child.Attrs["screen_width"], 0);
                    video.ScreenHeight = Int(child.Attrs["screen_height"], 0);
                    video.Orientation = Int(child.Attrs["orientation"], -1);
                    signal.Video.Add(video);
                    signal.IsVideo = true;
                    continue;
                }

                if (child.Tag == "enc")
                {
                    var key = new CallKeyPayload();
                    key.Type = child.Attrs["type"];
                    key.Version = Int(child.Attrs["v"], 0);
                    key.RetryCount = UInt(child.Attrs["count"]);
                    key.Ciphertext = child.Bytes() ?? Empty();

                    if (signal.Keys == null) signal.Keys = new List<CallKeyPayload>();
                    signal.Keys.Add(key);
                    if (signal.Key == null) signal.Key = key;
                    continue;
                }

                if (child.Tag == "te")
                {
                    // A `te` means two different things, and the verb decides which: inside an
                    // offer/accept/transport it is a candidate to send media to, inside a
                    // relaylatency/relayelection it is a relay's measured latency. Reading it
                    // as a candidate in both places gives every relay a priority of zero and
                    // no latency to elect it by.
                    if (signal.Kind == CallKind.RelayLatency || signal.Kind == CallKind.RelayElection)
                    {
                        if (signal.RelayLatency == null) signal.RelayLatency = new List<CallRelayLatency>();

                        var relay = new CallRelayLatency();
                        relay.Latency = Int(child.Attrs["latency"], 0);
                        relay.Data = child.Bytes() ?? Empty();
                        signal.RelayLatency.Add(relay);
                    }
                    else
                    {
                        if (signal.Candidates == null) signal.Candidates = new List<CallCandidate>();

                        var candidate = new CallCandidate();
                        candidate.Priority = Int(child.Attrs["priority"], 0);
                        candidate.Data = child.Bytes() ?? Empty();
                        signal.Candidates.Add(candidate);
                    }
                    continue;
                }

                if (child.Tag == "registration") signal.HasRegistration = true;
            }
        }

        /// <summary>The numbers a verb carries as attributes, and the state words.</summary>
        private static void ReadNumbers(BinaryNode verb, CallSignal signal)
        {
            signal.VideoState = Int(verb.Attrs["state"], -1);
            signal.Orientation = Int(verb.Attrs["orientation"], -1);
            signal.BatteryState = Int(verb.Attrs["batterystate"], -1);
            signal.DurationSeconds = Int(verb.Attrs["duration"], -1);

            // `state` is a number on a `video` verb and a word on `interruption`/`mute`: the
            // verb decides which, so the word is only read where it can be one.
            if (signal.Kind == CallKind.Interruption || signal.Kind == CallKind.Mute)
            {
                signal.State = verb.Attrs["state"];
                signal.VideoState = -1;
            }

            // The `videostate` of a terminate is the video's state, and it is a different
            // attribute from the `state` above for exactly that reason.
            int videostate = Int(verb.Attrs["videostate"], -1);
            if (videostate >= 0) signal.VideoState = videostate;

            BinaryNode registration = verb.Child("registration");
            if (registration != null)
            {
                signal.HasRegistration = true;
                signal.RegistrationId = UInt32Of(registration.Bytes());
            }

        }

        /// <summary>
        /// The call's id as this sender wrote it.
        ///
        /// Why more than one spelling: the official client logs **"no call-id in the payload
        /// of incoming &lt;call&gt; node"** - read out of its DEX on 2 November 2026 - so in its
        /// own traffic the id travels in the payload (the verb child) under `call-id`, while
        /// this client writes `id` on the node. A reader that insisted on `id` alone answers
        /// null for a real offer, and `AnnounceCall` drops it as unreadable: the phone simply
        /// never rings. The spellings are tried in order, the first non-empty one wins, and
        /// nothing is guessed beyond the ones the reference names.
        /// </summary>
        private static string IdOf(BinaryNode node, BinaryNode verb)
        {
            string id = First(node, "id");
            if (id != null) return id;

            id = First(node, "call-id");
            if (id != null) return id;

            id = First(node, "callId");
            if (id != null) return id;

            id = First(verb, "call-id");
            if (id != null) return id;

            id = First(verb, "callId");
            if (id != null) return id;

            return First(verb, "id");
        }

        /// <summary>An attribute that is present and says something, or null.</summary>
        private static string First(BinaryNode node, string key)
        {
            if (node == null) return null;

            string value = node.Attrs[key];
            return string.IsNullOrEmpty(value) ? null : value;
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
            if (tag == "relayelection") return CallKind.RelayElection;
            if (tag == "video") return CallKind.Video;
            if (tag == "notify") return CallKind.Notify;
            if (tag == "interruption") return CallKind.Interruption;
            if (tag == "mute") return CallKind.Mute;

            return CallKind.Unknown;
        }

        /// <summary>The first child, or null. A call node with two verbs is read by its first.</summary>
        private static BinaryNode FirstChild(BinaryNode node)
        {
            IList<BinaryNode> children = node.Children();
            if (children == null || children.Count == 0) return null;

            return children[0];
        }

        /// <summary>The node around one verb: `to` and `id` on it, and the verb inside.</summary>
        private static BinaryNode Wrap(string callId, string to, BinaryNode verb)
        {
            var node = new BinaryNode(Tag);
            node.Attrs.Add("to", to);
            node.Attrs.Add("id", callId);
            node.Content = new List<BinaryNode> { verb };

            return node;
        }

        /// <summary>
        /// One verb, with the `call-id` every verb of this family carries.
        ///
        /// The order is the official one - `call-id` first, then the verb's own attributes -
        /// because the NodeWriter emits attributes in insertion order and a vector built from
        /// the reference compares bytes.
        /// </summary>
        private static BinaryNode Verb(
            string tag, string callId, IList<KeyValuePair<string, string>> attrs, IList<BinaryNode> children)
        {
            return Verb(tag, callId, null, attrs, children);
        }

        /// <summary>
        /// The same verb with the caller named. `call-creator` goes straight after `call-id`,
        /// which is where the official client has it and what the server reads to attribute the
        /// call's later stanzas - a verb without it is an offer nobody can place.
        /// </summary>
        private static BinaryNode Verb(
            string tag,
            string callId,
            string caller,
            IList<KeyValuePair<string, string>> attrs,
            IList<BinaryNode> children)
        {
            var verb = new BinaryNode(tag);
            verb.Attrs.Add("call-id", callId);
            if (!string.IsNullOrEmpty(caller)) verb.Attrs.Add("call-creator", caller);

            if (attrs != null)
            {
                for (int i = 0; i < attrs.Count; i++) verb.Attrs.Add(attrs[i].Key, attrs[i].Value);
            }

            if (children != null && children.Count > 0) verb.Content = children;

            return verb;
        }

        /// <summary>An `interruption`/`mute` verb: the id and the `begin`/`end` word.</summary>
        private static BinaryNode StateVerb(string tag, string callId, bool begin)
        {
            var verb = new BinaryNode(tag);
            verb.Attrs.Add("call-id", callId);
            verb.Attrs.Add("state", begin ? "begin" : "end");

            return verb;
        }

        /// <summary>The audio children, one per stream, in the order the caller gave them.</summary>
        private static void AddAudio(IList<BinaryNode> children, IList<CallAudio> audio)
        {
            if (audio == null) return;

            for (int i = 0; i < audio.Count; i++)
            {
                var node = new BinaryNode("audio");
                node.Attrs.Add("enc", audio[i].Codec ?? "opus");
                node.Attrs.Add("rate", Number(audio[i].Rate));
                children.Add(node);
            }
        }

        /// <summary>The video descriptions, one per stream.</summary>
        private static void AddVideo(IList<BinaryNode> children, IList<CallVideoDescription> video)
        {
            if (video == null) return;

            for (int i = 0; i < video.Count; i++)
            {
                var node = new BinaryNode("video");
                node.Attrs.Add("enc", video[i].Codec ?? "h.264");
                node.Attrs.Add("screen_width", Number(video[i].ScreenWidth));
                node.Attrs.Add("screen_height", Number(video[i].ScreenHeight));
                node.Attrs.Add("orientation", Number(video[i].Orientation));
                children.Add(node);
            }
        }

        /// <summary>The encrypted call keys: one `enc` child per recipient device.</summary>
        private static void AddKeys(IList<BinaryNode> children, IList<CallKeyPayload> keys)
        {
            if (keys == null) return;

            for (int i = 0; i < keys.Count; i++)
            {
                CallKeyPayload key = keys[i];
                if (key == null || key.Ciphertext == null) continue;

                var node = new BinaryNode("enc");
                node.Attrs.Add("type", key.Type ?? "msg");
                node.Attrs.Add("v", Number(key.Version <= 0 ? 2 : key.Version));
                if (key.RetryCount != 0) node.Attrs.Add("count", Number(key.RetryCount));
                node.Content = key.Ciphertext;
                children.Add(node);
            }
        }

        /// <summary>
        /// The transport candidates, inside the `destination` node that holds them: a `te` at
        /// the top of an offer is not a candidate the peer's media engine looks for.
        /// </summary>
        private static void AddDestination(IList<BinaryNode> children, IList<CallCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return;

            var endpoints = new List<BinaryNode>();

            for (int i = 0; i < candidates.Count; i++)
            {
                var node = new BinaryNode("te");
                node.Attrs.Add("priority", Number(candidates[i].Priority));
                node.Content = candidates[i].Data;
                endpoints.Add(node);
            }

            var destination = new BinaryNode("destination");
            destination.Content = endpoints;
            children.Add(destination);
        }

        /// <summary>The network the offer comes from.</summary>
        private static void AddNetwork(IList<BinaryNode> children)
        {
            var net = new BinaryNode("net");
            net.Attrs.Add("medium", Number(NetworkMedium));
            children.Add(net);
        }

        /// <summary>The capabilities this build has, as the fixed seven-byte blob.</summary>
        private static void AddCapability(IList<BinaryNode> children)
        {
            var capability = new BinaryNode("capability");
            capability.Attrs.Add("ver", Number(1));
            capability.Content = CapabilityBlob;
            children.Add(capability);
        }

        /// <summary>Which key schedule the media key uses.</summary>
        private static void AddEncryptionOptions(IList<BinaryNode> children)
        {
            var encopt = new BinaryNode("encopt");
            encopt.Attrs.Add("keygen", Number(KeyGeneration));
            children.Add(encopt);
        }

        private static KeyValuePair<string, string> Key(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        /// <summary>A number as the wire spells it: no separators, no culture.</summary>
        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Number(uint value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>An attribute read as an int, or the fallback when it is absent or not one.</summary>
        private static int Int(string text, int fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;

            int value;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : fallback;
        }

        /// <summary>An attribute read as an unsigned int, or zero.</summary>
        private static uint UInt(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            uint value;
            return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
        }

        /// <summary>
        /// Four bytes, big-endian, which is what `FunXMPP.Connection.UIntToBytes` writes into
        /// the `registration` child of a `reject reason="enc"`.
        /// </summary>
        private static byte[] UInt32(uint value)
        {
            return new byte[]
            {
                (byte)(value >> 24),
                (byte)(value >> 16),
                (byte)(value >> 8),
                (byte)value
            };
        }

        /// <summary>The same four bytes read back, or zero when the node carried none.</summary>
        private static uint UInt32Of(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4) return 0;

            return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        }

        /// <summary>No bytes, for a `te`/`enc` that carried none: a node with null content.</summary>
        private static byte[] Empty()
        {
            return new byte[0];
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
