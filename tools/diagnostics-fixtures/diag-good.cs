// Extract of Diag.cs: the file sink, the run markers and the liveness marker. The
// guard reads text, it does not compile: this fixture says what has to be there.

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

        // One place pushes a line to the debugger, so the app's own log is
        // readable in the Visual Studio 2013 Output window while the app runs on
        // the device. The two sinks call it: the frame sink and the line sink.
        private static void EmitToDebugger(string text)
        {
            Debug.WriteLine("DIAG " + text);
        }

        private static void FrameSink(string text)
        {
            AppendLine(text);
            EmitToDebugger(text);
            Flush();
        }

        private static void LineSink(string line)
        {
            AppendLine(line);
            EmitToDebugger(line);
            Flush();
        }

        // The file is fetched when it is there and created only when it is not:
        // a create truncates there and then and the write is a second call.
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
