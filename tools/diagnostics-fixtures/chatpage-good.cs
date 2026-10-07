// Estratto di ChatPage.xaml.cs: l attesa della storia non e' piu' corta della
// lettura dell adapter (misurata ~15 s), o il bind arriva prima del burst.

        private const int HistoryWaitMaxMilliseconds = 20000;
