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

        // How many times each frame line has been written. A frame log keeps the
        // order and the repetition, but a line that repeats forever must not
        // push the failures out of a 200-line buffer: the count is kept beside
        // the line instead of one copy per repeat.
        private static readonly Dictionary<string, int> FrameRepeats =
            new Dictionary<string, int>();

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

        /// <summary>
        /// One control frame, in the order it was sent or received.
        ///
        /// Why it does not go through Write: Write keeps only the first copy of a
        /// line, and a frame log is read for its repetition - the second `chats`
        /// request is a fact the log has to show. The repetition is counted on the
        /// line instead of copied, so the buffer cannot fill with one frame and
        /// hide the failures it exists to keep.
        /// </summary>
        public static void LogFrame(string direction, string command, string detail)
        {
            // A media transfer is one `media.chunk` per 700000 characters: the
            // same frame a hundred times, and nothing in it that the begin and
            // the end do not already say.
            if (command == "media.chunk") return;

            var line = new StringBuilder();
            line.Append(direction).Append(' ').Append(command);
            if (!string.IsNullOrEmpty(detail))
            {
                line.Append("  ").Append(Clip(detail));
            }
            string text = line.ToString();

            lock (Gate)
            {
                int count;
                if (FrameRepeats.TryGetValue(text, out count))
                {
                    count++;
                    FrameRepeats[text] = count;

                    // The line already in the buffer is rewritten in place, so
                    // the count is visible and the buffer does not grow; if other
                    // lines have since evicted it, it is added back once.
                    string updated = text + " (x" + count + ")";
                    int at = FindFrame(text);
                    if (at >= 0) History[at] = updated;
                    else AppendLine(updated);
                }
                else
                {
                    FrameRepeats[text] = 1;
                    AppendLine(text);
                }
            }
            Debug.WriteLine("DIAG " + text);
        }

        /// <summary>Where a frame line sits in the buffer, or -1 if it was evicted.</summary>
        private static int FindFrame(string text)
        {
            for (int i = 0; i < History.Count; i++)
            {
                if (History[i] == text || History[i].StartsWith(text + " (x", StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>Adds a line, dropping the oldest if the buffer is full.</summary>
        private static void AppendLine(string line)
        {
            if (History.Count >= HistoryLimit) History.RemoveAt(0);
            History.Add(line);
        }

        /// <summary>
        /// A payload is not a log line.
        ///
        /// A login QR and a media blob both call SetText, so the payload can be
        /// thousands of characters of base64 that say nothing the first forty do
        /// not. The truncation keeps the buffer readable.
        /// </summary>
        private static string Clip(string value)
        {
            const int MaxDetail = 48;
            if (value.Length <= MaxDetail) return value;
            return value.Substring(0, MaxDetail) + "...";
        }

        /// <summary>Forgets everything written so far: the screen has a Clear.</summary>
        public static void Clear()
        {
            lock (Gate)
            {
                History.Clear();
                Seen.Clear();
                FrameRepeats.Clear();
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
                AppendLine(line);
            }
            Debug.WriteLine("DIAG " + line);
        }
    }
}
