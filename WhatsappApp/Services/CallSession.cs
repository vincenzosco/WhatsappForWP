using System;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The official client's call state, all fifteen values of
    /// `com.whatsapp.calling.infra.voipcalling.CallState`, read out of the APK's
    /// `classes*.dex` on 4 November 2026:
    ///
    ///   NONE, PRECALLING, CALLING, RECEIVED_CALL, PRE_ACCEPT_RECEIVED,
    ///   ACCEPT_SENT, ACCEPT_RECEIVED, LINK, ACTIVE, CONNECTED_LONELY,
    ///   REJOINING, ENDING, ACTIVE_ELSEWHERE, SCREENING, BCALL_STARTING
    ///
    /// The five names this app used before (`Idle`, `Ringing`, `Connecting`,
    /// `Active`, `Ended`) are not on the wire: they were a UI summary of these,
    /// and they are now `CallPhase`, derived from this enum by `PhaseOf`. The
    /// reason to keep both is that the states are not a linear ring - `LINK` is a
    /// call reached through a call link, `ACTIVE_ELSEWHERE` is one taken on
    /// another device, `SCREENING` is one the phone is filtering - and a UI that
    /// only knows five of them cannot say which of those it is looking at.
    ///
    /// What this client can actually reach today is a subset, and it says so
    /// rather than pretending: `None`, `ReceivedCall`, `Calling`,
    /// `PreAcceptReceived`, `AcceptSent`, `AcceptReceived`, `Active`, `Ending`.
    /// `Link`, `ConnectedLonely`, `Rejoining`, `ActiveElsewhere`, `Screening` and
    /// `BCallStarting` are values the model knows and the wire has not yet told it
    /// about (call links, group rejoin, multi-device takeover), so nothing here
    /// invents them.
    /// </summary>
    public enum CallState
    {
        /// <summary>No call. The only state a start-up is in.</summary>
        None,

        /// <summary>The other phone is being prepared before it rings (woipv pre-call).</summary>
        PreCalling,

        /// <summary>An offer went out and nobody has answered yet.</summary>
        Calling,

        /// <summary>An offer arrived: the phone is ringing and nothing has been said yet.</summary>
        ReceivedCall,

        /// <summary>The callee's phone heard the offer and is preparing its media.</summary>
        PreAcceptReceived,

        /// <summary>This phone answered and the caller has not confirmed yet.</summary>
        AcceptSent,

        /// <summary>The other phone accepted and this one has not started the media yet.</summary>
        AcceptReceived,

        /// <summary>A call reached through a call link rather than a direct offer.</summary>
        Link,

        /// <summary>The call is up: somebody answered.</summary>
        Active,

        /// <summary>Up, but every other participant has left (a group call of one).</summary>
        ConnectedLonely,

        /// <summary>Joining a group call again after a drop.</summary>
        Rejoining,

        /// <summary>The call is over; the reason is what the banner says.</summary>
        Ending,

        /// <summary>This call is up on another device of the account.</summary>
        ActiveElsewhere,

        /// <summary>The phone is screening the call before it rings.</summary>
        Screening,

        /// <summary>A broadcast call (the `b` in `bCallCreated`) is starting.</summary>
        BCallStarting
    }

    /// <summary>
    /// The five things a screen can say, derived from `CallState`.
    ///
    /// It exists because the pages ask a smaller question than the wire answers -
    /// "is there a banner, does it ring, is it up, is it over" - and asking it of
    /// fifteen values in five places is how the five places drift apart.
    /// </summary>
    public enum CallPhase
    {
        /// <summary>No call.</summary>
        Idle,

        /// <summary>An offer is ringing here.</summary>
        Ringing,

        /// <summary>Being set up, in either direction.</summary>
        Connecting,

        /// <summary>The call is up.</summary>
        Active,

        /// <summary>The call is over and the banner still shows it.</summary>
        Ended
    }

    /// <summary>
    /// The events of `com.whatsapp.calling.voipcalling.VoipEventCallback` that
    /// this client can act on, by their own names.
    ///
    /// The callback has 119 methods; this is the subset a signalling-only client
    /// has anything to do with. The rest (`callGridRankingChanged`,
    /// `fieldstatsReady`, `handleFDLeakDetected`, the `bCall*` family, the codec
    /// avatar events, the source set by the media engine) belong to a media
    /// engine this build does not have, and inventing a use for them here would
    /// be inventing behaviour.
    /// </summary>
    public enum CallEvent
    {
        /// <summary>The call moved; the state itself travels separately.</summary>
        CallStateChanged,

        /// <summary>The other phone has our offer and acknowledged it.</summary>
        CallOfferReceiptReceived,

        /// <summary>The offer was accepted by the server.</summary>
        CallOfferAcked,

        /// <summary>The offer was refused by the server.</summary>
        CallOfferNacked,

        /// <summary>The offer's ack did not arrive: the call is not up.</summary>
        HandleOfferAckFailed,

        /// <summary>The other side said no.</summary>
        CallRejectReceived,

        /// <summary>The other side hung up.</summary>
        CallTerminateReceived,

        /// <summary>The call is being torn down.</summary>
        CallEnding,

        /// <summary>Nobody answered in time.</summary>
        CallMissed,

        /// <summary>A second call is waiting on the line.</summary>
        CallWaitingStateChanged,

        /// <summary>The state of a call link changed.</summary>
        CallLinkStateChanged,

        /// <summary>The group's participants changed.</summary>
        GroupInfoChanged,

        /// <summary>Somebody left the group call.</summary>
        GroupParticipantLeft,

        /// <summary>The data channel is up.</summary>
        DataChannelReady,

        /// <summary>The data channel never came up.</summary>
        DataChannelConnectionTimeout,

        /// <summary>The media heartbeat was refused.</summary>
        HeartbeatNacked,

        /// <summary>The other phone's battery is low.</summary>
        BatteryLevelLow,

        /// <summary>The call engine failed: there is no call left to save.</summary>
        HandleCallFatal,

        /// <summary>The other side paused or resumed its video.</summary>
        AutoVideoPauseStateChanged
    }

    /// <summary>
    /// One call, as the app tracks it: who, which direction, which of the fifteen
    /// official states, and the events that move it.
    ///
    /// Why it is a class of its own with no WinRT in it: the transitions are the part
    /// worth a test, and the ones that are easy to get wrong are all here rather than
    /// in a page. A `terminate` for a call this phone never had must not open one; an
    /// `accept` for a call that is not ringing must not make it active; and a call
    /// that ended must not be ended again and lose the reason it ended for.
    ///
    /// The media is **not** here and does not exist yet: an `Active` call is a call
    /// whose signalling succeeded. See
    /// `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md`.
    /// </summary>
    public sealed class CallSession
    {
        /// <summary>Which call this is, as every node of it names it.</summary>
        public string CallId { get; private set; }

        /// <summary>The other party, without a device suffix: the address a chat has.</summary>
        public string PeerJid { get; private set; }

        public bool IsVideo { get; private set; }

        /// <summary>True when the other side offered and this phone has not answered yet.</summary>
        public bool IsIncoming { get; private set; }

        /// <summary>Whether the microphone is switched off. A switch, not a fact of the wire.</summary>
        public bool Muted { get; private set; }

        /// <summary>Whether the sound goes to the loudspeaker rather than to the earpiece.</summary>
        public bool SpeakerOn { get; private set; }

        /// <summary>The official state: one of the fifteen `CallState` values.</summary>
        public CallState State { get; private set; }

        /// <summary>The five-value summary the pages draw, derived from `State`.</summary>
        public CallPhase Phase
        {
            get { return PhaseOf(State); }
        }

        /// <summary>The last `VoipEventCallback` event this call was told about.</summary>
        public CallEvent? LastEvent { get; private set; }

        /// <summary>The `reason` the call ended with, or null while it has not ended.</summary>
        public string EndedReason { get; private set; }

        /// <summary>When the call started ringing or dialling, for the duration on screen.</summary>
        public DateTime StartedAt { get; private set; }

        /// <summary>
        /// The moment the call was answered, or `default` while nobody has. It is what a
        /// duration counts from: a call that rang for a minute and then connected lasted a
        /// second, not a minute.
        /// </summary>
        public DateTime ConnectedAt { get; private set; }

        /// <summary>True when somebody answered: the moment a duration starts to exist.</summary>
        public bool WasAnswered
        {
            get { return ConnectedAt != default(DateTime); }
        }

        /// <summary>
        /// How long the call has been up, in whole seconds; zero for one that never connected.
        /// It is read off the clock rather than incremented by a timer, so a call that was
        /// suspended with the app still counts the time it was gone.
        /// </summary>
        public int ConnectedSeconds
        {
            get
            {
                if (ConnectedAt == default(DateTime)) return 0;

                int seconds = (int)(DateTime.UtcNow - ConnectedAt).TotalSeconds;
                return seconds < 0 ? 0 : seconds;
            }
        }

        /// <summary>
        /// The phase a state belongs to. Every one of the fifteen is named here, which
        /// is what makes the table checkable: a state that is added upstream and not
        /// mapped would have to be a compile error, and a switch over an enum in C# 5 is
        /// not that - so the test enumerates the enum and fails on a value whose phase is
        /// the fallback.
        /// </summary>
        public static CallPhase PhaseOf(CallState state)
        {
            switch (state)
            {
                case CallState.None:
                    return CallPhase.Idle;

                case CallState.ReceivedCall:
                    return CallPhase.Ringing;

                case CallState.PreCalling:
                case CallState.Calling:
                case CallState.PreAcceptReceived:
                case CallState.AcceptSent:
                case CallState.AcceptReceived:
                case CallState.Rejoining:
                    return CallPhase.Connecting;

                // A call reached by link, one whose peers all left, one this account is
                // already on elsewhere, one being screened and a broadcast one are all
                // "the call is there": the screen has something up to draw, and how it got
                // there is the state's own business.
                case CallState.Link:
                case CallState.Active:
                case CallState.ConnectedLonely:
                case CallState.ActiveElsewhere:
                case CallState.Screening:
                case CallState.BCallStarting:
                    return CallPhase.Active;

                case CallState.Ending:
                    return CallPhase.Ended;
            }

            // Unreachable for a value of the enum; a state the enum does not name arrives
            // as a number and is not a call anybody can draw.
            return CallPhase.Idle;
        }

        /// <summary>
        /// Whether an unanswered call has waited long enough to be called over
        /// (`DataService.CallSetupSeconds`). Only a ring or a setup can expire: an
        /// answered call is waiting for nobody, and a deadline of zero seconds is no
        /// deadline at all rather than an immediate miss.
        /// </summary>
        public bool Expired(DateTime now, int seconds)
        {
            if (seconds <= 0) return false;
            if (Phase != CallPhase.Ringing && Phase != CallPhase.Connecting) return false;

            return (now - StartedAt).TotalSeconds >= seconds;
        }

        public CallSession()
        {
            State = CallState.None;
        }

        /// <summary>Something is happening: a page shows the call banner while this is true.</summary>
        public bool Shows
        {
            get { return State != CallState.None; }
        }

        /// <summary>A call is up or being set up: a second offer has to be refused as busy.</summary>
        public bool Occupied
        {
            get
            {
                CallPhase phase = Phase;
                return phase == CallPhase.Ringing || phase == CallPhase.Connecting || phase == CallPhase.Active;
            }
        }

        /// <summary>
        /// An offer arrived. Answers whether this phone should ring for it.
        ///
        /// It says no to an offer for the call already on screen - the server repeats an
        /// offer to every device and to the same device on a retry, and a second banner for
        /// one call is a phone that rings twice - and no to a call with no id, which cannot
        /// be answered or ended.
        /// </summary>
        public bool Ring(string callId, string peerJid, bool video)
        {
            if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(peerJid)) return false;

            if (Occupied && CallId == callId) return false;
            if (Occupied) return false;

            CallId = callId;
            PeerJid = peerJid;
            IsVideo = video;
            IsIncoming = true;
            Muted = false;
            SpeakerOn = false;
            EndedReason = null;
            StartedAt = DateTime.UtcNow;
            ConnectedAt = default(DateTime);
            State = CallState.ReceivedCall;

            return true;
        }

        /// <summary>This phone is calling somebody: the same wait, from the other end.</summary>
        public bool Dial(string callId, string peerJid, bool video)
        {
            if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(peerJid)) return false;
            if (Occupied) return false;

            CallId = callId;
            PeerJid = peerJid;
            IsVideo = video;
            IsIncoming = false;
            Muted = false;
            SpeakerOn = false;
            EndedReason = null;
            StartedAt = DateTime.UtcNow;
            ConnectedAt = default(DateTime);
            State = CallState.Calling;

            return true;
        }

        /// <summary>
        /// The call is answered: from a ring this phone is answering (`ACCEPT_SENT`),
        /// from a setup it is the other side's `accept` arriving (`ACCEPT_RECEIVED`).
        ///
        /// Both end in `ACTIVE` in the same call, because this build has no
        /// acknowledgement to wait for: the official client can sit in `ACCEPT_SENT`
        /// until the caller confirms, and a phone that did that here would show
        /// "connecting" for a call the other end is already treating as up. The two
        /// states are still the ones the model passes through, so the day a confirmation
        /// arrives it has somewhere to wait.
        /// </summary>
        public bool Connect(string callId)
        {
            if (!SpeaksOf(callId)) return false;
            if (Phase != CallPhase.Ringing && Phase != CallPhase.Connecting) return false;

            if (ConnectedAt == default(DateTime)) ConnectedAt = DateTime.UtcNow;

            State = Phase == CallPhase.Ringing ? CallState.AcceptSent : CallState.AcceptReceived;

            // The official client's next step, taken at once: see the comment above.
            State = CallState.Active;
            return true;
        }

        /// <summary>
        /// The callee's phone has the offer and is preparing its side. Only a call this
        /// phone dialled can be pre-accepted: a `preaccept` for a ringing call is that
        /// phone talking about its own outgoing call, not about this one.
        /// </summary>
        public bool PreAccept(string callId)
        {
            if (!SpeaksOf(callId)) return false;

            if (State != CallState.Calling && State != CallState.PreCalling) return false;

            State = CallState.PreAcceptReceived;
            return true;
        }

        /// <summary>
        /// The call is over. One that was never ringing is not a call: a `terminate` for
        /// another device's call must not open a banner nobody can close.
        /// </summary>
        public bool End(string callId, string reason)
        {
            if (!SpeaksOf(callId)) return false;
            if (State == CallState.None || State == CallState.Ending) return false;

            EndedReason = reason;
            State = CallState.Ending;
            return true;
        }

        /// <summary>
        /// One `VoipEventCallback` event, applied. Answers whether it changed the call.
        ///
        /// Only the five that end a call have a transition here: everything else is
        /// either the state itself (which the caller sets) or belongs to the media engine.
        /// The event is remembered either way, so a log line can say what arrived.
        /// </summary>
        public bool Apply(CallEvent what, string callId, string reason)
        {
            if (!SpeaksOf(callId)) return false;

            LastEvent = what;

            switch (what)
            {
                case CallEvent.CallRejectReceived:
                    return End(callId, string.IsNullOrEmpty(reason) ? "reject" : reason);

                case CallEvent.CallTerminateReceived:
                case CallEvent.CallEnding:
                    return End(callId, string.IsNullOrEmpty(reason) ? "terminate" : reason);

                case CallEvent.CallMissed:
                    return End(callId, "missed");

                case CallEvent.HandleCallFatal:
                    return End(callId, "failed");

                default:
                    return false;
            }
        }

        /// <summary>
        /// The microphone switch. It belongs to a call that is up or being set up: a switch on
        /// a call nobody is on is a switch over nothing.
        /// </summary>
        public bool SetMuted(bool muted)
        {
            if (!Occupied) return false;

            Muted = muted;
            return true;
        }

        /// <summary>The speaker switch, with the same rule as the microphone's.</summary>
        public bool SetSpeaker(bool on)
        {
            if (!Occupied) return false;

            SpeakerOn = on;
            return true;
        }

        /// <summary>Takes the ended call off the screen. Anything still up is left alone.</summary>
        public void Clear()
        {
            if (Phase != CallPhase.Ended) return;

            CallId = null;
            PeerJid = null;
            IsVideo = false;
            IsIncoming = false;
            Muted = false;
            SpeakerOn = false;
            EndedReason = null;
            LastEvent = null;
            StartedAt = default(DateTime);
            ConnectedAt = default(DateTime);
            State = CallState.None;
        }

        /// <summary>
        /// Whether a node is about this call. An empty id on the node is not a match: a
        /// sender that omits it must not be able to end whatever call is on screen.
        /// </summary>
        private bool SpeaksOf(string callId)
        {
            return !string.IsNullOrEmpty(callId) && CallId == callId;
        }
    }
}
