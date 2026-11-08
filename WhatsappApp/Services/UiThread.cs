using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Resolves the dispatcher of the Silverlight UI and returns no dispatcher in the scheduled
    /// task host, which has no root visual.
    /// </summary>
    public static class UiThread
    {
        public static Dispatcher Dispatcher()
        {
            try
            {
                Application app = Application.Current;
                if (app == null || app.RootVisual == null) return null;

                Deployment deployment = Deployment.Current;
                return deployment == null ? null : deployment.Dispatcher;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool HasWindow
        {
            get { return Dispatcher() != null; }
        }

        public static Task RunAsync(Dispatcher dispatcher, Action action)
        {
            if (action == null) throw new ArgumentNullException("action");
            if (dispatcher == null) return Task.FromResult(0);

            if (dispatcher.CheckAccess())
            {
                action();
                return Task.FromResult(0);
            }

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
                        completed.SetException(ex);
                    }
                });
            }
            catch (Exception ex)
            {
                completed.SetException(ex);
            }

            return completed.Task;
        }
    }
}
