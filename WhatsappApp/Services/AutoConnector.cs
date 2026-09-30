using System;
using System.Threading.Tasks;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decide a quale adapter connettersi senza che l'utente scriva nulla:
    /// l'indirizzo salvato se risponde, altrimenti l'unico adapter annunciato
    /// sulla rete. La usano sia l'avvio dell'app sia la pagina delle
    /// impostazioni, che e' l'unico posto in cui la logica puo' stare (DRY).
    ///
    /// Non mostra nulla e non lancia: il chiamante sa dove scrivere.
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
        /// True quando alla fine la connessione e' attiva.
        /// </summary>
        public async Task<bool> TryConnectAsync(string username, int discoverySeconds)
        {
            if (CommunicationService.Instance.IsConnected) return true;
            if (_running) return false;

            _running = true;
            try
            {
                // Il servizio pubblico: l'indirizzo si legge da un file, non si
                // scrive a mano, perche' il tunnel che lo espone cambia porta.
                // La scoperta in rete non ha niente da trovare qui.
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

                    // L'indirizzo salvato non risponde piu' (il DHCP ha dato al
                    // PC un altro IP): non e' un guasto da mostrare, e' un dato
                    // da dimenticare. Si prova l'unico adapter che si annuncia.
                    Diag.Failed("AutoConnector/saved",
                        new InvalidOperationException("no answer from " + address + ":" + port));
                }

                await DiscoveryService.Instance.StartAsync();
                DiscoveredServer server = await DiscoveryService.Instance.WaitForSingleAsync(discoverySeconds);
                if (server == null)
                {
                    // Nessun server che risponde, ne' quello salvato ne' uno
                    // annunciato: e' il caso che merita una frase all'utente.
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
