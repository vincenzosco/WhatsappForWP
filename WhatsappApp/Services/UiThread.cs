using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Finds the UI thread and runs code on it.
    ///
    /// In XAML, only the UI thread may change what is on screen. Work that finishes on a
    /// background thread (a file read, a network answer) therefore has to hand its result
    /// back to the UI thread before touching a control. A `Dispatcher` is the object that
    /// owns a thread and can queue work onto it.
    ///
    /// The background task host of this app has no window, and there is no dispatcher there:
    /// the methods return null and the callers skip the UI work.
    /// </summary>
    public static class UiThread
    {
        /// <summary>The UI dispatcher, or null when there is no window (the background host).</summary>
        public static Dispatcher Dispatcher()
        {
            try
            {
                // `Application.Current` is the running app; `RootVisual` is the page frame it
                // draws. No app or no frame means no screen, so there is nothing to dispatch to.
                Application app = Application.Current;
                if (app == null || app.RootVisual == null) return null;

                // `Deployment.Current.Dispatcher` is the thread the XAML is drawn on.
                Deployment deployment = Deployment.Current;
                return deployment == null ? null : deployment.Dispatcher;
            }
            catch (Exception)
            {
                // Asking for the app can throw during shutdown. That is not worth stopping
                // anything: a caller that gets null simply skips its UI update.
                return null;
            }
        }

        /// <summary>True when there is a window to draw on.</summary>
        public static bool HasWindow
        {
            get { return Dispatcher() != null; }
        }

        /// <summary>
        /// Runs `action` on the UI thread and returns a task that completes when it has run.
        ///
        /// If we are already on the UI thread it runs immediately; otherwise `BeginInvoke`
        /// queues it and the returned task completes later. A failure inside `action` is put
        /// into the returned task, so the caller can see it.
        /// </summary>
        public static Task RunAsync(Dispatcher dispatcher, Action action)
        {
            if (action == null) throw new ArgumentNullException("action");

            // No dispatcher: there is no UI, so there is nothing to do.
            if (dispatcher == null) return Task.FromResult(0);

            // `CheckAccess` answers "am I on this dispatcher's thread?".
            if (dispatcher.CheckAccess())
            {
                action();
                return Task.FromResult(0);
            }

            // `TaskCompletionSource` lets us create a task we complete by hand, later, when the
            // queued delegate runs.
            var completed = new TaskCompletionSource<bool>();
            try
            {
                dispatcher.BeginInvoke(delegate
                {
                    try
                    {
                        action();
                        completed.SetResult(true);
                    }
                    catch (Exception ex)
                    {
                        // Do not let the exception escape into the dispatcher: hand it to the
                        // task instead, where the awaiting caller can deal with it.
                        completed.SetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                // Even queuing can fail (the dispatcher may be shutting down).
                completed.SetException(ex);
            }

            return completed.Task;
        }
    }
}
