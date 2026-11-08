using System;
using Microsoft.Phone.Scheduler;
using WhatsappApp.Services;

namespace WhatsappApp.Tasks
{
    /// <summary>
    /// The Silverlight scheduled-agent class named by the phone manifest. The system
    /// creates this agent in its own process for each periodic-task invocation.
    /// </summary>
    public sealed class BackgroundSyncTask : ScheduledTaskAgent
    {
        /// <summary>How long the last diagnostic write is given before completion.</summary>
        private const int FlushLimit = 2000;

        /// <summary>
        /// Runs one bounded check, persists diagnostics, and tells the scheduler the
        /// invocation is complete. An abandoned running marker is reported when the
        /// next invocation starts.
        /// </summary>
        protected override void OnInvoke(ScheduledTask task)
        {
            try
            {
                if (task != null)
                {
                    BackgroundSync.MarkCanceled(task.LastExitReason.ToString());
                }

                BackgroundSync.RunNow();
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSyncTask/OnInvoke", ex);
            }
            finally
            {
                try
                {
                    Diag.FlushAndWait(FlushLimit);
                }
                catch (Exception ex)
                {
                    Diag.Failed("BackgroundSyncTask/Flush", ex);
                }
                finally
                {
                    NotifyComplete();
                }
            }
        }

        /// <summary>
        /// The system cancels a run that spent its budget, from a thread of its own,
        /// while `OnInvoke` is still blocked in a wait. The line this writes is the
        /// difference between a canceled check and a finished one in the log.
        /// </summary>
        protected override void OnCancel()
        {
            Diag.Ok("background: canceled by the system");
            try
            {
                Diag.FlushAndWait(FlushLimit);
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSyncTask/OnCancel", ex);
            }
        }
    }
}
