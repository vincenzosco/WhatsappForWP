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

        /// <summary>
        /// The probe the server sends, and the answer that keeps the connection.
        ///
        /// The node is not invented: it is the one the phone read at 14:38 on 2026-10-10,
        /// about thirty seconds after the QR appeared and two seconds before the server
        /// closed the connection. The answer is the shape the pairing already uses for
        /// `pair-device` - an `iq` of type `result` under the id that was asked.
        /// </summary>
        [Test]
        public static void TheServersProbeIsRecognisedAndAnsweredById()
        {
            var probe = new BinaryNode("iq");
            probe.Attrs.Add("from", "@s.whatsapp.net");
            probe.Attrs.Add("type", "get");
            probe.Attrs.Add("t", "1791635922");
            probe.Attrs.Add("xmlns", SessionKeepAlive.ProbeNamespace);

            Assert.IsTrue(SessionKeepAlive.IsPing(probe), "the namespace as an attribute is the shape WhatsApp sends");

            // No id: the answer is the keep-alive query, which the server answers in turn.
            BinaryNode answer = SessionKeepAlive.PingAnswer(probe, new SignalSessions.CounterRandom());
            Assert.AreEqual(SessionKeepAlive.PingNamespace, answer.Attrs["xmlns"]);
            Assert.AreEqual("ping", answer.Children()[0].Tag);

            // With an id there is something to answer *by*, and the result says so.
            probe.Attrs.Add("id", "391878761");
            BinaryNode byId = SessionKeepAlive.PingAnswer(probe, new SignalSessions.CounterRandom());
            Assert.AreEqual("iq", byId.Tag);
            Assert.AreEqual("result", byId.Attrs["type"]);
            Assert.AreEqual("391878761", byId.Attrs["id"]);
            Assert.AreEqual("@s.whatsapp.net", byId.Attrs["to"]);
            Assert.IsTrue(byId.Children() == null, "the answer to a probe carries nothing");

            // The plain XMPP form: the namespace on a child. Not what WhatsApp sends today,
            // and refused as a ping would mean a connection closed over a shape difference.
            var child = new BinaryNode("iq");
            child.Attrs.Add("type", "get");
            child.Attrs.Add("id", "7");
            var pingChild = new BinaryNode("ping");
            pingChild.Attrs.Add("xmlns", SessionKeepAlive.ProbeNamespace);
            child.Content = new System.Collections.Generic.List<BinaryNode> { pingChild };

            Assert.IsTrue(SessionKeepAlive.IsPing(child), "the namespace on a child is a ping too");

            // And everything else is not: an ordinary iq is not answered this way, or every
            // request the server makes would get a result that says nothing.
            var query = new BinaryNode("iq");
            query.Attrs.Add("type", "get");
            query.Attrs.Add("xmlns", "md");
            Assert.IsFalse(SessionKeepAlive.IsPing(query), "an md query is not a probe");
            Assert.IsFalse(SessionKeepAlive.IsPing(new BinaryNode("message")), "a message is not a probe");
            Assert.IsFalse(SessionKeepAlive.IsPing(null));
        }

        /// <summary>
        /// Answering is a frame and never a refusal: the first cut of this threw on the probe
        /// WhatsApp actually sends (`a ping with no id cannot be answered`), and the exception
        /// travelled out of the pairing wait - the attempt was aborted by its own answer, with
        /// the QR still on screen. Anything else still has to be refused, or every request the
        /// server makes would get a keep-alive query for a reply.
        /// </summary>
        [Test]
        public static void AnUnanswerableNodeIsRefusedAndAProbeIsAlwaysAnswered()
        {
            var random = new SignalSessions.CounterRandom();

            var probe = new BinaryNode("iq");
            probe.Attrs.Add("type", "get");
            probe.Attrs.Add("xmlns", SessionKeepAlive.ProbeNamespace);

            Assert.AreEqual(SessionKeepAlive.PingNamespace, SessionKeepAlive.PingAnswer(probe, random).Attrs["xmlns"]);

            Assert.Throws<ProtocolException>(delegate
            {
                SessionKeepAlive.PingAnswer(new BinaryNode("message"), random);
            });
        }
    }
}
