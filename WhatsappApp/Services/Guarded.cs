using System;
using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Runs async work that nobody waits for, and writes its failure to the log.
    ///
    /// Calling an async method without `await` starts a task and drops it. If that task fails,
    /// the exception sits inside a task that nobody reads, so the app shows nothing, logs
    /// nothing, and the tap that started it looks like it did not work. These methods are the
    /// answer: the work still runs in the background, but every failure is observed and
    /// written to the log (`Diag`).
    ///
    /// The rule is checked by `tools/check-fire-and-forget.js`.
    /// </summary>
    public static class Guarded
    {
        /// <summary>
        /// Starts work that has no awaiting caller. The delegate is called inside the try, so
        /// even a throw before the first await is caught and logged.
        /// </summary>
        public static async void RunGuarded(string where, Func<Task> work)
        {
            try
            {
                if (work == null) return;
                await work();
            }
            catch (Exception ex)
            {
                Diag.Failed(where, ex);
            }
        }

        /// <summary>
        /// Awaits an already-started task and logs its failure instead of throwing.
        ///
        /// It never throws: the caller had no way to observe the failure, which is exactly the
        /// case this method exists for.
        /// </summary>
        public static async Task RunGuardedAsync(string where, Task work)
        {
            try
            {
                await work;
            }
            catch (Exception ex)
            {
                Diag.Failed(where, ex);
            }
        }

        /// <summary>
        /// The same, for work that has not been started yet.
        ///
        /// The overload above receives a task, and building that argument already runs the
        /// method: a method that can throw before its first await (a factory such as
        /// `SerialQueue.RunAsync`, or an async method written without the `async` keyword)
        /// would throw outside the try. This overload takes the call as a delegate and starts
        /// it inside `StartGuarded`, so that throw is caught too.
        /// </summary>
        public static Task RunGuardedAsync(string where, Func<Task> work)
        {
            return RunGuardedAsync(where, StartGuarded(where, work));
        }

        public static async Task StartGuarded(string where, Func<Task> work)
        {
            Task task;
            try
            {
                // The call may throw right here, before any task exists.
                task = work();
            }
            catch (Exception ex)
            {
                Diag.Failed(where, ex);
                return;
            }

            await RunGuardedAsync(where, task);
        }
    }
}
