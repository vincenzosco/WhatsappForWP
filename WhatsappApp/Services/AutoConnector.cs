using System;
using System.Threading.Tasks;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decides which adapter to connect to without the user typing anything: the
    /// saved address if it answers, otherwise the single adapter announced on the
    /// network. Both the app startup and the settings page use it, which is the
    /// only place the logic can live (DRY).
    ///
    /// It shows nothing and throws nothing: the caller knows where to write.
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
                // The public service: the address is read from a file, not typed,
                // because the tunnel that exposes it changes port. Network
                // discovery has nothing to find here.
                if (SettingsService.UsePublicServer)
                {
                    DiscoveredServer remote = await EndpointService.Instance.ResolveAsync();
                    if (remote != null &&
                        await CommunicationService.Instance.ConnectToServerAsync(
                            remote.Address, remote.Port, username))
                    {
                        SettingsService.Save(remote.Address, remote.Port, username);
                        return true;
                    }

                    CommunicationService.Instance.NotifyServerUnavailable();
                    return false;
                }

                string address = SettingsService.ServerAddress;
                int port = SettingsService.ServerPort;

                if (!string.IsNullOrEmpty(address))
                {
                    bool onSaved = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
                    if (onSaved)
                    {
                        SettingsService.Save(address, port, username);
                        return true;
                    }

                    // The saved address no longer answers (DHCP gave the PC
                    // another IP): it is not a failure to show, it is data to
                    // forget. The single adapter that announces itself is tried.
                    Diag.Failed("AutoConnector/saved",
                        new InvalidOperationException("no answer from " + address + ":" + port));
                }

                await DiscoveryService.Instance.StartAsync();
                DiscoveredServer server = await DiscoveryService.Instance.WaitForSingleAsync(discoverySeconds);
                if (server == null)
                {
                    // No server answers, neither the saved one nor an announced
                    // one: this is the case that deserves a sentence for the user.
                    CommunicationService.Instance.NotifyServerUnavailable();
                    return false;
                }

                bool connected = await CommunicationService.Instance.ConnectToServerAsync(
                    server.Address, server.Port, username);
                if (connected) SettingsService.Save(server.Address, server.Port, username);
                else CommunicationService.Instance.NotifyServerUnavailable();
                return connected;
            }
            finally
            {
                _running = false;
            }
        }
    }
}
