using System.Threading.Tasks;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Drives the conversation with WhatsApp: connects, reads nodes, writes nodes.
    ///
    /// It deliberately knows nothing about WebSockets or WinRT - that is the
    /// transport's half - and nothing about logins or messages yet. What it does own
    /// is the one rule that makes the rest testable: a node is decoded from a whole
    /// frame, and a frame that does not decode is an exception rather than a node
    /// nobody understands.
    ///
    /// **This is a driver and not yet the handshake.** The plan expected the server's
    /// first message to be a `<stream:features>` node; the live probe showed it is 4
    /// bytes that are neither a node nor a protobuf nor a length-prefixed frame, and
    /// the reference explains why: the first exchange is a protobuf
    /// `HandshakeMessage`, and the Noise handshake travels inside it, not as WABinary.
    /// The handshake therefore belongs to `Noise/` and `WaProto/`, and this class
    /// stays the thing that reads and writes nodes once there is a channel to do it
    /// on. See the plan's "What execution changed about this plan".
    /// </summary>
    public sealed class WhatsAppSocket
    {
        /// <summary>The endpoint WhatsApp Web speaks on.</summary>
        public const string DefaultUrl = "wss://web.whatsapp.com/ws/chat";

        private readonly ITransport _transport;
        private readonly ILog _log;

        public WhatsAppSocket(ITransport transport, ILog log)
        {
            if (transport == null) throw new ProtocolException("a socket needs a transport");

            _transport = transport;
            _log = log == null ? new SilentLog() : log;
        }

        /// <summary>
        /// Connects and returns the first node the server sends.
        ///
        /// That node is not the first thing on the wire: the Noise handshake runs
        /// before any WABinary node, so a caller reaches this only once `Noise/`'s
        /// handshake has put the channel up. It is here because the transport and the
        /// codec are what it exercises, and those are one milestone of their own.
        /// </summary>
        public async Task<BinaryNode> ConnectAsync(string url)
        {
            await _transport.ConnectAsync(url);
            _log.Info("whatsapp: connected to " + url);

            BinaryNode first = await ReadNodeAsync();
            _log.Info("whatsapp: first node is <" + first.Tag + ">");
            return first;
        }

        /// <summary>Reads one node, refusing an empty frame instead of decoding it.</summary>
        public async Task<BinaryNode> ReadNodeAsync()
        {
            byte[] payload = await _transport.ReceiveAsync();
            if (payload == null || payload.Length == 0)
            {
                throw new ProtocolException("the server closed the connection");
            }

            BinaryNode node = NodeReader.Decode(payload);
            _log.Info("whatsapp: read <" + node.ToString() + "> (" + payload.Length + " bytes)");
            return node;
        }

        /// <summary>Sends one node.</summary>
        public Task SendAsync(BinaryNode node)
        {
            if (node == null) throw new ProtocolException("nothing to send");

            return _transport.SendAsync(NodeWriter.Encode(node));
        }

        public void Close()
        {
            _transport.Close();
        }
    }
}
