// Estratto di App.xaml.cs: i gestori delle eccezioni non gestite scrivono il log
// su disco prima che il processo possa morire. Il guard legge del testo, non
// compila: questo fixture dice cosa deve esserci.

        private void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            Diag.Failed("App/unhandled", e.Exception);
            Diag.Stack(e.Exception);
            Diag.Flush(true);
        }

        private void OnUnobservedTask(object sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
        {
            Diag.Failed("App/unobserved-task", e.Exception);
            Diag.Stack(e.Exception);
            Diag.Flush(true);
            e.SetObserved();
        }
