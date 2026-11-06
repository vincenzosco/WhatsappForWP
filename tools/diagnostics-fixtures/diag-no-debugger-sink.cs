// Extract of Diag.cs without the debugger sink: both sinks call Debug.WriteLine
// directly and there is no EmitToDebugger, so the guard has to say that the line no
// longer reaches the Output window of Visual Studio. The guard reads text, it does
// not compile: this fixture says what has to be there.

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

        private static void FrameSink(string text)
        {
            AppendLine(text);
            Debug.WriteLine("DIAG " + text);
            Flush();
        }

        private static void LineSink(string line)
        {
            AppendLine(line);
            Debug.WriteLine("DIAG " + line);
            Flush();
        }

        // The writer is the good one on purpose: this fixture is broken by rule F
        // alone, so a failing test points at the debugger sink and nothing else.
        private static async Task WriteLinesAsync(string added)
        {
            try
            {
                string text = await ReadAsync() + added;
                if (text.Length > MaxBytes) text = text.Substring(text.Length - MaxBytes);

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
            }
        }
