using System;
using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Awaits a task nobody can wait for, and keeps the fault.
    ///
    /// Why it exists: `StartRecordingAsync()` was called without await inside a
    /// `#pragma warning disable 4014`, and an exception thrown at that call site
    /// went into a Task nobody observed. The tap did nothing, no sentence
    /// appeared, and no DIAG line appeared either: a failure that was invisible
    /// in a log where everything else is visible. Every other fire-and-forget
    /// call in the app had the same hole.
    ///
    /// The rule that keeps it closed is machine-checked
    /// (tools/check-fire-and-forget.js): a call fired without await either goes
    /// through here, or the method it names answers for its own fault with a
    /// catch.
    /// </summary>
    public static class Guarded
    {
        /// <summary>
        /// Runs the work and logs its fault. It never throws: the caller had no
        /// way to observe it, which is the whole point.
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
        /// The Task overload observes the task it is given, not the call that
        /// produced it: passing `FooAsync()` has already run FooAsync, and a
        /// method that is not async - one that returns a Task without the async
        /// modifier, or a factory like SerialQueue.RunAsync - can throw while
        /// that argument is evaluated, before RunGuardedAsync is entered. This
        /// overload takes the call as a delegate, so even that throw is caught.
        /// An async method already captures its own faults in the returned Task,
        /// so the English way (pass the task) is right for it.
        /// </summary>
        public static Task RunGuardedAsync(string where, Func<Task> work)
        {
            return RunGuardedAsync(where, StartGuarded(where, work));
        }

        private static async Task StartGuarded(string where, Func<Task> work)
        {
            Task task;
            try
            {
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
