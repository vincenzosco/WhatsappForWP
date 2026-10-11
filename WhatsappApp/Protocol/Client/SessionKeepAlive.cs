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

        /// <summary>
        /// The namespace the server's own liveness probe arrives on.
        ///
        /// A different thing from <see cref="PingNamespace"/>, which is the namespace *we*
        /// ping on: this one comes in, and it has to be answered.
        /// </summary>
        public const string ProbeNamespace = "urn:xmpp:ping";

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
        /// Whether the server is asking whether this connection is still there.
        ///
        /// Both shapes it uses are recognised: the namespace as an attribute on the `iq`
        /// (`&lt;iq from="s.whatsapp.net" type="get" t="..." xmlns="urn:xmpp:ping"/&gt;`,
        /// which is what the phone saw on 2026-10-10) and as a child element, which is the
        /// plain XMPP form.
        ///
        /// Why it matters: the device run of 2026-10-10 was closed by the server about two
        /// seconds after this node arrived. The loop was waiting for `pair-success`, saw an
        /// `iq` that was not one, and read the next frame - which was the server hanging up
        /// (four bytes and then `0x80072EFE`). The QR was still on the screen, and the phone
        /// that scanned it could only say `Impossibile collegare il dispositivo`.
        /// </summary>
        public static bool IsPing(BinaryNode node)
        {
            if (node == null || node.Tag != "iq") return false;
            if (node.Attrs["xmlns"] == ProbeNamespace) return true;

            BinaryNode child = node.Child("ping");
            return child != null && child.Attrs["xmlns"] == ProbeNamespace;
        }

        /// <summary>
        /// What to send back when the server asks whether this connection is alive.
        ///
        /// An `iq` of type `result` only when the probe carries an id, because an id is what
        /// a result is *by* - that answer is the same three attributes the pairing already
        /// sends for `pair-device`, which the server accepts.
        ///
        /// WhatsApp's own probe carries **no id**:
        /// `&lt;iq from="@s.whatsapp.net" type="get" t="..." xmlns="urn:xmpp:ping"/&gt;`, 20
        /// bytes, read on the phone at 14:38 and at 14:45 on 2026-10-10. What it measures is
        /// whether any frame comes back at all, so it is answered with the keep-alive query
        /// itself - a shape the server answers, which proves this client is there in a way no
        /// node invented for the occasion would. (Refusing to answer it, which the first cut
        /// of this did with a sentence about the missing id, aborted the pairing instead: the
        /// exception travelled out of the wait and the page showed its failure sentence.)
        /// </summary>
        public static BinaryNode PingAnswer(BinaryNode probe, IRandomSource random)
        {
            if (!IsPing(probe)) throw new ProtocolException("an answer needs the ping it answers");

            string id = probe.Attrs["id"];
            if (string.IsNullOrEmpty(id)) return Ping(random);

            var answer = new BinaryNode("iq");
            answer.Attrs.Add("id", id);
            answer.Attrs.Add("type", "result");

            // The probe comes from `@s.whatsapp.net`; answering to whoever asked is what
            // keeps this correct if that ever changes.
            string from = probe.Attrs["from"];
            if (!string.IsNullOrEmpty(from)) answer.Attrs.Add("to", from);

            return answer;
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
