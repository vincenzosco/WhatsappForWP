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

        // Il progetto targetta .NET Framework 4.5.1 e il compilatore C# 5 non
        // accetta un entry point asincrono (deve essere void, non Task): la
        // soluzione rispondeva CS0028 (firma errata) + CS5001 (nessun Main) e non
        // si compilava affatto. Il corpo async resta in MainAsync, Main lo
        // attende. Anche check-csharp5.js ora riconosce questo caso.
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

            Console.WriteLine("Avvio server sulla porta " + port + "...");
            Console.WriteLine("In attesa di connessioni...\n");

            try
            {
                _server = new TcpListener(IPAddress.Any, port);
                _server.Start();
                Console.WriteLine("Server avviato! IP locale: " + GetLocalIPAddress());
                Console.WriteLine("I client possono connettersi con: " + GetLocalIPAddress() + ":" + port + "\n");
                Console.WriteLine("───────────────────────────────────────────────\n");

                while (_isRunning)
                {
                    var client = await _server.AcceptTcpClientAsync();
                    _clients.Add(client);

                    var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                    Console.WriteLine("Nuovo client connesso: " + Describe(endpoint));

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
                Console.WriteLine("Errore: " + ex.Message);
                Console.ResetColor();
            }
            finally
            {
                if (_server != null) _server.Stop();
            }

            Console.WriteLine("\nPremi un tasto per uscire...");
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

                    // Read message content
                    var messageData = new byte[messageLength];
                    bytesRead = await ReadExactAsync(stream, messageData, messageLength);
                    if (bytesRead == 0) break;

                    string json = Encoding.UTF8.GetString(messageData);
                    string timestamp = DateTime.Now.ToString("HH:mm:ss");

                    // Try to extract and display the message
                    Console.WriteLine("[" + timestamp + "] Messaggio ricevuto (" + json.Length + " byte)");
                    Console.WriteLine("   " + json.Substring(0, Math.Min(json.Length, 150)) + "\n");

                    // Broadcast to all other connected clients
                    await BroadcastMessageAsync(json, client);
                }
            }
            catch (Exception ex)
            {
                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                Console.WriteLine("Client disconnesso: " + Describe(endpoint) + " (" + ex.Message + ")");
            }
            finally
            {
                _clients.Remove(client);
                client.Close();
                Console.WriteLine("Client rimosso. Connessioni attive: " + _clients.Count + "\n");
            }
        }

        private static async Task BroadcastMessageAsync(string json, TcpClient sender)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] lengthPrefix = BitConverter.GetBytes(data.Length);

            var deadClients = new List<TcpClient>();

            foreach (var client in _clients)
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
                _clients.Remove(dead);
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
