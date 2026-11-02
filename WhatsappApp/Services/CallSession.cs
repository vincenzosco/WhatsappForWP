using System;

namespace WhatsappApp.Services
{
    /// <summary>Where a call is.</summary>
    public enum CallState
    {
        /// <summary>No call. The only state a start-up is in.</summary>
        Idle,

        /// <summary>An offer arrived and the phone is ringing: nothing has been said yet.</summary>
        Ringing,

        /// <summary>
        /// This phone answered, and the other side has not confirmed. A call dialled from
        /// here starts in this state too - it is "waiting for the other phone", which is
        /// the same wait from both ends.
        /// </summary>
        Connecting,

        /// <summary>The other side accepted: the call is up.</summary>
        Active,

        /// <summary>The call is over. The reason is what the banner says.</summary>
        Ended
    }

    /// <summary>
    /// One call, as the app tracks it: who, which direction, and which of the five states
    /// it is in.
    ///
    /// Why it is a class of its own with no WinRT in it: the transitions are the part worth
    /// a test, and the three that are easy to get wrong are all here rather than in a page.
    /// A `terminate` for a call this phone never had must not open one; an `accept` for a
    /// call that is not ringing must not make it active; and a call that ended must not be
    /// ended again and lose the reason it ended for.
    ///
    /// The media is **not** here and does not exist yet: an `Active` call is a call whose
    /// signalling succeeded. See `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md`.
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

        public CallState State { get; private set; }

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
        /// Whether an unanswered call has waited long enough to be called over
        /// (`DataService.CallSetupSeconds`). Only `Ringing` and `Connecting` can expire: an
        /// answered call is waiting for nobody, and a deadline of zero seconds is no deadline
        /// at all rather than an immediate miss.
        /// </summary>
        public bool Expired(DateTime now, int seconds)
        {
            if (seconds <= 0) return false;
            if (State != CallState.Ringing && State != CallState.Connecting) return false;

            return (now - StartedAt).TotalSeconds >= seconds;
        }

        public CallSession()
        {
            State = CallState.Idle;
        }

        /// <summary>Something is happening: a page shows the call banner while this is true.</summary>
        public bool Shows
        {
            get { return State != CallState.Idle; }
        }

        /// <summary>A call is up or being set up: a second offer has to be refused as busy.</summary>
        public bool Occupied
        {
            get { return State == CallState.Ringing || State == CallState.Connecting || State == CallState.Active; }
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
            EndedReason = null;
            StartedAt = DateTime.UtcNow;
            ConnectedAt = default(DateTime);
            State = CallState.Ringing;

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
            EndedReason = null;
            StartedAt = DateTime.UtcNow;
            ConnectedAt = default(DateTime);
            State = CallState.Connecting;

            return true;
        }

        /// <summary>
        /// The call is answered: from `Ringing` this phone is answering, from `Connecting`
        /// it is the other side's `accept` arriving. Both end in `Active`, which is why
        /// this is one method and not two.
        /// </summary>
        public bool Connect(string callId)
        {
            if (!SpeaksOf(callId)) return false;
            if (State != CallState.Ringing && State != CallState.Connecting) return false;

            if (ConnectedAt == default(DateTime)) ConnectedAt = DateTime.UtcNow;

            State = CallState.Active;
            return true;
        }

        /// <summary>The callee's phone has the offer and is preparing its side.</summary>
        public bool PreAccept(string callId)
        {
            if (!SpeaksOf(callId)) return false;
            if (State != CallState.Connecting) return false;

            // Nothing on screen changes: a `preaccept` is the other phone saying "I heard
            // you", and `Connecting` is already what the banner says.
            return true;
        }

        /// <summary>
        /// The call is over. One that was never ringing is not a call: a `terminate` for
        /// another device's call must not open a banner nobody can close.
        /// </summary>
        public bool End(string callId, string reason)
        {
            if (!SpeaksOf(callId)) return false;
            if (State == CallState.Idle || State == CallState.Ended) return false;

            EndedReason = reason;
            State = CallState.Ended;
            return true;
        }

        /// <summary>Takes the ended call off the screen. Anything still up is left alone.</summary>
        public void Clear()
        {
            if (State != CallState.Ended) return;

            CallId = null;
            PeerJid = null;
            IsVideo = false;
            IsIncoming = false;
            EndedReason = null;
            StartedAt = default(DateTime);
            ConnectedAt = default(DateTime);
            State = CallState.Idle;
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
