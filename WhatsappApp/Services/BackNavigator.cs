namespace WhatsappApp.Services
{
    /// <summary>
    /// A placeholder for the phone's hardware Back button.
    ///
    /// On Windows Phone Silverlight the page frame (`PhoneApplicationFrame`) already handles
    /// the Back key by itself: it goes to the previous page. This class does not navigate -
    /// it exists so the app's startup code can call one `Start` method per service and keep a
    /// single list of what has been started.
    ///
    /// It also writes one line to the log, which is how a run on the phone says that the
    /// frame, not this app, is doing the work.
    /// </summary>
    public static class BackNavigator
    {
        // A static field survives between calls: `Start` is safe to call twice.
        private static bool _started;

        public static void Start()
        {
            if (_started) return;
            _started = true;
            Diag.Ok("hardware back navigation is handled by the Silverlight frame");
        }
    }
}
