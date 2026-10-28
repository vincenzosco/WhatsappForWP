using System;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Where a tap on the tile - or on the lock screen, which shows the same tile -
    /// should land, when the thing that arrived is not a message.
    ///
    /// Why it is needed: a tap on the Start tile is a plain launch, and WP8.1 hands it
    /// the same empty arguments as the app list does. There is nothing in the launch
    /// that says where the tap was, so the only fact available is what the phone was
    /// showing: `DataService` records the destination here whenever the badge is drawn,
    /// and `App` gives it to the first page of the next start.
    ///
    /// Why "since you last looked" and not just "there is one": a missed call stays in
    /// the log for good and an update nobody watched stays unwatched for its day, so
    /// "there is one" would send every single start to the same tab until it was read
    /// by opening it. The route is what arrived **after** the user last had that section
    /// on screen - which is also exactly what the phone would have alerted about.
    ///
    /// This file has no WinRT in it and is compiled by the desktop runner
    /// (`ProtocolTests/LaunchRouteTests.cs`): it is the part of the rule that is a
    /// decision. The storage around it is in `SessionService`.
    /// </summary>
    public static class LaunchRoute
    {
        /// <summary>Something was posted by a contact who is not the user.</summary>
        public const string Status = "status";

        /// <summary>A call nobody answered arrived.</summary>
        public const string Calls = "calls";

        /// <summary>
        /// The newest thing of the two, or null when neither is newer than the last
        /// time that section was on screen.
        ///
        /// Both arguments are the timestamps of the *newest* row of their kind, in the
        /// clock the rows carry (`ChatMessage.Timestamp`, `CallLogEntry.Timestamp`),
        /// and the second of each pair is when the user last had that section open.
        /// When both qualify the later arrival wins: a tap opens what the phone would
        /// have told the user about last.
        /// </summary>
        public static string Decide(DateTime newestUnseenStatus, DateTime statusOpenedAt,
                                    DateTime newestMissedCall, DateTime callsOpenedAt)
        {
            bool status = IsNewerThan(newestUnseenStatus, statusOpenedAt);
            bool calls = IsNewerThan(newestMissedCall, callsOpenedAt);

            if (status && calls)
            {
                // A tie is not a tie in practice: the two clocks are the same, and one
                // of them is at least a second older. Deciding it anyway keeps the
                // function total, and a status is the rarer of the two.
                return newestUnseenStatus >= newestMissedCall ? Status : Calls;
            }

            if (status) return Status;
            if (calls) return Calls;
            return null;
        }

        /// <summary>
        /// A row is news when it arrived after the section was last on screen. A phone
        /// that has never had the section open counts as "before everything", which is
        /// what `default(DateTime)` is.
        /// </summary>
        private static bool IsNewerThan(DateTime newest, DateTime openedAt)
        {
            if (newest == default(DateTime)) return false;
            return newest > openedAt;
        }

        /// <summary>
        /// Whether a route recorded before a start is still worth acting on.
        ///
        /// Why it expires: the record is written when the badge is drawn and taken when
        /// a start consumes it, so the only way to find an old one is a start that never
        /// happened - the app closed, the phone rebooted, the notification layer drew
        /// nothing since. A tap hours later is a tap on a tile that no longer says that,
        /// and sending the user to the calls tab for it would be a wrong guess dressed
        /// up as a deep link.
        /// </summary>
        public static bool IsFresh(DateTime writtenUtc, DateTime nowUtc)
        {
            if (writtenUtc == default(DateTime)) return false;

            TimeSpan age = nowUtc - writtenUtc;

            // A clock that was put back (or forward and time has not caught up) gives a
            // negative age: the record cannot be older than it says, so it is kept.
            if (age < TimeSpan.Zero) return true;

            return age <= TimeSpan.FromHours(FreshHours);
        }

        /// <summary>How long a recorded route stays worth acting on.</summary>
        public const int FreshHours = 6;
    }
}
