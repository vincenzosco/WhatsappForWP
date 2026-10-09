using System;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The one thing here that is not offline: it opens the real endpoint and reads
    /// what WhatsApp sends first.
    ///
    /// It exists because "the codec is right" and "the codec reads WhatsApp" are two
    /// different claims, and only the second one matters. Running it from this machine
    /// answers the framing question with evidence - one WebSocket message is one node,
    /// or the node behind a length prefix - instead of a guess that would later cost a
    /// device round trip. Run it with `node tools/test-protocol.js --connect`.
    /// </summary>
    public static class LiveProbe
    {
        private const int TimeoutMilliseconds = 20000;

        public static int Run()
        {
            var transport = new DesktopTransport(TimeoutMilliseconds);

            try
            {
                Console.WriteLine("connecting to " + WhatsAppSocket.DefaultUrl);
                transport.ConnectAsync(WhatsAppSocket.DefaultUrl).Wait();
                Console.WriteLine("connected, waiting for the first message");

                byte[] raw = transport.ReceiveAsync().Result;
                if (raw == null)
                {
                    Console.WriteLine("PROBE FAILED: the server closed the connection without sending");
                    return 1;
                }

                Console.WriteLine("first message: " + raw.Length + " bytes");
                Console.WriteLine("first 16 bytes: " + Hex(raw, 16));

                string tag = TryTag(raw);
                if (tag != null)
                {
                    Console.WriteLine("decoded as a node with no length prefix");
                }
                else
                {
                    // The other possibility: a 3-byte big-endian length in front.
                    int announced = (raw[0] << 16) | (raw[1] << 8) | raw[2];
                    Console.WriteLine("no node decoded directly; a 3-byte prefix would announce "
                        + announced + " bytes and " + (raw.Length - 3) + " follow");

                    if (raw.Length > 3) tag = TryTag(Slice(raw, 3, raw.Length - 3));
                    if (tag != null) Console.WriteLine("decoded as a node behind a 3-byte length prefix");
                }

                Console.WriteLine();

                if (tag == null)
                {
                    // The current, expected answer, and the reason the milestone moved:
                    // the first exchange is a protobuf HandshakeMessage carrying the
                    // Noise handshake, not a WABinary node. Nothing is wrong with the
                    // codec - what is wrong is the plan's assumption, and this line is
                    // the evidence for it.
                    Console.WriteLine("PROBE OK (diagnostic): the first message is not a node.");
                    Console.WriteLine("  Next: implement the protobuf HandshakeMessage + Noise XX");
                    Console.WriteLine("  handshake and read the first ENCRYPTED node after it.");
                    return 0;
                }

                Console.WriteLine("PROBE OK: the first wire message decodes to <" + tag + ">");
                return 0;
            }
            catch (System.AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                {
                    Console.WriteLine("PROBE FAILED: " + inner.GetType().Name + ": " + inner.Message);
                }

                return 1;
            }
            catch (Exception error)
            {
                Console.WriteLine("PROBE FAILED: " + error.GetType().Name + ": " + error.Message);

                Exception cause = error.InnerException;
                if (cause != null) Console.WriteLine("  caused by: " + cause.GetType().Name + ": " + cause.Message);

                return 1;
            }
            finally
            {
                transport.Close();
            }
        }

        private static string TryTag(byte[] frame)
        {
            try
            {
                return NodeReader.Decode(frame).Tag;
            }
            catch (ProtocolException)
            {
                return null;
            }
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var copy = new byte[count];
            Buffer.BlockCopy(source, offset, copy, 0, count);
            return copy;
        }

        private static string Hex(byte[] bytes, int count)
        {
            var text = new StringBuilder();
            int take = bytes.Length < count ? bytes.Length : count;

            for (int i = 0; i < take; i++) text.Append(bytes[i].ToString("x2")).Append(' ');
            return text.ToString();
        }

        /// <summary>Writes what the driver reports to the console, for a live run.</summary>
        private sealed class ConsoleLog : ILog
        {
            public void Info(string line)
            {
                Console.WriteLine("  " + line);
            }

            public void Failure(string site, Exception error)
            {
                Console.WriteLine("  FAILED " + site + ": " + error.Message);
            }
        }
    }
}
