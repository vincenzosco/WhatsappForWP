using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// The clock of a call: when a duration starts existing, and when an unanswered call is
    /// over.
    ///
    /// Why these are here: the phone kept a call it had offered forever - the log of
    /// says "a call is already up, so. was not called" on the next dial,
    /// after an offer that never got an answer. The rule that ends it is a value, not a
    /// timer, so it is pinned here rather than waited for on a device.
    /// </summary>
    public static class CallSessionDurationTests
    {
        [Test]
        public static void ACallThatWasNeverAnsweredHasNoDuration()
        {
            var call = new CallSession();

            Assert.IsTrue(call.Dial("CALL0001", "393401234567@s.whatsapp.net", false));
            Assert.IsFalse(call.WasAnswered, "nobody answered yet");
            Assert.AreEqual(0, call.ConnectedSeconds, "so there is nothing to count");
        }

        [Test]
        public static void TheDurationStartsWhenTheCallIsAnswered()
        {
            var call = new CallSession();
            call.Dial("CALL0001", "393401234567@s.whatsapp.net", false);

            Assert.IsTrue(call.Connect("CALL0001"));
            Assert.IsTrue(call.WasAnswered, "the call is up");
            Assert.IsTrue(call.ConnectedAt != default(System.DateTime), "and it has a moment");

            // The duration is read off the clock, so it is 0 or 1 second here and never a
            // negative number from a clock that moved between the two lines.
            Assert.IsTrue(call.ConnectedSeconds >= 0 && call.ConnectedSeconds < 5,
                "a call answered a moment ago is a second old at most");
        }

        [Test]
        public static void EndingACallKeepsItsMomentAndItsDuration()
        {
            var call = new CallSession();
            call.Dial("CALL0001", "393401234567@s.whatsapp.net", true);

            call.Connect("CALL0001");
            System.DateTime answeredAt = call.ConnectedAt;

            Assert.IsTrue(call.End("CALL0001", "accepted"));
            Assert.AreEqual(answeredAt, call.ConnectedAt,
                "an ended call still knows when it was answered");
            Assert.IsTrue(call.WasAnswered, "so the log has a duration for it");
        }

        [Test]
        public static void ANewCallDoesNotInheritTheOldCallsMoment()
        {
            var call = new CallSession();
            call.Dial("CALL0001", "393401234567@s.whatsapp.net", false);
            call.Connect("CALL0001");
            call.End("CALL0001", "accepted");
            call.Clear();

            Assert.IsTrue(call.Dial("CALL0002", "393401234567@s.whatsapp.net", false));
            Assert.IsFalse(call.WasAnswered, "the second call has not been answered");
            Assert.AreEqual(0, call.ConnectedSeconds, "and inherits no duration from the first");
        }

        [Test]
        public static void ARingingCallExpiresAndAnAnsweredOneDoesNot()
        {
            var call = new CallSession();
            call.Ring("CALL0001", "393401234567@s.whatsapp.net", false);

            System.DateTime started = call.StartedAt;

            Assert.IsFalse(call.Expired(started.AddSeconds(10), 45), "ten seconds is not a miss");
            Assert.IsTrue(call.Expired(started.AddSeconds(45), 45), "forty-five is");

            call.Connect("CALL0001");

            Assert.IsFalse(call.Expired(started.AddSeconds(600), 45),
                "an answered call is not waiting for anybody");
        }

        [Test]
        public static void ASecondExpiryAsksForTheRuleToBeSensible()
        {
            var call = new CallSession();
            call.Ring("CALL0001", "393401234567@s.whatsapp.net", false);

            Assert.IsFalse(call.Expired(call.StartedAt.AddHours(1), 0),
                "a deadline of zero seconds is no deadline, not an immediate miss");
        }

        [Test]
        public static void AnIdleCallNeverExpires()
        {
            var call = new CallSession();

            Assert.IsFalse(call.Expired(System.DateTime.UtcNow.AddDays(1), 45),
                "there is no call to run out");
        }
    }
}
