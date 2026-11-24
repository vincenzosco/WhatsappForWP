using System;
using System.Windows.Threading;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Shell;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Keeps one SystemTray indicator attached to whichever page is visible.
    /// </summary>
    public static class LoadingIndicator
    {
        private static readonly object Gate = new object();
        private static PhoneApplicationFrame _frame;
        private static Dispatcher _dispatcher;
        private static ProgressIndicator _indicator;
        private static bool _started;

        public static void Start(PhoneApplicationFrame frame)
        {
            if (frame == null) return;

            lock (Gate)
            {
                if (_started) return;
                _frame = frame;
                _dispatcher = UiThread.Dispatcher();
                _indicator = new ProgressIndicator
                {
                    IsIndeterminate = true,
                    Text = Loc.Get("LoadingIndicator_Loading", "Loading"),
                    IsVisible = false
                };
                _frame.Navigated += OnNavigated;
                LoadingActivity.Changed += OnActivityChanged;
                _started = true;
            }

            Refresh();
        }

        public static void Refresh()
        {
            Dispatcher dispatcher;
            lock (Gate) dispatcher = _dispatcher;
            if (dispatcher == null)
            {
                dispatcher = UiThread.Dispatcher();
                if (dispatcher == null) return;
                lock (Gate)
                {
                    if (_dispatcher == null) _dispatcher = dispatcher;
                    dispatcher = _dispatcher;
                }
            }

            if (dispatcher.CheckAccess())
            {
                AttachToCurrentPage();
                return;
            }

            try
            {
                dispatcher.BeginInvoke(AttachToCurrentPage);
            }
            catch (InvalidOperationException)
            {
                // The foreground dispatcher can stop accepting work during shutdown.
            }
        }

        private static void OnNavigated(object sender, System.Windows.Navigation.NavigationEventArgs e)
        {
            Refresh();
        }

        private static void OnActivityChanged(object sender, EventArgs e)
        {
            Refresh();
        }

        private static void AttachToCurrentPage()
        {
            PhoneApplicationFrame frame;
            ProgressIndicator indicator;
            lock (Gate)
            {
                frame = _frame;
                indicator = _indicator;
            }

            if (frame == null || indicator == null) return;

            PhoneApplicationPage page = frame.Content as PhoneApplicationPage;
            if (page == null) return;

            SystemTray.SetProgressIndicator(page, indicator);
            indicator.IsVisible = LoadingActivity.IsActive;
        }
    }
}
