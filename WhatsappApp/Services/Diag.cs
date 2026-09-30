using System;
using System.Collections.Generic;
using System.Diagnostics;

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

        private static void Write(string line)
        {
            lock (Gate)
            {
                if (Seen.Contains(line)) return;
                Seen.Add(line);
            }
            Debug.WriteLine("DIAG " + line);
        }
    }
}
