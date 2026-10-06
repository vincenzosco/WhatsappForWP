using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decides which adapter to connect to without the user typing anything.
    ///
    /// It no longer has one address to try but a list: the servers the registry
    /// publishes (when the app is set up for the public service), then the saved
    /// address, then the single adapter that announces itself on the network.
    /// Each candidate is dialled in order, and the first one that answers wins.
    /// This is what keeps the app working when a server goes down: the watchdog
    /// closes the dead connection and calls this again, which no longer stops at
    /// the address that just failed.
    ///
    /// Both the app startup and the settings page use it, which is the only
    /// place the logic can live (DRY). It shows nothing and throws nothing: the
    /// caller knows where to write.
    /// </summary>
    public sealed class AutoConnector
    {
        private static AutoConnector _instance;

        public static AutoConnector Instance
        {
            get
            {
                if (_instance == null) _instance = new AutoConnector();
                return _instance;
            }
        }

        private bool _running;

        private AutoConnector()
        {
        }

        public bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>
        /// True when the connection is active in the end.
        /// </summary>
        public async Task<bool> TryConnectAsync(string username, int discoverySeconds)
        {
            if (CommunicationService.Instance.IsConnected) return true;
            if (_running) return false;

            _running = true;
            try
            {
                var candidates = new List<DiscoveredServer>();

                // The public service: the addresses are read from a file, not
                // typed, because the tunnel that exposes them changes port and a
                // deployment can run more than one. Network discovery has
                // nothing to find here.
                if (SettingsService.UsePublicServer)
                {
                    List<DiscoveredServer> remote = await EndpointService.Instance.ResolveAllAsync();
                    if (remote != null)
                    {
                        // One candidate per address, not per row: two registry rows
                        // that carry the same host and port are the same server, and
                        // dialling it twice doubles the wait on the one that is down.
                        for (int i = 0; i < remote.Count; i++)
                        {
                            AddCandidate(candidates, remote[i].Address, remote[i].Port);
                        }
                    }
                }

                // The saved address: the last server that answered, private or
                // public. Tried after the registry, so a registry that moved its
                // first server is followed rather than fought.
                string savedAddress = SettingsService.ServerAddress;
                if (!string.IsNullOrEmpty(savedAddress))
                {
                    AddCandidate(candidates, savedAddress, SettingsService.ServerPort);
                }

                // One candidate needs no ranking: measuring it would add one
                // connection to the common private-server case for nothing.
                if (candidates.Count > 1)
                {
                    await RankByLatencyAsync(candidates);
                }

                // Written down before the attempts: an empty list with a healthy
                // adapter is nearly always the address, and nothing else in the
                // app says which ones were tried.
                Diag.Ok("connecting: public=" + SettingsService.UsePublicServer
                    + " candidates=" + candidates.Count
                    + " saved=" + SettingsService.ServerAddress + ":" + SettingsService.ServerPort);

                for (int i = 0; i < candidates.Count; i++)
                {
                    DiscoveredServer candidate = candidates[i];
                    // The probe reports itself through the return value: the failure
                    // of one candidate is not a connection error to raise, it is a
                    // candidate out of the way.
                    if (await CommunicationService.Instance.ConnectToServerAsync(
                        candidate.Address, candidate.Port, username, true))
                    {
                        SettingsService.Save(candidate.Address, candidate.Port, username);
                        Diag.Ok("connected to " + candidate.Endpoint);
                        return true;
                    }

                    // The address no longer answers (a down server, or DHCP gave
                    // the machine another IP): it is not a failure to show, it is
                    // one candidate out of the way. The next one is tried.
                    Diag.Failed("AutoConnector/candidate",
                        new InvalidOperationException("no answer from " + candidate.Endpoint));
                }

                // Nothing we knew answered. The single adapter that announces
                // itself is tried: on a private network it is the one that works,
                // and on a public setup it is a last resort.
                await DiscoveryService.Instance.StartAsync();
                DiscoveredServer server = await DiscoveryService.Instance.WaitForSingleAsync(discoverySeconds);
                if (server == null)
                {
                    // No server answers, neither a known one nor an announced
                    // one: this is the case that deserves a sentence for the user.
                    CommunicationService.Instance.NotifyServerUnavailable();
                    return false;
                }

                bool connected = await CommunicationService.Instance.ConnectToServerAsync(
                    server.Address, server.Port, username, true);
                if (connected) SettingsService.Save(server.Address, server.Port, username);
                else CommunicationService.Instance.NotifyServerUnavailable();
                return connected;
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>
        /// One more candidate, unless the same address and port are already in
        /// the list: the saved address is often the first registry entry, and
        /// dialling it twice would double the wait on the server that is down.
        /// </summary>
        private static void AddCandidate(List<DiscoveredServer> candidates, string address, int port)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Address == address && candidates[i].Port == port) return;
            }

            var candidate = new DiscoveredServer();
            candidate.Address = address;
            candidate.Port = port;
            candidate.Name = "";
            candidates.Add(candidate);
        }

        /// <summary>
        /// Orders the candidates by how fast they answer, fastest first. A server
        /// that did not answer keeps a place at the end of the list instead of
        /// being dropped: the ping is a hint, the connection is the judge, and a
        /// lost probe must not lose a server.
        /// </summary>
        private static async Task RankByLatencyAsync(List<DiscoveredServer> candidates)
        {
            var probes = new Task<int>[candidates.Count];
            for (int i = 0; i < candidates.Count; i++)
            {
                probes[i] = ServerPinger.MeasureAsync(candidates[i].Address, candidates[i].Port);
            }
            int[] latencies = await Task.WhenAll(probes);

            var text = new System.Text.StringBuilder("ping: ");
            for (int i = 0; i < latencies.Length; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(candidates[i].Endpoint).Append("=");
                text.Append(latencies[i] == ServerPinger.NoAnswer
                    ? "no answer" : latencies[i] + " ms");
            }
            Diag.Ok(text.ToString());

            var ranked = new List<DiscoveredServer>();
            var taken = new bool[candidates.Count];
            while (ranked.Count < candidates.Count)
            {
                int best = -1;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (taken[i]) continue;
                    if (best == -1)
                    {
                        best = i;
                        continue;
                    }
                    int left = latencies[i] == ServerPinger.NoAnswer ? int.MaxValue : latencies[i];
                    int right = latencies[best] == ServerPinger.NoAnswer ? int.MaxValue : latencies[best];
                    // Strictly smaller, so equal scores keep the registry order.
                    if (left < right) best = i;
                }
                taken[best] = true;
                ranked.Add(candidates[best]);
            }

            candidates.Clear();
            candidates.AddRange(ranked);
        }
    }
}
