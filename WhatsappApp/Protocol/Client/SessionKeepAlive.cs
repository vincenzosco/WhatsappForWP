using System;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// What keeps a connection a connection: a ping the server answers, and the presence
    /// node that says this phone is here.
    ///
    /// Both shapes are the reference's (`@whiskeysockets/baileys`): the sender's keep-alive
    /// is an `iq` with `xmlns` `w:p` carrying one `ping` child - not an empty iq, which is
    /// the shape a first guess produces and the server ignores - and presence is
    /// `presence` with `type` and the account's own `name`.
    ///
    /// The name is not decoration: a phone that has not told the server who it is cannot go
    /// available, and the reference refuses to send the node at all without one. The same
    /// refusal is here, with a sentence, instead of a node the server drops.
    /// </summary>
    public static class SessionKeepAlive
    {
        /// <summary>How long a silent connection is tolerated before a ping is due.</summary>
        public const int PingSeconds = 30;

        /// <summary>The namespace of the keep-alive query.</summary>
        public const string PingNamespace = "w:p";

        /// <summary>This phone is here and can be sent to.</summary>
        public const string Available = "available";

        /// <summary>And this is it leaving.</summary>
        public const string Unavailable = "unavailable";

        /// <summary>
        /// The keep-alive: an `iq` the server answers, which is the only thing that tells a
        /// phone the connection is still there when nothing else is moving.
        /// </summary>
        public static BinaryNode Ping(IRandomSource random)
        {
            if (random == null) throw new ProtocolException("a ping needs randomness for its id");

            var ping = new BinaryNode("ping");

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("to", PairingSession.ServerJid);
            iq.Attrs.Add("type", "get");
            iq.Attrs.Add("xmlns", PingNamespace);
            iq.Attrs.Add("id", Hex(random.Bytes(8)));
            iq.Content = new System.Collections.Generic.List<BinaryNode> { ping };

            return iq;
        }

        /// <summary>
        /// Presence, with the account's own name on it. `available` and `unavailable` are
        /// the two the reference sends with a name; anything else is a chat state and
        /// belongs to a conversation, not here.
        /// </summary>
        public static BinaryNode Presence(string pushName, string type)
        {
            if (type != Available && type != Unavailable)
            {
                throw new ProtocolException("presence is available or unavailable, not " + type);
            }

            if (string.IsNullOrEmpty(pushName))
            {
                throw new ProtocolException("a phone that has not said who it is cannot go " + type);
            }

            var presence = new BinaryNode("presence");
            presence.Attrs.Add("name", pushName.Replace("@", string.Empty));
            presence.Attrs.Add("type", type);

            return presence;
        }

        /// <summary>
        /// Whether nothing has been heard for longer than the interval: a socket that has
        /// been quiet for longer than a ping is a socket worth pinging, and one that has
        /// been quiet far longer than that has a peer that is gone.
        /// </summary>
        public static bool IsStale(DateTime lastNode, DateTime now)
        {
            if (now < lastNode) return false;

            return (now - lastNode) > TimeSpan.FromSeconds(PingSeconds);
        }

        private static string Hex(byte[] bytes)
        {
            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));

            return text.ToString();
        }
    }
}
