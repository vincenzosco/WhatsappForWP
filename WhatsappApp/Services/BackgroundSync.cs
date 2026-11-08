using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Phone.Scheduler;
using Windows.Storage;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The message check that runs while the app is suspended.
    ///
    /// This app speaks WhatsApp from the phone, and WP8.1 closes a
    /// socket when the app is suspended - there is no push service behind it. The
    /// only way a message that arrives with the app closed produces a notification
    /// is a background task that wakes, connects, reads what is waiting, updates the
    /// tile and the badge, and closes again.
    ///
    /// Not a real-time client. Silverlight periodic agents run on the system's
    /// approximately 30-minute schedule; the app cannot choose a shorter interval or
    /// the exact wake time. Battery and system policy can also delay or refuse a run.
    /// The log line at the end of a run is the record of how long it took.
    ///
    /// The registration is remembered alongside the task so a build that changes the
    /// requested cadence or task class can remove and recreate an older registration.
    /// </summary>
    public static class BackgroundSync
    {
        /// <summary>The name the registration is remembered by.</summary>
        public const string TaskName = "WhatsAppMessageCheck";

        /// <summary>The class the manifest declares; it must be the full name.</summary>
        public const string EntryPoint = "WhatsappApp.Tasks.BackgroundSyncTask";

        /// <summary>
        /// The effective cadence of a Silverlight periodic task. The system controls
        /// the actual wake time and may delay or skip a run.
        /// </summary>
        public const int IntervalMinutes = 30;

        /// <summary>
        /// The effective cadence this build targets. This saved value lets a later
        /// build detect and replace registration metadata from an older cadence.
        /// </summary>
        private const string KeyInterval = "BackgroundCheck.IntervalMinutes";

        /// <summary>
        /// The agent class this build targets. The scheduler identifies actions by name,
        /// so this local value detects when a build changes the class declared in the
        /// Silverlight manifest and should refresh the existing action.
        /// </summary>
        private const string KeyEntryPoint = "BackgroundCheck.EntryPoint";
        private const string KeyRegistrationUnavailable = "BackgroundCheck.RegistrationUnavailable";

        /// <summary>
        /// How long a whole run may take.
        ///
        /// Silverlight agents have a short execution ceiling, and the process can end
        /// wherever a run is: a run still saving then can leave a file half written, which
        /// is worse than a run that read less. Twenty seconds is the work budget this code
        /// controls; the remaining time is left for agent startup and final diagnostics.
        /// </summary>
        private static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(20);

        /// <summary>How long a run waits for the login before it gives up.</summary>
        private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(8);

        // What the last wake left on the phone, for the diagnostics page: a wake that
        // read nothing and a wake that never ran look the same from the outside, and the
        // one question a report has to answer is which of the two happened.
        private const string KeyLastWake = "BackgroundCheck.LastWakeAt";
        private const string KeyLastOutcome = "BackgroundCheck.LastOutcome";
        private const string KeyLastUnread = "BackgroundCheck.LastUnread";
        private const string KeyLastMilliseconds = "BackgroundCheck.LastMilliseconds";

        /// <summary>Written when a wake starts, so a wake taken down reads as one.</summary>
        public const string OutcomeRunning = "running";

        /// <summary>The wake went through: it connected, read, saved and closed.</summary>
        public const string OutcomeDone = "done";

        /// <summary>The wake threw. The log has the exception; this is that it happened.</summary>
        public const string OutcomeFailed = "failed";

        /// <summary>
        /// The previous invocation ended before it recorded a terminal outcome. Silverlight
        /// agents have no supported cancellation callback, so the next invocation detects
        /// this from the persisted running marker.
        /// </summary>
        public const string OutcomeCanceled = "canceled";

        /// <summary>
        /// How long a run lets the backlog arrive before it reports. Clamped to what is
        /// left of the budget: eight for the login plus six here is over the twenty.
        /// </summary>
        private static readonly TimeSpan ReadWindow = TimeSpan.FromSeconds(6);

        private static readonly object Gate = new object();
        private static bool _running;

        /// <summary>
        /// How long the current run has been going. A terminated invocation cannot
        /// update its own outcome; the next run uses the persisted running marker to
        /// report that it ended before completion.
        /// </summary>
        public static long ElapsedMilliseconds
        {
            get { return Watch.ElapsedMilliseconds; }
        }

        private static readonly Stopwatch Watch = new Stopwatch();

        /// <summary>True while a run is in progress. A second wake does nothing.</summary>
        public static bool IsRunning
        {
            get
            {
                lock (Gate)
                {
                    return _running;
                }
            }
        }

        /// <summary>
        /// Registers the task at startup when the setting is on. Called once per
        /// process, from `App.StartServicesOnce`, off the launch path.
        /// </summary>
        public static void Start()
        {
            if (!SettingsService.BackgroundCheckEnabled) return;

            // A registration is not the same thing as a check that runs. The phone can
            // disable a scheduled agent in its background-task settings, so availability
            // checks the action's IsScheduled state as well as whether a registration exists.
            // The saved metadata also lets the app notice when the system removed the action.
            Refresh();
        }

        /// <summary>
        /// Checks the scheduled-action registration and recreates it when the saved
        /// cadence or task class no longer matches. Silverlight exposes no separate
        /// background-access query; an absent registration with prior registration metadata
        /// is treated as unavailable, while registration failures are recorded for settings.
        /// </summary>
        public static void Refresh()
        {
            if (!SettingsService.BackgroundCheckEnabled) return;

            if (!IsPermitted())
            {
                Diag.Ok("background: the scheduled check is unavailable, asking the system to register it again");

                // A registration attempt is still made when the system previously refused
                // or removed the task, so a user who re-enables background work can recover
                // without changing the app's setting.
#pragma warning disable 4014
                Guarded.RunGuardedAsync("BackgroundSync/request", SetEnabledAsync(true));
#pragma warning restore 4014
                return;
            }

            if (IsRegistered() && RegisteredInterval() == IntervalMinutes
                && RegisteredEntryPoint() == EntryPoint) return;

            // Missing or stale registration: SetEnabledAsync owns removal, registration,
            // and reporting any refusal from the scheduler.
            Diag.Ok("background: the scheduled check is missing or outdated"
                + (IsRegistered()
                    ? ", registering it again"
                    : ", registering it now"));

#pragma warning disable 4014
            Guarded.RunGuardedAsync("BackgroundSync/refresh", SetEnabledAsync(true));
#pragma warning restore 4014
        }

        /// <summary>
        /// Registers or unregisters the task. The scheduler reports refusal through
        /// registration state or an exception; both are recorded while foreground work
        /// remains available.
        /// </summary>
        public static Task SetEnabledAsync(bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    Unregister();
                    Diag.Ok("background: the check is off");
                    return Task.FromResult<object>(null);
                }

                if (IsRegistered() && RegisteredInterval() == IntervalMinutes
                    && RegisteredEntryPoint() == EntryPoint && IsPermitted())
                {
                    return Task.FromResult<object>(null);
                }

                if (IsRegistered())
                {
                    Unregister();
                    Diag.Ok("background: the registration was made by another build, registering again");
                }

                var task = new PeriodicTask(TaskName);
                task.Description = "Check for new WhatsApp messages";
                ScheduledActionService.Add(task);

                ApplicationData.Current.LocalSettings.Values[KeyInterval] = IntervalMinutes;
                ApplicationData.Current.LocalSettings.Values[KeyEntryPoint] = EntryPoint;
                ApplicationData.Current.LocalSettings.Values[KeyRegistrationUnavailable] = false;

                Diag.Ok("background: the scheduled check runs about every " + IntervalMinutes + " minute(s)");
            }
            catch (InvalidOperationException ex)
            {
                if (!IsRegistered())
                {
                    SetRegistrationUnavailable(true);
                    Diag.Ok("background: the system refused or could not schedule the check");
                }
                Diag.Failed("BackgroundSync/SetEnabled", ex);
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/SetEnabled", ex);
            }

            return Task.FromResult<object>(null);
        }

        /// <summary>
        /// One check: open the account, let the backlog arrive, put the count on the
        /// tile and the badge, close. It blocks on purpose so the scheduled agent does
        /// not call NotifyComplete before the protocol continuations and final writes finish.
        /// </summary>
        public static void RunNow()
        {
            if (!SettingsService.BackgroundCheckEnabled) return;

            lock (Gate)
            {
                if (_running) return;
                _running = true;
            }

            var watch = Watch;
            watch.Restart();

            try
            {
                Diag.Ok("background: woke");
                RememberWake(OutcomeRunning, LastUnread, 0);

                // What the last run left on the tile is read back first: this process
                // starts with nothing in memory, and a check that drew a tile without it
                // would either name nobody or clear what the phone was showing (see
                // NotificationService.BeginBackgroundCheck).
                NotificationService.BeginBackgroundCheck();

                DataService.Instance.Start();
                NativeConnection.Instance.Start();

                // The wait for the backlog only makes sense once the login is up: a run
                // that never connected has nothing to read, and waiting anyway is a
                // background task spending its budget on a socket that is not there.
                //
                // Waiting on a task keeps the bounded pause available to both the
                // foreground app and the Silverlight scheduled-agent target.
                if (WaitForConnection(ConnectLimit))
                {
                    // Named, with the time it took: the run has a twenty-second work budget,
                    // and this identifies the time consumed before reading the backlog.
                    Diag.OkNow("background: connected after " + watch.ElapsedMilliseconds + " ms");
                    WaitForBacklog(watch.Elapsed);
                    Diag.OkNow("background: the read window is over after " + watch.ElapsedMilliseconds + " ms");
                }

                int unread = DataService.Instance.UnreadChats();
                NotificationService.SetUnread(unread);

                // And the updates that arrived with this wake say so on the tile: a check that
                // read one and drew nothing leaves the phone saying only that no message came.
                NotificationService.SetStatuses(DataService.Instance.UnseenStatuses());

                // And what it read goes to the phone before this process ends. The tile
                // is not a record of anything: WhatsApp counts these messages as
                // delivered when they are acknowledged, and never sends them again, so a
                // run that drew the tile and closed left the app that opened next with no
                // backlog and no row that mentioned the message - the count on the tile
                // said one had arrived and the list showed nothing new.
                //
                // Waited for, not fired and forgotten: the process ends when this method
                // returns, and a write still in flight would be a file half written.
                try
                {
                    DataService.Instance.SaveSnapshotAsync().Wait();
                }
                catch (Exception ex)
                {
                    Diag.Failed("BackgroundSync/save", ex);
                }

                Diag.OkNow("background: the snapshot is on the phone after " + watch.ElapsedMilliseconds + " ms");

                RememberWake(OutcomeDone, unread, watch.ElapsedMilliseconds);

                // One line the next run can learn from: how much of the 30 seconds this
                // took, and whether it went over what the code budgeted for itself.
                Diag.Ok("background: checked in " + watch.ElapsedMilliseconds + " ms, "
                    + unread + " unread" + (watch.Elapsed > RunBudget
                        ? " (over the " + (int)RunBudget.TotalSeconds + " s budget)"
                        : ""));
            }
            catch (Exception ex)
            {
                RememberWake(OutcomeFailed, LastUnread, Watch.ElapsedMilliseconds);
                Diag.Failed("BackgroundSync/RunNow", ex);
            }
            finally
            {
                NotificationService.EndBackgroundCheck();

                try { NativeConnection.Instance.Stop(); }
                catch (Exception ex) { Diag.Failed("BackgroundSync/Stop", ex); }

                lock (Gate)
                {
                    _running = false;
                }
            }
        }

        /// <summary>
        /// The interval saved for this phone's registration, or 0 when there is none.
        /// </summary>
        private static int RegisteredInterval()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyInterval, out value)
                    && value is int)
                {
                    return (int)value;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/RegisteredInterval", ex);
            }

            return 0;
        }

        /// <summary>
        /// The class saved for this phone's registration, or empty when there is none.
        /// </summary>
        private static string RegisteredEntryPoint()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(KeyEntryPoint, out value)
                    && value is string)
                {
                    return (string)value;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/RegisteredEntryPoint", ex);
            }

            return "";
        }

        /// <summary>When the last wake started, in UTC, or `default(DateTime)` for never.</summary>
        public static DateTime LastWake
        {
            get
            {
                long ticks = LastNumber(KeyLastWake, 0);
                if (ticks <= 0) return default(DateTime);
                return new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        /// <summary>
        /// How the last wake ended: one of the `Outcome*` words, or empty when the phone
        /// has never recorded one. `running` is a wake that started and left no ending;
        /// the next agent invocation reports it as interrupted.
        /// </summary>
        public static string LastOutcome
        {
            get { return LastText(KeyLastOutcome); }
        }

        /// <summary>The unread count the last wake put on the tile.</summary>
        public static int LastUnread
        {
            get { return (int)LastNumber(KeyLastUnread, 0); }
        }

        /// <summary>How long the last wake took, in milliseconds.</summary>
        public static int LastMilliseconds
        {
            get { return (int)LastNumber(KeyLastMilliseconds, 0); }
        }

        /// <summary>
        /// Says on the phone how a wake is going, in the same place the diagnostics page
        /// reads: written at the start as `running` and overwritten with the ending, so
        /// an invocation that exits without an ending remains visible on the next start.
        /// </summary>
        private static void RememberWake(string outcome, int unread, long milliseconds)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[KeyLastWake] = DateTime.UtcNow.Ticks;
                ApplicationData.Current.LocalSettings.Values[KeyLastOutcome] = outcome;
                ApplicationData.Current.LocalSettings.Values[KeyLastUnread] = unread;
                ApplicationData.Current.LocalSettings.Values[KeyLastMilliseconds] = (int)milliseconds;
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/RememberWake", ex);
            }
        }

        /// <summary>
        /// Records a previous invocation that was terminated before it replaced the
        /// running marker. Silverlight's ScheduledTaskAgent.OnCancel is unsupported, so
        /// the next OnInvoke supplies the scheduler's last-exit reason.
        /// </summary>
        public static void MarkCanceled(string reason)
        {
            lock (Gate)
            {
                if (LastOutcome != OutcomeRunning) return;

                RememberWake(OutcomeCanceled, LastUnread, LastMilliseconds);
            }

            Diag.Ok("background: the previous check was interrupted (" + reason + ")");
        }

        private static long LastNumber(string key, long fallback)
        {
            try
            {
                object stored;
                long value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out stored) && stored != null
                    && long.TryParse(stored.ToString(), out value)) return value;
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/LastNumber", ex);
            }

            return fallback;
        }

        private static string LastText(string key)
        {
            try
            {
                object stored;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(key, out stored) && stored != null)
                {
                    return stored.ToString();
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/LastText", ex);
            }

            return "";
        }

        private static bool RegistrationUnavailable()
        {
            try
            {
                object value;
                return ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    KeyRegistrationUnavailable, out value) && value is bool && (bool)value;
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/RegistrationUnavailable", ex);
                return false;
            }
        }

        private static void SetRegistrationUnavailable(bool unavailable)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[KeyRegistrationUnavailable] = unavailable;
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/SetRegistrationUnavailable", ex);
            }
        }

        /// <summary>
        /// Whether the scheduled check appears available. Silverlight has no direct
        /// background-access status API, so this uses current scheduling state and the
        /// recorded result of an attempted registration.
        /// </summary>
        public static bool IsPermitted()
        {
            try
            {
                ScheduledAction action = ScheduledActionService.Find(TaskName);
                if (action != null) return action.IsScheduled;
                if (RegistrationUnavailable()) return false;
                return RegisteredInterval() == 0 && RegisteredEntryPoint() == "";
            }
            catch (Exception ex)
            {
                // Not knowing is not a refusal: the line under the switch must not accuse
                // the phone of something it never said.
                Diag.Failed("BackgroundSync/IsPermitted", ex);
                return true;
            }
        }

        /// <summary>True when this app already has the task registered.</summary>
        public static bool IsRegistered()
        {
            try
            {
                return ScheduledActionService.Find(TaskName) != null;
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/IsRegistered", ex);
            }

            return false;
        }

        private static void Unregister()
        {
            try
            {
                if (ScheduledActionService.Find(TaskName) != null)
                {
                    ScheduledActionService.Remove(TaskName);
                }

                // Nothing is registered now, so nothing remembers what it was made with either.
                ApplicationData.Current.LocalSettings.Values[KeyInterval] = 0;
                ApplicationData.Current.LocalSettings.Values[KeyEntryPoint] = "";
                ApplicationData.Current.LocalSettings.Values[KeyRegistrationUnavailable] = false;
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/Unregister", ex);
            }
        }

        /// <summary>
        /// Waits for the backlog to arrive, for what is left of the budget rather than for
        /// the whole window: a login that took eight seconds has already spent the part the
        /// read was going to use, and waiting anyway is how a run reaches the thirty with
        /// the save still to do.
        /// </summary>
        private static void WaitForBacklog(TimeSpan spent)
        {
            TimeSpan left = RunBudget - spent;
            TimeSpan wait = left < ReadWindow ? left : ReadWindow;
            if (wait <= TimeSpan.Zero) return;

            Task.Delay(wait).Wait();
        }

        /// <summary>
        /// Waits for the login, and says whether it came. False is why the run skips the
        /// read window: there is nothing to read from, and the tile it is about to draw is
        /// the one the last run left - which is the point, a wake that changed nothing on
        /// screen is a check nobody can see.
        /// </summary>
        private static bool WaitForConnection(TimeSpan limit)
        {
            DateTime end = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < end)
            {
                if (NativeConnection.Instance.IsConnected) return true;
                Task.Delay(200).Wait();
            }

            bool connected = NativeConnection.Instance.IsConnected;
            if (!connected)
            {
                Diag.Ok("background: not connected within " + (int)limit.TotalSeconds
                    + " s, the tile keeps what the last run left");
            }

            return connected;
        }
    }
}
