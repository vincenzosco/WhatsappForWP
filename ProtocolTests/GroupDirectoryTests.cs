using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The group query and its answer, against the reference's own shape
    /// (`@whiskeysockets/baileys`, `Socket/groups.js`, lines 10 to 40 and 290 to 340):
    ///
    /// ```js
    /// const groupQuery = async (jid, type, content) => query({
    ///     tag: 'iq',
    ///     attrs: { type, xmlns: 'w:g2', to: jid },
    ///     content
    /// });
    /// const groupMetadata = async (jid) => groupQuery(jid, 'get', [{ tag: 'query', attrs: { request: 'interactive' } }]);
    /// // ... group.attrs.subject, group.attrs.creator, getBinaryNodeChildren(group, 'participant').map(({ attrs }) => attrs.jid)
    /// ```
    /// </summary>
    public static class GroupDirectoryTests
    {
        public const string GroupJid = "120363000000000000@g.us";

        [Test]
        public static void TheQueryIsTheGroupIq()
        {
            BinaryNode query = GroupDirectory.Query("GRP00001", GroupJid);

            Assert.AreEqual("iq", query.Tag);
            Assert.AreEqual("get", query.Attrs["type"]);
            Assert.AreEqual("w:g2", query.Attrs["xmlns"]);
            Assert.AreEqual(GroupJid, query.Attrs["to"]);
            Assert.AreEqual("GRP00001", query.Attrs["id"]);

            IList<BinaryNode> children = query.Children();
            Assert.IsTrue(children != null && children.Count == 1, "one child, the query");
            Assert.AreEqual("query", children[0].Tag);
            Assert.AreEqual("interactive", children[0].Attrs["request"]);

            Assert.Throws<ProtocolException>(delegate { GroupDirectory.Query("GRP00002", "15551230000@s.whatsapp.net"); });
        }

        [Test]
        public static void AGroupIsToldFromAContact()
        {
            Assert.IsTrue(GroupDirectory.IsGroup(GroupJid));
            Assert.IsFalse(GroupDirectory.IsGroup("15551230000@s.whatsapp.net"));
            Assert.IsFalse(GroupDirectory.IsGroup("status@broadcast"));
            Assert.IsFalse(GroupDirectory.IsGroup(null));
        }

        [Test]
        public static void TheMetadataAnswerBecomesAParticipantList()
        {
            GroupInfo info = GroupDirectory.Read(GroupJid, Answer());

            Assert.AreEqual(GroupJid, info.Jid);
            Assert.AreEqual("Il gruppo", info.Subject);
            Assert.AreEqual("15551230000@s.whatsapp.net", info.OwnerJid);
            Assert.IsTrue(info.Participants.Count == 3, "every member, and only the members");
            Assert.AreEqual("15551230000@s.whatsapp.net", info.Participants[0]);
            Assert.AreEqual("391234567890@s.whatsapp.net", info.Participants[1]);
            Assert.AreEqual("447700900123@s.whatsapp.net", info.Participants[2]);
            Assert.AreEqual("Il gruppo", info.DisplayName);
        }

        [Test]
        public static void AnAnswerWithNoGroupIsRefusedByName()
        {
            var answer = new BinaryNode("iq");
            answer.Attrs.Add("type", "result");

            ProtocolException failed = null;

            try
            {
                GroupDirectory.Read(GroupJid, answer);
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "an answer that is not a group is not a group");
            Assert.IsTrue(failed.Message.Contains("group"), "<" + failed.Message + ">");
        }

        [Test]
        public static void AMemberWithNoJidIsSkipped()
        {
            BinaryNode answer = Answer();
            IList<BinaryNode> group = answer.Child("group").Children();

            var nameless = new BinaryNode("participant");
            nameless.Attrs.Add("type", "admin");
            group.Add(nameless);

            GroupInfo info = GroupDirectory.Read(GroupJid, answer);

            Assert.IsTrue(info.Participants.Count == 3,
                "a participant with no JID is not a member: it would be a sender key handed to nobody");
        }

        /// <summary>The answer as the server sends it: a `group` with its attributes and its members.</summary>
        private static BinaryNode Answer()
        {
            var group = new BinaryNode("group");
            group.Attrs.Add("id", "120363000000000000");
            group.Attrs.Add("subject", "Il gruppo");
            group.Attrs.Add("creator", "15551230000@s.whatsapp.net");
            group.Attrs.Add("s_t", "1760000000");

            var members = new List<BinaryNode>();

            var first = new BinaryNode("participant");
            first.Attrs.Add("jid", "15551230000@s.whatsapp.net");
            first.Attrs.Add("type", "admin");
            members.Add(first);

            var second = new BinaryNode("participant");
            second.Attrs.Add("jid", "391234567890@s.whatsapp.net");
            members.Add(second);

            var third = new BinaryNode("participant");
            third.Attrs.Add("jid", "447700900123@s.whatsapp.net");
            members.Add(third);

            var description = new BinaryNode("description");
            description.Attrs.Add("id", "abc");
            members.Add(description);

            group.Content = members;

            var answer = new BinaryNode("iq");
            answer.Attrs.Add("type", "result");
            answer.Content = new List<BinaryNode> { group };

            return answer;
        }
    }
}
