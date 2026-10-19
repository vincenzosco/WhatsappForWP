using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Transport;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The phone's <see cref="IMediaFetcher"/>: one HTTPS GET for a blob and one POST for an
    /// upload, read whole into memory.
    ///
    /// Whole, and not streamed to a file, because that is what the format needs: a media blob
    /// is one AES-CBC ciphertext with a ten-byte MAC at the end, so its last ten bytes are the
    /// signature of everything before them and the decrypt needs the end before it can start.
    /// A phone that streamed it would have to verify a signature it has not read yet.
    ///
    /// The size is the cost: a 4 MB video is 4 MB of memory for as long as it takes to decode
    /// it, on a device this app already watches for pressure (`MemoryWatcher`). The media
    /// store writes it to the phone's folder and the bytes are dropped (see
    /// `IncomingMediaStore`).
    /// </summary>
    public sealed class WinRtMediaFetcher : IMediaFetcher
    {
        /// <summary>
        /// How long a blob is waited for before the download is given up on. WinRT's
        /// `HttpClient` has no `Timeout` property (the desktop one does), so this is a
        /// `CancellationTokenSource`: without it a download that never answers would park the
        /// stanza reader - the same loop that carries every other message - forever.
        ///
        /// Why not shorter: a 4 MB video over a phone's Wi-Fi is a minute's work on a Lumia.
        /// </summary>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

        public async Task<byte[]> PostAsync(string url, byte[] body,
            System.Collections.Generic.IList<System.Collections.Generic.KeyValuePair<string, string>> headers)
        {
            if (string.IsNullOrEmpty(url)) throw new ProtocolException("there is no address to upload media to");
            if (body == null) throw new ProtocolException("there is nothing to upload");

            var patience = new System.Threading.CancellationTokenSource(Patience);

            try
            {
                using (var client = new Windows.Web.Http.HttpClient())
                using (var content = new Windows.Web.Http.HttpBufferContent(body.AsBuffer()))
                {
                    if (headers != null)
                    {
                        for (int i = 0; i < headers.Count; i++)
                        {
                            // Content headers and request headers are two collections on
                            // this API, and the WP8.1 projection has no SendAsync to put
                            // a request together with: `Origin` is a request header, so it
                            // goes on the client's defaults (the content one collects
                            // only the Content-* names).
                            if (headers[i].Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                            {
                                content.Headers.TryAppendWithoutValidation(headers[i].Key, headers[i].Value);
                            }
                            else
                            {
                                client.DefaultRequestHeaders.TryAppendWithoutValidation(headers[i].Key, headers[i].Value);
                            }
                        }
                    }

                    Windows.Web.Http.HttpResponseMessage response =
                        await client.PostAsync(new Uri(url), content).AsTask(patience.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        // The body is WhatsApp's own sentence (a JSON error or a line
                        // of text): without it the log says "403" and the next person
                        // has no way to know whether it is the token, the host or the
                        // bytes.
                        string why = "";
                        try
                        {
                            why = await response.Content.ReadAsStringAsync().AsTask(patience.Token);
                        }
                        catch (Exception)
                        {
                            why = "";
                        }

                        throw new ProtocolException("the upload to " + url + " answered "
                            + (int)response.StatusCode
                            + (string.IsNullOrEmpty(why) ? "" : ": " + why));
                    }

                    Windows.Storage.Streams.IBuffer buffer =
                        await response.Content.ReadAsBufferAsync().AsTask(patience.Token);
                    var bytes = new byte[buffer.Length];

                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                    {
                        reader.ReadBytes(bytes);
                    }

                    return bytes;
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception error)
            {
                throw new ProtocolException("the upload to " + url + " failed: " + error.Message, error);
            }
        }

        public async Task<byte[]> GetAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) throw new ProtocolException("there is no address to fetch media from");

            var patience = new System.Threading.CancellationTokenSource(Patience);

            try
            {
                using (var client = new Windows.Web.Http.HttpClient())
                {
                    Windows.Web.Http.HttpResponseMessage response =
                        await client.GetAsync(new Uri(url)).AsTask(patience.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new ProtocolException("the media at " + url + " answered "
                            + (int)response.StatusCode);
                    }

                    Windows.Storage.Streams.IBuffer buffer =
                        await response.Content.ReadAsBufferAsync().AsTask(patience.Token);
                    var bytes = new byte[buffer.Length];

                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                    {
                        reader.ReadBytes(bytes);
                    }

                    return bytes;
                }
            }
            catch (ProtocolException)
            {
                throw;
            }
            catch (Exception error)
            {
                // The name of the place, because a download that fails without saying where
                // from leaves the next person reading the log with nothing to try.
                throw new ProtocolException("the media at " + url + " did not download: " + error.Message, error);
            }
        }
    }
}
