using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
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
    /// What it is not: a real-time client. A `TimeTrigger` has a minimum of 15
    /// minutes and no promise about the exact moment; 30 is the interval this asks
    /// for, and the system decides when it happens (battery saver can refuse it).
    /// The log line at the end of a run is the record of how long it took.
    /// </summary>
    public static class BackgroundSync
    {
        /// <summary>The name the registration is remembered by.</summary>
        public const string TaskName = "WhatsAppMessageCheck";

        /// <summary>The class the manifest declares; it must be the full name.</summary>
        public const string EntryPoint = "WhatsappApp.BackgroundSyncTask";

        /// <summary>How often the system is asked to wake the task.</summary>
        public const int IntervalMinutes = 30;

        /// <summary>How long a run waits for the login before it gives up.</summary>
        private static readonly TimeSpan ConnectLimit = TimeSpan.FromSeconds(10);

        /// <summary>How long a run lets the backlog arrive before it reports.</summary>
        private static readonly TimeSpan ReadWindow = TimeSpan.FromSeconds(7);

        private static readonly object Gate = new object();
        private static bool _running;

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
            if (IsRegistered()) return;

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

                if (IsRegistered()) return;

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

            var watch = Stopwatch.StartNew();

            try
            {
                Diag.Ok("background: woke");

                // The last message the app showed, so a count read from the cache has
                // a face and a line if nothing new has arrived yet.
                NotificationService.RestoreLastMessage();

                DataService.Instance.Start();
                NativeConnection.Instance.Start();

                WaitForConnection(ConnectLimit);

                // Not Thread.Sleep: the WP8.1 Runtime profile the app compiles
                // against does not carry that type (CS0103). Waiting on a task is
                // the same pause without the missing reference.
                Task.Delay(ReadWindow).Wait();

                int unread = DataService.Instance.UnreadChats();
                NotificationService.SetUnread(unread);

                Diag.Ok("background: checked in " + watch.ElapsedMilliseconds + " ms, "
                    + unread + " unread");
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/RunNow", ex);
            }
            finally
            {
                try { NativeConnection.Instance.Stop(); }
                catch (Exception ex) { Diag.Failed("BackgroundSync/Stop", ex); }

                lock (Gate)
                {
                    _running = false;
                }
            }
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
            }
            catch (Exception ex)
            {
                Diag.Failed("BackgroundSync/Unregister", ex);
            }
        }

        private static void WaitForConnection(TimeSpan limit)
        {
            DateTime end = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < end)
            {
                if (NativeConnection.Instance.IsConnected) return;
                Task.Delay(200).Wait();
            }

            Diag.Ok("background: not connected within " + (int)limit.TotalSeconds + " s");
        }
    }
}
