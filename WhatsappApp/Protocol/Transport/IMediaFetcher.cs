using System.Collections.Generic;
using System.Threading.Tasks;

namespace WhatsappApp.Protocol.Transport
{
    /// <summary>
    /// The two HTTP calls the media path needs: a GET for a blob and a POST of one.
    ///
    /// It is an interface for the same reason <see cref="ITransport"/> is: the core compiles
    /// twice - into the phone, where the answer is WinRT's `HttpClient`, and into the desktop
    /// test host, where it is a scripted dictionary of URLs to bytes - and a `HttpClient`
    /// inside `Protocol/Media` would make the download path untestable on the machine that
    /// writes it.
    ///
    /// Nothing here decides *what* to fetch: the URL comes off the message the server signed,
    /// and this only carries the bytes.
    /// </summary>
    public interface IMediaFetcher
    {
        /// <summary>The bytes at a URL, or a <see cref="ProtocolException"/> naming it.</summary>
        Task<byte[]> GetAsync(string url);

        /// <summary>
        /// A body POSTed to a URL, and the answer's bytes. The media upload is the only
        /// caller: WhatsApp takes the encrypted file as one `application/octet-stream`
        /// body and answers a small JSON object naming where it now lives.
        /// </summary>
        Task<byte[]> PostAsync(string url, byte[] body, IList<KeyValuePair<string, string>> headers);
    }
}
