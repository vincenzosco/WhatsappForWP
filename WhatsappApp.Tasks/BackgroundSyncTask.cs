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
        /// <summary>How long the last write is given before the process ends anyway.</summary>
        private const int FlushLimit = 2000;

        public void Run(IBackgroundTaskInstance taskInstance)
        {
            BackgroundTaskDeferral deferral = null;

            try
            {
                if (taskInstance != null)
                {
                    deferral = taskInstance.GetDeferral();

                    // The system takes a run down when it has spent its budget, and it does it
                    // from a thread of its own: `RunNow` is blocked in a wait at that moment and
                    // its thread is not coming back, so this handler is the only place that can
                    // say a run was canceled rather than finished. Without it the two looked
                    // identical in `diag.log` - the 21 wakes of 23-24 October 2026, not one of
                    // them with a closing line, and no way to tell which had reached the save.
                    taskInstance.Canceled += OnCanceled;
                }

                BackgroundSync.RunNow();
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSyncTask/Run", ex);
            }
            finally
            {
                // Waited for, not queued. The host takes the process down the moment the
                // deferral is complete, and `Flush(true)` only queues its write: that is why
                // every one of those 21 wakes ended mid-sentence, with the line that says
                // what it did still in memory. Bounded, because a write that never comes back
                // must not keep a task open either.
                Diag.FlushAndWait(FlushLimit);

                if (deferral != null) deferral.Complete();
            }
        }

        /// <summary>
        /// The system is taking the run down. Says so, and puts the lines on the phone.
        /// </summary>
        private static void OnCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            Diag.OkNow("background: the system canceled the check after "
                + BackgroundSync.ElapsedMilliseconds + " ms (" + reason + ")");

            Diag.FlushAndWait(FlushLimit);
        }
    }
}
