using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Storage;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The message check that runs while the app is suspended.
    ///
    /// Why it exists: this app speaks WhatsApp from the phone, and WP8.1 closes a
    /// socket when the app is suspended - there is no push service behind it. The
    /// only way a message that arrives with the app closed produces a notification
    /// is a background task that wakes, connects, reads what is waiting, updates the
    /// tile and the badge, and closes again.
    ///
    /// What it is not: a real-time client. A `TimeTrigger` on WP8.1 has a **floor of
    /// 15 minutes** - ten is not an interval the system accepts, and asking for it
    /// registers nothing - and no promise about the exact moment; 15 is the interval
    /// this asks for, and the system decides when it happens (battery saver can refuse
    /// it). The log line at the end of a run is the record of how long it took.
    ///
    /// The interval is also **registered**, not just asked for: a task the phone already
    /// has keeps the trigger it was created with, so a new build that asks for a different
    /// interval changes nothing until the old registration is taken out. That is why the
    /// interval is remembered next to the task and compared on every start.
    /// </summary>
    public static class BackgroundSync
    {
        /// <summary>The name the registration is remembered by.</summary>
        public const string TaskName = "WhatsAppMessageCheck";

        /// <summary>The class the manifest declares; it must be the full name.</summary>
        public const string EntryPoint = "WhatsappApp.Tasks.BackgroundSyncTask";

        /// <summary>
        /// How often the system is asked to wake the task.
        ///
        /// 15 is the smallest a `TimeTrigger` accepts on WP8.1 (ten throws, and the
        /// catch below turns that into "the check never runs" with a line in the log
        /// that says the system refused it), so this is as close to "every ten minutes"
        /// as the platform allows. The system adds its own delay on top.
        /// </summary>
        public const int IntervalMinutes = 15;

        /// <summary>
        /// The interval the registration on this phone was made with. A task keeps its
        /// own trigger, so this is what decides whether the one on the phone is still
        /// the one this build asks for.
        /// </summary>
        private const string KeyInterval = "BackgroundCheck.IntervalMinutes";

        /// <summary>
        /// The class the registration on this phone was made with.
        ///
        /// It is remembered for the same reason the interval is: a registration stores
        /// the entry point it was created with, and it keeps it. A build that moves the
        /// task - it lived in the app's own assembly until the manifest stopped being
        /// enough - would otherwise leave the phone waking a class that no longer exists,
        /// for as long as the user does not turn the check off and on again. What is
        /// stored here is what was asked for, and a value that does not match this build
        /// is a registration to take out and make again.
        /// </summary>
        private const string KeyEntryPoint = "BackgroundCheck.EntryPoint";

        /// <summary>
        /// How long a whole run may take.
        ///
        /// WP8.1 closes the process of a background task after about **30 seconds**, and
        /// it closes it wherever it is: a run still saving when that happens leaves a file
        /// half written, which is worse than a run that read less. Twenty is the part this
        /// code can control, and the waits below stay inside it; the ten left are the load
        /// of the process itself, which happens before the first line of the log.
        /// </summary>
        private static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(20);

        /// <summary>How long a run waits for the login before it gives up.</summary>
        private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(8);

        /// <summary>
        /// How long a run lets the backlog arrive before it reports. Clamped to what is
        /// left of the budget: eight for the login plus six here is over the twenty.
        /// </summary>
        private static readonly TimeSpan ReadWindow = TimeSpan.FromSeconds(6);

        private static readonly object Gate = new object();
        private static bool _running;

        /// <summary>
        /// How long the run in progress has been going.
        ///
        /// It is here and not only in the local of `RunNow` because the system cancels a task
        /// on its own thread: the line the task writes when it is canceled has to say how far
        /// the run had got, and on 23-24 October 2026 there was no such line at all - 21 wakes
        /// and not one of them said whether it had finished or been taken down in the middle.
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
            if (IsRegistered() && RegisteredInterval() == IntervalMinutes && RegisteredEntryPoint() == EntryPoint) return;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("BackgroundSync/register", SetEnabledAsync(true));
#pragma warning restore 4014
        }

        /// <summary>
        /// Registers or unregisters the task. The request goes to the system because
        /// a background task needs the user's permission: the first time this runs,
        /// WP8.1 asks whether the app may work in the background. A refusal is
        /// written down and the app keeps working in the foreground only.
        /// </summary>
        public static async Task SetEnabledAsync(bool enabled)
        {
            try
            {
                if (!enabled)
                {
                    Unregister();
                    Diag.Ok("background: the check is off");
                    return;
                }

                // The task the phone has was made with some interval, and a task keeps
                // the trigger it was registered with. Without this the build that asked
                // for a shorter check would keep the old one forever - which is a phone
                // that keeps waking every half hour whatever the source says. The entry
                // point is remembered for the same reason: a task keeps that too.
                if (IsRegistered() && RegisteredInterval() == IntervalMinutes
                    && RegisteredEntryPoint() == EntryPoint) return;
                if (IsRegistered())
                {
                    Unregister();
                    Diag.Ok("background: the registration was made by another build, registering again");
                }

                BackgroundAccessStatus access = await BackgroundExecutionManager.RequestAccessAsync();
                if (access == BackgroundAccessStatus.Denied)
                {
                    Diag.Ok("background: the system refused the check");
                    return;
                }

                var builder = new BackgroundTaskBuilder();
                builder.Name = TaskName;
                builder.TaskEntryPoint = EntryPoint;
                builder.SetTrigger(new TimeTrigger(IntervalMinutes, false));
                builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
                builder.Register();

                ApplicationData.Current.LocalSettings.Values[KeyInterval] = IntervalMinutes;
                ApplicationData.Current.LocalSettings.Values[KeyEntryPoint] = EntryPoint;

                Diag.Ok("background: the check runs every " + IntervalMinutes + " minute(s)");
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/SetEnabled", ex);
            }
        }

        /// <summary>
        /// One check: open the account, let the backlog arrive, put the count on the
        /// tile and the badge, close. It blocks on purpose - a background task owns a
        /// thread and has a deferral to finish, and the protocol continuations run on
        /// the thread pool, not on this thread.
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
                // Not Thread.Sleep: the WP8.1 Runtime profile the app compiles against
                // does not carry that type (CS0103). Waiting on a task is the same pause
                // without the missing reference.
                if (WaitForConnection(ConnectLimit))
                {
                    // Named, with the time it took: the run has a budget of twenty seconds
                    // inside a host that takes the process down at thirty, and until now
                    // nothing said which of the steps spent what.
                    Diag.OkNow("background: connected after " + watch.ElapsedMilliseconds + " ms");
                    WaitForBacklog(watch.Elapsed);
                    Diag.OkNow("background: the read window is over after " + watch.ElapsedMilliseconds + " ms");
                }

                int unread = DataService.Instance.UnreadChats();
                NotificationService.SetUnread(unread);

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

                // One line the next run can learn from: how much of the 30 seconds this
                // took, and whether it went over what the code budgeted for itself.
                Diag.Ok("background: checked in " + watch.ElapsedMilliseconds + " ms, "
                    + unread + " unread" + (watch.Elapsed > RunBudget
                        ? " (over the " + (int)RunBudget.TotalSeconds + " s budget)"
                        : ""));
            }
            catch (Exception ex)
            {
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
        /// The interval the task on this phone was registered with, or 0 when this phone
        /// has never written one - which is a registration made by an older build, and one
        /// that has to be made again.
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
        /// The class the task on this phone was registered for, or empty when this phone
        /// has never written one - which is a registration made by a build that kept the
        /// task in the app assembly, and one that has to be made again.
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

        /// <summary>True when this app already has the task registered.</summary>
        public static bool IsRegistered()
        {
            try
            {
                foreach (IBackgroundTaskRegistration registration in BackgroundTaskRegistration.AllTasks.Values)
                {
                    if (registration != null && registration.Name == TaskName) return true;
                }
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
                var found = new List<IBackgroundTaskRegistration>();
                foreach (IBackgroundTaskRegistration registration in BackgroundTaskRegistration.AllTasks.Values)
                {
                    if (registration != null && registration.Name == TaskName) found.Add(registration);
                }

                for (int i = 0; i < found.Count; i++) found[i].Unregister(true);

                // Nothing is registered now, so nothing remembers what it was made with either.
                ApplicationData.Current.LocalSettings.Values[KeyInterval] = 0;
                ApplicationData.Current.LocalSettings.Values[KeyEntryPoint] = "";
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
