// Estratto di App.xaml.cs: il gestore dell eccezione non gestita scrive il log
// su disco prima che il processo possa morire.

        private void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            Diag.Failed("App/unhandled", e.Exception);
            Diag.Flush(true);
        }

        private void OnUnobservedTask(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            Diag.Failed("App/unobserved-task", e.Exception);
            Diag.Flush(true);
            e.SetObserved();
        }
