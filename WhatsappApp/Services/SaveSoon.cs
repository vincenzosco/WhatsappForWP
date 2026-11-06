using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace WhatsappApp.Services
{
    /// <summary>
    /// A write that happens a moment after the last thing that made it necessary.
    ///
    /// The only writer of the chat list and of a conversation was
    /// `DataService.SaveSnapshotAsync`, and it was called at the end of a history sync, by
    /// `BackgroundSync`, and by `App.OnSuspending`. On Windows Phone that last one is the
    /// promise nobody keeps - the process is terminated for memory, or closed from the task
    /// switcher, and the suspension never runs. Everything a message had added was gone while
    /// WhatsApp counted that message as delivered and never sent it again: the phone of
    /// showed a chat that stopped at the last time the app was closed
    /// gracefully, in the list and inside the conversation.
    ///
    /// So the write is armed by the message itself and fires a couple of seconds later. A
    /// burst costs one write and not one per message, and the window in which a kill can lose
    /// something goes from minutes to that quiet period.
    ///
    /// Two details are the whole implementation:
    ///
    /// 1. **Arming restarts the clock.** `Arm` stops the timer before starting it, so a
    /// second message pushes the write back instead of being written on the first one's
    /// deadline. A timer left running would be a beat, and a beat writes the list every
    /// two seconds for as long as the app is open.
    /// 2. **A write that is already running is not started twice.** It read the list before
    /// the newest message landed, so it is asked to go once more when it finishes; two
    /// snapshots at the same time would be two reads of one list and one of them stale.
    /// </summary>
    public sealed class SaveSoon
    {
        private readonly DispatcherTimer _timer;
        private readonly Func<Task> _work;
        private readonly string _name;

        /// <summary>Whether the write is in flight right now.</summary>
        private bool _running;

        /// <summary>Whether something armed the write while it was in flight.</summary>
        private bool _again;

        /// <summary>
        /// `quiet` is how long the list has to stay unchanged, `work` is the write, and `name`
        /// is what a failure is filed under in the log.
        /// </summary>
        public SaveSoon(TimeSpan quiet, Func<Task> work, string name)
        {
            if (work == null) throw new ArgumentNullException("work");

            _work = work;
            _name = string.IsNullOrEmpty(name) ? "SaveSoon" : name;

            _timer = new DispatcherTimer();
            _timer.Interval = quiet;
            _timer.Tick += OnTick;
        }

        /// <summary>Something changed: write it if nothing else changes for a moment.</summary>
        public void Arm()
        {
            if (_running)
            {
                // The write in flight took a copy of the list before this change: one more
                // pass when it ends, not a second write beside it.
                _again = true;
                return;
            }

            _timer.Stop();
            _timer.Start();
        }

        private void OnTick(object sender, object e)
        {
            _timer.Stop();
            _running = true;

            // Never awaited by the timer, so it is the guarded entry point that owns it: a
            // write that throws must not take the thread the connection is reading on. The
            // suppression is the same one the rest of the app writes this call with - there is
            // nobody above a timer to await it.
#pragma warning disable 4014
            Guarded.RunGuardedAsync(_name, RunAsync());
#pragma warning restore 4014
        }

        private async Task RunAsync()
        {
            try
            {
                await _work();
            }
            finally
            {
                _running = false;

                if (_again)
                {
                    _again = false;
                    _timer.Start();
                }
            }
        }
    }
}
