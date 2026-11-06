// Extract of CrashReport.cs: the tail of the run that did not finish is read at
// start-up and written into this run's log, without waiting for a connection.

        public static void Start()
        {
            if (_started) return;
            _started = true;

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
