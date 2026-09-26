using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Ascolta i beacon dell'adapter e tiene la lista di quelli visti negli
    /// ultimi secondi. Non mostra niente e non lancia: chi la usa decide cosa
    /// fare dei server trovati.
    /// </summary>
    public sealed class DiscoveryService
    {
        /// <summary>Porta UDP annunciata dall'adapter (DISCOVERY_PORT).</summary>
        public const int Port = 8587;

        /// <summary>Secondi dopo i quali un adapter che non si annuncia piu' sparisce.</summary>
        private const int TtlSeconds = 6;

        private const int PollIntervalMs = 250;

        private static DiscoveryService _instance;

        public static DiscoveryService Instance
        {
            get
            {
                if (_instance == null) _instance = new DiscoveryService();
                return _instance;
            }
        }

        private readonly List<DiscoveredServer> _servers = new List<DiscoveredServer>();
        private readonly object _gate = new object();
        private DatagramSocket _socket;
        private bool _starting;

        /// <summary>Sollevato su un thread di rete quando la lista cambia.</summary>
        public event EventHandler ServersChanged;

        private DiscoveryService()
        {
        }

        public bool IsListening
        {
            get { return _socket != null; }
        }

        /// <summary>Gli adapter noti adesso, dal piu' recente.</summary>
        public List<DiscoveredServer> Snapshot()
        {
            lock (_gate)
            {
                Prune();
                var copy = new List<DiscoveredServer>(_servers);
                copy.Sort(delegate(DiscoveredServer a, DiscoveredServer b)
                {
                    return b.LastSeen.CompareTo(a.LastSeen);
                });
                return copy;
            }
        }

        /// <summary>
        /// Apre la porta UDP. Se la rete o la porta non lo permettono resta
        /// spento: l'inserimento manuale dell'indirizzo continua a funzionare.
        /// </summary>
        public async Task StartAsync()
        {
            if (_socket != null || _starting) return;
            _starting = true;
            try
            {
                var socket = new DatagramSocket();
                socket.MessageReceived += OnMessageReceived;
                await socket.BindServiceNameAsync(Port.ToString());
                _socket = socket;
            }
            catch (Exception ex)
            {
                // Se la porta non si apre non c'e' niente da riprovare qui: si
                // registra perche' e quale, e la pagina continua con
                // l'inserimento manuale dell'indirizzo.
                Diag.Failed("DiscoveryService.StartAsync", ex);
                _socket = null;
            }
            finally
            {
                _starting = false;
            }
        }

        public void Stop()
        {
            DatagramSocket socket = _socket;
            _socket = null;
            if (socket == null) return;
            try { socket.MessageReceived -= OnMessageReceived; }
            catch (Exception ex) { Diag.Failed("DiscoveryService.Stop/handler", ex); }
            try { socket.Dispose(); }
            catch (Exception ex) { Diag.Failed("DiscoveryService.Stop/dispose", ex); }
        }

        /// <summary>
        /// Aspetta fino a <paramref name="seconds"/> secondi che si veda un solo
        /// adapter. Con zero o piu' di uno restituisce null: con piu' di uno la
        /// scelta tocca all'utente.
        /// </summary>
        public async Task<DiscoveredServer> WaitForSingleAsync(int seconds)
        {
            int attempts = seconds * (1000 / PollIntervalMs);
            for (int i = 0; i < attempts; i++)
            {
                List<DiscoveredServer> found = Snapshot();
                if (found.Count == 1) return found[0];
                if (found.Count > 1) return null;
                await Task.Delay(PollIntervalMs);
            }
            return null;
        }

        private void Prune()
        {
            DateTime limit = DateTime.Now.AddSeconds(-TtlSeconds);
            for (int i = _servers.Count - 1; i >= 0; i--)
            {
                if (_servers[i].LastSeen < limit) _servers.RemoveAt(i);
            }
        }

        /// <summary>
        /// Un datagramma di beacon. Il reader che arriva qui contiene gia' il
        /// datagramma: chiamare LoadAsync su di lui risponde
        /// "The operation identifier is not valid" (0x800710DD) a ogni pacchetto
        /// ricevuto, ed e' quello che riempiva il log. Si legge direttamente, e
        /// non e' piu' async: un handler async void che lancia non lo vede
        /// nessuno.
        /// </summary>
        private void OnMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                DataReader reader = args.GetDataReader();
                uint size = reader.UnconsumedBufferLength;
                if (size == 0) return;

                string json = reader.ReadString(size);

                BeaconPayload beacon = Parse(json);
                if (beacon == null) return;
                if (beacon.Service != "whatsapp-wp8-adapter") return;
                if (beacon.Port <= 0) return;

                string address = args.RemoteAddress == null ? "" : args.RemoteAddress.RawName;
                if (string.IsNullOrEmpty(address)) return;

                if (AddOrUpdate(address, beacon)) RaiseServersChanged();
            }
            catch (Exception ex)
            {
                // Un datagramma malformato non deve fermare l'ascolto, ma un
                // guasto che si ripete a ogni beacon va visto una volta.
                Diag.Failed("DiscoveryService.OnMessageReceived", ex);
            }
        }

        private bool AddOrUpdate(string address, BeaconPayload beacon)
        {
            string name = beacon.Name == null ? "" : beacon.Name;
            string state = beacon.State == null ? "" : beacon.State;
            string account = beacon.Account == null ? "" : beacon.Account;

            lock (_gate)
            {
                Prune();
                for (int i = 0; i < _servers.Count; i++)
                {
                    DiscoveredServer known = _servers[i];
                    if (known.Address != address) continue;

                    bool changed = known.Port != beacon.Port
                        || known.Name != name
                        || known.State != state
                        || known.AccountJid != account;

                    known.Port = beacon.Port;
                    known.Name = name;
                    known.State = state;
                    known.AccountJid = account;
                    known.LastSeen = DateTime.Now;
                    return changed;
                }

                var found = new DiscoveredServer();
                found.Address = address;
                found.Port = beacon.Port;
                found.Name = name;
                found.State = state;
                found.AccountJid = account;
                found.LastSeen = DateTime.Now;
                _servers.Add(found);
                return true;
            }
        }

        private void RaiseServersChanged()
        {
            EventHandler handler = ServersChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static BeaconPayload Parse(string json)
        {
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(BeaconPayload));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return serializer.ReadObject(stream) as BeaconPayload;
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("DiscoveryService.Parse", ex);
                return null;
            }
        }
    }
}
