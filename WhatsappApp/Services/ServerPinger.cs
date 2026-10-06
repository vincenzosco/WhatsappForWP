using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;

namespace WhatsappApp.Services
{
    /// <summary>
    /// How fast a server answers, measured on the one thing the app can time
    /// without speaking the frame protocol: the TCP connection. The socket is
    /// opened and closed, and the answer is the milliseconds it took, or
    /// NoAnswer when it did not answer within the same deadline every other
    /// connection of the app uses. Nothing here throws: an unreachable server
    /// is a low score, not a failure.
    /// </summary>
    public static class ServerPinger
    {
        /// <summary>The score of a server that did not answer.</summary>
        public const int NoAnswer = -1;

        public static async Task<int> MeasureAsync(string address, int port)
        {
            var socket = new StreamSocket();
            var watch = Stopwatch.StartNew();
            try
            {
                await CommunicationService.ConnectWithDeadlineAsync(
                    socket, new HostName(address), port);
                watch.Stop();
                return (int)watch.ElapsedMilliseconds;
            }
            catch (Exception)
            {
                return NoAnswer;
            }
            finally
            {
                try { socket.Dispose(); }
                catch (Exception) { }
            }
        }
    }
}
