using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The two nodes that keep a quiet connection alive, and the interval that decides when
    /// one is due.
    ///
    /// The ping's child is the whole point of the test: an empty `iq` on the `w:p`
    /// namespace looks right and is answered with nothing, which is indistinguishable from
    /// a connection that has died until the next message fails to arrive.
    /// </summary>
    public static class SessionKeepAliveTests
    {
        [Test]
        public static void ThePingIsTheEmptyIqOnThePingNamespace()
        {
            BinaryNode ping = SessionKeepAlive.Ping(new SignalSessions.CounterRandom());

            Assert.AreEqual("iq", ping.Tag);
            Assert.AreEqual("get", ping.Attrs["type"]);
            Assert.AreEqual(SessionKeepAlive.PingNamespace, ping.Attrs["xmlns"]);
            Assert.AreEqual(PairingSession.ServerJid, ping.Attrs["to"]);
            Assert.IsTrue(!string.IsNullOrEmpty(ping.Attrs["id"]), "the server answers a ping by its id");

            Assert.AreEqual(1, ping.Children().Count);
            Assert.AreEqual("ping", ping.Children()[0].Tag);
            Assert.IsTrue(ping.Children()[0].Children() == null, "the ping child carries nothing");
        }

        [Test]
        public static void ThePresenceNodeIsAvailableAndCarriesTheName()
        {
            BinaryNode presence = SessionKeepAlive.Presence("Vincenzo", SessionKeepAlive.Available);

            Assert.AreEqual("presence", presence.Tag);
            Assert.AreEqual("available", presence.Attrs["type"]);
            Assert.AreEqual("Vincenzo", presence.Attrs["name"]);

            // The reference strips every '@' from the name rather than parsing a JID out of
            // it (`me.name.replace(/@/g, '')`), so a name with one loses it and nothing
            // else changes. This pins that, rather than a nicer rule nobody sends.
            Assert.AreEqual("Vincenzophone",
                SessionKeepAlive.Presence("Vincenzo@phone", SessionKeepAlive.Available).Attrs["name"]);

            Assert.Throws<ProtocolException>(delegate
            {
                SessionKeepAlive.Presence(null, SessionKeepAlive.Available);
            });

            Assert.Throws<ProtocolException>(delegate
            {
                SessionKeepAlive.Presence("Vincenzo", "typing");
            });
        }

        [Test]
        public static void ASilentConnectionIsStaleAfterTheInterval()
        {
            var start = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual(30, SessionKeepAlive.PingSeconds);
            Assert.IsFalse(SessionKeepAlive.IsStale(start, start.AddSeconds(29)));
            Assert.IsFalse(SessionKeepAlive.IsStale(start, start.AddSeconds(30)), "exactly one interval is not late");
            Assert.IsTrue(SessionKeepAlive.IsStale(start, start.AddSeconds(31)));
            Assert.IsFalse(SessionKeepAlive.IsStale(start, start.AddSeconds(-5)),
                "a clock that moved backwards is not a stale connection");
        }
    }
}
