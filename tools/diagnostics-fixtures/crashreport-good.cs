// Estratto di CrashReport.cs: la coda della corsa che non e' finita parte verso
// l adapter come frame diag, appena la connessione esiste.

        private static void OnConnectionEstablished(object sender, EventArgs e)
        {
            if (_sent) return;

            string tail = Diag.PendingCrashTail;
            if (string.IsNullOrEmpty(tail)) return;

            _sent = true;
            Diag.Ok("crash report sent");
#pragma warning disable 4014
            Guarded.RunGuardedAsync("CrashReport/send",
                CommunicationService.Instance.SendControlAsync("diag",
                    "previous run did not end\r\n" + tail));
#pragma warning restore 4014
        }
