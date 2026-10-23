using System.Collections.Generic;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// One call as the log shows it: who, whether it was video, how long, and how it ended.
    ///
    /// `IsMissed` is not a field WhatsApp sends: it is the `callResult` of the record
    /// (`CallLogRecord.CallResult`, `WAProto/index.d.ts:2345`), where `MISSED` is 4 and the
    /// values around it are `REJECTED` 1, `CANCELLED` 2, `CONNECTED` 0. A call the phone never
    /// answered and a call that was declined are different things on a call log's screen, and
    /// this keeps them different: `IsMissed` is `MISSED` alone.
    /// </summary>
    public sealed class CallLogEntryModel
    {
        public string Id;
        public string Jid;
        public bool IsVideo;
        public bool IsMissed;
        public bool IsIncoming;

        /// <summary>The moment the call started, in seconds.</summary>
        public long Timestamp;

        /// <summary>How long it lasted, in seconds. Zero for one that never connected.</summary>
        public int Seconds;
    }

    /// <summary>
    /// The call log, and the one control a call log has: refusing a call that is ringing.
    ///
    /// The rows come from the **history sync** and from nowhere else: WhatsApp puts the call
    /// log in `HistorySync.callLogRecords` (`Conversation.cs`'s neighbour in the reference,
    /// `HistorySync` in `WAProto`), which is why a call made while the phone was off is in the
    /// list the next time the app opens and a call made while it was on arrives with the next
    /// sync. That is also why there is no `query` here: there is nothing to ask.
    /// </summary>
    public static class Calls
    {
        /// <summary>The `callResult` a missed call has (`CallLogRecord.CallResult.MISSED`).</summary>
        public const int Missed = 4;

        /// <summary>
        /// The log as rows, newest first: a call log reads like a chat, and the sync sends its
        /// records oldest first.
        ///
        /// `ownJid` is this account, and it has to be told rather than guessed: the record's
        /// `callCreatorJID` is the caller, so for a call this phone made it is this account
        /// itself and the chat is only knowable by taking the other name off the participant
        /// list. A caller that passed null would get its own number as the chat of every
        /// outgoing call.
        /// </summary>
        public static IList<CallLogEntryModel> FromHistory(HistorySync sync, string ownJid)
        {
            var calls = new List<CallLogEntryModel>();
            if (sync == null || sync.CallLogRecords == null) return calls;

            for (int i = 0; i < sync.CallLogRecords.Count; i++)
            {
                CallLogRecord record = sync.CallLogRecords[i];
                if (record == null) continue;

                string jid = string.IsNullOrEmpty(record.Group) ? Other(record, ownJid) : record.Group;

                // A record that names nobody but this account is a call with no conversation
                // to appear in, on WhatsApp and here alike.
                if (string.IsNullOrEmpty(jid)) continue;

                var entry = new CallLogEntryModel();
                entry.Id = record.CallId;
                entry.Jid = jid;
                entry.IsVideo = record.IsVideo;
                entry.IsIncoming = record.IsIncoming;
                entry.IsMissed = record.Result == Missed;
                entry.Timestamp = record.StartTime;

                // A call that never connected has no duration, and the absence is a zero here
                // and not a random: `AMissedCallWithNoDurationSaysZero`.
                entry.Seconds = record.Duration > 0 && record.Duration < int.MaxValue
                    ? (int)record.Duration
                    : 0;

                calls.Add(entry);
            }

            calls.Sort(delegate (CallLogEntryModel left, CallLogEntryModel right)
            {
                return right.Timestamp.CompareTo(left.Timestamp);
            });

            return calls;
        }

        /// <summary>
        /// The other party of a one-to-one call, or null when the record names nobody else.
        ///
        /// The order matters: the participant list first, because the creator is this account
        /// on every outgoing call and the list is where the person we called is; the creator
        /// second, for a record whose participant list did not come with the sync. The device
        /// suffix is ignored, because the same person appears with one device here and
        /// another there.
        /// </summary>
        private static string Other(CallLogRecord record, string ownJid)
        {
            bool known = !string.IsNullOrEmpty(ownJid);

            for (int i = 0; i < record.Participants.Count; i++)
            {
                string who = record.Participants[i];

                // Without its device, as the doc comment says: the call row is keyed on the
                // chat's address, and `15551230000:5` is no chat's address - the list then
                // printed the number with its device in it and found no name for it.
                if (!known || !SamePerson(who, ownJid)) return JidUtils.Normalize(who);
            }

            if (!SamePerson(record.CallCreatorJid, ownJid)) return JidUtils.Normalize(record.CallCreatorJid);

            return null;
        }

        /// <summary>The user half of two JIDs, ignoring the device and the server.</summary>
        private static bool SamePerson(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

            return User(left) == User(right);
        }

        private static string User(string jid)
        {
            int at = jid.IndexOf('@');
            string user = at < 0 ? jid : jid.Substring(0, at);

            int colon = user.IndexOf(':');
            return colon < 0 ? user : user.Substring(0, colon);
        }

        /// <summary>
        /// Refusing a call: `call` with a `reject` child carrying the call's id, addressed to
        /// the caller (`whatsmeow`, `send.go` and `call.go`, `RejectCall`: `<call to=... id=...>
        /// <reject/></call>`).
        /// </summary>
        public static BinaryNode Reject(string callId, string jid)
        {
            if (string.IsNullOrEmpty(callId)) throw new ProtocolException("a call is refused by its id");
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a call is refused to somebody");

            var node = new BinaryNode("call");
            node.Attrs.Add("to", jid);
            node.Attrs.Add("id", callId);

            node.Content = new List<BinaryNode> { new BinaryNode("reject") };

            return node;
        }
    }
}
