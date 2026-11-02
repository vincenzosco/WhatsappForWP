using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// The five states of a call, and the four transitions that are easy to get wrong.
    ///
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

            Assert.AreEqual(CallState.Idle, session.State);
            Assert.IsFalse(session.Shows, "no call, no banner");
            Assert.IsFalse(session.Occupied);
        }

        [Test]
        public static void AnOfferRingsAndTheIdentityIsTheCalls()
        {
            var session = new CallSession();

            Assert.IsTrue(session.Ring(CallId, Peer, true), "an offer rings");

            Assert.AreEqual(CallState.Ringing, session.State);
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
            Assert.AreEqual(CallState.Ringing, session.State);
        }

        [Test]
        public static void ACallMadeFromThisPhoneStartsConnecting()
        {
            var session = new CallSession();

            Assert.IsTrue(session.Dial(CallId, Peer, false));

            Assert.AreEqual(CallState.Connecting, session.State);
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

            Assert.IsFalse(session.Connect(CallId), "an accept for a call already up changes nothing");
        }

        [Test]
        public static void AnAcceptForAnotherCallsIdIsIgnored()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.Connect("SOMEONEELSES"), "a node about another call is not about this one");
            Assert.AreEqual(CallState.Ringing, session.State);
        }

        [Test]
        public static void AnAcceptForACallThatIsNotUpIsIgnored()
        {
            var session = new CallSession();

            Assert.IsFalse(session.Connect(CallId), "no call to make active");
            Assert.AreEqual(CallState.Idle, session.State, "and one is not opened by it");
        }

        [Test]
        public static void ATerminateForACallThisPhoneNeverHadOpensNothing()
        {
            var session = new CallSession();

            Assert.IsFalse(session.End(CallId, "timeout"));
            Assert.AreEqual(CallState.Idle, session.State);
            Assert.IsFalse(session.Shows, "a call nobody started is not a banner nobody can close");
        }

        [Test]
        public static void TheEndKeepsTheReasonAndIsNotRepeated()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);
            session.Connect(CallId);

            Assert.IsTrue(session.End(CallId, "timeout"));
            Assert.AreEqual(CallState.Ended, session.State);
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

            Assert.AreEqual(CallState.Idle, session.State);
            Assert.IsTrue(session.CallId == null, "the ended call is gone");

            var live = new CallSession();
            live.Ring(CallId, Peer, false);
            live.Clear();

            Assert.AreEqual(CallState.Ringing, live.State, "a call still up is not cleared");
        }

        [Test]
        public static void ANodeWithNoIdCannotEndWhatIsOnScreen()
        {
            var session = new CallSession();
            session.Ring(CallId, Peer, false);

            Assert.IsFalse(session.End(null, "timeout"), "an empty id matches nothing");
            Assert.IsFalse(session.End("", "timeout"));
            Assert.AreEqual(CallState.Ringing, session.State);
        }

        [Test]
        public static void APreAcceptOnlyMovesACallThatIsBeingSetUp()
        {
            var session = new CallSession();
            session.Dial(CallId, Peer, false);

            Assert.IsTrue(session.PreAccept(CallId), "the other phone heard the offer");
            Assert.AreEqual(CallState.Connecting, session.State, "the banner says the same thing");

            var ringing = new CallSession();
            ringing.Ring(CallId, Peer, false);

            Assert.IsFalse(ringing.PreAccept(CallId), "a preaccept is not the callee's own call");
            Assert.AreEqual(CallState.Ringing, ringing.State);
        }
    }
}
