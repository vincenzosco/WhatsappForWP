using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// What a companion says about *itself* after the login: that it is here, and which
    /// session it is.
    ///
    /// Why these nodes exist and are not decoration: the login payload of a companion asks
    /// to be brought up **passively** (`ClientPayload.Login` sends `passive` and `pull`),
    /// and the reference then says the opposite on the connection the server answered - an
    /// `iq` on the `passive` namespace whose child is `active`
    /// (`@whiskeysockets/baileys`, `Socket/socket.js`, the `CB:success` handler:
    /// `await sendPassiveIq('active')`). A device that logs in and then says nothing stays
    /// the passive half of its own login, which is exactly what this phone did on
    /// 2026-10-17: `diag.log` shows the login, two `ib` nodes, and the server hanging up
    /// about two seconds later, at every single start.
    ///
    /// The `unified_session` is the reference's own telemetry
    /// (`Socket/socket.js`, `sendUnifiedSession`): an id derived from the clock, sent on an
    /// `ib` node, which is the same channel the server's bootstrap arrives on.
    ///
    /// Nothing here reads an account or a key: these are sentences about the connection,
    /// which is why they live beside the keep-alive and not in the connection loop.
    /// </summary>
    public static class ClientState
    {
        /// <summary>The namespace the `active` command travels on.</summary>
        public const string PassiveNamespace = "passive";

        /// <summary>The command a companion that just logged in sends: it is not passive any more.</summary>
        public const string Active = "active";

        /// <summary>The `ib` child that says this client is too old for multi-device: the reference ends the connection on it.</summary>
        public const string Downgrade = "downgrade_webclient";

        /// <summary>The `ib` child that closes the server's bootstrap: the last backlog frame has been sent.</summary>
        public const string Offline = "offline";

        /// <summary>The `ib` child that asks how much backlog this client wants.</summary>
        public const string OfflinePreview = "offline_preview";

        /// <summary>The `ib` child that carries the server's routing addresses for this client.</summary>
        public const string EdgeRouting = "edge_routing";

        /// <summary>The `ib` child that answers <see cref="OfflinePreview"/>.</summary>
        public const string OfflineBatchChild = "offline_batch";

        /// <summary>The `ib` child that carries the unified-session telemetry.</summary>
        public const string UnifiedSessionChild = "unified_session";

        /// <summary>How much backlog this client asks for when the server offers a preview.</summary>
        public const int OfflineBatchCount = 100;

        /// <summary>
        /// The command that takes this companion out of the passive half of its login:
        /// `iq` on the `passive` namespace, one child carrying the command, and an id
        /// because the server answers an `iq`.
        /// </summary>
        public static BinaryNode PassiveIq(string command, IRandomSource random)
        {
            if (string.IsNullOrEmpty(command)) throw new ProtocolException("a passive iq without a command says nothing");
            if (random == null) throw new ProtocolException("a passive iq needs randomness for its id");

            var child = new BinaryNode(command);

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", PairingSession.ServerJid);
            iq.Attrs.Add("type", "set");
            iq.Attrs.Add("xmlns", PassiveNamespace);
            iq.Attrs.Add("id", Hex(random.Bytes(8)));
            iq.Content = new List<BinaryNode> { child };

            return iq;
        }

        /// <summary>
        /// The backlog this client is willing to take, in answer to the server's
        /// `offline_preview` (`Socket/socket.js`, the `CB:ib,,offline_preview` handler).
        ///
        /// It is an answer and not a hint: a client that is offered a preview and says
        /// nothing is a client the server has no size for, which is the shape the log of
        /// 2026-10-17 shows - an `ib` arriving and nothing going back.
        /// </summary>
        public static BinaryNode OfflineBatch()
        {
            return OfflineBatch(OfflineBatchCount);
        }

        /// <summary>The same answer with a count of its own, which is what a test can pin.</summary>
        public static BinaryNode OfflineBatch(int count)
        {
            if (count <= 0) throw new ProtocolException("a backlog batch asks for at least one message");

            var child = new BinaryNode(OfflineBatchChild);
            child.Attrs.Add("count", count.ToString());

            var ib = new BinaryNode("ib");
            ib.Content = new List<BinaryNode> { child };

            return ib;
        }

        /// <summary>
        /// The unified-session telemetry: the id the reference derives from the clock
        /// (`Socket/socket.js`, `sendUnifiedSession` and `getUnifiedSessionId`), on the one
        /// channel the server's bootstrap also uses.
        /// </summary>
        public static BinaryNode UnifiedSession(DateTime now)
        {
            var child = new BinaryNode(UnifiedSessionChild);
            child.Attrs.Add("id", UnifiedSessionId(now));

            var ib = new BinaryNode("ib");
            ib.Content = new List<BinaryNode> { child };

            return ib;
        }

        /// <summary>
        /// The id itself: now, three days ahead, taken modulo a week - the reference's own
        /// arithmetic, kept as numbers so the week it wraps in is visible here.
        /// </summary>
        public static string UnifiedSessionId(DateTime now)
        {
            const long threeDays = 3L * 24 * 60 * 60 * 1000;
            const long oneWeek = 7L * 24 * 60 * 60 * 1000;

            long milliseconds = (long)(now.ToUniversalTime()
                - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;

            return ((milliseconds + threeDays) % oneWeek).ToString();
        }

        /// <summary>
        /// Whether this notification is the server refusing **this client** rather than
        /// telling it something: `<ib><downgrade_webclient/></ib>`, which the reference
        /// answers by ending the connection with a multi-device mismatch
        /// (`Socket/socket.js`: `ws.on('CB:ib,,downgrade_webclient', ...)`).
        ///
        /// Why it is a rule of its own: on the phone this arrived as an anonymous `ib`, was
        /// written down as "nothing reads it yet", and the connection ended - so the one
        /// line that said why was the one line nobody read.
        /// </summary>
        public static bool IsClientRefusal(BinaryNode node)
        {
            string type = InboundRouter.NotificationType(node);
            return type == Downgrade;
        }

        private static string Hex(byte[] bytes)
        {
            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));

            return text.ToString();
        }
    }
}
