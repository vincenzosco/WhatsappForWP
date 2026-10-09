using System.Threading.Tasks;

namespace WhatsappApp.Protocol.Transport
{
    /// <summary>
    /// A connection that carries one protocol node at a time.
    ///
    /// The core is written against this instead of a socket so that the same driver
    /// runs in two places: on the phone over WinRT's `MessageWebSocket`, and in the
    /// desktop runner over `ClientWebSocket` - which is how the driver is tested
    /// against the real server without a phone in the loop. Each implementation owns
    /// its own wire framing; the core only sees whole nodes.
    /// </summary>
    public interface ITransport
    {
        /// <summary>Opens the connection. The URL is a `wss://` address.</summary>
        Task ConnectAsync(string url);

        /// <summary>Sends one encoded node.</summary>
        Task SendAsync(byte[] payload);

        /// <summary>Reads one encoded node; throws when the peer has closed.</summary>
        Task<byte[]> ReceiveAsync();

        /// <summary>Closes the connection. Safe to call more than once.</summary>
        void Close();
    }
}
