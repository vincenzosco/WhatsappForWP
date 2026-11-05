using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.System.Threading;
using WhatsappApp.Protocol.Media;
using WhatsappApp.Services;

namespace WhatsappApp.Protocol.Transport
{
    /// <summary>
    /// The phone's end of a call's media path: one UDP socket, `CallTransport` behind it, and the
    /// clock that repeats a handshake datagram nobody answered.
    ///
    /// Why this file is here and not in `Protocol/Media`: everything in there is portable and is
    /// compiled by `ProtocolTests` on a desktop, and `DatagramSocket` only exists on the phone.
    /// The seam is the same one `WebSocketTransport` uses - the protocol class takes bytes and an
    /// address, this class owns the operating system's end - and `ProtocolTests.csproj` excludes
    /// this file by name for exactly that reason.
    ///
    /// What it carries, in the order a datagram meets it:
    /// - a STUN check goes to the ICE-lite agent, which answers it and nominates a pair;
    /// - the nomination starts the handshake, whose flight goes back to the address that asked;
    /// - the datagrams after it are DTLS records, then SRTP;
    /// - and audio goes out through `Send`, addressed to the nominated peer.
    ///
    /// The retransmission schedule lives in `CallTransport.Tick`, not here: it is a rule about the
    /// handshake, and rules that can be tested on a desktop are kept off the phone's thread pool.
    /// This class only asks it what is due and puts the answer on the wire.
    /// </summary>
    public sealed class CallMediaSocket : IDisposable
    {
        /// <summary>How often the clock is asked; a retransmission is late by at most this much.</summary>
        private const int TickMilliseconds = 100;

        /// <summary>
        /// The largest datagram read at once. A DTLS record never carries more than the peer's
        /// MTU and an SRTP packet less than that; the ceiling is here so a datagram claiming a
        /// gigabyte cannot become an allocation on a phone with 512 MB.
        /// </summary>
        private const int MaxDatagram = 2048;

        /// <summary>One write at a time: a `DataWriter` per datagram, and two at once interleave.</summary>
        private readonly SemaphoreSlim _sending = new SemaphoreSlim(1, 1);

        private readonly object _gate = new object();

        /// <summary>
        /// One thread in the handshake at a time. The socket's handler and the timer both drive
        /// the same `CallTransport`, and neither the transcript nor the retransmission counters
        /// were written for two threads: a hello answered while its flight is being resent is a
        /// transcript the peer does not have.
        /// </summary>
        private readonly object _path = new object();

        private DatagramSocket _socket;
        private CallTransport _transport;
        private ThreadPoolTimer _timer;

        private HostName _remoteAddress;
        private string _remotePort;
        private bool _closed;

        /// <summary>
        /// What has crossed the socket, counted where it crosses: one datagram read from the
        /// wire and one written to it, whatever they carried. A page cannot see the transport's
        /// own counters for STUN and handshake traffic, and those are exactly the ones that say
        /// whether a peer is answering at all.
        /// </summary>
        private int _received;

        private int _sent;

        /// <summary>
        /// How many arrivals with no path attached have been named in the log. Bounded because a
        /// port nobody owns is a port anything on the network can find: the first few say who
        /// found it, and the rest would only say it again.
        /// </summary>
        private const int UnattachedLogs = 4;

        private int _unattachedLogged;

        /// <summary>The UDP port the socket is bound to, which is what a candidate names.</summary>
        public int Port { get; private set; }

        /// <summary>The path this socket drives, or null while no call has attached one.</summary>
        public CallTransport Transport
        {
            get { return _transport; }
        }

        /// <summary>Why the path stopped, or null: the transport's own reason, re-told.</summary>
        public string Failure
        {
            get
            {
                CallTransport transport = _transport;
                return transport == null ? null : transport.Failure;
            }
        }

        /// <summary>
        /// Binds an ephemeral port and starts reading it, so the port an offer carries is held
        /// and answered from the moment it is written down.
        /// </summary>
        public async Task<int> BindAsync()
        {
            DatagramSocket socket = new DatagramSocket();
            socket.MessageReceived += OnMessageReceived;

            await socket.BindServiceNameAsync("0");

            int port;
            if (!int.TryParse(socket.Information.LocalPort, out port) || port <= 0)
            {
                // A socket the platform cannot name a port for is one nobody can reach.
                socket.Dispose();
                throw new ProtocolException("the call media socket answered no port");
            }

            _socket = socket;
            Port = port;

            _timer = ThreadPoolTimer.CreatePeriodicTimer(OnTick,
                TimeSpan.FromMilliseconds(TickMilliseconds));

            Diag.Ok("call media: listening on UDP " + port);

            return port;
        }

