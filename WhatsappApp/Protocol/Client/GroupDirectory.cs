using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// A group as the server describes it: its subject, who made it, and every member's JID.
    ///
    /// The three things this client does with it: name a group chat, hand a new message's
    /// sender key to each member's devices, and know when a member left and their sender key
    /// is dead.
    /// </summary>
    public sealed class GroupInfo
    {
        public string Jid;
        public string Subject;
        public string OwnerJid;

        /// <summary>The members, in the order the server named them.</summary>
        public IList<string> Participants = new List<string>();

        /// <summary>
        /// `addressing_mode`, when the group has one: `lid` means the group addresses its
        /// members by their LID and not by their phone number, and it has to be echoed on
        /// every message sent into the group (`whatsmeow`, `send.go`,
        /// `attrs["addressing_mode"]`). Empty when the answer says nothing, and then no
        /// attribute is sent: a guess here is a message the group cannot attribute.
        /// </summary>
        public string AddressingMode;

        /// <summary>What a chat list row shows: the subject, or the JID when it has none.</summary>
        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Subject) ? Jid : Subject; }
        }
    }

    /// <summary>
    /// The group metadata query and the reading of its answer.
    ///
    /// The query is `type="get"`, `xmlns="w:g2"`, to the group's own JID, with one `query`
    /// child carrying `request="interactive"` - and the answer is a `group` node whose
    /// `subject` and `creator` are attributes and whose `participant` children are the
    /// members, each named by its `jid` attribute
    /// (`@whiskeysockets/baileys`, `Socket/groups.js`, `groupQuery` at line 10 and
    /// `extractGroupMetadata` at line 290).
    ///
    /// The `@g.us` server in the JID is what makes a chat a group everywhere else in this
    /// client, so a query for anything else is refused here rather than answered with
    /// somebody's contact card.
    /// </summary>
    public static class GroupDirectory
    {
        /// <summary>The namespace of every group query.</summary>
        public const string NameSpace = "w:g2";

        /// <summary>The server part of a group's JID.</summary>
        public const string GroupServer = "@g.us";

        /// <summary>Whether a JID names a group.</summary>
        public static bool IsGroup(string jid)
        {
            return !string.IsNullOrEmpty(jid)
                && jid.EndsWith(GroupServer, System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The metadata query, with the id the caller waits on: the answer arrives on the
        /// same socket as the messages and the id is what ties it to the wait.
        /// </summary>
        public static BinaryNode Query(string id, string groupJid)
        {
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a group query needs an id to be answered by");
            if (!IsGroup(groupJid)) throw new ProtocolException("not a group: " + groupJid);

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("id", id);
            iq.Attrs.Add("type", "get");
            iq.Attrs.Add("xmlns", NameSpace);
            iq.Attrs.Add("to", groupJid);

            var query = new BinaryNode("query");
            query.Attrs.Add("request", "interactive");

            iq.Content = new List<BinaryNode> { query };

            return iq;
        }

        /// <summary>
        /// The answer as a group. A member whose `jid` the answer does not carry is skipped
        /// rather than added as an empty string: an entry with no JID would be a sender key
        /// handed to nobody, and it would fail later where nothing names the group.
        /// </summary>
        public static GroupInfo Read(string groupJid, BinaryNode answer)
        {
            if (answer == null) throw new ProtocolException("the server answered nothing to the group query");

            BinaryNode group = answer.Child("group");
            if (group == null)
            {
                throw new ProtocolException("the group query answer carries no group: " + answer);
            }

            var info = new GroupInfo();

            // The answer's own `id` is usually the bare group number, without a server: the
            // reference turns it into a JID the same way (`groups.js`, `extractGroupMetadata`,
            // `jidEncode(attrs.id, 'g.us')`), because everything downstream - the sender key,
            // the chat row, the send - is keyed by the JID and not by the number.
            string id = group.Attrs["id"];
            if (string.IsNullOrEmpty(id)) info.Jid = groupJid;
            else if (id.IndexOf('@') >= 0) info.Jid = id;
            else info.Jid = id + GroupServer;
            info.Subject = group.Attrs["subject"];
            info.OwnerJid = group.Attrs["creator"];
            info.AddressingMode = group.Attrs["addressing_mode"];

            IList<BinaryNode> children = group.Children();
            if (children != null)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    if (children[i].Tag != "participant") continue;

                    string jid = children[i].Attrs["jid"];
                    if (string.IsNullOrEmpty(jid)) continue;

                    info.Participants.Add(jid);
                }
            }

            return info;
        }
    }
}
