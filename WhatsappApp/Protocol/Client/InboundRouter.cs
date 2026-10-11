using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// What an incoming node is.
    ///
    /// One member per thing this client can be told, and no member per thing it cannot:
    /// adding a kind is what forces whoever adds it to say what the client does with it,
    /// which is how the login of 2026-10-10 ended - the server pushed the conversations,
    /// the loop looked for `pair-success`, and everything else fell on the floor without a
    /// line in the log.
    /// </summary>
    public enum InboundKind
    {
        /// <summary>The server asking whether this connection is still alive. Answered, not read.</summary>
        Probe,

        /// <summary>The server saying this connection is over (`failure`, `stream:error`, `xmlstreamend`).</summary>
        Refusal,

        /// <summary>A message: text, media, or a protocol message such as the history bootstrap.</summary>
        Message,

        /// <summary>A delivery or read receipt for something this phone sent.</summary>
        Receipt,

        /// <summary>The other side is typing, or has stopped typing.</summary>
        ChatState,

        /// <summary>Someone is online, or has gone offline.</summary>
        Presence,

        /// <summary>An incoming call offer, or its outcome.</summary>
        Call,

        /// <summary>The server's acknowledgement of a stanza this phone sent.</summary>
        Ack,

        /// <summary>An `ib`/`notification`: the channel that carries encrypt, media retry, server sync.</summary>
        Notification,

        /// <summary>The login worked (`success`).</summary>
        Success,

        /// <summary>Something this client has no answer for. Named in the log, never guessed at.</summary>
        Ignored
    }

    /// <summary>
    /// The classifier: one node in, one meaning out.
    ///
    /// Its table is the reference's own registration list and not a reading of the
    /// protocol (`@whiskeysockets/baileys`, `Socket/messages-recv.js`, the `ws.on('CB:...')`
    /// registrations), and the order matters in exactly one place: an `iq` is the liveness
    /// probe or it is nothing, because the probe is an `iq` and is answered before anything
    /// else is asked of the node.
    /// </summary>
    public static class InboundRouter
    {
        /// <summary>What the node is.</summary>
        public static InboundKind Classify(BinaryNode node)
        {
            if (node == null) return InboundKind.Ignored;

            if (node.Tag == "iq") return SessionKeepAlive.IsPing(node) ? InboundKind.Probe : InboundKind.Ignored;

            if (node.Tag == "failure") return InboundKind.Refusal;
            if (node.Tag == "stream:error") return InboundKind.Refusal;
            if (node.Tag == "xmlstreamend") return InboundKind.Refusal;

            if (node.Tag == "message") return InboundKind.Message;
            if (node.Tag == "receipt") return InboundKind.Receipt;
            if (node.Tag == "chatstate") return InboundKind.ChatState;
            if (node.Tag == "presence") return InboundKind.Presence;
            if (node.Tag == "call") return InboundKind.Call;
            if (node.Tag == "ack") return InboundKind.Ack;
            if (node.Tag == "success") return InboundKind.Success;
            if (node.Tag == "ib" || node.Tag == "notification") return InboundKind.Notification;

            return InboundKind.Ignored;
        }

        /// <summary>
        /// Which notification an `ib` carries, or null when it carries none.
        ///
        /// It is the first child's **tag** and not a `type` attribute: the reference's own
        /// dispatch is `switch (child.tag)` over `encrypt`, `mediaretry`, `server_sync`,
        /// `picture`, `w:gp2`, `devices`, `account_sync` and the rest.
        /// </summary>
        public static string NotificationType(BinaryNode node)
        {
            if (node == null) return null;
            if (node.Tag != "ib" && node.Tag != "notification") return null;

            var children = node.Children();
            if (children == null || children.Count == 0) return null;

            return children[0].Tag;
        }
    }
}
