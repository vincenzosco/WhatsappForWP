using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>One row of `call_log`, with the same field names the official app uses.</summary>
    public sealed class CallLogRow
    {
        /// <summary>The row's own number. Participants point back at it.</summary>
        public long RowId;

        /// <summary>When the call happened, in seconds.</summary>
        public long Timestamp;

        /// <summary>The call id, the one every message about this call carries.</summary>
        public string CallId;

        /// <summary>The other person, as the log stores them.</summary>
        public string UserJid;

        /// <summary>True if we made the call.</summary>
        public bool FromMe;

        /// <summary>True if it was a video call.</summary>
        public bool IsVideo;

        /// <summary>How it ended: the number the official log calls `CallResult`.</summary>
        public int Result;

        /// <summary>How long it lasted. Zero for a call nobody answered.</summary>
        public int DurationSeconds;

        /// <summary>The id the phone's own dialer knows this call by, when there is one.</summary>
        public string TelecomUuid;

        /// <summary>The id of the message or action that started the call.</summary>
        public string TransactionId;

        /// <summary>Who was on the call, in the order they joined (the `call_log_participant_v2` rows).</summary>
        public readonly List<string> Participants = new List<string>();
    }

    /// <summary>
    /// One row of `call_link`: a call you can join with a link instead of calling.
    ///
    /// The token is what identifies the link. The official database has a unique index
    /// on it, and the event table points at it, so this store does the same: one token,
    /// one row. Adding the same token twice updates that row instead of making a second.
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
    /// The call log tables, with the same lookups (indices) the official database keeps.
    ///
    /// This part is separate from the file around it (`Services/CallLogStore`) because
    /// this is the part worth testing: which lookup answers what, and which one is
    /// unique. There is no file and no WinRT here, so a test can use it directly.
    ///
    /// These are the official indices, from the `CREATE INDEX` statements in the APK's
    /// string pool (`2026-11-04-calls-apk-inventory.md`):
    ///
    /// call_log_ts_index (timestamp) newest first on screen
    /// call_log_call_id (call_id) one call, one row
    /// call_log_telecom_uuid_index (telecom_uuid)
    /// call_log_user_jid (user_jid) every call with a person
    /// call_log_participant_v2 (call_log_row_id) who was on it
    /// call_link_token_index (token) UNIQUE the link's identity
    ///
    /// Rows are added once and then read. The only thing ever changed in place is a
    /// link's creator, which is what `UpdateLinkCreator` is for.
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

        /// <summary>How many calls are in the log.</summary>
        public int Count
        {
            get { return _rows.Count; }
        }

        /// <summary>How many call links there are.</summary>
        public int LinkCount
        {
            get { return _links.Count; }
        }

        /// <summary>
        /// Adds a call and gives back its row number.
        ///
        /// The same call can arrive from the history sync and from a call made on this
        /// phone. The call id is unique on the network, so a second copy is not a second
        /// call: the row that is already there is returned instead.
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

        /// <summary>The whole log, newest call first. This is what the call list draws.</summary>
        public IList<CallLogRow> ByTimestamp()
        {
            var ordered = new List<CallLogRow>(_rows.Count);

            // A SortedDictionary reads oldest first and the screen wants it the other
            // way round, so the times are walked backwards. Sorting the rows again would
            // do the same work twice.
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

        /// <summary>The call the phone's dialer knows by this id, or null.</summary>
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

        /// <summary>Who was on a call, given the call's row number.</summary>
        public IList<string> Participants(long rowId)
        {
            CallLogRow row;
            if (!_byId.TryGetValue(rowId, out row)) return new List<string>();

            return row.Participants;
        }

        /// <summary>
        /// Adds or replaces a call link. The token is unique, so this cannot make the
        /// table grow by accident: the same link seen twice is one row.
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
        /// Changes who created a link. The official database does the same, through its
        /// unique token index. Returns false if there is no such link.
        /// </summary>
        public bool UpdateLinkCreator(string token, string creatorJid)
        {
            CallLinkRow link = LinkByToken(token);
            if (link == null) return false;

            link.CreatorJid = JidUtils.Normalize(creatorJid);
            return true;
        }

        /// <summary>Every call, in the order it was added. This is what the file keeps.</summary>
        public IList<CallLogRow> Rows()
        {
            return _rows;
        }

        /// <summary>Every link. This is what the file keeps.</summary>
        public IList<CallLinkRow> Links()
        {
            var all = new List<CallLinkRow>(_links.Count);
            foreach (KeyValuePair<string, CallLinkRow> pair in _links) all.Add(pair.Value);
            return all;
        }

        /// <summary>
        /// A person's key for the user index: the address without the device part.
        ///
        /// Two calls with the same person belong to the same chat, and a chat address
        /// does not include the device they happened to call from.
        /// </summary>
        private static string Key(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return "";

            string normalized = JidUtils.Normalize(jid);
            return normalized == null ? "" : normalized.ToLowerInvariant();
        }
    }
}
