using System;
using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Runs async jobs one at a time, in the order they were handed in.
    ///
    /// Many jobs in this app are asynchronous: saving a file, writing a cache, uploading
    /// something. Two of them touching the same file at once would interleave and corrupt it.
    /// This class keeps them in a single line: the next job starts only after the previous one
    /// has finished.
    ///
    /// It is not a thread: it is a chain of tasks. When nothing is queued it costs nothing, and
    /// a job that fails does not block the ones behind it - the failure is given back to the
    /// caller who queued it, and the line moves on.
    /// </summary>
    public sealed class SerialQueue
    {
        // `_gate` guards `_tail` so two threads cannot append to the chain at the same time.
        private readonly object _gate = new object();

        // `_tail` is the task that represents "everything queued so far". A new job is chained
        // after it. It starts out already finished, so the first job runs immediately.
        private Task _tail = Done();

        private static Task Done()
        {
            // A task that is already complete: `SetResult` finishes it. It is the empty
            // starting point of the chain.
            var source = new TaskCompletionSource<bool>();
            source.SetResult(true);
            return source.Task;
        }

        /// <summary>
        /// Adds a job and returns a task that completes when that job completes.
        ///
        /// The caller can await the returned task to know the result, or ignore it. Either way
        /// the queue keeps its order.
        /// </summary>
        public Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            if (work == null) throw new ArgumentNullException("work");

            Task<T> next;
            lock (_gate)
            {
                // Chain the job after `_tail`. `ContinueWith` means "when the previous task
                // has finished, run this delegate". `ExecuteSynchronously` lets it run on the
                // same thread when the previous task was already done, which is what makes an
                // empty queue feel like a direct call. `Unwrap` turns Task&lt;Task&lt;T&gt;&gt;
                // (the delegate returns a task) into a flat Task&lt;T&gt;.
                next = _tail
                    .ContinueWith(delegate { return work(); },
                        TaskContinuationOptions.ExecuteSynchronously)
                    .Unwrap();

                // The chain continues even when this job fails: the exception stays inside
                // `next`, where the caller can see it, and `_tail` only waits for the job to
                // finish. Reading `.Exception` here is also what marks the failure as seen, so
                // it is not reported as an unobserved task exception.
                _tail = next.ContinueWith(
                    delegate(Task<T> finished) { AggregateException ignored = finished.Exception; },
                    TaskContinuationOptions.ExecuteSynchronously);
            }
            return next;
        }

        /// <summary>The same queue, for jobs that return nothing.</summary>
        public Task RunAsync(Func<Task> work)
        {
            if (work == null) throw new ArgumentNullException("work");

            // Wrap the void job into one that returns `true`, so there is only one queue
            // implementation to understand and to fix.
            return RunAsync<bool>(async delegate
            {
                await work();
                return true;
            });
        }
    }
}
