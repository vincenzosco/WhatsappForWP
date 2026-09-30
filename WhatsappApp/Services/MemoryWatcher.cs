using System;
using Windows.System;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The memory budget of this phone, and what the app does when it gets close
    /// to it.
    ///
    /// Why it exists: on WP8.1 the memory is not declared anywhere - the manifest
    /// schema does not have it - and it is not visible. The system warns and then
    /// suspends or terminates; the documented way to listen to it is
    /// `MemoryManager`. The useful call here is `AppMemoryUsageIncreased`: when the
    /// level moves to High, the app must free what it can at once.
    ///
    /// Two things that do NOT exist on this platform, and that the compiler flags
    /// instead of leaving them uncovered at run time:
    ///
    ///  - `AppMemoryUsageLimitChanging` exists since Windows 10 1607 (on WP8.1 it
    ///    is not declared);
    ///  - `AppMemoryUsageLevel` here has three values - Low, Medium, High - and
    ///    **OverLimit does not exist** (CS0117): that one came with Windows 10.
    ///    So the threshold to watch is `High`, and that is all.
    ///
    /// What it frees: the decoded avatar bitmaps - the heavy thing, one per
    /// conversation - and the history of the chats nobody is reading. It does not
    /// free what the user is looking at: the open chat stays whole.
    ///
    /// While the pressure lasts nothing new is decoded: without this, the first
    /// frame of conversations puts back everything just dropped. When the level
    /// falls again, decoding resumes.
    /// </summary>
    public sealed class MemoryWatcher
    {
        private static readonly MemoryWatcher InstanceHolder = new MemoryWatcher();

        private bool _hooked;
        private bool _underPressure;

        public static MemoryWatcher Instance
        {
            get { return InstanceHolder; }
        }

        /// <summary>True while the memory level is High.</summary>
        public bool IsUnderPressure
        {
            get { return _underPressure; }
        }

        /// <summary>
        /// Once per process. App.StartServicesOnce calls it: an app started from a
        /// share does not go through OnLaunched, and a second hookup would double
        /// the handlers. Every call is guarded: a phone that refuses the event must
        /// not bring the app startup down.
        /// </summary>
        public void Start()
        {
            if (_hooked) return;
            _hooked = true;

            try
            {
                MemoryManager.AppMemoryUsageIncreased += OnUsageIncreased;
                MemoryManager.AppMemoryUsageDecreased += OnUsageDecreased;

                // The phone limit, once in the log: it is the only place a 512 MB
                // device is told apart from a 1 GB one.
                Diag.Ok("memory budget " + Mbytes(MemoryManager.AppMemoryUsageLimit) + " MB");

                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                _hooked = false;
                Diag.Failed("MemoryWatcher.Start", ex);
            }
        }

        private void OnUsageIncreased(object sender, object e)
        {
            try
            {
                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                Diag.Failed("MemoryWatcher.OnUsageIncreased", ex);
            }
        }

        private void OnUsageDecreased(object sender, object e)
        {
            try
            {
                Apply(MemoryManager.AppMemoryUsageLevel);
            }
            catch (Exception ex)
            {
                Diag.Failed("MemoryWatcher.OnUsageDecreased", ex);
            }
        }

        /// <summary>
        /// Applies a level: it frees on entering pressure, it stops freeing on
        /// leaving it. The freeing happens once per transition, not on every event:
        /// under pressure the events follow one another.
        ///
        /// High is the highest threshold this system can name: OverLimit does not
        /// exist on WP8.1.
        /// </summary>
        private void Apply(AppMemoryUsageLevel level)
        {
            bool pressure = level == AppMemoryUsageLevel.High;
            if (pressure == _underPressure) return;

            _underPressure = pressure;
            if (pressure)
            {
                Diag.Ok("memory under pressure: releasing decoded images");
                DataService.Instance.TrimForMemory();
            }
        }

        private static ulong Mbytes(ulong bytes)
        {
            return bytes / (1024UL * 1024UL);
        }
    }
}
