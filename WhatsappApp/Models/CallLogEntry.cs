using System;
using WhatsappApp.Services;

namespace WhatsappApp.Models
{
    /// <summary>
    /// Una voce del registro chiamate. I dati arrivano dall'adapter, che li
    /// ricava dalla history di GOWA: sono solo chiamate in entrata, prese dalle
    /// chat piu' recenti che il server ha scansionato.
    ///
    /// Non implementa INotifyPropertyChanged: la collezione viene svuotata e
    /// riempita ad ogni scansione, non modificata campo per campo.
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

        /// <summary>Iniziali per l'avatar, come nell'elenco chat.</summary>
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

        /// <summary>Orario come nell'elenco chat (oggi -> HH:mm, ieri -> "Yesterday").</summary>
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
        /// Riga di dettaglio: esito, eventuale "video" e durata quando la
        /// conosciamo. Un esito che non riconosciamo resta una chiamata in
        /// entrata generica, invece di mostrare testo preso dal server.
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
