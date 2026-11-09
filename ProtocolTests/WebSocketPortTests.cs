using System;
using WhatsappApp.Protocol.Transport;

namespace ProtocolTests
{
    public static class WebSocketPortTests
    {
        [Test]
        public static void SecureWebSocketWithoutExplicitPortUsesTlsDefault()
        {
            var uri = new Uri("wss://web.whatsapp.com/ws/chat");
            Assert.AreEqual(443, WebSocketHandshake.PortFor(uri));
        }

        [Test]
        public static void SecureWebSocketWithExplicitPortKeepsIt()
        {
            var uri = new Uri("wss://web.whatsapp.com:8443/ws/chat");
            Assert.AreEqual(8443, WebSocketHandshake.PortFor(uri));
        }

        [Test]
        public static void PortLookupRejectsNonSecureAndNullUris()
        {
            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(delegate
            {
                WebSocketHandshake.PortFor(new Uri("http://web.whatsapp.com/ws/chat"));
            });
            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(delegate
            {
                WebSocketHandshake.PortFor(null);
            });
        }
    }
}