        /// <summary>
        /// Puts one call's path behind this socket. The offer and the answer carry the same port
        /// for every call while the app is open, so this replaces the previous call's transport
        /// rather than binding again.
        /// </summary>
        public void Attach(CallTransport transport)
        {
            if (transport == null) throw new ProtocolException("a media socket needs a transport");

            lock (_gate)
            {
                _transport = transport;
            }

            Diag.Ok("call media: the path is attached to UDP " + Port);
        }

        /// <summary>
        /// Zeroes the counters a call reports back. The socket holds its port for the life of the
        /// app and counts what crosses it; without this, the "datagram(s) in" an ended call
        /// reports is every datagram since the app started, and a call nothing ever reached reads
        /// like one that was busy an hour ago.
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _received, 0);
            Interlocked.Exchange(ref _sent, 0);
            Interlocked.Exchange(ref _unattachedLogged, 0);
        }

        /// <summary>
        /// One Opus frame, sealed and sent, or false when the path is not up yet or the peer has
        /// not proved the identity the signalling named. The frame's twenty milliseconds are
        /// what moves the RTP clock.
        /// </summary>
        public bool SendAudio(byte[] opusFrame)
        {
            CallTransport transport = _transport;
            if (transport == null) return false;

            // A handshake can finish without proving who answered it, and audio sent over one
            // is audio given to whoever picked up the port. `CallTransport.IsTrusted` is the
            // question, and the caller of a media path is not the place to ask it.
            if (!transport.IsTrusted) return false;

            byte[] packet;
            lock (_path)
            {
                packet = transport.Send(opusFrame);
            }

            if (packet == null) return false;

            Send(packet);
            return true;
        }

        /// <summary>
        /// Takes one call's path off the socket when the call ends, leaving the port bound for
        /// the next one. The transport is the same object that was attached, so a call that
        /// ended while the next one was starting does not detach its successor.
        /// </summary>
        public void Detach(CallTransport transport)
        {
            if (transport == null) return;

            lock (_gate)
            {
                if (_transport != transport) return;

                _transport = null;
            }

            Diag.Ok("call media: the path is off UDP " + Port);
        }

        /// <summary>
        /// The path as a value, for a page that draws it. Taken under the same locks the reader
        /// and the timer take, so every number in it belongs to one moment instead of to three.
        /// </summary>
        public CallMediaStatus Snapshot()
        {
            CallTransport transport;
            lock (_gate)
            {
                transport = _transport;
            }

            var status = new CallMediaStatus();
            status.Listening = Port > 0;
            status.Port = Port;
            status.Attached = transport != null;
            status.Received = Interlocked.CompareExchange(ref _received, 0, 0);
            status.Sent = Interlocked.CompareExchange(ref _sent, 0, 0);

            // Nothing is attached before a call exists, and a status for a call that does not
            // exist says only that the port is held.
            if (transport == null) return status;

            lock (_path)
            {
                status.State = transport.State;
                status.Trusted = transport.IsTrusted;
                status.Failure = transport.Failure;
                status.Peer = Describe(transport.Peer);
                status.Dropped = transport.Dropped;
                status.NotFromPeer = transport.NotFromPeer;
                status.MediaRefused = transport.MediaRefused;
            }

            return status;
        }

        /// <summary>
        /// An endpoint as a person reads it: four dotted numbers for IPv4, and the eight groups
        /// in hex for the other family - this app offers IPv4 candidates only, but a peer is
        /// not something this side chooses.
        /// </summary>
        private static string Describe(StunEndpoint endpoint)
        {
            if (endpoint == null || endpoint.Address == null) return null;

            byte[] address = endpoint.Address;

            if (address.Length == 4)
            {
                return address[0] + "." + address[1] + "." + address[2] + "." + address[3]
                    + ":" + endpoint.Port;
            }

            var text = new StringBuilder();
            for (int i = 0; i + 1 < address.Length; i += 2)
            {
                if (i > 0) text.Append(':');
                text.Append(((address[i] << 8) | address[i + 1]).ToString("x"));
            }

            return "[" + text + "]:" + endpoint.Port;
        }

        /// <summary>Stops the clock and closes the socket. Safe to call more than once.</summary>
        public void Dispose()
        {
            ThreadPoolTimer timer;
            DatagramSocket socket;

            lock (_gate)
            {
                if (_closed) return;
                _closed = true;

                timer = _timer;
                socket = _socket;
                _timer = null;
                _socket = null;
            }

            if (timer != null) timer.Cancel();

            if (socket != null)
            {
                socket.MessageReceived -= OnMessageReceived;
                socket.Dispose();
            }

            Diag.Ok("call media: the socket on UDP " + Port + " is closed");
        }

        private async void OnMessageReceived(DatagramSocket sender,
            DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                DataReader reader = args.GetDataReader();
                uint length = reader.UnconsumedBufferLength;

                if (length == 0 || length > MaxDatagram)
                {
                    reader.Dispose();
                    return;
                }

                var datagram = new byte[length];
                reader.ReadBytes(datagram);
                reader.Dispose();

                Interlocked.Increment(ref _received);

                int port;
                if (!int.TryParse(args.RemotePort, out port)) return;

                StunEndpoint from = StunEndpoint.Parse(args.RemoteAddress.CanonicalName, port);

                CallTransport transport;
                lock (_gate)
                {
                    transport = _transport;
                    _remoteAddress = args.RemoteAddress;
                    _remotePort = args.RemotePort;
                }

                // Nothing is attached before a call exists: the port is held from the moment the
                // app opens, and a socket that is only holding a port has nothing to answer. It
                // is, however, the answer to "does the peer reach this phone at all", which is
                // the first thing to know about a call that rings nowhere and answers nothing -
                // so the arrival is named instead of dropped in silence.
                if (transport == null)
                {
                    if (Interlocked.Increment(ref _unattachedLogged) <= UnattachedLogs)
                    {
                        Diag.Ok("call media: " + datagram.Length + " byte(s) from " + Describe(from)
                            + " reached UDP " + Port + " with no call attached");
                    }

                    return;
                }

                IList<byte[]> outbound;
                lock (_path)
                {
                    outbound = transport.Receive(datagram, from);
                }

                for (int i = 0; i < outbound.Count; i++)
                {
                    await SendAsync(outbound[i], args.RemoteAddress, args.RemotePort);
                }
            }
            catch (Exception ex)
            {
                // A datagram that cannot be read is one datagram: an exception escaping an event
                // handler on this platform takes the process with it.
                Diag.Failed("call media: a datagram", ex);
            }
        }

        /// <summary>The clock, on the thread pool: ask the transport what is due and send it.</summary>
        private void OnTick(ThreadPoolTimer timer)
        {
            CallTransport transport = _transport;
            if (transport == null) return;

            // The same lock the reader takes: a retransmission must not land in the middle of a
            // handshake message the other thread is hashing.
            byte[] again;
            lock (_path)
            {
                again = transport.Tick(NowMilliseconds());
            }

            if (again == null) return;

            Diag.Ok("call media: the handshake datagram goes again, " + again.Length + " bytes");
            Send(again);
        }

        private void Send(byte[] datagram)
        {
            HostName address;
            string port;

            lock (_gate)
            {
                address = _remoteAddress;
                port = _remotePort;
            }

            if (address == null || port == null) return;

            SendInBackground(datagram, address, port);
        }

        private async void SendInBackground(byte[] datagram, HostName address, string port)
        {
            try
            {
                await SendAsync(datagram, address, port);
            }
            catch (Exception ex)
            {
                Diag.Failed("call media: a send", ex);
            }
        }

        private async Task SendAsync(byte[] datagram, HostName address, string port)
        {
            await _sending.WaitAsync();

            try
            {
                DatagramSocket socket;
                lock (_gate)
                {
                    socket = _socket;
                }

                if (socket == null) return;

                IOutputStream stream = await socket.GetOutputStreamAsync(address, port);

                // A writer per datagram, detached before it is disposed: disposing it while it
                // still owns the stream would close the path the next datagram needs.
                DataWriter writer = new DataWriter(stream);

                try
                {
                    writer.WriteBytes(datagram);
                    await writer.StoreAsync();
                    await writer.FlushAsync();

                    Interlocked.Increment(ref _sent);
                }
                finally
                {
                    // Detached first, or disposing it would close the stream the next datagram
                    // needs; and detached in a `finally`, or a send that throws leaks both.
                    writer.DetachStream();
                    writer.Dispose();
                }
            }
            finally
            {
                _sending.Release();
            }
        }

        /// <summary>
        /// A monotonic count of milliseconds for the retransmission schedule. UtcNow's ticks and
        /// not `TickCount`, which is signed and wraps after twenty-five days.
        /// </summary>
        private static long NowMilliseconds()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }
    }
}
