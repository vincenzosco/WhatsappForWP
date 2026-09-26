using System;
using Windows.UI.Xaml;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Tiene viva la connessione quando WP8.1 la uccide senza dirlo.
    ///
    /// Il caso vero: l'app viene sospesa, l'OS chiude il socket, e alla ripresa
    /// `CommunicationService.IsConnected` e' ancora true perche' nessuno ha
    /// letto niente. Da fuori l'app sembra collegata e invece e' muta: i
    /// messaggi non arrivano piu' e l'elenco chat resta quello di prima. Lo
    /// stesso capita senza sospensione, quando cambia la rete.
    ///
    /// Come se ne accorge: ogni tick manda il comando `status`, che l'adapter
    /// risponde con un frame di stato. Finche' le risposte tornano, il socket e'
    /// vivo. Se non torna niente entro la scadenza, la connessione si chiude e
    /// si riprova con AutoConnector, che e' gia' il posto dove vive la scelta
    /// dell'indirizzo (quello salvato, o l'unico adapter annunciato).
    ///
    /// Non mostra niente: la pagina delle impostazioni e' l'unica che parla, e
    /// il suo stato si aggiorna da solo perche' la riconnessione solleva gli
    /// stessi eventi di un collegamento normale.
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

        /// <summary>Ogni quanto si chiede lo stato quando la connessione tace.</summary>
        private const int IntervalSeconds = 20;

        /// <summary>
        /// Da quanto silenzio la connessione si considera morta. Tre volte
        /// l'intervallo: una risposta lenta o un tick saltato non devono
        /// chiudere una connessione che funziona.
        /// </summary>
        private const int StaleSeconds = 60;

        /// <summary>Secondi di attesa dell'adapter annunciato, quando si riprova.</summary>
        private const int DiscoverySeconds = 6;

        private DispatcherTimer _timer;

        // Un tick alla volta: il controllo fa I/O, e due in parallelo
        // chiederebbero due volte lo stato e potrebbero chiudere la stessa
        // connessione a vicenda.
        private bool _checking;

        private ConnectionWatchdog()
        {
        }

        /// <summary>
        /// Avvia il controllo periodico. Va chiamato una volta sola, all'avvio,
        /// sul thread UI: DispatcherTimer vive del thread che lo crea.
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
        /// Verifica subito, senza aspettare il tick: e' quello che serve alla
        /// ripresa, dove il socket e' appena stato chiuso dall'OS e aspettare
        /// venti secondi significa venti secondi di app muta.
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

                // Non collegata: la prima connessione non e' compito di questo
                // servizio, e inseguirla qui vorrebbe dire due tentativi in
                // parallelo con AutoConnector.
                if (!comm.IsConnected) return;

                if (IsStale(comm.LastInboundUtc))
                {
                    Diag.Failed("ConnectionWatchdog/silent",
                        new TimeoutException("no frame from the adapter within " +
                            StaleSeconds + " s: reconnecting"));

                    // Chiudere prima di riprovare non e' una formalita':
                    // AutoConnector restituisce true subito quando IsConnected
                    // e' true, quindi senza questo non proverebbe nemmeno.
                    comm.Disconnect();

                    await AutoConnector.Instance.TryConnectAsync(
                        SettingsService.Username, DiscoverySeconds);
                    return;
                }

                // La richiesta che genera la risposta: l'adapter risponde a
                // `status` con un frame di stato, che rinfresca LastInboundUtc.
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
            // Mai letto niente (default(DateTime)): e' il caso di una
            // connessione appena aperta, che ha gia' impostato il campo. Se
            // resta a zero, e' comunque piu' vecchio della scadenza.
            return (DateTime.UtcNow - lastInboundUtc).TotalSeconds > StaleSeconds;
        }
    }
}
