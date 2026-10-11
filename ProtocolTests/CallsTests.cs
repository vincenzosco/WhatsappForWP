using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The call log, out of the history.
    ///
    /// The shape is `WAProto`'s `CallLogRecord` and the numbers are the reference's own
    /// (`/tmp/ref/whatsmeow-main/proto/waSyncAction/WAWebProtobufSyncAction.pb.go`, the
    /// `CallLogRecord` struct): `callResult` 1, `duration` 4, `startTime` 5, `isIncoming` 6,
    /// `isVideo` 7, `callID` 11, `callCreatorJID` 12, `groupJID` 13, `participants` 14 - and
    /// the participant's `userJID` is its field 1.
    ///
    /// The one that is easy to get wrong is field 12. It is the *creator*, not a peer: for a
    /// call this phone received that is the caller, but for one this phone made it is this
    /// account itself, so an outgoing call whose chat was taken from it was filed under our own
    /// number (`AnOutgoingCallIsThePersonWeCalled`). What is *not* a field is `IsMissed`: it is
    /// `callResult == 4` (`CallLogRecord_CallResult`, where `CONNECTED` is 0, `REJECTED` 1,
    /// `CANCELLED` 2 and `MISSED` 4).
    /// </summary>
    public static class CallsTests
    {
        public const string Peer = "15551230000@s.whatsapp.net";
        public const string Me = "393892672185:5@s.whatsapp.net";

        [Test]
        public static void TheHistoryCallLogBecomesRows()
        {
            HistorySync sync = HistorySync.Decode(Payload());

            IList<CallLogEntryModel> calls = Calls.FromHistory(sync, Me);

            Assert.IsTrue(calls.Count == 2, "two records, two rows");
            Assert.AreEqual("CALL0002", calls[0].Id, "newest first, because a call log reads like a chat");
            Assert.AreEqual("CALL0001", calls[1].Id);

            Assert.IsTrue(calls[0].IsVideo, "the newer one was a video call");
            Assert.IsFalse(calls[0].IsMissed, "and it connected");
            Assert.AreEqual(42, calls[0].Seconds);
            Assert.AreEqual(1760000200L, calls[0].Timestamp);
            Assert.AreEqual(Peer, calls[0].Jid, "a call we received is filed under the caller");

            Assert.IsTrue(calls[1].IsMissed, "the older one was missed: callResult 4");
            Assert.IsFalse(calls[1].IsVideo);
            Assert.AreEqual(0, calls[1].Seconds, "and it has no duration to show");
            Assert.AreEqual(Peer, calls[1].Jid);
        }

        /// <summary>
        /// The bug field 12 caused: on a call this phone made, the creator is this account, so
        /// a reader that treated it as the peer filed the call under our own number.
        /// </summary>
        [Test]
        public static void AnOutgoingCallIsThePersonWeCalled()
        {
            byte[] record = new ProtoWriter()
                .Varint(1, 0)
                .Varint(5, 1760000300)
                .Varint(6, 0)
                .Varint(7, 0)
                .Text(11, "CALL0006")
                .Text(12, Me)
                .Bytes(14, Participant(Me))
                .Bytes(14, Participant(Peer))
                .ToArray();

            IList<CallLogEntryModel> calls = Calls.FromHistory(
                HistorySync.Decode(new ProtoWriter().Varint(1, 0).Bytes(13, record).ToArray()), Me);

            Assert.IsTrue(calls.Count == 1, "one record, one row");
            Assert.AreEqual(Peer, calls[0].Jid,
                "the person we called, not this account's own number");
            Assert.IsFalse(calls[0].IsMissed);
        }

        [Test]
        public static void AGroupCallIsFiledUnderTheGroup()
        {
            byte[] record = new ProtoWriter()
                .Varint(1, 1)
                .Varint(5, 1760000400)
                .Text(11, "CALL0007")
                .Text(12, Peer)
                .Text(13, "120363000000000000@g.us")
                .Bytes(14, Participant(Peer))
                .Bytes(14, Participant(Me))
                .ToArray();

            IList<CallLogEntryModel> calls = Calls.FromHistory(
                HistorySync.Decode(new ProtoWriter().Bytes(13, record).ToArray()), Me);

            Assert.AreEqual("120363000000000000@g.us", calls[0].Jid, "a group call belongs to the group");
        }

        [Test]
        public static void AMissedCallWithNoDurationSaysZero()
        {
            // No duration field at all, which is what a call that never connected has.
            byte[] record = new ProtoWriter()
                .Varint(1, 4)
                .Varint(5, 1760000000)
                .Varint(6, 1)
                .Text(11, "CALL0003")
                .Text(12, Peer)
                .ToArray();

            IList<CallLogEntryModel> calls = Calls.FromHistory(
                HistorySync.Decode(new ProtoWriter().Bytes(13, record).ToArray()), Me);

            Assert.IsTrue(calls.Count == 1, "one record, one row");
            Assert.AreEqual(0, calls[0].Seconds, "a missing duration is a zero, not a crash and not a random");
            Assert.IsTrue(calls[0].IsMissed);
            Assert.IsTrue(calls[0].IsIncoming);
            Assert.AreEqual(Peer, calls[0].Jid, "a record with no participant list still names its caller");
        }

        [Test]
        public static void AnEmptyCallLogIsAnEmptyList()
        {
            Assert.IsTrue(Calls.FromHistory(null, Me).Count == 0);
            Assert.IsTrue(Calls.FromHistory(new HistorySync(), Me).Count == 0, "no records, no rows, no exception");
        }

        [Test]
        public static void ACallWithNobodyInItIsNotARow()
        {
            // Only this account: a call that was made to nobody has no conversation to appear
            // in, and a row for ourselves is worse than no row.
            byte[] record = new ProtoWriter()
                .Varint(1, 0)
                .Varint(5, 1760000000)
                .Text(11, "CALL0004")
                .Text(12, Me)
                .Bytes(14, Participant(Me))
                .ToArray();

            Assert.IsTrue(Calls.FromHistory(
                HistorySync.Decode(new ProtoWriter().Bytes(13, record).ToArray()), Me).Count == 0,
                "a record that names nobody else has no conversation to appear in");
        }

        [Test]
        public static void TheRejectIsTheCallNodeTheServerTakes()
        {
            BinaryNode reject = Calls.Reject("CALL0005", Peer);

            Assert.AreEqual("call", reject.Tag);
            Assert.AreEqual(Peer, reject.Attrs["to"]);
            Assert.AreEqual("CALL0005", reject.Attrs["id"]);

            IList<BinaryNode> children = reject.Children();
            Assert.IsTrue(children != null && children.Count == 1, "one child, the refusal");
            Assert.AreEqual("reject", children[0].Tag);

            Assert.Throws<ProtocolException>(delegate { Calls.Reject(null, Peer); });
            Assert.Throws<ProtocolException>(delegate { Calls.Reject("CALL0005", null); });
        }

        /// <summary>One `CallLogRecord_ParticipantInfo`: its `userJID` is field 1.</summary>
        private static byte[] Participant(string jid)
        {
            return new ProtoWriter().Text(1, jid).ToArray();
        }

        /// <summary>Two records inside a `HistorySync`: a video call that connected, and a missed voice call.</summary>
        private static byte[] Payload()
        {
            byte[] newer = new ProtoWriter()
                .Varint(1, 0)
                .Varint(4, 42)
                .Varint(5, 1760000200)
                .Varint(6, 1)
                .Varint(7, 1)
                .Text(11, "CALL0002")
                .Text(12, Peer)
                .Bytes(14, Participant(Peer))
                .Bytes(14, Participant(Me))
                .ToArray();

            byte[] older = new ProtoWriter()
                .Varint(1, 4)
                .Varint(5, 1760000000)
                .Varint(6, 1)
                .Text(11, "CALL0001")
                .Text(12, Peer)
                .ToArray();

            return new ProtoWriter()
                .Varint(1, 0)
                .Bytes(13, newer)
                .Bytes(13, older)
                .ToArray();
        }
    }
}
