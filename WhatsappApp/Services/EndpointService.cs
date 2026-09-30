using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Web.Http;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// L'indirizzo pubblico del servizio condiviso, letto da un file che sta su
    /// GitHub invece che compilato nell'app.
    ///
    /// Perche' non e' una costante: il servizio sta dietro un tunnel bore.pub, e
    /// bore assegna una porta nuova a ogni riavvio. Un indirizzo scritto nel
    /// codice sarebbe sbagliato entro un giorno, e per correggerlo servirebbe un
    /// aggiornamento dell'app su ogni telefono. Il file lo riscrive lo script
    /// del tunnel; l'app lo rilegge.
    ///
    /// Se la rete non risponde si usa l'ultimo indirizzo letto bene, e solo se
    /// non ce n'e' mai stato uno si dichiara il guasto: un telefono che ha
    /// gia' parlato col servizio deve poterci riprovare anche se GitHub non
    /// risponde in quel momento.
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

        /// <summary>Il file dell'indirizzo, in un posto solo.</summary>
        public const string DefaultUrl =
            "https://raw.githubusercontent.com/vincenzosco/whatsappforwp-endpoint/main/endpoint.json";

        private const string KeyHost = "EndpointHost";
        private const string KeyPort = "EndpointPort";

        private bool _fetching;

        private EndpointService()
        {
        }

        /// <summary>L'ultimo indirizzo letto bene, o null se non ce n'e' mai stato uno.</summary>
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
        /// L'indirizzo da usare adesso: quello appena letto, o l'ultimo buono.
        /// Null solo quando non c'e' ne' rete ne' memoria.
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

            if (fetched != null) return fetched;

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
