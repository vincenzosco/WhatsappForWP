using System;
using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The sentences a companion owes the server after its login is answered, and the one
    /// notification that refuses the client instead of telling it something.
    ///
    /// Why these are pinned: the phone logged in, received two `ib` nodes, and lost the
    /// connection about two seconds later - four runs in a row - with nothing going back
    /// on the wire. The reference answers that moment with `sendPassiveIq('active')` and the
    /// unified-session telemetry, and the shapes are the reference's own: an `iq` on the
    /// `passive` namespace, and an `ib` carrying the session id.
    /// </summary>
    public static class ClientStateTests
    {
        [Test]
        public static void ThePassiveIqIsTheCommandOnThePassiveNamespace()
        {
            BinaryNode iq = ClientState.PassiveIq(ClientState.Active, new SignalSessions.CounterRandom());

            Assert.AreEqual("iq", iq.Tag);
            Assert.AreEqual("set", iq.Attrs["type"]);
            Assert.AreEqual(ClientState.PassiveNamespace, iq.Attrs["xmlns"]);
            Assert.AreEqual(PairingSession.ServerJid, iq.Attrs["to"]);
            Assert.IsTrue(!string.IsNullOrEmpty(iq.Attrs["id"]), "the server answers an iq by its id");

            Assert.AreEqual(1, iq.Children().Count);
            Assert.AreEqual("active", iq.Children()[0].Tag);
            Assert.IsTrue(iq.Children()[0].Children() == null, "the command child carries nothing");

            // A passive iq with no command is a node that says nothing: it is refused here
            // rather than sent, because the server's answer to an empty one is silence.
            Assert.Throws<ProtocolException>(delegate
            {
                ClientState.PassiveIq(null, new SignalSessions.CounterRandom());
            });

            Assert.Throws<ProtocolException>(delegate
            {
                ClientState.PassiveIq(ClientState.Active, null);
            });
        }

        /// <summary>
        /// The session id is the reference's arithmetic - now plus three days, taken modulo a
        /// week - and the two vectors are dates that make the wrap visible without a clock:
        /// four days after the epoch wraps to zero, and the epoch itself is exactly three
        /// days in.
        /// </summary>
        [Test]
        public static void TheUnifiedSessionIdWrapsAWeekFromThreeDaysAhead()
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            Assert.AreEqual("259200000", ClientState.UnifiedSessionId(epoch));
            Assert.AreEqual("0", ClientState.UnifiedSessionId(epoch.AddDays(4)));
            Assert.AreEqual("86400000", ClientState.UnifiedSessionId(epoch.AddDays(5)));

            // And the node it travels on: an ib, one child, the id on the child.
            DateTime now = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            BinaryNode ib = ClientState.UnifiedSession(now);

            Assert.AreEqual("ib", ib.Tag);
            Assert.AreEqual(1, ib.Children().Count);
            Assert.AreEqual("unified_session", ib.Children()[0].Tag);
            Assert.AreEqual(ClientState.UnifiedSessionId(now), ib.Children()[0].Attrs["id"]);
        }

        [Test]
        public static void TheBacklogBatchAsksForItsCount()
        {
            BinaryNode ib = ClientState.OfflineBatch();

            Assert.AreEqual("ib", ib.Tag);
            Assert.AreEqual(1, ib.Children().Count);
            Assert.AreEqual("offline_batch", ib.Children()[0].Tag);
            Assert.AreEqual(ClientState.OfflineBatchCount.ToString(),
                ib.Children()[0].Attrs["count"]);

            Assert.AreEqual("7", ClientState.OfflineBatch(7).Children()[0].Attrs["count"]);

            Assert.Throws<ProtocolException>(delegate
            {
                ClientState.OfflineBatch(0);
            });
        }

        /// <summary>
        /// Only `downgrade_webclient` is a refusal. The other children are things the server
        /// is telling this client - the end of the backlog, the routing addresses - and
        /// treating them as a refusal would end a working connection at its first bootstrap.
        /// </summary>
        [Test]
        public static void OnlyTheDowngradeNotificationRefusesTheClient()
        {
            Assert.IsTrue(ClientState.IsClientRefusal(Notification(ClientState.Downgrade)));

            // The same child under the other tag the router accepts, because the server
            // sends this channel both ways and the rule is about the child, not the parent.
            var notification = new BinaryNode("notification");
            notification.Content = new List<BinaryNode> { new BinaryNode(ClientState.Downgrade) };
            Assert.IsTrue(ClientState.IsClientRefusal(notification));

            Assert.IsFalse(ClientState.IsClientRefusal(Notification(ClientState.Offline)));
            Assert.IsFalse(ClientState.IsClientRefusal(Notification(ClientState.EdgeRouting)));
            Assert.IsFalse(ClientState.IsClientRefusal(Notification(ClientState.OfflinePreview)));
            Assert.IsFalse(ClientState.IsClientRefusal(new BinaryNode("ib")), "an ib with no child refuses nothing");
            Assert.IsFalse(ClientState.IsClientRefusal(new BinaryNode("message")));
            Assert.IsFalse(ClientState.IsClientRefusal(null));
        }

        private static BinaryNode Notification(string child)
        {
            var ib = new BinaryNode("ib");
            ib.Content = new List<BinaryNode> { new BinaryNode(child) };

            return ib;
        }
    }
}
