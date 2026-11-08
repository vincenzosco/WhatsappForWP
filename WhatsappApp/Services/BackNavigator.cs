namespace WhatsappApp.Services
{
    /// <summary>
    /// The Windows Phone Silverlight frame owns hardware Back navigation. The app starts this
    /// service at the same point as its other one-time services so callers keep one startup path.
    /// </summary>
    public static class BackNavigator
    {
        private static bool _started;

        public static void Start()
        {
            if (_started) return;
            _started = true;
            Diag.Ok("hardware back navigation is handled by the Silverlight frame");
        }
    }
}
