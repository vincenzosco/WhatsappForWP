using System.Collections.Generic;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The driver against a scripted transport: deterministic, offline, and free of the
    /// real server, so a failure here is about the driver and not about the network.
    /// The live probe (`--connect`) is the other half, where the same driver talks to
    /// WhatsApp itself.
    /// </summary>
    public static class WhatsAppSocketTests
    {
        /// <summary>
        /// An arbitrary tag: the driver's job is to read a node, not to know which one
        /// is first (that is the Noise handshake's business, and the live probe showed
        /// the first thing on the wire is not a node at all).
        /// </summary>
        private const string AnyNodeTag = "stream:features";

        [Test]
        public static void The_socket_reads_the_first_node_the_server_sends()
        {
            BinaryNode features = new BinaryNode(AnyNodeTag);
            features.Content = new List<BinaryNode>();

            var transport = new ScriptedTransport(NodeWriter.Encode(features));

            BinaryNode first = new WhatsAppSocket(transport, new SilentLog())
                .ConnectAsync(WhatsAppSocket.DefaultUrl).GetAwaiter().GetResult();

            Assert.IsTrue(transport.Connected);
            Assert.AreEqual(AnyNodeTag, first.Tag);
        }

        [Test]
        public static void The_socket_sends_an_encoded_node()
        {
            var transport = new ScriptedTransport();

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("type", "get");
            new WhatsAppSocket(transport, new SilentLog()).SendAsync(iq).GetAwaiter().GetResult();

            Assert.AreEqual(1, transport.Sent.Count);

            BinaryNode sent = NodeReader.Decode(transport.Sent[0]);
            Assert.AreEqual("iq", sent.Tag);
            Assert.AreEqual("get", sent.Attrs["type"]);
        }

        [Test]
        public static void A_closed_connection_is_reported()
        {
            var transport = new ScriptedTransport();
            transport.Closed = true;

            // GetAwaiter().GetResult() and not Wait(): Wait() reports the failure as an
            // AggregateException, which would hide the protocol error being asserted.
            Assert.Throws<ProtocolException>(delegate
            {
                new WhatsAppSocket(transport, new SilentLog())
                    .ConnectAsync(WhatsAppSocket.DefaultUrl).GetAwaiter().GetResult();
            });
        }

        /// <summary>A transport that hands back scripted frames and records what was sent.</summary>
        private sealed class ScriptedTransport : ITransport
        {
            private readonly Queue<byte[]> _incoming = new Queue<byte[]>();

            public readonly List<byte[]> Sent = new List<byte[]>();
            public bool Connected;
            public bool Closed;

            public ScriptedTransport(params byte[][] incoming)
            {
                for (int i = 0; i < incoming.Length; i++) _incoming.Enqueue(incoming[i]);
            }

            public Task ConnectAsync(string url)
            {
                Connected = true;
                return Done();
            }

            public Task SendAsync(byte[] payload)
            {
                Sent.Add(payload);
                return Done();
            }

            public Task<byte[]> ReceiveAsync()
            {
                if (Closed || _incoming.Count == 0) return Task.FromResult<byte[]>(null);
                return Task.FromResult(_incoming.Dequeue());
            }

            public void Close()
            {
                Closed = true;
            }

            private static Task Done()
            {
                var completion = new TaskCompletionSource<bool>();
                completion.SetResult(true);
                return completion.Task;
            }
        }
    }
}
