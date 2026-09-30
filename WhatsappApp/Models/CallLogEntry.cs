using System;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    /// <summary>
    /// A call-log entry. The data comes from the adapter, which derives it from
    /// the GOWA history: they are incoming calls only, taken from the most recent
    /// chats the server scanned.
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

        /// <summary>Initials for the avatar, as in the chat list.</summary>
        public string Initials
        {
            get
            {
                if (string.IsNullOrEmpty(Name)) return "?";
                string trimmed = Name.Trim();
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
                    case "timeout": kind = Loc.Get("CallsPage_Missed", "Missed call"); break;
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
