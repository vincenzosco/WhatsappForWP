using System;
using Windows.ApplicationModel.Background;
using WhatsappApp.Services;

namespace WhatsappApp.Tasks
{
    /// <summary>
    /// The class the manifest names (`windows.backgroundTasks`, entry point
    /// `WhatsappApp.Tasks.BackgroundSyncTask`): one wake of the periodic message check.
    ///
    /// It lives in this component and not in the app because on WP8.1 the system
    /// instantiates a background task in a process of its own, from a class an
    /// activatable-class declaration names - a class in the app's own assembly is
    /// never one it can create (see WhatsappApp.Tasks.csproj).
    ///
    /// It is deliberately thin. The work is `BackgroundSync.RunNow`, which is the
    /// same code the app uses, and this is only the contract the platform insists
    /// on: take the deferral first, so the process is not closed while the check is
    /// reading, and complete it in a `finally` whatever happened.
    /// </summary>
    public sealed class BackgroundSyncTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            BackgroundTaskDeferral deferral = null;

            try
            {
                if (taskInstance != null) deferral = taskInstance.GetDeferral();

                BackgroundSync.RunNow();
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSyncTask/Run", ex);
            }
            finally
            {
                // The lines above are only in memory until they are written, and this
                // process is about to end: the flush is forced for the same reason the
                // crash handler forces it.
                Diag.Flush(true);

                if (deferral != null) deferral.Complete();
            }
        }
    }
}
