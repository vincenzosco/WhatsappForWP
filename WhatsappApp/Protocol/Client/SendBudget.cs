using System;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The time a send may spend asking the server questions before the stanza is written.
    ///
    /// On a message took about a minute to leave. Every question
    /// in front of a send waited `NativeConnection.AnswerSeconds` on its own - the device list,
    /// the bundles per device, the bundles per person, and the privacy token twice - and nothing
    /// bounded their sum, so three silent questions were sixty seconds of a bubble that said
    /// "sending" (the device log carried `the per-device bundle query. the server did not
    /// answer` three times). Most of those answers are not needed for the message to exist: the
    /// one thing it cannot do without is a key for one device, and that is the only question
    /// allowed to outlast this budget.
    ///
    /// It is a value with an injected clock and no WinRT in it, so `SendBudgetTests` drives it
    /// on the desktop: the service that uses it cannot be compiled off the phone.
    /// </summary>
    public struct SendBudget
    {
        /// <summary>
        /// How long the questions in front of a send may take, together. Three seconds is the
        /// time a person gives a message before they start to wonder; past it the stanza goes
        /// out with what this phone already knows.
        /// </summary>
        public const int Milliseconds = 3000;

        private readonly long _started;

        private SendBudget(long started)
        {
            _started = started;
        }

        /// <summary>A budget that starts at `ticks`, on the caller's clock.</summary>
        public static SendBudget Start(long ticks)
        {
            return new SendBudget(ticks);
        }

        /// <summary>
        /// How much of it is left, in milliseconds, and never a negative number. A clock that
        /// moved back under the start is not a clock that owes time: the budget is whole again.
        /// </summary>
        public long MillisecondsLeft(long ticks)
        {
            long spent = (ticks - _started) / TimeSpan.TicksPerMillisecond;
            if (spent < 0) spent = 0;

            long left = Milliseconds - spent;
            return left < 0 ? 0 : left;
        }

        /// <summary>
        /// The same in whole seconds, for a deadline that is expressed in seconds, and 1 while
        /// any of it remains: a question with a zero deadline is not a question but a failure
        /// that was decided in advance.
        /// </summary>
        public int SecondsLeft(long ticks)
        {
            long left = MillisecondsLeft(ticks);
            if (left <= 0) return 0;

            int seconds = (int)(left / 1000);
            return seconds < 1 ? 1 : seconds;
        }

        /// <summary>Nothing is left: a question asked now would be one the message does not need.</summary>
        public bool IsSpent(long ticks)
        {
            return MillisecondsLeft(ticks) <= 0;
        }
    }
}
