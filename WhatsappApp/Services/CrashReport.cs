using System;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The crash of the previous run, put in front of the next one.
    ///
    /// Why it exists: the phone has no debugger attached, and a run that died left
    /// its tail in `Diag.PendingCrashTail` with nothing to read it. A new run's log
    /// therefore began at its own first line, and the crash that explains why the
    /// previous one never connected was only visible as an absence.
    ///
    /// This is what carries it across: at start-up the tail is written into the run
    /// that is now beginning, line by line, so it appears on the diagnostics page
    /// and in `diag.log` - which is the file `ISETool` pulls off the phone (see
    /// `test-the-app`). It used to be sent to the adapter as a `diag` frame; there
    /// is no adapter any more, and the phone's own log is where a crash is read.
    ///
    /// One report per run, and only for a run that did not end on purpose: `EndRun`
    /// is what clears the tail, and a crash never reaches it.
    /// </summary>
    public static class CrashReport
    {
        private static bool _started;

        /// <summary>
        /// Called once per process from App.StartServicesOnce, next to the other
        /// services. A run that ended properly has nothing to report and leaves this
        /// silent, so the log only carries the runs that really died.
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            try
            {
                // The tail is read back from a file written with CRLF: split on an
                // LF leaves a carriage return at the end of every line, and this text
                // is going into a log that is read by hand.
                string tail = Diag.PendingCrashTail.Replace("\r\n", "\n");
                if (string.IsNullOrEmpty(tail)) return;

                Diag.Ok("the previous run did not end");
                string[] lines = tail.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Length == 0) continue;
                    Diag.Ok("  " + lines[i]);
                }
                Diag.Flush(true);
            }
            catch (Exception ex)
            {
                Diag.Failed("CrashReport.Start", ex);
            }
        }
    }
}
