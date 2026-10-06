using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Web.Http;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The public addresses of the shared service, read from a file on GitHub
    /// instead of compiled into the app.
    ///
    /// Why it is not a constant: the service sits behind a bore.pub tunnel, and
    /// bore assigns a new port on every restart. An address written into the code
    /// would be wrong within a day, and fixing it would need an app update on
    /// every phone. The tunnel script rewrites the file; the app reads it again.
    ///
    /// Why it is a list and not one address: a deployment can run more than one
    /// server, and the app has to keep working when the one it was using goes
    /// down. Every server announces its own address in the same file, under its
    /// own id; the app reads all of them and hands them to AutoConnector, which
    /// tries them in order until one answers. The first address is also written
    /// at the top level, where an app built before the list existed reads it.
    ///
    /// If the network does not answer, the last successfully read list is used,
    /// and only if there never was one is the failure declared: a phone that has
    /// already talked to a server must be able to try again even if GitHub is
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

        /// <summary>The registry file, in one place only.</summary>
        public const string DefaultUrl =
            "https://raw.githubusercontent.com/vincenzosco/whatsappforwp-endpoint/main/endpoint.json";

        private const string KeyHost = "EndpointHost";
        private const string KeyPort = "EndpointPort";
        private const string KeyServers = "EndpointServers";

        private bool _fetching;

        private EndpointService()
        {
        }

        /// <summary>
        /// Every address read successfully, in the order the file lists them
        /// (the preferred one first). Empty if there never was one. The JSON of
        /// the whole list is kept, so a network failure still has all of it and
        /// not only the first address.
        /// </summary>
        public List<DiscoveredServer> CachedAll()
        {
            var result = new List<DiscoveredServer>();

            object stored;
            ApplicationDataContainer settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(KeyServers, out stored) && stored != null)
            {
                string json = stored.ToString();
                if (!string.IsNullOrEmpty(json))
                {
                    try
                    {
                        JsonArray array = JsonArray.Parse(json);
                        for (int i = 0; i < array.Count; i++)
                        {
                            IJsonValue item = array[i];
                            if (item == null || item.ValueType != JsonValueType.Object) continue;
                            DiscoveredServer server = FromJson(item.GetObject(), "server-" + (i + 1));
                            if (server != null) result.Add(server);
                        }
                    }
                    catch (Exception ex)
                    {
                        Diag.Failed("EndpointService/cache-read", ex);
                    }
                }
            }

            // Written by a build that knew only one address: it is still a server.
            if (result.Count == 0)
            {
                DiscoveredServer single = Cached();
                if (single != null) result.Add(single);
            }
            return result;
        }

        /// <summary>
        /// The first address read successfully, or null if there never was one.
        /// Kept because the pairing screen opens one socket of its own and needs
        /// a single address, not a list.
        /// </summary>
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
            server.Id = "public";
            server.Name = Loc.Get("EndpointService_PublicName", "Public server");
            return server;
        }

        /// <summary>
        /// Every address to try now: the file just read, or the last list read.
        /// Never null, and never empty when there is a memory of one.
        /// </summary>
        public async Task<List<DiscoveredServer>> ResolveAllAsync()
        {
            string url = SettingsService.EndpointUrl;
            if (string.IsNullOrEmpty(url)) url = DefaultUrl;

            List<DiscoveredServer> fetched = null;
            if (!_fetching)
            {
                _fetching = true;
                try
                {
                    fetched = await FetchListAsync(url);
                }
                catch (Exception ex)
                {
                    Diag.Failed("EndpointService.ResolveAllAsync", ex);
                }
                finally
                {
                    _fetching = false;
                }
            }

            if (fetched != null && fetched.Count > 0)
            {
                // What the registry answered. Diag dedupes, so a retry loop adds
                // one line, not one per attempt.
                Diag.Ok("endpoint " + fetched.Count + " server(s), first " + fetched[0].Endpoint);
                return fetched;
            }

            List<DiscoveredServer> cached = CachedAll();
            if (cached.Count > 0)
            {
                Diag.Failed("EndpointService/fallback",
                    new InvalidOperationException("using " + cached.Count + " remembered endpoint(s)"));
            }
            return cached;
        }

        /// <summary>
        /// The first address from the registry, or the remembered one. Null only
        /// when there is neither network nor memory.
        /// </summary>
        public async Task<DiscoveredServer> ResolveAsync()
        {
            List<DiscoveredServer> servers = await ResolveAllAsync();
            return servers.Count > 0 ? servers[0] : null;
        }

        /// <summary>
        /// The file as a list. Both shapes are accepted: the `servers` array the
        /// current registry writes, and the single top-level `host`/`port` the
        /// old one wrote (and which the current one still keeps, for an app that
        /// cannot read the list).
        /// </summary>
        private async Task<List<DiscoveredServer>> FetchListAsync(string url)
        {
            var servers = new List<DiscoveredServer>();

            var client = new HttpClient();
            string text = await client.GetStringAsync(new Uri(url));
            if (string.IsNullOrEmpty(text)) return servers;

            JsonObject json;
            try
            {
                json = JsonObject.Parse(text);
            }
            catch (Exception ex)
            {
                Diag.Failed("EndpointService/parse", ex);
                return servers;
            }

            JsonValue listValue = Value(json, "servers");
            if (listValue != null && listValue.ValueType == JsonValueType.Array)
            {
                JsonArray list = listValue.GetArray();
                for (int i = 0; i < list.Count; i++)
                {
                    IJsonValue item = list[i];
                    if (item == null || item.ValueType != JsonValueType.Object) continue;

                    DiscoveredServer server = FromJson(item.GetObject(), "server-" + (i + 1));
                    if (server != null) servers.Add(server);
                }
            }

            DiscoveredServer top = FromJson(json, "public");
            if (top != null && !ContainsAddress(servers, top.Address, top.Port))
            {
                servers.Insert(0, top);
            }

            if (servers.Count > 0) SaveList(servers);
            return servers;
        }

        /// <summary>The whole list, kept as it is, so the next failure still has it all.</summary>
        private static void SaveList(List<DiscoveredServer> servers)
        {
            try
            {
                var array = new JsonArray();
                for (int i = 0; i < servers.Count; i++)
                {
                    var entry = new JsonObject();
                    entry["id"] = JsonValue.CreateStringValue(servers[i].Id == null ? "" : servers[i].Id);
                    entry["name"] = JsonValue.CreateStringValue(servers[i].Name == null ? "" : servers[i].Name);
                    entry["host"] = JsonValue.CreateStringValue(servers[i].Address);
                    entry["port"] = JsonValue.CreateNumberValue(servers[i].Port);
                    array.Add(entry);
                }

                ApplicationDataContainer settings = ApplicationData.Current.LocalSettings;
                settings.Values[KeyServers] = array.Stringify();

                // And the single address stays in step: the pairing screen and
                // an upgrade from the older build both read it.
                settings.Values[KeyHost] = servers[0].Address;
                settings.Values[KeyPort] = servers[0].Port;
            }
            catch (Exception ex)
            {
                // A registry that cannot be remembered is the next attempt's
                // problem, not this one's: the addresses read now are in hand.
                Diag.Failed("EndpointService/cache", ex);
            }
        }

        /// <summary>
        /// One entry, or null when it carries no usable address. A row the file
        /// does not shape as expected is dropped, not thrown over.
        /// </summary>
        private static DiscoveredServer FromJson(JsonObject json, string fallbackId)
        {
            if (json == null) return null;

            string host = ReadString(json, "host", "").Trim();
            int port = ReadInt(json, "port", 0);
            if (string.IsNullOrEmpty(host) || port < 1 || port > 65535) return null;

            var server = new DiscoveredServer();
            server.Address = host;
            server.Port = port;
            server.Id = ReadString(json, "id", fallbackId).Trim();
            server.Name = ReadString(json, "name", "").Trim();
            return server;
        }

        private static bool ContainsAddress(List<DiscoveredServer> servers, string address, int port)
        {
            for (int i = 0; i < servers.Count; i++)
            {
                if (servers[i].Address == address && servers[i].Port == port) return true;
            }
            return false;
        }

        /// <summary>
        /// A string read without trusting the type: JsonObject.GetNamedString
        /// throws when the value is not a string, and this file is written by
        /// several machines.
        /// </summary>
        private static string ReadString(JsonObject json, string key, string fallback)
        {
            JsonValue value = Value(json, key);
            if (value == null || value.ValueType != JsonValueType.String) return fallback;
            return value.GetString();
        }

        private static int ReadInt(JsonObject json, string key, int fallback)
        {
            JsonValue value = Value(json, key);
            if (value == null || value.ValueType != JsonValueType.Number) return fallback;
            return (int)value.GetNumber();
        }

        private static JsonValue Value(JsonObject json, string key)
        {
            if (json == null || !json.ContainsKey(key)) return null;
            return json.GetNamedValue(key, null);
        }
    }
}
