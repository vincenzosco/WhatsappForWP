using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Transport;

namespace ProtocolTests
{
    public static class WebSocketHandshakeTests
    {
        [Test]
        public static void TheRequestCarriesTheWebSocketUpgradeAndOrigin()
        {
            var uri = new Uri("wss://web.whatsapp.com/ws/chat?version=1");
            string key = "dGhlIHNhbXBsZSBub25jZQ==";

            string request = WebSocketHandshake.BuildRequest(
                uri, "https://web.whatsapp.com", key);

            Assert.IsTrue(request.StartsWith("GET /ws/chat?version=1 HTTP/1.1\r\n", StringComparison.Ordinal));
            Assert.IsTrue(request.IndexOf("Host: web.whatsapp.com\r\n", StringComparison.Ordinal) >= 0);
            Assert.IsTrue(request.IndexOf("Upgrade: websocket\r\n", StringComparison.Ordinal) >= 0);
            Assert.IsTrue(request.IndexOf("Connection: Upgrade\r\n", StringComparison.Ordinal) >= 0);
            Assert.IsTrue(request.IndexOf("Sec-WebSocket-Version: 13\r\n", StringComparison.Ordinal) >= 0);
            Assert.IsTrue(request.IndexOf("Origin: https://web.whatsapp.com\r\n", StringComparison.Ordinal) >= 0);
            Assert.AreEqual(request,
                WebSocketHandshake.DecodeResponseHeaders(WebSocketHandshake.BuildRequestBytes(
                    uri, "https://web.whatsapp.com", key)));
        }

        [Test]
        public static void TheAcceptValueMatchesTheRfc6455Vector()
        {
            Assert.AreEqual("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=",
                WebSocketHandshake.AcceptForKey("dGhlIHNhbXBsZSBub25jZQ=="));
        }

        [Test]
        public static void AValidUpgradeResponseIsAccepted()
        {
            const string response =
                "HTTP/1.1 101 Switching Protocols\r\n"
                + "cOnNeCtIoN: keep-alive, Upgrade\r\n"
                + "Upgrade: WebSocket\r\n"
                + "Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n";

            WebSocketHandshake.ValidateResponse(response, "dGhlIHNhbXBsZSBub25jZQ==");
        }

        [Test]
        public static void AResponseWithTheWrongStatusOrAcceptIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketHandshake.ValidateResponse("HTTP/1.1 200 OK\r\n\r\n",
                    "dGhlIHNhbXBsZSBub25jZQ==");
            });

            const string wrongAccept =
                "HTTP/1.1 101 Switching Protocols\r\n"
                + "Connection: Upgrade\r\n"
                + "Upgrade: websocket\r\n"
                + "Sec-WebSocket-Accept: wrong\r\n\r\n";

            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketHandshake.ValidateResponse(wrongAccept, "dGhlIHNhbXBsZSBub25jZQ==");
            });
        }

        [Test]
        public static void ResponseHeadersRejectNonAsciiBytes()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketHandshake.DecodeResponseHeaders(new byte[] { 0x80 });
            });
        }

        [Test]
        public static void TheRequestRefusesInvalidKeysAndHeaderInjection()
        {
            var uri = new Uri("wss://web.whatsapp.com/ws/chat");

            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketHandshake.BuildRequest(uri, "https://web.whatsapp.com", "not-base64");
            });

            Assert.Throws<ProtocolException>(delegate
            {
                WebSocketHandshake.BuildRequest(uri, "https://web.whatsapp.com\r\nX-Leak: yes",
                    "dGhlIHNhbXBsZSBub25jZQ==");
            });
        }
    }
}
