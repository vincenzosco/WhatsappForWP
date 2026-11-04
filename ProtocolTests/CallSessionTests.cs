using System;
using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// The call's states and the transitions that are easy to get wrong.
    ///
    /// The model now carries the official client's fifteen `CallState` values (read out of
    /// the APK on 4 November 2026) and derives the five-value `CallPhase` the pages draw.
    /// These are tests and not a page's behaviour because they are the only part of a call
    /// that can be checked here: the media does not exist yet (see
    /// `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md`), so what a call
    /// *is* until then is exactly this machine.
    /// </summary>
    public static class CallSessionTests
    {
        private const string CallId = "CALLBBBB0002";
        private const string Peer = "15551230000@s.whatsapp.net";

        [Test]
        public static void ASessionStartsIdleAndShowsNothing()
        {
            var session = new CallSession();

            Assert.AreEqual(CallState.None, session.State, "the wire's own word for no call");
            Assert.AreEqual(CallPhase.Idle, session.Phase, "and the screen's");
            Assert.IsFalse(session.Shows, "no call, no banner");
            Assert.IsFalse(session.Occupied);
        }

        [Test]
        public static void AnOfferRingsAndTheIdentityIsTheCalls()
        {
            var session = new CallSession();

            Assert.IsTrue(session.Ring(CallId, Peer, true), "an offer rings");

            Assert.AreEqual(CallState.ReceivedCall, session.State, "the official state of a ringing offer");
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
            Assert.AreEqual(CallId, session.CallId);
            Assert.AreEqual(Peer, session.PeerJid);
            Assert.IsTrue(session.IsVideo);
            Assert.IsTrue(session.IsIncoming);
            Assert.IsTrue(session.Occupied, "a second call has to be refused as busy");
        }

        [Test]
        public static void TheSameOfferTwiceRingsOnce()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.Ring(CallId, Peer, false),
                "the server repeats an offer, and a phone that rings twice is a bug");
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
        }

        [Test]
        public static void ACallMadeFromThisPhoneStartsCalling()
        {
            var session = new CallSession();

            Assert.IsTrue(session.Dial(CallId, Peer, false));

            Assert.AreEqual(CallState.Calling, session.State, "the official state of an offer that went out");
            Assert.AreEqual(CallPhase.Connecting, session.Phase);
            Assert.IsFalse(session.IsIncoming);
            Assert.IsTrue(session.Occupied);
        }

        [Test]
        public static void AnsweringMakesTheCallActiveAndOnlyOnce()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsTrue(session.Connect(CallId), "this phone answering");
            Assert.AreEqual(CallState.Active, session.State);
            Assert.AreEqual(CallPhase.Active, session.Phase);
            Assert.IsTrue(session.WasAnswered);

            Assert.IsFalse(session.Connect(CallId), "an accept for a call already up changes nothing");
        }

        [Test]
        public static void AnAcceptForAnotherCallsIdIsIgnored()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.Connect("SOMEONEELSES"), "a node about another call is not about this one");
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
        }

        [Test]
        public static void AnAcceptForACallThatIsNotUpIsIgnored()
        {
            var session = new CallSession();

            Assert.IsFalse(session.Connect(CallId), "no call to make active");
            Assert.AreEqual(CallState.None, session.State, "and one is not opened by it");
        }

        [Test]
        public static void ATerminateForACallThisPhoneNeverHadOpensNothing()
        {
            var session = new CallSession();

            Assert.IsFalse(session.End(CallId, "timeout"));
            Assert.AreEqual(CallPhase.Idle, session.Phase);
            Assert.IsFalse(session.Shows, "a call nobody started is not a banner nobody can close");
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
                "a second end does not overwrite the reason the call really ended for");
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

            Assert.AreEqual(CallPhase.Ringing, live.Phase, "a call still up is not cleared");
        }

        [Test]
        public static void ANodeWithNoIdCannotEndWhatIsOnScreen()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.End(null, "timeout"), "an empty id matches nothing");
            Assert.IsFalse(session.End("", "timeout"));
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
        }

        [Test]
        public static void APreAcceptMovesACallThatIsBeingSetUpAndSaysWhichStateItIs()
        {
            var session = new CallSession();
            session.Dial(CallId, Peer, false);

            Assert.IsTrue(session.PreAccept(CallId), "the other phone heard the offer");
            Assert.AreEqual(CallState.PreAcceptReceived, session.State, "the official state of that news");
            Assert.AreEqual(CallPhase.Connecting, session.Phase, "the banner says the same thing");

            var ringing = new CallSession();
            ringing.Ring(CallId, Peer, false);

            Assert.IsFalse(ringing.PreAccept(CallId), "a preaccept is not the callee's own call");
            Assert.AreEqual(CallPhase.Ringing, ringing.Phase);
        }

        [Test]
        public static void TheFifteenOfficialStatesAreAllMappedToAPhase()
        {
            // The enum has to be the official list, all fifteen of it: a state added
            // upstream is a decision here, not a number that falls through a default.
            CallState[] official = new CallState[]
            {
                CallState.None, CallState.PreCalling, CallState.Calling,
                CallState.ReceivedCall, CallState.PreAcceptReceived, CallState.AcceptSent,
                CallState.AcceptReceived, CallState.Link, CallState.Active,
                CallState.ConnectedLonely, CallState.Rejoining, CallState.Ending,
                CallState.ActiveElsewhere, CallState.Screening, CallState.BCallStarting
            };

            Assert.AreEqual(15, official.Length, "the official client has fifteen states");
            Assert.AreEqual(15, Enum.GetValues(typeof(CallState)).Length, "and the model carries all of them");

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
                "an event that is not a transition leaves the call where it is");
            Assert.AreEqual(CallPhase.Ringing, session.Phase);
            Assert.AreEqual(CallEvent.BatteryLevelLow, session.LastEvent);

            Assert.IsTrue(session.Apply(CallEvent.CallRejectReceived, CallId, "busy"));
            Assert.AreEqual("busy", session.EndedReason, "a refusal keeps the word it was given");
        }

        [Test]
        public static void AnEventForAnotherCallChangesNothing()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.Apply(CallEvent.CallTerminateReceived, "SOMEONEELSES", "timeout"));
            Assert.AreEqual(CallPhase.Ringing, session.Phase, "a node about another call is not about this one");
            Assert.IsTrue(session.LastEvent == null, "and it is not this call's last event");
        }
    }
}
