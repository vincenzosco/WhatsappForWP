using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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

    /// <summary>
    /// The parts of an offer this build cannot invent: the caller's own number and country,
    /// whether the call can be joined, and the three children the official client attaches
    /// (`metadata`, `rte`, `voip_settings`).
    ///
    /// They are one object because they arrive together - the offer captured on 4 November
    /// 2026 carries all six - and because they are the only fields of an offer whose values
    /// this build has never seen. `Offer` writes each one only when it has a value, so an empty
    /// profile produces exactly the offer this app sent before this type existed.
    /// </summary>
    public sealed class CallWireProfile
    {
        /// <summary>`caller_pn`: the caller's own number, spelled as the official client does.</summary>
        public string CallerPn;

        /// <summary>`caller_country_code`: the caller's country, spelled as the official client does.</summary>
        public string CallerCountryCode;

        /// <summary>`joinable`: whether the call can be joined; null leaves the attribute out.</summary>
        public bool? Joinable;

        /// <summary>The `metadata` child, whole, as a captured node carries it.</summary>
        public BinaryNode Metadata;

        /// <summary>The `rte` child, whole.</summary>
        public BinaryNode Rte;

        /// <summary>The `voip_settings` child, whole.</summary>
        public BinaryNode VoipSettings;

        /// <summary>
        /// The children the parser registers after `voip_settings`, in its own order: `relay`,
        /// `group_info`, `av_upgrade`, `link_info`. They are copied whole, like the three
        /// above, because nothing in this build knows what their attributes mean.
        /// </summary>
        public BinaryNode Relay;
        public BinaryNode GroupInfo;
        public BinaryNode AvUpgrade;
        public BinaryNode LinkInfo;

        /// <summary>
        /// `metadata`'s own fields, as the callee's parser names them: it reads the node and
        /// refuses the offer with `Metadata node not found`, so the node is not decoration.
        /// ``ver`` is the shape of the node, `peer_abtest_bucket` the bucket this side is in,
        /// and `peer_abtest_bucket_id_list` the ones it can name.
        /// </summary>
        public string MetadataVersion;
        public string PeerAbtestBucket;
        public string PeerAbtestBucketIds;

        /// <summary>
        /// `voip_settings`'s own fields: `type`, and `uncompressed`, which says the params are
        /// the plaintext ones. The parser refuses a `voip_settings` it cannot read
        /// (`unexpected compressed voip params`) and an offer that has none at all
        /// (`missing voip_settings`).
        /// </summary>
        public string VoipSettingsType;
        public bool UncompressedVoipSettings;

        /// <summary>
        /// The `metadata` child the callee expects, with the fields above on it.
        ///
        /// A profile that captured the node whole (`Metadata`) uses that one instead: this is
        /// only for the case where a capture gave the fields and not the bytes.
        /// </summary>
        public BinaryNode MetadataNode()
        {
            if (Metadata != null) return Metadata;
            if (string.IsNullOrEmpty(MetadataVersion) && string.IsNullOrEmpty(PeerAbtestBucket)) return null;

            var node = new BinaryNode("metadata");
            node.Attrs.Add("ver", string.IsNullOrEmpty(MetadataVersion) ? "1" : MetadataVersion);
            if (!string.IsNullOrEmpty(PeerAbtestBucket)) node.Attrs.Add("peer_abtest_bucket", PeerAbtestBucket);
            if (!string.IsNullOrEmpty(PeerAbtestBucketIds)) node.Attrs.Add("peer_abtest_bucket_id_list", PeerAbtestBucketIds);
            return node;
        }

        /// <summary>The `voip_settings` child from its fields, when no node was captured whole.</summary>
        public BinaryNode VoipSettingsNode()
        {
            if (VoipSettings != null) return VoipSettings;
            if (string.IsNullOrEmpty(VoipSettingsType)) return null;

            var node = new BinaryNode("voip_settings");
            node.Attrs.Add("type", VoipSettingsType);
            if (UncompressedVoipSettings) node.Attrs.Add("uncompressed", "true");
            return node;
        }

        /// <summary>
        /// The offer's own fields as this account can state them, without a capture and without
        /// guessing: the caller is the number inside this account's own JID (read, never
        /// typed), a call this client places is one person calling one person and so cannot be
        /// joined, and the country is the one the caller passes in.
        ///
        /// That last one is the only value the app has to be told: a JID carries digits and no
        /// country, and the letter code is not derivable from them. Null for an account whose
        /// JID is not a phone number - a `@lid` is an identifier, not a number - so nothing is
        /// invented for it.
        /// </summary>
        public static CallWireProfile ForAccount(string ownJid, string countryCode)
        {
            var profile = new CallWireProfile();
            profile.CallerPn = PhoneNumbers.UserDigits(ownJid);
            profile.CallerCountryCode = countryCode;

            // Only a call this account can actually be joined in may say it is joinable, and
            // this client only ever offers one-to-one calls.
            if (profile.CallerPn != null) profile.Joinable = false;

            return profile.IsEmpty ? null : profile;
        }

        /// <summary>
        /// The profile as it is written down in the settings: `name=value`, one line, `;`
        /// between the fields, and only the fields that have a value.
        /// </summary>
        public string ToStore()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(CallerPn)) parts.Add("caller_pn=" + CallerPn);
            if (!string.IsNullOrEmpty(CallerCountryCode)) parts.Add("caller_country_code=" + CallerCountryCode);
            if (Joinable.HasValue) parts.Add("joinable=" + (Joinable.Value ? "true" : "false"));

            return string.Join(";", parts.ToArray());
        }

        /// <summary>
        /// The profile a stored line describes, or null when it describes nothing. It reads and
        /// decides nothing: a name it does not know is skipped, never guessed at.
        /// </summary>
        public static CallWireProfile FromStore(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            var profile = new CallWireProfile();
            string[] parts = text.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < parts.Length; i++)
            {
                int at = parts[i].IndexOf('=');
                if (at <= 0) continue;

                string name = parts[i].Substring(0, at).Trim();
                string value = parts[i].Substring(at + 1).Trim();

                if (name == "caller_pn") profile.CallerPn = value;
                else if (name == "caller_country_code") profile.CallerCountryCode = value;
                else if (name == "joinable") profile.Joinable = value == "true";
            }

            return profile.IsEmpty ? null : profile;
        }

        /// <summary>Whether there is anything to write at all.</summary>
        public bool IsEmpty
        {
            get
            {
                return string.IsNullOrEmpty(CallerPn) && string.IsNullOrEmpty(CallerCountryCode)
                    && !Joinable.HasValue && Metadata == null && Rte == null && VoipSettings == null
                    && Relay == null && GroupInfo == null && AvUpgrade == null && LinkInfo == null
                    && string.IsNullOrEmpty(MetadataVersion) && string.IsNullOrEmpty(VoipSettingsType);
            }
        }

        /// <summary>
        /// The fields one captured `call` node carries, ready to be written on an offer of
        /// ours.
        ///
        /// It reads and never decides: `caller_pn` on the other phone's offer is *their*
        /// number, and an offer of ours that carried it would name the wrong caller - so this
        /// hands the values back and the caller says which of them are ours. The three children
        /// are copied whole because they are the values themselves.
        /// </summary>
        public static CallWireProfile FromCallNode(BinaryNode call)
        {
            if (call == null) return null;

            BinaryNode offer = call.Child("offer");
            if (offer == null) return null;

            var profile = new CallWireProfile();
            profile.CallerPn = offer.Attrs["caller_pn"];
            profile.CallerCountryCode = offer.Attrs["caller_country_code"];

            string joinable = offer.Attrs["joinable"];
            bool word;
            if (!string.IsNullOrEmpty(joinable) && bool.TryParse(joinable, out word)) profile.Joinable = word;

            profile.Metadata = offer.Child("metadata");
            profile.Rte = offer.Child("rte");
            profile.VoipSettings = offer.Child("voip_settings");
            profile.Relay = offer.Child("relay");
            profile.GroupInfo = offer.Child("group_info");
            profile.AvUpgrade = offer.Child("av_upgrade");
            profile.LinkInfo = offer.Child("link_info");

            if (profile.Metadata != null)
            {
                profile.MetadataVersion = profile.Metadata.Attrs["ver"];
                profile.PeerAbtestBucket = profile.Metadata.Attrs["peer_abtest_bucket"];
                profile.PeerAbtestBucketIds = profile.Metadata.Attrs["peer_abtest_bucket_id_list"];
            }

            if (profile.VoipSettings != null)
            {
                profile.VoipSettingsType = profile.VoipSettings.Attrs["type"];
                profile.UncompressedVoipSettings = profile.VoipSettings.Attrs["uncompressed"] == "true";
            }

            return profile.IsEmpty ? null : profile;
        }

        /// <summary>The attributes, on the verb, after `call-id` and `call-creator`.</summary>
        internal void ApplyAttrs(BinaryNode verb)
        {
            if (verb == null) return;

            if (!string.IsNullOrEmpty(CallerPn)) verb.Attrs.Add("caller_pn", CallerPn);
            if (!string.IsNullOrEmpty(CallerCountryCode)) verb.Attrs.Add("caller_country_code", CallerCountryCode);
            if (Joinable.HasValue) verb.Attrs.Add("joinable", Joinable.Value ? "true" : "false");
        }

        /// <summary>
        /// The three children, in the order the captured offer has them, before the
        /// `device-identity` that has to stay last.
        /// </summary>
        internal void AppendChildren(IList<BinaryNode> children)
        {
            if (children == null) return;

            BinaryNode metadata = MetadataNode();
            if (metadata != null) children.Add(metadata);
            if (Rte != null) children.Add(Rte);

            BinaryNode settings = VoipSettingsNode();
            if (settings != null) children.Add(settings);

            // `relay`, `group_info`, `av_upgrade`, `link_info`: the order the parser's own
            // name table gives them, one after the other (see the table on `Offer`).
            if (Relay != null) children.Add(Relay);
            if (GroupInfo != null) children.Add(GroupInfo);
            if (AvUpgrade != null) children.Add(AvUpgrade);
            if (LinkInfo != null) children.Add(LinkInfo);
        }
    }

    /// <summary>A relay's measured latency: `<te latency="42">bytes</te>`.</summary>
    public sealed class CallRelayLatency
    {
        public int Latency;
        public byte[] Data;
    }

    /// <summary>
    /// One relay the network offered for a call: where it is, and the token that names it.
    ///
    /// The answer to an offer carries them - the flow the reference thread documents
    /// (`WhiskeySockets/Baileys#40`): a `relay` per relay, its `te2` the address to probe and
    /// its `token` the bytes handed back when one is elected. Nothing else about a relay is
    /// known here: the latency is measured with a datagram, and the election is that
    /// measurement's answer.
    /// </summary>
    public sealed class CallRelay
    {
        /// <summary>The address as the node spelled it, for the log and for the probe.</summary>
        public string Endpoint;

        /// <summary>The host without the port, which is what a socket is handed, or null.</summary>
        public string Host;

        /// <summary>The port, or 0 when the node named no readable one.</summary>
        public int Port;

        /// <summary>What an election hands back, or null when the node carried no bytes.</summary>
        public byte[] Token;
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

        /// <summary>
        /// The fingerprint of the peer's DTLS certificate, as `destination` carried it, or null.
        /// It is the value the media handshake is pinned to: without it a handshake can finish
        /// and still prove nobody, and the audio refuses to go over it.
        /// </summary>
        public string Fingerprint;

        /// <summary>The relay latencies of a `relaylatency`, or null.</summary>
        public List<CallRelayLatency> RelayLatency;

        /// <summary>
        /// The relays the network offered for this call, or null. They arrive on the answer to an
        /// offer, one `relay` per relay, and the media goes through the one elected from their
        /// measured latencies.
        /// </summary>
        public List<CallRelay> Relays;

        /// <summary>The server's `rte` child, or null: the other half of the same answer.</summary>
        public BinaryNode Rte;

        /// <summary>The video state of a `video` verb, or -1 when the node carries none.</summary>
        public int VideoState = -1;

        /// <summary>The orientation of a `video` verb, or -1.</summary>
        public int Orientation = -1;

        /// <summary>The other phone's battery state from a `notify`, or -1.</summary>
        public int BatteryState = -1;

        /// <summary>`begin` or `end` for an `interruption`/`mute`, or null.</summary>
        public string State;

        /// <summary>The `t` the stanza was stamped with, in unix seconds, or 0 when it carries none.</summary>
        public long Timestamp;

        /// <summary>Whether the node was `offline="1"`: a copy the server kept for this device.</summary>
        public bool Offline;

        /// <summary>How old an offline copy is, in seconds; 0 when the node names no time.</summary>
        public long AgeSeconds
        {
            get
            {
                if (Timestamp <= 0) return 0;

                long age = CallSignaling.UnixTime() - Timestamp;
                return age < 0 ? 0 : age;
            }
        }

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
    /// - the id travels twice - `id` on the `call` node (so the stanza can be
    /// acknowledged) and `call-id` on the verb (so the other phone knows which call the
    /// payload is about);
    /// - an offer carries the encrypted call key (`enc`) and the transport candidates (`te`)
    /// beside the media. Without them the other phone rings and can neither decrypt the
    /// media nor reach this one.
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
        /// <summary>
        /// The client version a call node announces.
        ///
        /// Why it is not this app's own number: the two call nodes captured
        /// from other phones both carry it - `version="2.26.42.74" platform="android"` and
        /// `version="2.26.42.77" platform="iphone"` - and the callee reads it to decide which
        /// features the caller can do. A version that reads as an ancient build is a call the
        /// other phone has no reason to ring for; this is the version of the protocol core the
        /// shapes here were written against, and it is what a callee is told.
        /// </summary>
        public const string ClientVersion = "2.26.42.74";

        /// <summary>
        /// The platform word a call node announces, for the same reason as the version: the
        /// captured offers carry `android` and `iphone`, and a call node without either is a
        /// caller the other phone knows nothing about.
        /// </summary>
        public const string ClientPlatform = "android";

        /// <summary>
        /// How old an offline copy of a call may be before it is not answered at all, in
        /// seconds.
        ///
        /// Ninety is longer than any ring: a call still being offered when the app opens is
        /// answered, and a copy that waited minutes in the queue - up to thirteen minutes
        /// old - is a call that is over.
        /// </summary>
        public const int MaxOfflineAgeSeconds = 90;

        /// <summary>The unix time, in seconds.</summary>
        public static long UnixTime()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        /// <summary>
        /// Whether this node is a call the app is too late for: an offline copy older than
        /// `MaxOfflineAgeSeconds`. A live node - no `offline` mark - is never stale, however
        /// old its stamp, because a caller's clock is not something to hang up on.
        /// </summary>
        public static bool IsStale(CallSignal signal)
        {
            if (signal == null || !signal.Offline || signal.Timestamp <= 0) return false;

            return signal.AgeSeconds > MaxOfflineAgeSeconds;
        }

        /// <summary>The unix time a call node is stamped with, in seconds.</summary>
        private static string Now()
        {
            return UnixTime().ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>An attribute read as a long, or the fallback when it is absent or not one.</summary>
        private static long LongOf(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;

            long value;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value
                : 0;
        }

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
        /// 439` in the reconstructions). The offer this app sent until had no
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
            return Offer(callId, to, caller, null, audio, video, keys, candidates);
        }

        /// <summary>
        /// The same offer with this device's signed identity, which is the last child of an
        /// offer that opens any session.
        ///
        /// Why it is not optional: the `device-identity` child is what proves this device to a
        /// recipient whose session is being opened, and an `enc` of type `pkmsg` is exactly
        /// that. The message path has carried it from the start (`OutgoingMessage`,
        /// `shouldIncludeDeviceIdentity`); the offer did not, and a call whose five keys were
        /// all `pkmsg` went out with no identity at all - the log, two
        /// offers, not one ack from the server, both ending `reason timeout`. A recipient is
        /// entitled to ignore a pre-key message without it, which is a phone that does not
        /// ring.
        ///
        /// The order the server reads is `audio, net, capability, destination, enc, encopt,
        /// device-identity` (wacrg.org, "Call offer", which pins the same order in both
        /// reconstructions); the identity is what the callee verifies, so it comes after the
        /// node it proves and not before it.
        /// </summary>
        public static BinaryNode Offer(
            string callId,
            string to,
            string caller,
            byte[] deviceIdentity,
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
            AddDeviceIdentity(children, keys, deviceIdentity);

            return Wrap(callId, to, caller, Verb("offer", callId, caller, null, children));
        }

        /// <summary>
        /// The offer with the fields this build cannot invent filled in from a capture: the
        /// caller's own number and country, `joinable`, and the `metadata`, `rte` and
        /// `voip_settings` children the callee's phone reads.
        ///
        /// A null profile - or an empty one - is the offer this app sent before this overload
        /// existed, byte for byte: each field is written only when it has a value. That is the
        /// point of the shape: the offer is prepared, and the values arrive when a captured
        /// node gives them, without a rewrite on the day they do.
        ///
        /// Where the names come from: the offer captured carries all six,
        /// and the same tokens sit in the official client's own dex (`caller_pn`,
        /// `caller_country_code`, `joinable` in classes2, `metadata` and `voip_settings` beside
        /// them), so the spelling is read and not guessed. The values are what is still missing.
        ///
        /// The offer, child by child, in the order the client's parser names them. The run is
        /// one string table of `call_control::stanza_deserializer::stanzas::offer`, read out of
        /// libwhatsappmerged's pool (x 14,410,496):
        ///
        /// handle_incoming_xmpp_receipt offer audio &lt;unknown&gt; receipt
        /// call-creator parse_xmpp_offer: empty call-id voip_settings relay
        /// group_info av_upgrade link_info handle_incoming_xmpp_receipt: empty call-id
        ///
        /// which gives this table, in this order:
        ///
        /// # name kind where it sits
        /// 1 audio child first name the table registers for the offer
        /// 2 voip_settings child after the `call-creator` attribute
        /// 3 relay child after `voip_settings`
        /// 4 group_info child after `relay`
        /// 5 av_upgrade child after `group_info`
        /// 6 link_info child after `av_upgrade`
        /// - receipt not an offer child: the receipt stanza of the same unit
        /// - &lt;unknown&gt; a sentinel, not an element
        ///
        /// and on the verb, in the same order: `call-creator`, then the caller's own
        /// `caller_pn`, `caller_country_code`, `joinable`, and `device_class` beside them.
        ///
        /// Two children have no place in that table because the pool already carried their
        /// names from another translation unit - `video`, `net`, `capability`, `destination`
        /// with its `te`, `enc`, `encopt` - and they keep the wire order the captures pin:
        /// media, `net`, `capability`, `destination`, `enc`, `encopt`, and `device-identity`
        /// last. `metadata` and `rte` are not in the table either, and stay where the captured
        /// offer has them: with `voip_settings`, ahead of the four the table orders.
        /// </summary>
        public static BinaryNode Offer(
            string callId,
            string to,
            string caller,
            byte[] deviceIdentity,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallKeyPayload> keys,
            IList<CallCandidate> candidates,
            CallWireProfile profile)
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
            if (profile != null) profile.AppendChildren(children);
            AddDeviceIdentity(children, keys, deviceIdentity);

            BinaryNode verb = Verb("offer", callId, caller, null, children);
            if (profile != null) profile.ApplyAttrs(verb);

            return Wrap(callId, to, caller, verb);
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
            return Offer(callId, to, caller, null, audio, video, key, candidates);
        }

        /// <summary>The same offer, one key and the identity that proves it.</summary>
        public static BinaryNode Offer(
            string callId,
            string to,
            string caller,
            byte[] deviceIdentity,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            CallKeyPayload key,
            IList<CallCandidate> candidates)
        {
            var keys = new List<CallKeyPayload>();
            if (key != null) keys.Add(key);

            return Offer(callId, to, caller, deviceIdentity, audio, video, keys, candidates);
        }

        /// <summary>The callee's phone has the offer: `<call><preaccept call-id/></call>`.</summary>
        public static BinaryNode PreAccept(string callId, string to)
        {
            Require(callId, to, "pre-accepted");

            return Wrap(callId, to, Verb("preaccept", callId, null, null));
        }

        /// <summary>
        /// A pre-accept with the media it will use: `<preaccept call-id><audio/><video/>.`.
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
        /// The official client takes the first audio node only (`GetAudioNodes(.).Take(1)`):
        /// the stream is negotiated by then, and a list here is a list nobody reads.
        /// </summary>
        public static BinaryNode Accept(
            string callId,
            string to,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallCandidate> candidates)
        {
            return Accept(callId, to, audio, video, candidates, null);
        }

        /// <summary>
        /// The same answer, with the fingerprint of the certificate this side signs the media
        /// handshake with. It rides on `destination`, beside the candidates it belongs to: the
        /// APK's transport negotiation carries the credential, the candidates and the fingerprint
        /// as one message (`Web P2P: sending type 13 (., has_ufrag=%d, num_cands=%d, has_fp=%d)`),
        /// and `destination` is where this client already writes the candidates.
        ///
        /// The attribute name is this build's, and named as an inference: the native library
        /// builds the stanza and its attribute names are not in the strings, so a capture that
        /// spells it otherwise is one line here and one test. The value is not an inference:
        /// `sha-256` and the certificate's digest, the form the peer's own engine logs (`algo=%s`).
        /// </summary>
        public static BinaryNode Accept(
            string callId,
            string to,
            IList<CallAudio> audio,
            IList<CallVideoDescription> video,
            IList<CallCandidate> candidates,
            string fingerprint)
        {
            Require(callId, to, "accepted");

            var children = new List<BinaryNode>();
            if (audio != null && audio.Count > 0) AddAudio(children, new List<CallAudio> { audio[0] });
            AddVideo(children, video);
            AddDestination(children, candidates, fingerprint);

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

        /// <summary>
        /// Which relay is the one, given what each answered: the lowest latency, and the first of
        /// equals.
        ///
        /// Why here and not at the measurement: the rule is the wire's, both ends elect from the
        /// same list, and one that lived in the app would be a second copy of it.
        /// </summary>
        public static int Election(IList<CallRelayLatency> relays)
        {
            if (relays == null || relays.Count == 0) return -1;

            int best = -1;
            for (int i = 0; i < relays.Count; i++)
            {
                if (relays[i] == null) continue;
                if (best < 0 || relays[i].Latency < relays[best].Latency) best = i;
            }

            return best;
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

            // The time and the offline mark are what tell a call that is happening from the
            // copy of one that ended twenty minutes ago: without them an offer the server kept
            // while the app was closed is announced again at every start, and it rings for a
            // call nobody is making.
            signal.Timestamp = LongOf(node.Attrs["t"]);
            signal.Offline = node.Attrs["offline"] == "1";

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

            ReadRelays(node, verb, signal);

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
                    if (signal.Fingerprint == null) signal.Fingerprint = child.Attrs["fingerprint"];

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

        /// <summary>
        /// The relays an answer offers, and whether it carried the server's `rte` beside them.
        ///
        /// Read from the verb and from the `call` node: the reconstructions of the flow do not
        /// agree about which one holds the list, and a reader that picked one would answer "no
        /// relay" for the half of the traffic that spells it the other way.
        /// </summary>
        private static void ReadRelays(BinaryNode node, BinaryNode verb, CallSignal signal)
        {
            signal.Rte = Tagged(node, "rte");
            if (signal.Rte == null) signal.Rte = Tagged(verb, "rte");

            var relays = new List<CallRelay>();
            CollectRelays(node, relays);
            if (verb != null && verb != node) CollectRelays(verb, relays);

            if (relays.Count > 0) signal.Relays = relays;
        }

        /// <summary>The first child with a tag, or null: a null parent has none.</summary>
        private static BinaryNode Tagged(BinaryNode parent, string tag)
        {
            return parent == null ? null : parent.Child(tag);
        }

        /// <summary>Every `relay` child of a node, in the order the node carried them.</summary>
        private static void CollectRelays(BinaryNode parent, List<CallRelay> into)
        {
            if (parent == null) return;

            IList<BinaryNode> children = parent.Children();
            if (children == null) return;

            for (int i = 0; i < children.Count; i++)
            {
                BinaryNode child = children[i];
                if (child == null || child.Tag != "relay") continue;

                CallRelay relay = ReadRelay(child);
                if (relay != null) into.Add(relay);
            }
        }

        /// <summary>
        /// One `relay`: its `te2` is the address and its `token` the bytes, as a child or as an
        /// attribute. A relay whose address cannot be read is still returned with `Port` 0 -
        /// the count is what tells "relays this build cannot reach" from "no relay at all".
        /// </summary>
        private static CallRelay ReadRelay(BinaryNode node)
        {
            var relay = new CallRelay();

            BinaryNode te2 = node.Child("te2");
            string endpoint = te2 == null ? null : te2.Text();
            if (string.IsNullOrEmpty(endpoint)) endpoint = node.Attrs["te2"];
            if (string.IsNullOrEmpty(endpoint)) endpoint = node.Text();

            relay.Endpoint = endpoint;
            SplitEndpoint(endpoint, relay);

            BinaryNode token = node.Child("token");
            byte[] bytes = token == null ? null : token.Bytes();
            if (bytes == null) bytes = node.Bytes();

            relay.Token = bytes;
            return relay;
        }

        /// <summary>
        /// `host:port`, and `[address]:port` for the other family, into the two fields a socket
        /// needs. Anything else leaves the port 0 rather than guessing one.
        /// </summary>
        private static void SplitEndpoint(string endpoint, CallRelay relay)
        {
            if (string.IsNullOrEmpty(endpoint)) return;

            string text = endpoint.Trim();
            int colon = text.LastIndexOf(':');
            if (colon <= 0 || colon == text.Length - 1) return;

            string host = text.Substring(0, colon);
            if (host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']')
            {
                host = host.Substring(1, host.Length - 2);
            }

            int port;
            if (!int.TryParse(text.Substring(colon + 1), out port)) return;
            if (port <= 0 || port > 65535) return;

            relay.Host = host;
            relay.Port = port;
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
        /// of incoming &lt;call&gt; node"** - read out of its DEX - so in its
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
            return Wrap(callId, to, null, verb);
        }

        /// <summary>
        /// The same node with the caller on it: `from`, `to`, `id`.
        ///
        /// The attribute is the spec's (`wacrg.org`, "Call offer": `from` is the JID of the
        /// caller, and the copy the callee receives always carries it); the reference client is
        /// said to leave it to the server on the way out, and this client writes it because the
        /// server has already shown it will route nothing at all for a call it cannot attribute
        /// - the log has two offers and no ack, while an ordinary message
        /// with the same routing got `class="message"` in under a second.
        ///
        /// It goes first, before `to` and `id`, because that is the order the spec's examples
        /// write and the writer here emits attributes in insertion order.
        /// </summary>
        private static BinaryNode Wrap(string callId, string to, string from, BinaryNode verb)
        {
            var node = new BinaryNode(Tag);

            // The five attributes a real offer carries, in the order both captured nodes write
            // them (`<call from="…" version="2.26.42.74" platform="android" id="…" offline="0"
            // t="…">`): the caller, what it is, the id, whether the offer is an offline copy,
            // and when it was made. Only an offer passes `from` here, so only an offer gets the
            // four that describe its sender.
            if (!string.IsNullOrEmpty(from))
            {
                node.Attrs.Add("from", from);
                node.Attrs.Add("version", ClientVersion);
                node.Attrs.Add("platform", ClientPlatform);
            }

            node.Attrs.Add("to", to);
            node.Attrs.Add("id", callId);

            if (!string.IsNullOrEmpty(from))
            {
                node.Attrs.Add("offline", "0");
                node.Attrs.Add("t", Now());
            }

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
            AddDestination(children, candidates, null);
        }

        /// <summary>
        /// The same `destination`, with the fingerprint of the peer's certificate on it when
        /// there is one to name. An answer that carries a fingerprint and no candidate still
        /// needs the node: the fingerprint is the pin, and a node it cannot live on is a pin
        /// nobody reads.
        /// </summary>
        private static void AddDestination(
            IList<BinaryNode> children, IList<CallCandidate> candidates, string fingerprint)
        {
            bool hasFingerprint = !string.IsNullOrEmpty(fingerprint);
            if ((candidates == null || candidates.Count == 0) && !hasFingerprint) return;

            var endpoints = new List<BinaryNode>();

            if (candidates != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var node = new BinaryNode("te");
                    node.Attrs.Add("priority", Number(candidates[i].Priority));
                    node.Content = candidates[i].Data;
                    endpoints.Add(node);
                }
            }

            var destination = new BinaryNode("destination");
            if (endpoints.Count > 0) destination.Content = endpoints;
            if (hasFingerprint) destination.Attrs.Add("fingerprint", fingerprint);
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

        /// <summary>
        /// The identity child of an offer that opens a session, or nothing when every key is
        /// `msg` or the account has no identity to show.
        ///
        /// The rule is the message path's, verbatim (`OutgoingMessage.DeviceIdentityNode` is
        /// reused rather than re-shaped here): the node is present exactly when one of the
        /// `enc` children is a pre-key message, because that is the case in which the callee
        /// has to verify who this device is before it can use the key.
        /// </summary>
        private static void AddDeviceIdentity(
            IList<BinaryNode> children, IList<CallKeyPayload> keys, byte[] deviceIdentity)
        {
            if (!AnyPreKey(keys)) return;

            BinaryNode identity = OutgoingMessage.DeviceIdentityNode(deviceIdentity);
            if (identity != null) children.Add(identity);
        }

        /// <summary>Whether any of the keys opens a session rather than using one.</summary>
        private static bool AnyPreKey(IList<CallKeyPayload> keys)
        {
            if (keys == null) return false;

            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] == null) continue;

                if (string.Equals(keys[i].Type, CallKeyExchange.PreKeyType, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A node by tag and attribute name, with its children's tags: what the log says about
        /// an offer, because a shape the server will not route is otherwise a wait that ends
        /// `reason timeout` with nothing to point at. No ciphertext and no address is in the
        /// text: the keys are the one thing that must never be logged.
        /// </summary>
        public static string Describe(BinaryNode node)
        {
            if (node == null) return "nothing";

            var text = new StringBuilder();
            text.Append(node.Tag).Append(AttrNames(node));

            IList<BinaryNode> children = node.Children();
            if (children == null || children.Count == 0) return text.ToString();

            // The verb and the shape inside it: the order the server reads lives in the verb's
            // own children, so a description that stopped at the verb would say nothing about
            // the one thing the log needs to show.
            BinaryNode verb = children[0];
            text.Append(' ').Append(verb.Tag).Append(AttrNames(verb));

            IList<BinaryNode> inner = verb.Children();
            if (inner != null && inner.Count > 0)
            {
                text.Append(" with ");
                for (int i = 0; i < inner.Count; i++)
                {
                    if (i > 0) text.Append(", ");
                    text.Append(inner[i].Tag);
                }
            }

            return text.ToString();
        }

        /// <summary>How deep a dump goes: a call node is three levels at most.</summary>
        private const int DumpDepth = 4;

        /// <summary>
        /// Every field of a call node with its value: the log line that says what a phone
        /// actually wrote.
        ///
        /// `Describe` gives the shape - tags and attribute names - which is enough to see that
        /// two offers differ and not enough to see how. This gives the values too, so an offer the
        /// server will not route can be compared with one that rings field by field instead of
        /// guessed at. The one thing it never writes is a key: an `enc` child and a device
        /// identity are named with their length and nothing else.
        /// </summary>
        public static string Dump(BinaryNode node)
        {
            if (node == null) return "nothing";

            var text = new StringBuilder();
            AppendFields(text, node, 0);

            return text.ToString();
        }

        private static void AppendFields(StringBuilder text, BinaryNode node, int depth)
        {
            if (node == null) return;

            text.Append(node.Tag);

            bool first = true;
            foreach (KeyValuePair<string, string> attribute in node.Attrs)
            {
                text.Append(first ? "[" : ", ");
                text.Append(attribute.Key).Append('=').Append(attribute.Value);
                first = false;
            }

            if (!first) text.Append(']');

            AppendValue(text, node);

            IList<BinaryNode> children = node.Children();
            if (children == null || children.Count == 0) return;

            if (depth >= DumpDepth)
            {
                text.Append(" {").Append(children.Count).Append(" child(ren)}");
                return;
            }

            text.Append(" { ");
            for (int i = 0; i < children.Count; i++)
            {
                if (i > 0) text.Append(" | ");
                AppendFields(text, children[i], depth + 1);
            }

            text.Append(" }");
        }

        /// <summary>
        /// The content of one node, with keys withheld: the ciphertext must never reach a log
        /// file, and its length is all the shape needs.
        /// </summary>
        private static void AppendValue(StringBuilder text, BinaryNode node)
        {
            // A list of children is written as the children, not as a value.
            if (node.Content is IList<BinaryNode>) return;

            if (IsSecret(node.Tag))
            {
                byte[] secret = node.Content as byte[];
                text.Append(secret == null ? "(withheld)" : "(withheld, " + secret.Length + " byte(s))");
                return;
            }

            byte[] bytes = node.Content as byte[];
            if (bytes != null)
            {
                text.Append("(hex ").Append(Hex(bytes)).Append(", ").Append(bytes.Length).Append(" byte(s))");
                return;
            }

            string words = node.Content as string;
            if (words != null)
            {
                text.Append("(\"").Append(words).Append("\")");
                return;
            }

            if (node.Content != null) text.Append('(').Append(node.Content.ToString()).Append(')');
        }

        /// <summary>Whether a node's content is a key rather than a fact about the call.</summary>
        private static bool IsSecret(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;

            string lowered = tag.ToLowerInvariant();

            return lowered == "enc"
                || lowered == "device-identity"
                || lowered == "identity"
                || lowered == "skey";
        }

        /// <summary>The first bytes of a value in hex: enough to recognise, short enough to read.</summary>
        private static string Hex(byte[] bytes)
        {
            if (bytes == null) return "";

            int count = bytes.Length < 64 ? bytes.Length : 64;

            var text = new StringBuilder();
            for (int i = 0; i < count; i++) text.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));

            if (count < bytes.Length) text.Append('.');

            return text.ToString();
        }

        /// <summary>The names of a node's attributes, in the order they will be written.</summary>
        private static string AttrNames(BinaryNode node)
        {
            var text = new StringBuilder("[");

            if (node != null)
            {
                bool first = true;
                foreach (KeyValuePair<string, string> attribute in node.Attrs)
                {
                    if (!first) text.Append(',');
                    text.Append(attribute.Key);
                    first = false;
                }
            }

            return text.Append(']').ToString();
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
