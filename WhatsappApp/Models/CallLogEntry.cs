using System;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    /// <summary>
    /// A call-log entry, as `Protocol/Client/Calls.cs` read it out of WhatsApp's
    /// own call log. `DataService` turns each one into a `ChatMessage` row for the
    /// Calls page, so the words a row shows (its direction, whether it was missed)
    /// come from here.
    ///
    /// It does not implement INotifyPropertyChanged: the collection is emptied and
    /// refilled on every scan, not changed field by field.
    /// </summary>
    public class CallLogEntry
    {
        public string ChatId { get; set; }
        public string Name { get; set; }
        public DateTime Timestamp { get; set; }
        public string CallId { get; set; }
        public string Reason { get; set; }
        public int DurationSeconds { get; set; }
        public bool IsVideo { get; set; }

        /// <summary>
        /// Nobody answered this one. The list paints it red: the word in `Detail` says the
        /// same thing, but a missed call is the one row of the log a person scans for, and
        /// the released app colours it instead of asking to read every line.
        /// </summary>
        public bool IsMissed
        {
            get
            {
                string reason = (Reason ?? "").Trim().ToLowerInvariant();
                return reason == "missed" || reason == "timeout";
            }
        }

        /// <summary>Initials for the avatar, as in the chat list.</summary>
        public string Initials
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return "?";
                string trimmed = Name.Trim();

                // A name of spaces is empty once trimmed, and `Substring(0, 1)` on it throws
                // out of a property the call row binds: one such contact took the whole Calls
                // tab down instead of drawing its initials.
                if (trimmed.Length == 0) return "?";

                if (trimmed.StartsWith("+") && trimmed.Length > 1)
                    return trimmed.Substring(1, Math.Min(2, trimmed.Length - 1)).ToUpper();
                return trimmed.Substring(0, 1).ToUpper();
            }
        }

        /// <summary>Time as in the chat list (today -> HH:mm, yesterday -> "Yesterday").</summary>
        public string TimeText
        {
            get
            {
                if (Timestamp == default(DateTime)) return "";

                DateTime local = Timestamp.Kind == DateTimeKind.Utc ? Timestamp.ToLocalTime() : Timestamp;
                DateTime now = DateTime.Now;
                if (local.Date == now.Date) return local.ToString("HH:mm");
                if (local.Date == now.Date.AddDays(-1)) return Loc.Get("ChatMessage_Yesterday", "Yesterday");
                if (local.Year == now.Year) return local.ToString("dd/MM");
                return local.ToString("dd/MM/yy");
            }
        }

        /// <summary>
        /// Detail line: outcome, an optional "video" and the duration when we know
        /// it. An outcome we do not recognize stays a generic incoming call,
        /// instead of showing text taken from the server.
        /// </summary>
        public string Detail
        {
            get
            {
                string kind;
                switch ((Reason ?? "").Trim().ToLowerInvariant())
                {
                    // `missed` is the native client's word for a call nobody answered;
                    // `timeout` is the old server's. Both are the same row on screen.
                    case "missed":
                    case "timeout": kind = Loc.Get("CallsPage_Missed", "Missed call"); break;
                    case "outgoing": kind = Loc.Get("CallsPage_Outgoing", "Outgoing call"); break;
                    case "reject": kind = Loc.Get("CallsPage_Declined", "Declined call"); break;
                    case "busy": kind = Loc.Get("CallsPage_Busy", "Call, line busy"); break;
                    case "cancel": kind = Loc.Get("CallsPage_Cancelled", "Cancelled call"); break;
                    case "accepted": kind = Loc.Get("CallsPage_Answered", "Answered call"); break;
                    default: kind = Loc.Get("CallsPage_Incoming", "Incoming call"); break;
                }

                if (IsVideo) kind = kind + " · " + Loc.Get("CallsPage_Video", "Video");
                if (DurationSeconds > 0) kind = kind + " · " + FormatDuration(DurationSeconds);
                return kind;
            }
        }

        private static string FormatDuration(int seconds)
        {
            int minutes = seconds / 60;
            int rest = seconds % 60;
            return minutes + ":" + rest.ToString("00");
        }
    }
}
