// Extract of ChatPage.xaml.cs: waiting for the history is not shorter than reading
// the adapter (~15 s measured), or the bind lands before the burst.

        private const int HistoryWaitMaxMilliseconds = 20000;
