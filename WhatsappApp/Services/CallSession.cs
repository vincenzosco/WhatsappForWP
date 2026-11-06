using System;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The call states the official app uses. There are fifteen of them, and these
    /// are their names, copied from `com.whatsapp.calling.infra.voipcalling.CallState`
    /// in the APK's `classes*.dex`:
    ///
    /// NONE, PRECALLING, CALLING, RECEIVED_CALL, PRE_ACCEPT_RECEIVED,
    /// ACCEPT_SENT, ACCEPT_RECEIVED, LINK, ACTIVE, CONNECTED_LONELY,
    /// REJOINING, ENDING, ACTIVE_ELSEWHERE, SCREENING, BCALL_STARTING
    ///
    /// This app used to have five states of its own (`Idle`, `Ringing`, `Connecting`,
    /// `Active`, `Ended`). Those five were never on the network: they were a summary
    /// of these fifteen, made for the screens. They now live in `CallPhase`, and this
    /// enum keeps the real thing.
    ///
    /// Why keep both: the fifteen are not a straight line. `Link` means the call was
    /// joined through a call link, `ActiveElsewhere` means the same account is already
    /// on it from another device, `Screening` means the phone is checking the caller
    /// before it rings. If the model only knew five words, the screens could not tell
    /// those apart.
    ///
    /// This app can only reach some of them today: `None`, `ReceivedCall`, `Calling`,
    /// `PreAcceptReceived`, `AcceptSent`, `AcceptReceived`, `Active` and `Ending`. The
    /// others (`Link`, `ConnectedLonely`, `Rejoining`, `ActiveElsewhere`, `Screening`,
    /// `BCallStarting`) are real states that nothing in this app produces yet. They are
    /// named here and nothing more - no code pretends to reach them.
    /// </summary>
    public enum CallState
    {
        /// <summary>No call at all. This is where the app starts.</summary>
        None,

        /// <summary>The other phone is being prepared, a moment before it rings.</summary>
        PreCalling,

        /// <summary>We called, and nobody has picked up yet.</summary>
        Calling,

        /// <summary>Somebody called us: the phone is ringing, nobody has spoken yet.</summary>
        ReceivedCall,

        /// <summary>Their phone got our call and is getting ready to answer it.</summary>
        PreAcceptReceived,

        /// <summary>We answered, and the caller has not confirmed it yet.</summary>
        AcceptSent,

        /// <summary>They answered, and our side has not started the call yet.</summary>
        AcceptReceived,

        /// <summary>The call was joined through a call link instead of a normal call.</summary>
        Link,

        /// <summary>The call is up: somebody answered.</summary>
        Active,

        /// <summary>The call is up but everyone else has left, so we are alone on it.</summary>
        ConnectedLonely,

        /// <summary>Coming back into a group call after losing it.</summary>
        Rejoining,

        /// <summary>The call is over. The reason for it is what the banner shows.</summary>
        Ending,

        /// <summary>The same call is up on another device of this account.</summary>
        ActiveElsewhere,

        /// <summary>The phone is checking the call before letting it ring.</summary>
        Screening,

        /// <summary>A broadcast call is starting (the "b" in `bCallCreated`).</summary>
        BCallStarting
    }

    /// <summary>
    /// The five things a screen can say about a call.
    ///
    /// A page only ever asks a small question: is there a banner, is it ringing, is it
    /// up, is it over. Answering that with fifteen values in five different pages is
    /// how the pages end up disagreeing with each other, so they ask this instead.
    /// Each of these comes from a `CallState` through `PhaseOf`.
    /// </summary>
    public enum CallPhase
    {
        /// <summary>No call.</summary>
        Idle,

        /// <summary>A call is ringing here.</summary>
        Ringing,

        /// <summary>A call is being set up, in one direction or the other.</summary>
        Connecting,

        /// <summary>The call is up.</summary>
        Active,

        /// <summary>The call is over and the banner is still on screen.</summary>
        Ended
    }

    /// <summary>
    /// The call events this app can actually do something with, by the names the
    /// official app uses in `VoipEventCallback`.
    ///
    /// That callback has 119 methods. Most of them are about the audio and video
    /// engine - grid ranking, codec stats, file-descriptor leaks, the avatar events -
    /// and this app has no audio or video engine yet, so they would have nothing to
    /// act on. These are the ones a client that only does signalling cares about.
    ///
    /// Five of them end the call (a refusal, a hang-up, a teardown, a missed call, a
    /// fatal error). The rest only tell us something happened, and the call stays
    /// where it is.
    /// </summary>
    public enum CallEvent
    {
        /// <summary>The call moved. The new state arrives with the event.</summary>
        CallStateChanged,

        /// <summary>Their phone got our call and said so.</summary>
        CallOfferReceiptReceived,

        /// <summary>The server accepted our call.</summary>
        CallOfferAcked,

        /// <summary>The server refused our call.</summary>
        CallOfferNacked,

        /// <summary>The server never confirmed our call, so it never started.</summary>
        HandleOfferAckFailed,

        /// <summary>They said no.</summary>
        CallRejectReceived,

        /// <summary>They hung up.</summary>
        CallTerminateReceived,

        /// <summary>The call is being taken down.</summary>
        CallEnding,

        /// <summary>Nobody answered in time.</summary>
        CallMissed,

        /// <summary>A second call is waiting on the line.</summary>
        CallWaitingStateChanged,

        /// <summary>A call link changed its state.</summary>
        CallLinkStateChanged,

        /// <summary>The people in the group call changed.</summary>
        GroupInfoChanged,

        /// <summary>Somebody left the group call.</summary>
        GroupParticipantLeft,

        /// <summary>The data channel is ready.</summary>
        DataChannelReady,

        /// <summary>The data channel never came up.</summary>
        DataChannelConnectionTimeout,

        /// <summary>The media heartbeat was refused.</summary>
        HeartbeatNacked,

        /// <summary>Their battery is running low.</summary>
        BatteryLevelLow,

        /// <summary>The call engine broke, so there is nothing left to keep.</summary>
        HandleCallFatal,

        /// <summary>They paused or resumed their video.</summary>
        AutoVideoPauseStateChanged
    }

    /// <summary>
    /// One call as this app tracks it: who it is with, who called whom, which of the
    /// fifteen states it is in, and the events that move it.
    ///
    /// This class knows nothing about WinRT on purpose. The interesting part of a call
    /// is how it moves from one state to the next, and those rules are worth a test.
    /// They also share the same three mistakes: a hang-up for a call we never had must
    /// not open a banner, an answer for a call that is not ringing must not make it
    /// active, and a call that already ended must not be ended again - otherwise the
    /// reason it ended for gets overwritten.
    ///
    /// There is no audio or video here, and there is none in the app yet: a call in
    /// `Active` is a call whose signalling worked. See
    /// `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md`.
    /// </summary>
    public sealed class CallSession
    {
        /// <summary>The call's id, the one every message about it carries.</summary>
        public string CallId { get; private set; }

        /// <summary>The other person, without the device part: the address a chat uses.</summary>
        public string PeerJid { get; private set; }

        public bool IsVideo { get; private set; }

        /// <summary>True while this call came from them and we have not answered yet.</summary>
        public bool IsIncoming { get; private set; }

        /// <summary>Microphone off. This is our switch, not something the network told us.</summary>
        public bool Muted { get; private set; }

        /// <summary>Sound on the loudspeaker instead of the earpiece.</summary>
        public bool SpeakerOn { get; private set; }

        /// <summary>The real state: one of the fifteen `CallState` values.</summary>
        public CallState State { get; private set; }

        /// <summary>The short version the pages draw, worked out from `State`.</summary>
        public CallPhase Phase
        {
            get { return PhaseOf(State); }
        }

        /// <summary>The last event this call heard about, or null if it heard none yet.</summary>
        public CallEvent? LastEvent { get; private set; }

        /// <summary>Why the call ended, or null while it is still going.</summary>
        public string EndedReason { get; private set; }

        /// <summary>When the call started ringing or dialling. The screens use it for the clock.</summary>
        public DateTime StartedAt { get; private set; }

        /// <summary>
        /// When somebody answered, or `default` if nobody has. A duration counts from
        /// here: a call that rang for a minute and then lasted a second lasted a second.
        /// </summary>
        public DateTime ConnectedAt { get; private set; }

        /// <summary>True once somebody answered, which is when a duration starts to mean anything.</summary>
        public bool WasAnswered
        {
            get { return ConnectedAt != default(DateTime); }
        }

        /// <summary>
        /// How many whole seconds the call has been up. Zero if it never connected.
        ///
        /// It is worked out from the clock every time it is asked, not counted by a
        /// timer, so a call that kept going while the app was suspended still shows the
        /// right length.
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
        /// Which of the five phases a state belongs to.
        ///
        /// All fifteen are listed by name, on purpose: C# 5 lets a switch over an enum
        /// fall through to the default without a word, so a state added later would
        /// quietly land in the wrong phase. Listing them all means the test that walks
        /// the enum has something to check.
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

                // All of these mean "there is a call to show". How it got there - a
                // link, another device, a screening, a broadcast - is the state's own
                // business, not the screen's.
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

            // No real state gets here. A number the enum does not know is not a call
            // anyone can draw, so it counts as no call.
            return CallPhase.Idle;
        }

        /// <summary>
        /// Whether an unanswered call has waited long enough to give up on
        /// (`DataService.CallSetupSeconds`).
        ///
        /// Only a ringing or a half-built call can expire: an answered call is not
        /// waiting for anybody. A limit of zero seconds means "no limit", not "give up
        /// at once".
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

        /// <summary>There is a call to show: a page draws the banner while this is true.</summary>
        public bool Shows
        {
            get { return State != CallState.None; }
        }

        /// <summary>A call is ringing, being set up or already up, so a second one has to be refused.</summary>
        public bool Occupied
        {
            get
            {
                CallPhase phase = Phase;
                return phase == CallPhase.Ringing || phase == CallPhase.Connecting || phase == CallPhase.Active;
            }
        }

        /// <summary>
        /// A call is coming in. Returns true if this phone should ring for it.
        ///
        /// It says no in two cases. A call with no id cannot be answered or hung up, so
        /// ringing for it would leave a banner nobody can close. And the same offer can
        /// arrive twice - the server sends it to every device, and again on a retry - so
        /// a call we are already handling is not a second call. One banner, one ring.
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

        /// <summary>We are calling somebody: the same wait as `Ring`, from the other side.</summary>
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
        /// The call is answered. If it was ringing here, that is us answering
        /// (`ACCEPT_SENT`); if we were the one calling, it is them answering
        /// (`ACCEPT_RECEIVED`).
        ///
        /// Both go straight on to `ACTIVE` in the same step. The official app can stop
        /// at `ACCEPT_SENT` until the caller confirms, but this app has no confirmation
        /// to wait for. If it stopped there, the screen would say "connecting" for a
        /// call the other end already treats as up. Going through the two states and on
        /// to `Active` keeps the names honest without freezing the banner.
        /// </summary>
        public bool Connect(string callId)
        {
            if (!SpeaksOf(callId)) return false;
            if (Phase != CallPhase.Ringing && Phase != CallPhase.Connecting) return false;

            if (ConnectedAt == default(DateTime)) ConnectedAt = DateTime.UtcNow;

            State = Phase == CallPhase.Ringing ? CallState.AcceptSent : CallState.AcceptReceived;

            // Straight on to the state the call is really in; see the note above.
            State = CallState.Active;
            return true;
        }

        /// <summary>
        /// Their phone got our call and is getting ready.
        ///
        /// Only a call we dialled can be pre-accepted. If we are the one ringing, a
        /// pre-accept is that phone talking about a call of its own, not about ours.
        /// </summary>
        public bool PreAccept(string callId)
        {
            if (!SpeaksOf(callId)) return false;

            if (State != CallState.Calling && State != CallState.PreCalling) return false;

            State = CallState.PreAcceptReceived;
            return true;
        }

        /// <summary>
        /// The call is over.
        ///
        /// A hang-up for a call we never had does nothing. Otherwise any device of the
        /// account could open a banner here just by ending a call that was never ours.
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
        /// Takes one call event and applies it. Returns true if the call changed.
        ///
        /// Only the five events that end a call move it. The others either carry the
        /// state themselves (the caller sets that) or belong to an audio/video engine
        /// this app does not have. Either way the event is remembered, so a log line can
        /// say what arrived last.
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
        /// Turns the microphone off or on. Only while there is a call to mute: flipping
        /// this with no call would be a switch over nothing.
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

        /// <summary>Takes an ended call off the screen. A call still going is left alone.</summary>
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
        /// Whether a message is about this call.
        ///
        /// A message with no call id on it is never a match. Otherwise anything that
        /// forgot the id could end whatever call happens to be on screen.
        /// </summary>
        private bool SpeaksOf(string callId)
        {
            return !string.IsNullOrEmpty(callId) && CallId == callId;
        }
    }
}
