using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Transport
{
    public static class WebSocketHandshake
    {
        private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

        public static string BuildRequest(Uri uri, string origin, string key)
        {
            if (uri == null || uri.Scheme != "wss" || string.IsNullOrEmpty(uri.Host))
                throw new ProtocolException("a secure WebSocket URI is required");
            if (string.IsNullOrEmpty(origin) || origin.IndexOf('\r') >= 0 || origin.IndexOf('\n') >= 0)
                throw new ProtocolException("the WebSocket origin is invalid");

            ValidateKey(key);

            string host = uri.IsDefaultPort ? uri.Host : uri.Authority;
            string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            return "GET " + path + " HTTP/1.1\r\n"
                + "Host: " + host + "\r\n"
                + "Upgrade: websocket\r\n"
                + "Connection: Upgrade\r\n"
                + "Sec-WebSocket-Key: " + key + "\r\n"
                + "Sec-WebSocket-Version: 13\r\n"
                + "Origin: " + origin + "\r\n\r\n";
        }

        public static int PortFor(Uri uri)
        {
            if (uri == null || !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(uri.Host))
                throw new ProtocolException("a secure WebSocket URI is required");

            return uri.IsDefaultPort || uri.Port < 1 ? 443 : uri.Port;
        }

        public static byte[] BuildRequestBytes(Uri uri, string origin, string key)
        {
            return ToAsciiBytes(BuildRequest(uri, origin, key));
        }

        public static string AcceptForKey(string key)
        {
            ValidateKey(key);
            byte[] digest = Sha1.Hash(ToAsciiBytes(key + WebSocketGuid));
            return Convert.ToBase64String(digest);
        }

        public static string DecodeResponseHeaders(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("the WebSocket response headers are missing");

            var characters = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] > 0x7f)
                    throw new ProtocolException("the WebSocket response headers are not ASCII");
                characters[i] = (char)bytes[i];
            }

            return new string(characters);
        }

        public static void ValidateResponse(string response, string key)
        {
            if (string.IsNullOrEmpty(response))
                throw new ProtocolException("the WebSocket upgrade response is empty");

            string[] lines = response.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] status = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (status.Length < 2 || status[0] != "HTTP/1.1" || status[1] != "101")
                throw new ProtocolException("the server did not switch to WebSocket protocol");

            var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            bool ended = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    ended = true;
                    break;
                }

                int separator = line.IndexOf(':');
                if (separator <= 0 || line[0] == ' ' || line[0] == '\t')
                    throw new ProtocolException("the WebSocket upgrade response has a malformed header");

                string name = line.Substring(0, separator);
                string value = line.Substring(separator + 1).Trim();
                List<string> values;
                if (!headers.TryGetValue(name, out values))
                {
                    values = new List<string>();
                    headers.Add(name, values);
                }
                values.Add(value);
            }

            if (!ended) throw new ProtocolException("the WebSocket upgrade response has no header terminator");
            if (!HasToken(headers, "Upgrade", "websocket"))
                throw new ProtocolException("the WebSocket upgrade response omitted Upgrade");
            if (!HasToken(headers, "Connection", "Upgrade"))
                throw new ProtocolException("the WebSocket upgrade response omitted Connection: Upgrade");

            List<string> accepts;
            if (!headers.TryGetValue("Sec-WebSocket-Accept", out accepts) || accepts.Count != 1
                || accepts[0] != AcceptForKey(key))
                throw new ProtocolException("the WebSocket upgrade accept value is invalid");

            if (HasNonEmptyHeader(headers, "Sec-WebSocket-Extensions")
                || HasNonEmptyHeader(headers, "Sec-WebSocket-Protocol"))
                throw new ProtocolException("the server selected an unrequested WebSocket extension or subprotocol");
        }

        private static byte[] ToAsciiBytes(string value)
        {
            var bytes = new byte[value.Length];
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7f)
                    throw new ProtocolException("the WebSocket handshake contains non-ASCII text");
                bytes[i] = (byte)value[i];
            }
            return bytes;
        }

        private static void ValidateKey(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ProtocolException("the WebSocket key is missing");

            try
            {
                byte[] decoded = Convert.FromBase64String(key);
                if (decoded.Length != 16 || Convert.ToBase64String(decoded) != key)
                    throw new ProtocolException("the WebSocket key is not a canonical 16-byte nonce");
            }
            catch (FormatException)
            {
                throw new ProtocolException("the WebSocket key is not valid base64");
            }
        }

        private static bool HasToken(Dictionary<string, List<string>> headers, string name, string token)
        {
            List<string> values;
            if (!headers.TryGetValue(name, out values)) return false;

            for (int i = 0; i < values.Count; i++)
            {
                string[] tokens = values[i].Split(',');
                for (int j = 0; j < tokens.Length; j++)
                {
                    if (string.Equals(tokens[j].Trim(), token, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static bool HasNonEmptyHeader(Dictionary<string, List<string>> headers, string name)
        {
            List<string> values;
            if (!headers.TryGetValue(name, out values)) return false;

            for (int i = 0; i < values.Count; i++)
            {
                if (values[i].Length > 0) return true;
            }

            return false;
        }
    }
}
