using System;
using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// The call states, and the moves between them that are easy to get wrong.
    ///
    /// A call here is only its signalling: there is no audio and no video yet (see
    /// `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md`). So the
    /// thing worth testing is this little state machine - which is also the thing that
    /// would break without anyone noticing.
    /// </summary>
    public static class CallSessionTests
    {
        private const string CallId = "CALLBBBB0002";
        private const string Peer = "15551230000@s.whatsapp.net";

        [Test]
        public static void ASessionStartsIdleAndShowsNothing()
        {
            var session = new CallSession();

            Assert.AreEqual(CallState.None, session.State, "no call, in the network's own word");
            Assert.AreEqual(CallPhase.Idle, session.Phase, "and in the screen's");
            Assert.IsFalse(session.Shows, "nothing to draw");
            Assert.IsFalse(session.Occupied);
        }

        [Test]
        public static void AnOfferRingsAndTheIdentityIsTheCalls()
        {
            var session = new CallSession();

            Assert.IsTrue(session.Ring(CallId, Peer, true), "a call is coming in");

            Assert.AreEqual(CallState.ReceivedCall, session.State, "that is the official name for a call that rings");
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
            Assert.AreEqual(CallId, session.CallId);
            Assert.AreEqual(Peer, session.PeerJid);
            Assert.IsTrue(session.IsVideo);
            Assert.IsTrue(session.IsIncoming);
            Assert.IsTrue(session.Occupied, "so a second call has to be told the line is busy");
        }

        [Test]
        public static void TheSameOfferTwiceRingsOnce()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.Ring(CallId, Peer, false),
                "the server can send the same offer twice, and a phone that rings twice is a bug");
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
        }

        [Test]
        public static void ACallMadeFromThisPhoneStartsCalling()
        {
            var session = new CallSession();

            Assert.IsTrue(session.Dial(CallId, Peer, false));

            Assert.AreEqual(CallState.Calling, session.State, "the official name for a call that went out");
            Assert.AreEqual(CallPhase.Connecting, session.Phase);
            Assert.IsFalse(session.IsIncoming);
            Assert.IsTrue(session.Occupied);
        }

        [Test]
        public static void AnsweringMakesTheCallActiveAndOnlyOnce()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsTrue(session.Connect(CallId), "we answered");
            Assert.AreEqual(CallState.Active, session.State);
            Assert.AreEqual(CallPhase.Active, session.Phase);
            Assert.IsTrue(session.WasAnswered);

            Assert.IsFalse(session.Connect(CallId), "answering a call that is already up changes nothing");
        }

        [Test]
        public static void AnAcceptForAnotherCallsIdIsIgnored()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.Connect("SOMEONEELSES"), "an answer for another call is not an answer to this one");
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
        }

        [Test]
        public static void AnAcceptForACallThatIsNotUpIsIgnored()
        {
            var session = new CallSession();

            Assert.IsFalse(session.Connect(CallId), "there is no call to answer");
            Assert.AreEqual(CallState.None, session.State, "and answering must not create one");
        }

        [Test]
        public static void ATerminateForACallThisPhoneNeverHadOpensNothing()
        {
            var session = new CallSession();

            Assert.IsFalse(session.End(CallId, "timeout"));
            Assert.AreEqual(CallPhase.Idle, session.Phase);
            Assert.IsFalse(session.Shows, "a call nobody started would be a banner nobody can close");
        }

        [Test]
        public static void TheEndKeepsTheReasonAndIsNotRepeated()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);
            session.Connect(CallId);

            Assert.IsTrue(session.End(CallId, "timeout"));
            Assert.AreEqual(CallState.Ending, session.State, "the official word for a call on its way out");
            Assert.AreEqual(CallPhase.Ended, session.Phase);
            Assert.AreEqual("timeout", session.EndedReason);
            Assert.IsFalse(session.Occupied, "the line is free again");

            Assert.IsFalse(session.End(CallId, "reject"),
                "ending twice would overwrite the reason it really ended for");
            Assert.AreEqual("timeout", session.EndedReason);
        }

        [Test]
        public static void ClearingTakesTheEndedCallAwayAndLeavesALiveOne()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, true);
            session.End(CallId, "reject");

            session.Clear();

            Assert.AreEqual(CallState.None, session.State);
            Assert.IsTrue(session.CallId == null, "the ended call is gone");

            var live = new CallSession();
            live.Ring(CallId, Peer, false);
            live.Clear();

            Assert.AreEqual(CallPhase.Ringing, live.Phase, "a call that is still going is not cleared");
        }

        [Test]
        public static void ANodeWithNoIdCannotEndWhatIsOnScreen()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.End(null, "timeout"), "a message with no id matches nothing");
            Assert.IsFalse(session.End("", "timeout"));
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
        }

        [Test]
        public static void APreAcceptMovesACallThatIsBeingSetUpAndSaysWhichStateItIs()
        {
            var session = new CallSession();
            session.Dial(CallId, Peer, false);

            Assert.IsTrue(session.PreAccept(CallId), "their phone got our call");
            Assert.AreEqual(CallState.PreAcceptReceived, session.State, "which is what that state means");
            Assert.AreEqual(CallPhase.Connecting, session.Phase, "and the banner still says connecting");

            var ringing = new CallSession();
            ringing.Ring(CallId, Peer, false);

            Assert.IsFalse(ringing.PreAccept(CallId), "a pre-accept is about their own call, not about ours");
            Assert.AreEqual(CallPhase.Ringing, ringing.Phase);
        }

        [Test]
        public static void TheFifteenOfficialStatesAreAllMappedToAPhase()
        {
            // The enum has to hold the official list, all fifteen. A state added later
            // is a decision here, not a number that silently lands in the wrong phase.
            CallState[] official = new CallState[]
            {
                CallState.None, CallState.PreCalling, CallState.Calling,
                CallState.ReceivedCall, CallState.PreAcceptReceived, CallState.AcceptSent,
                CallState.AcceptReceived, CallState.Link, CallState.Active,
                CallState.ConnectedLonely, CallState.Rejoining, CallState.Ending,
                CallState.ActiveElsewhere, CallState.Screening, CallState.BCallStarting
            };

            Assert.AreEqual(15, official.Length, "the official app has fifteen states");
            Assert.AreEqual(15, Enum.GetValues(typeof(CallState)).Length, "and the model has all of them");

            Assert.AreEqual(CallPhase.Idle, CallSession.PhaseOf(CallState.None));
            Assert.AreEqual(CallPhase.Connecting, CallSession.PhaseOf(CallState.PreCalling));
            Assert.AreEqual(CallPhase.Connecting, CallSession.PhaseOf(CallState.AcceptSent));
            Assert.AreEqual(CallPhase.Connecting, CallSession.PhaseOf(CallState.Rejoining));
            Assert.AreEqual(CallPhase.Active, CallSession.PhaseOf(CallState.Link));
            Assert.AreEqual(CallPhase.Active, CallSession.PhaseOf(CallState.ActiveElsewhere));
            Assert.AreEqual(CallPhase.Active, CallSession.PhaseOf(CallState.BCallStarting));
            Assert.AreEqual(CallPhase.Ended, CallSession.PhaseOf(CallState.Ending));
        }

        [Test]
        public static void AFatalCallbackEventEndsTheCallAsFailed()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);
            session.Connect(CallId);

            Assert.IsTrue(session.Apply(CallEvent.HandleCallFatal, CallId, null));

            Assert.AreEqual(CallState.Ending, session.State);
            Assert.AreEqual("failed", session.EndedReason);
            Assert.AreEqual(CallEvent.HandleCallFatal, session.LastEvent, "and the event is remembered");
        }

        [Test]
        public static void AMissedCallbackEventEndsTheCallAsMissed()
        {
            var session = new CallSession();
            session.Dial(CallId, Peer, false);

            Assert.IsTrue(session.Apply(CallEvent.CallMissed, CallId, null));
            Assert.AreEqual("missed", session.EndedReason);
        }

        [Test]
        public static void ARejectEventKeepsItsOwnReasonAndAnUnknownOneChangesNothing()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.Apply(CallEvent.BatteryLevelLow, CallId, null),
                "an event that does not end a call leaves the call where it is");
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
            Assert.AreEqual(CallEvent.BatteryLevelLow, session.LastEvent);

            Assert.IsTrue(session.Apply(CallEvent.CallRejectReceived, CallId, "busy"));
            Assert.AreEqual("busy", session.EndedReason, "a refusal keeps the reason it came with");
        }

        [Test]
        public static void AnEventForAnotherCallChangesNothing()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.Apply(CallEvent.CallTerminateReceived, "SOMEONEELSES", "timeout"));
            Assert.AreEqual(CallPhase.Ringing, session.Phase, "a message about another call is about another call");
            Assert.IsTrue(session.LastEvent == null, "so it is not this call's last event");
        }
    }
}
