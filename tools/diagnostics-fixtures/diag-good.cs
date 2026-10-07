// Estratto di Diag.cs: il file sink, i marcatori di corsa e il marcatore di vita.
// Il guard legge del testo, non compila: questo fixture dice cosa deve esserci.

        private const string FileName = "diag.log";
        private const string MarkerName = "diag-run.marker";
        private const int MaxBytes = 65536;

        private const string RunStarted = "=== run started ";
        private const string RunEnded = "=== run ended ";

        private static readonly SerialQueue Writes = new SerialQueue();

        private static void BeginRun()
        {
            AppendLine(RunStarted + Stamp() + " ===");
        }

        private static void EndRun()
        {
            AppendLine(RunEnded + Stamp() + " ===");
        }

        private static async Task DeleteMarkerAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder
                    .GetFileAsync(MarkerName);
                await file.DeleteAsync();
            }
            catch (Exception)
            {
            }
        }
