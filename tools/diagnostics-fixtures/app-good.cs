// Extract of App.xaml.cs: the handlers for unhandled exceptions write the log to
// disk before the process can die. The guard reads text, it does not compile: this
// fixture says what has to be there.

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
