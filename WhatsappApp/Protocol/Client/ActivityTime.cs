using System;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// When a conversation was last active - and what to do with a time that cannot be one.
    ///
    /// Why it exists: the chat list is ordered by the time of its last message, so one wrong
    /// time is a row in the wrong place. On 2026-10-22 the list opened with chats nobody had
    /// written to in weeks: their stored previews were dated in November and December of a
    /// year that had not happened yet, so they outranked everything. Those dates were a
    /// day/month swap of the real ones (12 October had become 10 December), written by an
    /// older build, and they were sitting in the copy on disk - being replaced by the server
    /// at the next sync is not good enough, because that is many seconds of a wrong list and
    /// a wrong list is what a person reads as \"the app is broken\".
    ///
    /// A time in the future is not a claim this phone has to accept: WhatsApp never sends
    /// one, and the only sources of one are a bug here or a clock that is wrong somewhere.
    /// The tolerance is there so that a phone and a server a few minutes apart still agree.
    /// </summary>
    public static class ActivityTime
    {
        /// <summary>
        /// How far ahead of this phone's clock a time may still be believed. Five minutes is
        /// clock drift, not a message from the future.
        /// </summary>
        public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Whether a time can be one: a real moment, and not later than this phone's clock
        /// plus the tolerance. `DateTime.MinValue` is \"no time at all\", which is not a time.
        /// </summary>
        public static bool IsSane(DateTime at, DateTime now)
        {
            if (at == DateTime.MinValue) return false;

            return at <= now + Tolerance;
        }

        /// <summary>
        /// The newest of two times that can be believed, or `MinValue` when neither can.
        ///
        /// This is what a row does with a time it has been handed: a candidate that is in the
        /// future is ignored in favour of the row's own, and a row with neither stays at the
        /// bottom of the list until something true arrives - which is where a conversation
        /// nobody can date belongs, not at the top.
        /// </summary>
        public static DateTime Trust(DateTime candidate, DateTime known, DateTime now)
        {
            if (IsSane(candidate, now)) return candidate;
            if (IsSane(known, now)) return known;

            return DateTime.MinValue;
        }
    }
}
