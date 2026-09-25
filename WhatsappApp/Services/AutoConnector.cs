using System.Threading.Tasks;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Decide a quale adapter connettersi senza che l'utente scriva nulla:
    /// l'indirizzo salvato se c'e', altrimenti l'unico adapter annunciato sulla
    /// rete. La usano sia l'avvio dell'app sia la pagina delle impostazioni, che
    /// e' l'unico posto in cui la logica puo' stare (DRY).
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
                string address = SettingsService.ServerAddress;
                int port = SettingsService.ServerPort;

                if (string.IsNullOrEmpty(address))
                {
                    await DiscoveryService.Instance.StartAsync();
                    DiscoveredServer server = await DiscoveryService.Instance.WaitForSingleAsync(discoverySeconds);
                    if (server == null) return false;
                    address = server.Address;
                    port = server.Port;
                }

                bool connected = await CommunicationService.Instance.ConnectToServerAsync(address, port, username);
                if (connected) SettingsService.Save(address, port, username);
                return connected;
            }
            finally
            {
                _running = false;
            }
        }
    }
}
