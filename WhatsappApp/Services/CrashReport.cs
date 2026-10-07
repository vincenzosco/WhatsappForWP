using System;
using System.Threading.Tasks;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The crash of the previous run, sent to the adapter by itself.
    ///
    /// Why it exists: the phone has no debugger attached, and the diagnostics page
    /// needs the app to still be alive to show anything. A run that died leaves its
    /// tail in `Diag.PendingCrashTail`, and this is what carries it off the device:
    /// on the first connection of the next run the tail goes to the adapter as a
    /// `diag` frame, which the adapter writes to its container log, so the crash is
    /// read on a PC instead of copied off a screen by hand.
    ///
    /// One report per run, and only for a run that did not end on purpose: `EndRun`
    /// is what clears the tail, and a crash never reaches it. A tail that could not
    /// be sent - no socket yet, no pairing yet - is therefore still there for the
    /// next run, which is the case that matters: a phone that cannot connect is the
    /// phone whose crash is most worth reading.
    /// </summary>
    public static class CrashReport
    {
        private static bool _started;
        private static bool _sent;

        /// <summary>
        /// Subscribes once per process. Called from App.StartServicesOnce, next to
        /// the other services: the connection event exists from then on, and a
        /// second subscription would send the same report twice.
        /// </summary>
        public static void Start()
        {
            if (_started) return;
            _started = true;

            try
            {
                CommunicationService.Instance.ConnectionEstablished += OnConnectionEstablished;
            }
            catch (Exception ex)
            {
                Diag.Failed("CrashReport.Start", ex);
            }
        }

        /// <summary>
        /// The socket is up: the previous run's tail goes out, once. A run that ended
        /// properly has nothing to send and leaves this silent, so the container log
        /// only carries runs that really died.
        /// </summary>
        private static void OnConnectionEstablished(object sender, EventArgs e)
        {
            try
            {
                if (_sent) return;

                string tail = Diag.PendingCrashTail;
                if (string.IsNullOrEmpty(tail)) return;

                _sent = true;
                Diag.Ok("crash report sent");
#pragma warning disable 4014
                Guarded.RunGuardedAsync("CrashReport/send",
                    CommunicationService.Instance.SendControlAsync("diag",
                        "previous run did not end\r\n" + tail));
#pragma warning restore 4014
            }
            catch (Exception ex)
            {
                Diag.Failed("CrashReport/OnConnectionEstablished", ex);
            }
        }
    }
}
