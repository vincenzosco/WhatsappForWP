using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// What two people send each other besides the messages: whether one of them is typing,
    /// and whether a message arrived and was read.
    ///
    /// Both shapes are the reference's (`@whiskeysockets/baileys`, `Socket/chats.js` for the
    /// chat state, `Socket/messages-recv.js` for the receipt's `list`), and both are here
    /// rather than in the service because they are rules: what a typing indicator looks like
    /// on the wire, and which ids a receipt answers, are the parts worth a test.
    ///
    /// The typing node carries `from` as well as `to`, which the other nodes this client
    /// sends do not: the reference sets it to the account's own device JID, and the server
    /// is free to drop a chat state that cannot say who is composing.
    /// </summary>
    public static class Conversation
    {
        /// <summary>Someone is writing.</summary>
        public const string Composing = "composing";

        /// <summary>Someone stopped writing, or cleared what they had typed.</summary>
        public const string Paused = "paused";

        /// <summary>
        /// This phone is typing in a chat (or has stopped). `ourJid` is the account's own
        /// device JID, which is what the reference puts in `from`.
        /// </summary>
        public static BinaryNode Typing(string ourJid, string chatJid, string kind)
        {
            if (string.IsNullOrEmpty(chatJid)) throw new ProtocolException("a chat state needs the chat it is about");
            if (kind != Composing && kind != Paused)
            {
                throw new ProtocolException("a chat state is composing or paused, not " + kind);
            }

            var state = new BinaryNode("chatstate");
            if (!string.IsNullOrEmpty(ourJid)) state.Attrs.Add("from", ourJid);
            state.Attrs.Add("to", chatJid);
            state.Content = new List<BinaryNode> { new BinaryNode(kind) };

            return state;
        }

        /// <summary>
        /// Every id a receipt answers: the one in its `id`, plus the `item` children of its
        /// `list` - a receipt for a chat's backlog names them all, which is why a caller that
        /// read only the attribute would leave every message but the first on "delivered".
        /// </summary>
        public static IList<string> Ids(BinaryNode receipt)
        {
            var ids = new List<string>();
            if (receipt == null || receipt.Tag != "receipt") return ids;

            string first = receipt.Attrs["id"];
            if (!string.IsNullOrEmpty(first)) ids.Add(first);

            BinaryNode list = receipt.Child("list");
            if (list == null) return ids;

            var items = list.Children();
            if (items == null) return ids;

            for (int i = 0; i < items.Count; i++)
            {
                string id = items[i].Attrs["id"];
                if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
            }

            return ids;
        }

        /// <summary>The chat a receipt is about.</summary>
        public static string From(BinaryNode receipt)
        {
            if (receipt == null || receipt.Tag != "receipt") return null;

            string from = receipt.Attrs["from"];
            if (!string.IsNullOrEmpty(from)) return from;

            // In a group the receipt names the chat in `to` and the reader in `participant`;
            // the chat is what the app's status belongs to either way.
            return receipt.Attrs["to"];
        }

        /// <summary>
        /// Whether the receipt says the message was read, as opposed to merely delivered.
        /// `read-self` is the same news for a chat's own other device, and it counts.
        /// </summary>
        public static bool IsRead(BinaryNode receipt)
        {
            if (receipt == null) return false;

            string type = receipt.Attrs["type"];
            return type == "read" || type == "read-self";
        }
    }
}
