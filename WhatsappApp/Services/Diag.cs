using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

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
    ///
    /// Why there is also a file: the list below is in memory, so the process that
    /// dies takes it with it, and every crash investigated in this repo arrived as
    /// a log that stopped at the assembly list with no DIAG line at all. The lines
    /// are therefore appended to `diag.log` as the run goes, through the same
    /// SerialQueue the other files use, cut to the last MaxBytes so the tail is
    /// always the newest lines. A marker file says the run is alive; a marker file
    /// still there at the next startup is what a crash looks like, and the tail the
    /// previous run left is kept as PendingCrashTail for the report. The marker is
    /// a file and not a line in the log because OnSuspending is the normal end of a
    /// run on this platform: "the log has no end marker" is the normal case too,
    /// and a crash after a resume would be missed by anything that read only the
    /// log.
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

        // ── the file the run leaves behind ───────────────────────────────────

        /// <summary>The log file, and how large it may get before it is cut to its tail.</summary>
        private const string FileName = "diag.log";
        private const int MaxBytes = 65536;

        /// <summary>
        /// The marker a live run leaves: while it exists, the run has not ended on
        /// purpose. A different file from the log, because a suspended run is a
        /// normal run (see the class comment).
        /// </summary>
        private const string MarkerName = "diag-run.marker";

        /// <summary>How much of the previous run travels to the adapter.</summary>
        private const int CrashTailChars = 4000;

        /// <summary>What the run writes when it starts, and when it is closed on purpose.</summary>
        private const string RunStarted = "=== run started ";
        private const string RunEnded = "=== run ended ";

        /// <summary>The file writes, one at a time and in order (the ChatPreferences pattern).</summary>
        private static readonly SerialQueue Writes = new SerialQueue();

        /// <summary>How many lines of the history are already in the file.</summary>
        private static int _written;

        /// <summary>When the file was last written: the sink is not one open per line.</summary>
        private static long _lastFlushTicks;

        private static string _pendingTail = "";

        /// <summary>
        /// What the previous run left when it did not end on purpose, empty when it
        /// did. It is what the next connection sends to the adapter, so the crash is
        /// read in the container log instead of on a screen.
        ///
        /// It survives a run that could not send it: only EndRun clears it, and
        /// EndRun is the deliberate close a crash never reaches.
        /// </summary>
        public static string PendingCrashTail
        {
            get { lock (Gate) { return _pendingTail; } }
        }

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
            EmitToDebugger(text);
            Flush();
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

                // The file keeps what it has - clearing the screen is not deleting
                // the log - but the counter follows the buffer, or the first lines
                // of the new history would never be written: the sink would think
                // they were already there.
                _written = 0;
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
            EmitToDebugger(line);
            Flush();
        }

        /// <summary>
        /// One place that pushes a line to the debugger, so the app's own log is
        /// readable in the Visual Studio 2013 Output window (Debug → Windows →
        /// Output) while the app runs on the device under F5, without exporting the
        /// phone's file first. `Debug.WriteLine` is the channel a managed debugger
        /// listens on. `Debugger.Log` would reach it even for a line written before
        /// the debugger attached, but Windows Phone 8.1 has no such member: the ARM
        /// build answered `error CS0117: 'System.Diagnostics.Debugger' does not
        /// contain a definition for 'Log'`, so this stays with `Debug.WriteLine`
        /// alone. Nothing appears with no debugger attached, and `diag.log` plus the
        /// Diagnostics page remain the channels on the phone.
        /// </summary>
        private static void EmitToDebugger(string text)
        {
            Debug.WriteLine("DIAG " + text);
        }

        // ── the file sink ────────────────────────────────────────────────────

        /// <summary>Appends what is new, at most once a second.</summary>
        public static void Flush()
        {
            Flush(false);
        }

        /// <summary>
        /// Appends the lines that are not in the file yet and returns. Forced when
        /// the process may be about to die, where waiting for the second is waiting
        /// for a write that never happens.
        ///
        /// The lines are marked as written before the write is queued, so a second
        /// call inside the same second cannot queue the same lines twice. A write
        /// that fails therefore loses those lines from the file - they stay in the
        /// in-memory history - which is the trade the interval buys: without it, a
        /// frame log would open the file once per frame.
        /// </summary>
        public static void Flush(bool force)
        {
            string text;
            lock (Gate)
            {
                long now = DateTime.Now.Ticks;
                if (!force && now - _lastFlushTicks < TimeSpan.TicksPerSecond) return;
                if (_written >= History.Count) return;

                _lastFlushTicks = now;

                var builder = new StringBuilder();
                for (int i = _written; i < History.Count; i++)
                {
                    builder.Append(History[i]);
                    builder.Append("\r\n");
                }
                _written = History.Count;
                text = builder.ToString();
            }

            string adding = text;
            Guarded.RunGuardedAsync("Diag/Flush",
                delegate { return Writes.RunAsync(delegate { return WriteLinesAsync(adding); }); });
        }

        /// <summary>
        /// Reads the whole file, adds the lines and writes it back, cut to its last
        /// MaxBytes. The cut is on the tail on purpose: a phone left in a crash loop
        /// must still answer with the newest lines.
        /// </summary>
        private static async Task WriteLinesAsync(string added)
        {
            try
            {
                string text = await ReadAsync() + added;
                if (text.Length > MaxBytes) text = text.Substring(text.Length - MaxBytes);

                // The file is fetched when it is there, and created only when it is
                // not: a create truncates the file there and then, while the write is
                // a second call. An app suspended between the two left a `diag.log`
                // of 0 bytes with the run marker still in place, so the whole run was
                // lost, crash tail included (seen on the phone on 2026-10-07).
                // WriteTextAsync truncates and writes in one operation on a file that
                // already exists, so there is no window in which to lose it, and a
                // file that is not there yet has nothing to lose.
                // No `await` in the catch: C# 5 answers CS1985, so a failed fetch
                // leaves a null that the create below fills in.
                StorageFile file = null;
                try
                {
                    file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                }
                catch (Exception)
                {
                    file = null;
                }

                if (file == null)
                {
                    file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        FileName, CreationCollisionOption.ReplaceExisting);
                }

                await FileIO.WriteTextAsync(file, text);
            }
            catch (Exception)
            {
                // A log that cannot be written is not reported through the log: the
                // report takes the same path that just failed, and a phone with no
                // storage left must not lose the app over its own diagnostics.
            }
        }

        /// <summary>The file, or an empty string when there is none. Never throws.</summary>
        private static async Task<string> ReadAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                return await FileIO.ReadTextAsync(file);
            }
            catch (FileNotFoundException)
            {
                return "";
            }
            catch (Exception ex)
            {
                Diag.Failed("Diag/read", ex);
                return "";
            }
        }

        /// <summary>
        /// Begins a run: it reads what the previous one left, puts the tail in the
        /// history, writes the start marker and leaves the marker file. Answers true
        /// when the previous run did not end on purpose.
        ///
        /// One method and not two, because the marker has to be read before it is
        /// written: a run that wrote it first would find its own marker and report
        /// itself as a crash.
        /// </summary>
        public static async Task<bool> StartRunAsync()
        {
            bool crashed = false;

            try
            {
                if (await MarkerExistsAsync())
                {
                    crashed = true;
                    string tail = Tail(await ReadAsync(), CrashTailChars);

                    lock (Gate)
                    {
                        _pendingTail = tail;
                        AppendLine("previous run did not end: " + tail.Length + " characters");
                        string[] lines = SplitLines(tail);
                        for (int i = 0; i < lines.Length; i++)
                        {
                            if (lines[i].Length > 0) AppendLine(lines[i]);
                        }

                        // It is already in the file: writing it back would only grow it.
                        _written = History.Count;
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("Diag/StartRun", ex);
            }

            lock (Gate)
            {
                AppendLine(RunStarted + Stamp() + " ===");
            }
            Flush(true);
            MarkAlive();
            return crashed;
        }

        /// <summary>
        /// Says the run is alive. Called at startup and on every resume: the marker
        /// was removed by the suspension that preceded it, and a run that comes back
        /// and later dies must still be reported.
        /// </summary>
        public static void MarkAlive()
        {
            Guarded.RunGuardedAsync("Diag/alive",
                delegate { return Writes.RunAsync(delegate { return WriteMarkerAsync(); }); });
        }

        /// <summary>
        /// The run ends on purpose: the closing line is written and the marker is
        /// removed, so the next start does not read this run as a crash. Called from
        /// the suspension and the close, which is every way a run is supposed to end.
        /// </summary>
        public static void EndRun()
        {
            lock (Gate)
            {
                AppendLine(RunEnded + Stamp() + " ===");
                _pendingTail = "";
            }
            Flush(true);

            Guarded.RunGuardedAsync("Diag/end-run",
                delegate { return Writes.RunAsync(delegate { return DeleteMarkerAsync(); }); });
        }

        private static async Task<bool> MarkerExistsAsync()
        {
            try
            {
                await ApplicationData.Current.LocalFolder.GetFileAsync(MarkerName);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        private static async Task WriteMarkerAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    MarkerName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, Stamp());
            }
            catch (Exception ex)
            {
                Diag.Failed("Diag/alive-write", ex);
            }
        }

        private static async Task DeleteMarkerAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(MarkerName);
                await file.DeleteAsync();
            }
            catch (FileNotFoundException)
            {
                // Nothing to remove: the marker is gone, which is what was wanted.
            }
            catch (Exception ex)
            {
                Diag.Failed("Diag/alive-delete", ex);
            }
        }

        /// <summary>The last <paramref name="chars"/> characters, the crash report's shape.</summary>
        private static string Tail(string text, int chars)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= chars) return text;
            return text.Substring(text.Length - chars);
        }

        private static string[] SplitLines(string text)
        {
            return text.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.None);
        }

        /// <summary>The sortable local time the run markers carry.</summary>
        private static string Stamp()
        {
            return DateTime.Now.ToString("s");
        }
    }
}
