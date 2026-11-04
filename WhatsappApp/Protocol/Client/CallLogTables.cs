using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>One row of `call_log`, with the names the client's own schema uses.</summary>
    public sealed class CallLogRow
    {
        /// <summary>`call_log._id`: the row this participants and links point at.</summary>
        public long RowId;

        /// <summary>`call_log.timestamp`, in seconds.</summary>
        public long Timestamp;

        /// <summary>`call_log.call_id`: the id every node of the call carries.</summary>
        public string CallId;

        /// <summary>`call_log.user_jid`: the other party, as the log keys it.</summary>
        public string UserJid;

        /// <summary>`call_log.from_me`.</summary>
        public bool FromMe;

        /// <summary>`call_log.video_call`.</summary>
        public bool IsVideo;

        /// <summary>`call_log.call_result`: the `CallLogRecord.CallResult` value.</summary>
        public int Result;

        /// <summary>The seconds it lasted; zero for a call that never connected.</summary>
        public int DurationSeconds;

        /// <summary>`call_log.telecom_uuid`, for a call the system dialer knows.</summary>
        public string TelecomUuid;

        /// <summary>`call_log.transaction_id`.</summary>
        public string TransactionId;

        /// <summary>`call_log_participant_v2` for this call, in the order they joined.</summary>
        public readonly List<string> Participants = new List<string>();
    }

    /// <summary>
    /// One row of `call_link`: a call reachable by a token instead of an offer.
    ///
    /// The token is the whole identity of the row - the official schema has a unique
    /// index on it (`call_link_token_index`) and the event table points at it - so the
    /// store here does the same: one token, one row, and an insert of a token that
    /// exists is the existing row updated rather than a second one.
    /// </summary>
    public sealed class CallLinkRow
    {
        public string Token;
        public string CreatorJid;
        public string State;
        public string MediaType;
        public string LobbyToken;
        public bool WaitingRoomEnabled;
        public long LastUsedTimestamp;
        public string RandomId;
        public string SharedApp;
    }

    /// <summary>
    /// The call-log tables, and the indices the official database keeps on them.
    ///
    /// Why it is here and not in a store class: this is the part worth a test - which
    /// lookup answers what, and which one is unique - and it has no file and no WinRT in
    /// it. `Services/CallLogStore` is the thin file around it.
    ///
    /// The indices are the schema's own, from the `CREATE INDEX` statements in the APK's
    /// string pool (`2026-11-04-calls-apk-inventory.md`):
    ///
    ///   call_log_ts_index                 (timestamp)          newest first on screen
    ///   call_log_call_id                  (call_id)            one call, one row
    ///   call_log_telecom_uuid_index       (telecom_uuid)
    ///   call_log_user_jid                 (user_jid)           every call with a person
    ///   call_log_participant_v2           (call_log_row_id)    who was on it
    ///   call_link_token_index             (token) UNIQUE       the link's identity
    ///
    /// A row is inserted once and then read: the log the sync sends is not edited in
    /// place except for a link's creator, which `UpdateLinkCreator` is for.
    /// </summary>
    public sealed class CallLogTables
    {
        private readonly List<CallLogRow> _rows = new List<CallLogRow>();
        private readonly Dictionary<long, CallLogRow> _byId = new Dictionary<long, CallLogRow>();
        private readonly Dictionary<string, CallLogRow> _byCallId = new Dictionary<string, CallLogRow>();
        private readonly Dictionary<string, CallLogRow> _byTelecomUuid = new Dictionary<string, CallLogRow>();
        private readonly Dictionary<string, List<CallLogRow>> _byUser =
            new Dictionary<string, List<CallLogRow>>();
        private readonly SortedDictionary<long, List<CallLogRow>> _byTime =
            new SortedDictionary<long, List<CallLogRow>>();
        private readonly Dictionary<string, CallLinkRow> _links = new Dictionary<string, CallLinkRow>();

        private long _nextRowId = 1;

        /// <summary>How many calls the log holds.</summary>
        public int Count
        {
            get { return _rows.Count; }
        }

        /// <summary>How many call links it holds.</summary>
        public int LinkCount
        {
            get { return _links.Count; }
        }

        /// <summary>
        /// Adds a call and returns its `_id`. A call already in the log - the history
        /// sync and a local call can both carry one - is not added twice: the id is
        /// unique on the wire, so a second copy is the same call.
        /// </summary>
        public long Insert(CallLogRow row)
        {
            if (row == null) throw new ArgumentNullException("row");

            if (!string.IsNullOrEmpty(row.CallId) && _byCallId.ContainsKey(row.CallId))
                return _byCallId[row.CallId].RowId;

            row.RowId = _nextRowId;
            _nextRowId++;

            _rows.Add(row);
            _byId[row.RowId] = row;

            if (!string.IsNullOrEmpty(row.CallId)) _byCallId[row.CallId] = row;
            if (!string.IsNullOrEmpty(row.TelecomUuid)) _byTelecomUuid[row.TelecomUuid] = row;

            string user = Key(row.UserJid);
            if (user.Length > 0)
            {
                List<CallLogRow> mine;
                if (!_byUser.TryGetValue(user, out mine))
                {
                    mine = new List<CallLogRow>();
                    _byUser[user] = mine;
                }
                mine.Add(row);
            }

            List<CallLogRow> atTime;
            if (!_byTime.TryGetValue(row.Timestamp, out atTime))
            {
                atTime = new List<CallLogRow>();
                _byTime[row.Timestamp] = atTime;
            }
            atTime.Add(row);

            return row.RowId;
        }

        /// <summary>The log newest first: what a call list draws.</summary>
        public IList<CallLogRow> ByTimestamp()
        {
            var ordered = new List<CallLogRow>(_rows.Count);

            // A SortedDictionary enumerates oldest first; the screen wants the other way,
            // so the keys are walked backwards instead of sorting the rows again.
            var keys = new List<long>(_byTime.Keys);
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                List<CallLogRow> atTime = _byTime[keys[i]];
                for (int k = atTime.Count - 1; k >= 0; k--) ordered.Add(atTime[k]);
            }

            return ordered;
        }

        /// <summary>The call with this id, or null.</summary>
        public CallLogRow ByCallId(string callId)
        {
            if (string.IsNullOrEmpty(callId)) return null;

            CallLogRow row;
            return _byCallId.TryGetValue(callId, out row) ? row : null;
        }

        /// <summary>The call the system dialer knows by this uuid, or null.</summary>
        public CallLogRow ByTelecomUuid(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return null;

            CallLogRow row;
            return _byTelecomUuid.TryGetValue(uuid, out row) ? row : null;
        }

        /// <summary>Every call with one person, newest first.</summary>
        public IList<CallLogRow> ByUser(string jid)
        {
            var found = new List<CallLogRow>();
            string user = Key(jid);
            if (user.Length == 0) return found;

            List<CallLogRow> mine;
            if (!_byUser.TryGetValue(user, out mine)) return found;

            for (int i = mine.Count - 1; i >= 0; i--) found.Add(mine[i]);
            return found;
        }

        /// <summary>Who was on a call, by its `_id`.</summary>
        public IList<string> Participants(long rowId)
        {
            CallLogRow row;
            if (!_byId.TryGetValue(rowId, out row)) return new List<string>();

            return row.Participants;
        }

        /// <summary>
        /// Adds or replaces a call link. The token is unique, so this cannot grow the
        /// table by accident: the same link seen twice is one row.
        /// </summary>
        public void InsertLink(CallLinkRow link)
        {
            if (link == null) throw new ArgumentNullException("link");
            if (string.IsNullOrEmpty(link.Token))
                throw new ArgumentException("a call link is its token", "link");

            _links[link.Token] = link;
        }

        /// <summary>The link with this token, or null.</summary>
        public CallLinkRow LinkByToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return null;

            CallLinkRow link;
            return _links.TryGetValue(token, out link) ? link : null;
        }

        /// <summary>
        /// The link's creator, which the official schema updates under the same unique
        /// index (`call_link_store/update_creator`). Answers whether the link exists.
        /// </summary>
        public bool UpdateLinkCreator(string token, string creatorJid)
        {
            CallLinkRow link = LinkByToken(token);
            if (link == null) return false;

            link.CreatorJid = JidUtils.Normalize(creatorJid);
            return true;
        }

        /// <summary>Every call, in the order it was added: what the file keeps.</summary>
        public IList<CallLogRow> Rows()
        {
            return _rows;
        }

        /// <summary>Every link: what the file keeps.</summary>
        public IList<CallLinkRow> Links()
        {
            var all = new List<CallLinkRow>(_links.Count);
            foreach (KeyValuePair<string, CallLinkRow> pair in _links) all.Add(pair.Value);
            return all;
        }

        /// <summary>
        /// A person's key: the user half, without the device. Two rows about the same
        /// person are the same chat, and the device suffix is not part of the chat's
        /// address.
        /// </summary>
        private static string Key(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "";

            string normalized = JidUtils.Normalize(jid);
            return normalized == null ? "" : normalized.ToLowerInvariant();
        }
    }
}
