using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace WhatsappApp.Services
{
    /// <summary>
    /// One place for the failures the app decides to survive.
    ///
    /// Why it exists: every silent `catch` hides a real failure, and in the
    /// debugger log a WinRT exception arrives as
    ///
    ///     A first chance exception of type 'System.Exception' occurred ...
    ///     WinRT information: The operation identifier is not valid.
    ///
    /// without saying *which* call threw it. Here the failure is recorded with
    /// the exact site and the HRESULT, which is the only thing that tells a member
    /// missing on the platform (E_NOTIMPL) from a call made on the wrong thread
    /// (E_ILLEGAL_METHOD_CALL) or from an operation no longer valid (0x800710DD).
    ///
    /// Every line appears once: without the deduplication, a loop that fails every
    /// two seconds (the discovery beacon) would fill the log and hide everything
    /// else. Debug.WriteLine is compiled out in release builds, so in production
    /// this code writes nothing and costs nothing.
    /// </summary>
    public static class Diag
    {
        private static readonly List<string> Seen = new List<string>();
        private static readonly object Gate = new object();

        /// <summary>
        /// How many lines the history keeps. A phone run has no debugger: this
        /// list is the only copy of what the app did, so it holds the most recent
        /// lines - and the last line before a crash survives.
        /// </summary>
        private const int HistoryLimit = 200;

        private static readonly List<string> History = new List<string>();

        /// <summary>To call inside a catch, for a failure we carry on from.</summary>
        public static void Failed(string where, Exception ex)
        {
            Write(where + ": " + Describe(ex));
        }

        /// <summary>
        /// A capability that is present instead. The startup probe records it
        /// once per run: the line says "this phone can do it".
        /// </summary>
        public static void Ok(string what)
        {
            Write("ok: " + what);
        }

        /// <summary>
        /// Type, HRESULT in hexadecimal and message: without the code two
        /// different failures stay indistinguishable in the log.
        /// </summary>
        public static string Describe(Exception ex)
        {
            if (ex == null) return "(no exception)";
            return ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + (ex.Message ?? "");
        }

        /// <summary>
        /// The lines written so far, oldest first. A copy: the caller may keep it.
        /// This is what the diagnostics page shows, because Debug.WriteLine needs
        /// a PC with a debugger and the run being diagnosed is on the phone.
        /// </summary>
        public static IList<string> HistoryLines()
        {
            lock (Gate)
            {
                return new List<string>(History);
            }
        }

        /// <summary>The whole history as one string, for a TextBlock.</summary>
        public static string HistoryText()
        {
            lock (Gate)
            {
                var text = new StringBuilder();
                for (int i = 0; i < History.Count; i++)
                {
                    text.Append(History[i]);
                    text.Append('\n');
                }
                return text.ToString();
            }
        }

        /// <summary>Forgets everything written so far: the screen has a Clear.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                History.Clear();
                Seen.Clear();
            }
        }

        private static void Write(string line)
        {
            lock (Gate)
            {
                if (Seen.Contains(line)) return;
                Seen.Add(line);

                // The cap keeps the history the most recent lines: the buffer is
                // what a report carries, and a run that fails every two seconds
                // must not push everything else out of it.
                if (History.Count >= HistoryLimit) History.RemoveAt(0);
                History.Add(line);
            }
            Debug.WriteLine("DIAG " + line);
        }
    }
}
