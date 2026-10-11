using System;
using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// One thing at a time, in the order they arrive.
    ///
    /// Why it exists: half of this app's work is asynchronous, and much of it is
    /// started without anyone awaiting it - a media write, a cache save, a handler
    /// that was fired and not awaited. Two of those touching one resource
    /// interleave: two writes of the same file land out of order (that bug was
    /// fixed once in `ChatPreferences`), and two `StoreAsync` on one `DataWriter`
    /// put one payload's bytes in front of the other's.
    ///
    /// Work enters here and runs to completion, in line: the queue is a chain of
    /// Tasks, not a thread, so it costs nothing when empty. A piece that fails
    /// does not stop the queue: the caller sees the failure, and the next piece
    /// starts anyway.
    /// </summary>
    public sealed class SerialQueue
    {
        private readonly object _gate = new object();
        private Task _tail = Done();

        private static Task Done()
        {
            var source = new TaskCompletionSource<bool>();
            source.SetResult(true);
            return source.Task;
        }

        /// <summary>
        /// Queues the work and returns its outcome. The first piece runs
        /// immediately if the queue is empty (ExecuteSynchronously), as a direct
        /// call would.
        /// </summary>
        public Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            if (work == null) throw new ArgumentNullException("work");

            Task<T> next;
            lock (_gate)
            {
                next = _tail
                    .ContinueWith(delegate { return work(); },
                        TaskContinuationOptions.ExecuteSynchronously)
                    .Unwrap();

                // The queue continues even if this piece fails: the exception
                // stays in the returned Task, and is observed there.
                _tail = next.ContinueWith(
                    delegate(Task<T> finished) { AggregateException ignored = finished.Exception; },
                    TaskContinuationOptions.ExecuteSynchronously);
            }
            return next;
        }

        /// <summary>The same queue, for work that returns nothing.</summary>
        public Task RunAsync(Func<Task> work)
        {
            if (work == null) throw new ArgumentNullException("work");

            return RunAsync<bool>(async delegate
            {
                await work();
                return true;
            });
        }
    }
}
