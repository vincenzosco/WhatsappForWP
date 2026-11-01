using System.Collections.Generic;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WABinary;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    public static class PrivacyTokenTests
    {
        [Test]
        public static void QueryUsesTrustedContactShape()
        {
            BinaryNode query = PrivacyTokens.Query(
                "393761539975@s.whatsapp.net", "TOKEN1", 1793522412);

            Assert.AreEqual("iq", query.Tag);
            Assert.AreEqual("@s.whatsapp.net", query.Attrs["to"]);
            Assert.AreEqual("set", query.Attrs["type"]);
            Assert.AreEqual("privacy", query.Attrs["xmlns"]);
            Assert.AreEqual("TOKEN1", query.Attrs["id"]);

            BinaryNode token = query.Child("tokens").Child("token");
            Assert.AreEqual("393761539975@s.whatsapp.net", token.Attrs["jid"]);
            Assert.AreEqual("1793522412", token.Attrs["t"]);
            Assert.AreEqual("trusted_contact", token.Attrs["type"]);
        }

        [Test]
        public static void ReadFindsTrustedContactToken()
        {
            var token = new BinaryNode("token");
            token.Attrs.Add("type", "trusted_contact");
            token.Attrs.Add("t", "1793522412");
            token.Content = new byte[] { 1, 2, 3 };

            var tokens = new BinaryNode("tokens");
            tokens.Content = new List<BinaryNode> { token };

            var answer = new BinaryNode("iq");
            answer.Content = new List<BinaryNode> { tokens };

            PrivacyToken read = PrivacyTokens.Read(answer);
            Assert.IsTrue(read != null);
            Assert.AreEqual("1793522412", read.Timestamp);
            Assert.AreEqual(3, read.Bytes.Length);
        }

        [Test]
        public static void DirectNodeCarriesTrustedContactToken()
        {
            var token = new PrivacyToken();
            token.Timestamp = "1793522412";
            token.Bytes = new byte[] { 9, 8, 7 };

            Credentials us = Credentials.Create(new SignalSessions.CounterRandom());
            Credentials peer = Credentials.Create(new SignalSessions.CounterRandom());
            var store = new SessionStore(new Memory(), new SignalSessions.CounterRandom());
            IList<KeyValuePair<string, SessionRecord>> devices =
                new List<KeyValuePair<string, SessionRecord>>();
            devices.Add(new KeyValuePair<string, SessionRecord>(
                PeerBundleFixture.PeerPhone,
                DeviceDirectory.Session(PeerBundleFixture.PeerPhone,
                    PeerBundleFixture.Bundle(peer, PeerBundleFixture.PeerPhone, true),
                    us, store, new SignalSessions.CounterRandom())));

            BinaryNode node = OutgoingMessage.Node(
                PeerBundleFixture.Peer, "TOKENMSG", Message.Text("hello"),
                OutgoingMessage.TextType, null, devices, 1793522412,
                new SignalSessions.CounterRandom(), token);

            BinaryNode wireToken = node.Child("tctoken");
            Assert.IsTrue(wireToken != null);
            Assert.AreEqual("1793522412", wireToken.Attrs["t"]);
            Assert.AreEqual(3, wireToken.Bytes().Length);
        }

        private sealed class Memory : ISessionStorage
        {
            private readonly Dictionary<string, byte[]> _blobs = new Dictionary<string, byte[]>();

            public bool TryRead(string key, out byte[] value)
            {
                return _blobs.TryGetValue(key, out value);
            }

            public void Write(string key, byte[] value)
            {
                _blobs[key] = value;
            }

            public void Remove(string key)
            {
                _blobs.Remove(key);
            }
        }
    }
}
