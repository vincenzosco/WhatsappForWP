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
    /// Listens to the adapter beacons and keeps the list of those seen in the
    /// last seconds. It shows nothing and throws nothing: whoever uses it decides
    /// what to do with the found servers.
    /// </summary>
    public sealed class DiscoveryService
    {
        /// <summary>UDP port announced by the adapter (DISCOVERY_PORT).</summary>
        public const int Port = 8587;

        /// <summary>Seconds after which an adapter that no longer announces itself disappears.</summary>
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

        /// <summary>Raised on a network thread when the list changes.</summary>
        public event EventHandler ServersChanged;

        private DiscoveryService()
        {
        }

        public bool IsListening
        {
            get { return _socket != null; }
        }

        /// <summary>The adapters known now, most recent first.</summary>
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
        /// Opens the UDP port. If the network or the port does not allow it, it
        /// stays off: manual entry of the address keeps working.
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
                // If the port does not open there is nothing to retry here: the
                // reason and the port are recorded, and the page carries on with
                // manual entry of the address.
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
        /// Waits up to <paramref name="seconds"/> seconds for a single adapter to
        /// be seen. With zero or more than one it returns null: with more than one
        /// the choice is up to the user.
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
        /// A beacon datagram. The reader that arrives here already holds the
        /// datagram: calling LoadAsync on it answers "The operation identifier is
        /// not valid" (0x800710DD) on every received packet, and that is what
        /// filled the log. It is read directly, and it is no longer async: an
        /// async void handler that throws is seen by nobody.
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
                // A malformed datagram must not stop the listening, but a failure
                // that repeats on every beacon must be seen once.
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
