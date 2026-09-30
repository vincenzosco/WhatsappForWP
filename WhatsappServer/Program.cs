using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;

namespace WhatsappServer
{
    class Program
    {
        private static TcpListener _server;
        private static List<TcpClient> _clients = new List<TcpClient>();
        private static Dictionary<string, string> _userNames = new Dictionary<string, string>();
        private static bool _isRunning = true;

        /// <summary>
        /// The ceiling of a frame. It is the same number as in
        /// CommunicationService and server.js: the 4-byte prefix is the only thing
        /// the other end controls, and a believed length is an allocation size.
        /// Without this a client announcing 2 GB makes this process allocate
        /// 2 GB, and a misaligned client brings it down wherever it likes.
        /// </summary>
        private const int MaxFrameLength = 8 * 1024 * 1024;

        /// <summary>
        /// _clients is touched by the accept loop and by every client task:
        /// without a lock a broadcast during a connection or a disconnection can
        /// throw "Collection was modified".
        /// </summary>
        private static readonly object _clientsGate = new object();

        private static void AddClient(TcpClient client)
        {
            lock (_clientsGate) { _clients.Add(client); }
        }

        private static void RemoveClient(TcpClient client)
        {
            lock (_clientsGate) { _clients.Remove(client); }
        }

        private static List<TcpClient> SnapshotClients()
        {
            lock (_clientsGate) { return new List<TcpClient>(_clients); }
        }

        private static int ClientCount()
        {
            lock (_clientsGate) { return _clients.Count; }
        }

        // The project targets .NET Framework 4.5.1 and the C# 5 compiler does not
        // accept an async entry point (it must be void, not Task): the solution
        // answered CS0028 (wrong signature) + CS5001 (no Main) and did not compile
        // at all. The async body stays in MainAsync, Main awaits it. check-csharp5.js
        // recognizes this case too now.
        static void Main(string[] args)
        {
            MainAsync(args).GetAwaiter().GetResult();
        }

        private static async Task MainAsync(string[] args)
        {
            int port = 8585;
            int customPort;
            if (args.Length > 0 && int.TryParse(args[0], out customPort))
            {
                port = customPort;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(@"
  __      __      ___.                           ____ ___ 
 /  \    /  \ ____\_ |__   ___________   ____   /    \   \ 
 \   \/\/   // __ \| __ \ /  _ \_  __ \_/ __ \ /  /\  \   \
  \        /\  ___/| \_\ (  <_> )  | \/\  ___/ \  \_\  \   \
   \__/\  /  \___  >___  /\____/|__|    \___  > \______  \__\
        \/       \/    \/                   \/         \/ 
");
            Console.WriteLine("  ========= WhatsApp Community Server =========\n");
            Console.ResetColor();

            Console.WriteLine("Starting the server on port " + port + "...");
            Console.WriteLine("Waiting for connections...\n");

            try
            {
                _server = new TcpListener(IPAddress.Any, port);
                _server.Start();
                Console.WriteLine("Server started. Local IP: " + GetLocalIPAddress());
                Console.WriteLine("Clients can connect to: " + GetLocalIPAddress() + ":" + port + "\n");
                Console.WriteLine("───────────────────────────────────────────────\n");

                while (_isRunning)
                {
                    var client = await _server.AcceptTcpClientAsync();
                    AddClient(client);

                    var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                    Console.WriteLine("New client connected: " + Describe(endpoint));

                    // Handle each client in a separate task
                    var clientId = Guid.NewGuid().ToString("N").Substring(0, 6);
#pragma warning disable 4014
                    Task.Run(() => HandleClientAsync(client, clientId));
#pragma warning restore 4014
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Error: " + ex.Message);
                Console.ResetColor();
            }
            finally
            {
                if (_server != null) _server.Stop();
            }

            Console.WriteLine("\nPress a key to exit...");
            Console.ReadKey();
        }

        private static string Describe(IPEndPoint endpoint)
        {
            if (endpoint == null) return "?";
            return endpoint.Address + ":" + endpoint.Port;
        }

        private static async Task HandleClientAsync(TcpClient client, string clientId)
        {
            try
            {
                var stream = client.GetStream();
                var reader = new BinaryReader(stream);

                while (client.Connected)
                {
                    // Read message length (4 bytes)
                    var lengthBytes = new byte[4];
                    int bytesRead = await ReadExactAsync(stream, lengthBytes, 4);
                    if (bytesRead == 0) break;

                    int messageLength = BitConverter.ToInt32(lengthBytes, 0);

                    // An empty, negative or over-the-ceiling frame is not a
                    // payload: it is a misalignment or a client asking for memory.
                    // This connection is closed instead of believing it.
                    if (messageLength < 1 || messageLength > MaxFrameLength)
                    {
                        Console.WriteLine("Client sent an unusable frame length (" +
                            messageLength + "), connection dropped: " +
                            Describe(client.Client.RemoteEndPoint as IPEndPoint));
                        break;
                    }

                    // Read message content
                    var messageData = new byte[messageLength];
                    bytesRead = await ReadExactAsync(stream, messageData, messageLength);
                    if (bytesRead == 0) break;

                    string json = Encoding.UTF8.GetString(messageData);
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");

                    // Try to extract and display the message
                    Console.WriteLine("[" + timestamp + "] Message received (" + json.Length + " bytes)");
                    Console.WriteLine("   " + json.Substring(0, Math.Min(json.Length, 150)) + "\n");

                    // Broadcast to all other connected clients
                    await BroadcastMessageAsync(json, client);
                }
            }
            catch (Exception ex)
            {
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                Console.WriteLine("Client disconnected: " + Describe(endpoint) + " (" + ex.Message + ")");
            }
            finally
            {
                RemoveClient(client);
                client.Close();
                Console.WriteLine("Client removed. Active connections: " + ClientCount() + "\n");
            }
        }

        private static async Task BroadcastMessageAsync(string json, TcpClient sender)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] lengthPrefix = BitConverter.GetBytes(data.Length);

            var deadClients = new List<TcpClient>();

            foreach (var client in SnapshotClients())
            {
                if (client == sender || !client.Connected) 
                {
                    if (!client.Connected) deadClients.Add(client);
                    continue;
                }

                try
                {
                    var stream = client.GetStream();
                    await stream.WriteAsync(lengthPrefix, 0, lengthPrefix.Length);
                    await stream.WriteAsync(data, 0, data.Length);
                    await stream.FlushAsync();
                }
                catch
                {
                    deadClients.Add(client);
                }
            }

            // Clean up dead clients
            foreach (var dead in deadClients)
            {
                RemoveClient(dead);
            }
        }

        private static async Task<int> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, totalRead, count - totalRead);
                if (read == 0) return totalRead;
                totalRead += read;
            }
            return totalRead;
        }

        private static string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }
    }
}
