// Estratto di CrashReport.cs: la coda della corsa che non e' finita viene letta
// all'avvio e scritta nel log di questa corsa, senza aspettare una connessione.

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
