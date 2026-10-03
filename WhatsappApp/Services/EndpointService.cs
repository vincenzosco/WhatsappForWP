using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Web.Http;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The public address of the shared service, read from a file on GitHub
    /// instead of compiled into the app.
    ///
    /// Why it is not a constant: the service sits behind a bore.pub tunnel, and
    /// bore assigns a new port on every restart. An address written into the code
    /// would be wrong within a day, and fixing it would need an app update on
    /// every phone. The tunnel script rewrites the file; the app reads it again.
    ///
    /// If the network does not answer, the last successfully read address is used,
    /// and only if there never was one is the failure declared: a phone that has
    /// already talked to the service must be able to try again even if GitHub is
    /// not answering at that moment.
    /// </summary>
    public sealed class EndpointService
    {
        private static EndpointService _instance;

        public static EndpointService Instance
        {
            get
            {
                if (_instance == null) _instance = new EndpointService();
                return _instance;
            }
        }

        /// <summary>The address file, in one place only.</summary>
        public const string DefaultUrl =
            "https://raw.githubusercontent.com/vincenzosco/whatsappforwp-endpoint/main/endpoint.json";

        private const string KeyHost = "EndpointHost";
        private const string KeyPort = "EndpointPort";

        private bool _fetching;

        private EndpointService()
        {
        }

        /// <summary>The last address read successfully, or null if there never was one.</summary>
        public DiscoveredServer Cached()
        {
            object hostValue;
            object portValue;
            ApplicationDataContainer settings = ApplicationData.Current.LocalSettings;

            if (!settings.Values.TryGetValue(KeyHost, out hostValue)) return null;
            if (!settings.Values.TryGetValue(KeyPort, out portValue)) return null;

            string host = hostValue == null ? "" : hostValue.ToString();
            int port;
            if (string.IsNullOrEmpty(host) || !int.TryParse(portValue == null ? "" : portValue.ToString(), out port))
            {
                return null;
            }

            var server = new DiscoveredServer();
            server.Address = host;
            server.Port = port;
            server.Name = Loc.Get("EndpointService_PublicName", "Public server");
            return server;
        }

        /// <summary>
        /// The address to use now: the one just read, or the last good one. Null
        /// only when there is neither network nor memory.
        /// </summary>
        public async Task<DiscoveredServer> ResolveAsync()
        {
            string url = SettingsService.EndpointUrl;
            if (string.IsNullOrEmpty(url)) url = DefaultUrl;

            DiscoveredServer fetched = null;
            if (!_fetching)
            {
                _fetching = true;
                try
                {
                    fetched = await FetchAsync(url);
                }
                catch (Exception ex)
                {
                    Diag.Failed("EndpointService.ResolveAsync", ex);
                }
                finally
                {
                    _fetching = false;
                }
            }

            if (fetched != null)
            {
                // What the endpoint file answered. Diag dedupes, so a retry loop
                // adds one line, not one per attempt.
                Diag.Ok("endpoint " + fetched.Address + ":" + fetched.Port);
                return fetched;
            }

            DiscoveredServer cached = Cached();
            if (cached != null)
            {
                Diag.Failed("EndpointService/fallback",
                    new InvalidOperationException("using the remembered endpoint " + cached.Endpoint));
            }
            return cached;
        }

        private async Task<DiscoveredServer> FetchAsync(string url)
        {
            var client = new HttpClient();
            string text = await client.GetStringAsync(new Uri(url));
            if (string.IsNullOrEmpty(text)) return null;

            JsonObject json = JsonObject.Parse(text);
            if (!json.ContainsKey("host") || !json.ContainsKey("port")) return null;

            string host = json.GetNamedString("host", "").Trim();
            double portValue = json.GetNamedNumber("port", 0);
            int port = (int)portValue;

            if (string.IsNullOrEmpty(host) || port < 1 || port > 65535) return null;

            ApplicationDataContainer settings = ApplicationData.Current.LocalSettings;
            settings.Values[KeyHost] = host;
            settings.Values[KeyPort] = port;

            var server = new DiscoveredServer();
            server.Address = host;
            server.Port = port;
            server.Name = Loc.Get("EndpointService_PublicName", "Public server");
            return server;
        }
    }
}
