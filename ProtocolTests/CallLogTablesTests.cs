using System.Collections.Generic;
using WhatsappApp.Protocol.Client;

namespace ProtocolTests
{
    /// <summary>
    /// The `call_log` and `call_link` tables, with the lookups the official database
    /// keeps (read out of the APK).
    ///
    /// Four things are worth checking here, and they are four things people get wrong:
    /// a call logged twice is still one row, the list reads newest first, one person is
    /// one chat whatever device they called from, and a call link is identified by its
    /// token.
    /// </summary>
    public static class CallLogTablesTests
    {
        private const string Peer = "15551230000@s.whatsapp.net";
        private const string Other = "15559876543@s.whatsapp.net";

        private static CallLogRow Row(string callId, string user, long timestamp, bool video)
        {
            var row = new CallLogRow();
            row.CallId = callId;
            row.UserJid = user;
            row.Timestamp = timestamp;
            row.IsVideo = video;
            row.Result = 0;
            row.DurationSeconds = 30;

            return row;
        }

        [Test]
        public static void ARowIsFoundByItsCallIdAndKeepsItsParticipants()
        {
            var tables = new CallLogTables();

            CallLogRow row = Row("CALL0001", Peer, 1000, false);
            row.TelecomUuid = "uuid-1";
            row.Participants.Add(Peer);

            long id = tables.Insert(row);

            Assert.AreEqual(1, tables.Count);
            Assert.AreEqual(id, tables.ByCallId("CALL0001").RowId, "the call id finds it");
            Assert.AreEqual(id, tables.ByTelecomUuid("uuid-1").RowId, "and so does the dialer's id");
            Assert.AreEqual(1, tables.Participants(id).Count);
            Assert.IsTrue(tables.ByCallId("NOTACALL") == null, "a call that is not there is null");
        }

        [Test]
        public static void TheLogReadsNewestFirst()
        {
            var tables = new CallLogTables();

            tables.Insert(Row("CALL0001", Peer, 1000, false));
            tables.Insert(Row("CALL0003", Other, 3000, true));
            tables.Insert(Row("CALL0002", Peer, 2000, false));

            IList<CallLogRow> log = tables.ByTimestamp();

            Assert.AreEqual("CALL0003", log[0].CallId, "the newest call is at the top, like a chat list");
            Assert.AreEqual("CALL0002", log[1].CallId);
            Assert.AreEqual("CALL0001", log[2].CallId);
        }

        [Test]
        public static void TheSameCallIsOneRow()
        {
            var tables = new CallLogTables();

            long first = tables.Insert(Row("CALL0001", Peer, 1000, false));
            long again = tables.Insert(Row("CALL0001", Peer, 1000, false));

            Assert.AreEqual(first, again, "the history sync and a call made here can both carry the same call");
            Assert.AreEqual(1, tables.Count);
        }

        [Test]
        public static void APersonIsOneChatWhateverTheDevice()
        {
            var tables = new CallLogTables();

            tables.Insert(Row("CALL0001", "15551230000:5@s.whatsapp.net", 1000, false));
            tables.Insert(Row("CALL0002", Peer, 2000, false));
            tables.Insert(Row("CALL0003", Other, 3000, false));

            IList<CallLogRow> theirs = tables.ByUser(Peer);

            Assert.AreEqual(2, theirs.Count, "the device they called from is not part of the chat's address");
            Assert.AreEqual("CALL0002", theirs[0].CallId, "and that person's calls read newest first too");
        }

        [Test]
        public static void ACallLinkIsItsTokenAndItsCreatorCanChange()
        {
            var tables = new CallLogTables();

            var link = new CallLinkRow();
            link.Token = "TOKEN1";
            link.CreatorJid = "15550000000:1@s.whatsapp.net";

            tables.InsertLink(link);

            var second = new CallLinkRow();
            second.Token = "TOKEN1";
            second.State = "ACTIVE";

            tables.InsertLink(second);

            Assert.AreEqual(1, tables.LinkCount, "one token is one link, even if it is seen twice");
            Assert.AreEqual("ACTIVE", tables.LinkByToken("TOKEN1").State);

            Assert.IsTrue(tables.UpdateLinkCreator("TOKEN1", Peer));
            Assert.IsTrue(tables.LinkByToken("TOKEN1").CreatorJid == Peer,
                "the creator is stored as the person, without the device");

            Assert.IsFalse(tables.UpdateLinkCreator("NOLINK", Peer), "and a link that is not there cannot be changed");
        }
    }
}
