using System;
using Windows.UI.Xaml;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Keeps the connection alive when WP8.1 kills it without saying so.
    ///
    /// The real case: the app is suspended, the OS closes the socket, and on
    /// resume `CommunicationService.IsConnected` is still true because nobody has
    /// read anything. From outside the app looks connected and is mute instead:
    /// messages no longer arrive and the chat list stays the previous one. The
    /// same happens without suspension, when the network changes.
    ///
    /// How it notices: every tick it sends the `status` command, which the adapter
    /// answers with a state frame. As long as the answers come back, the socket is
    /// alive. If nothing comes back before the deadline, the connection is closed
    /// and retried with AutoConnector, which is already the place where the
    /// address choice lives (the saved one, or the single announced adapter).
    ///
    /// It shows nothing: the settings page is the only one that speaks, and its
    /// state updates by itself because the reconnection raises the same events as
    /// a normal connection.
    /// </summary>
    public sealed class ConnectionWatchdog
    {
        private static ConnectionWatchdog _instance;

        public static ConnectionWatchdog Instance
        {
            get
            {
                if (_instance == null) _instance = new ConnectionWatchdog();
                return _instance;
            }
        }

        /// <summary>How often the state is asked for when the connection is silent.</summary>
        private const int IntervalSeconds = 20;

        /// <summary>
        /// After how much silence the connection is considered dead. Three times
        /// the interval: a slow answer or a skipped tick must not close a working
        /// connection.
        /// </summary>
        private const int StaleSeconds = 60;

        /// <summary>Seconds to wait for the announced adapter, when retrying.</summary>
        private const int DiscoverySeconds = 6;

        private DispatcherTimer _timer;

        // One tick at a time: the check does I/O, and two in parallel would ask
        // for the state twice and could close the same connection for each other.
        private bool _checking;

        private ConnectionWatchdog()
        {
        }

        /// <summary>
        /// Starts the periodic check. Call it once only, at startup, on the UI
        /// thread: DispatcherTimer lives on the thread that creates it.
        /// </summary>
        public void Start()
        {
            if (_timer != null) return;

            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(IntervalSeconds);
            _timer.Tick += Tick;
            _timer.Start();
        }

        /// <summary>
        /// Checks at once, without waiting for the tick: that is what resume
        /// needs, where the socket has just been closed by the OS and waiting
        /// twenty seconds means twenty seconds of a mute app.
        /// </summary>
        public void CheckNow()
        {
            Tick(null, null);
        }

        private void Tick(object sender, object e)
        {
            if (_checking) return;
            _checking = true;
#pragma warning disable 4014
            CheckAsync();
#pragma warning restore 4014
        }

        private async System.Threading.Tasks.Task CheckAsync()
        {
            try
            {
                var comm = CommunicationService.Instance;

                // Not connected: the first connection is not this service's job,
                // and chasing it here would mean two attempts in parallel with
                // AutoConnector.
                if (!comm.IsConnected) return;

                if (IsStale(comm.LastInboundUtc))
                {
                    Diag.Failed("ConnectionWatchdog/silent",
                        new TimeoutException("no frame from the adapter within " +
                            StaleSeconds + " s: reconnecting"));

                    // Closing before retrying is not a formality: AutoConnector
                    // returns true at once when IsConnected is true, so without this
                    // it would not even try.
                    comm.Disconnect();

                    await AutoConnector.Instance.TryConnectAsync(
                        SettingsService.Username, DiscoverySeconds);
                    return;
                }

                // The request that produces the answer: the adapter answers
                // `status` with a state frame, which refreshes LastInboundUtc.
                await comm.SendControlAsync("status");
            }
            catch (Exception ex)
            {
                Diag.Failed("ConnectionWatchdog", ex);
            }
            finally
            {
                _checking = false;
            }
        }

        private static bool IsStale(DateTime lastInboundUtc)
        {
            // Never read anything (default(DateTime)): that is the case of a
            // connection just opened, which has already set the field. If it stays
            // at zero, it is older than the deadline anyway.
            return (DateTime.UtcNow - lastInboundUtc).TotalSeconds > StaleSeconds;
        }
    }
}
