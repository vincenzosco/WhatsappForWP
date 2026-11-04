using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading.Tasks;
using Windows.Storage;
using WhatsappApp.Models;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The call log on the phone: `call-log.json` in `LocalFolder`, holding the
    /// `call_log`, `call_log_participant_v2` and `call_link` rows.
    ///
    /// Why a file and not SQLite: WP8.1 has no SQLite in the box, and the schema this
    /// reproduces is small enough that the indices are worth more than the engine -
    /// `CallLogTables` holds them, this class is the file around it. The names inside the
    /// file are the schema's own (`row_id`, `call_id`, `user_jid`, `call_link.token`), so
    /// a reader that knows the official database reads this one.
    ///
    /// The write goes through a `SerialQueue`: a call ends while another is being logged,
    /// and two `FileIO` writes on one file lose one of them.
    /// </summary>
    public sealed class CallLogStore
    {
        private const string FileName = "call-log.json";

        private readonly SerialQueue _writes = new SerialQueue();
        private readonly CallLogTables _tables = new CallLogTables();

        private bool _loadStarted;
        private bool _loaded;

        /// <summary>The tables, for a reader that wants a lookup rather than a write.</summary>
        public CallLogTables Tables
        {
            get
            {
                EnsureLoaded();
                return _tables;
            }
        }

        /// <summary>
        /// Reads the file once per run, in the background. A failure is a log line, not an
        /// exception: a call log that could not be read is still a call log that works for
        /// the calls of this run.
        /// </summary>
        public void EnsureLoaded()
        {
            if (_loadStarted) return;
            _loadStarted = true;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("CallLogStore/load", LoadAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// Logs one finished call. The row keeps the id the call had on the wire, which is
        /// what makes a second copy of the same call one row instead of two.
        /// </summary>
        public void Record(CallLogEntry entry, string callId, bool fromMe)
        {
            if (entry == null || string.IsNullOrEmpty(callId)) return;

            EnsureLoaded();

            var row = new CallLogRow();
            row.CallId = callId;
            row.UserJid = entry.ChatId;
            row.Timestamp = ToUnixSeconds(entry.Timestamp);
            row.FromMe = fromMe;
            row.IsVideo = entry.IsVideo;
            row.Result = ResultOf(entry.Reason);
            row.DurationSeconds = entry.DurationSeconds;
            row.Participants.Add(entry.ChatId);

            long id = _tables.Insert(row);
            if (id != row.RowId) return;

            SaveSoon();
        }

        /// <summary>
        /// The `CallLogRecord.CallResult` a row's word stands for: `CONNECTED` 0,
        /// `REJECTED` 1, `CANCELLED` 2, `MISSED` 4. A call this phone placed that nobody
        /// took is a timeout on the wire, which the log's own enum files under `MISSED` -
        /// and that is why the check for "outgoing" comes before it.
        /// </summary>
        private static int ResultOf(string reason)
        {
            string word = (reason ?? "").Trim().ToLowerInvariant();

            if (word == "accepted") return 0;
            if (word == "reject" || word == "busy") return 1;
            if (word == "cancel") return 2;
            if (word == "outgoing") return 2;
            if (word == "missed" || word == "timeout") return 4;
            return 4;
        }

        private static long ToUnixSeconds(DateTime when)
        {
            if (when == default(DateTime)) return 0;

            DateTime utc = when.Kind == DateTimeKind.Utc ? when : when.ToUniversalTime();
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static DateTime FromUnixSeconds(long seconds)
        {
            if (seconds <= 0) return default(DateTime);

            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }

        /// <summary>
        /// Writes the file: one write per hang-up, serialized so that two hang-ups close
        /// message, and one write per hang-up is one write too many on this phone.
        /// </summary>
        private void SaveSoon()
        {
            _writes.RunAsync(() => SaveAsync());
        }

        public async Task LoadAsync()
        {
            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string text = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrEmpty(text)) return;

                var serializer = new DataContractJsonSerializer(typeof(CallLogFile));
                CallLogFile parsed;

                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)))
                {
                    parsed = serializer.ReadObject(stream) as CallLogFile;
                }

                if (parsed == null) return;
                if (_loaded) return;
                _loaded = true;

                if (parsed.CallLog != null)
                {
                    for (int i = 0; i < parsed.CallLog.Count; i++)
                    {
                        CallLogRowDto dto = parsed.CallLog[i];
                        if (dto == null || string.IsNullOrEmpty(dto.CallId)) continue;

                        var row = new CallLogRow();
                        row.CallId = dto.CallId;
                        row.UserJid = dto.UserJid;
                        row.Timestamp = dto.Timestamp;
                        row.FromMe = dto.FromMe;
                        row.IsVideo = dto.Video;
                        row.Result = dto.Result;
                        row.DurationSeconds = dto.Duration;
                        row.TelecomUuid = dto.TelecomUuid;
                        row.TransactionId = dto.TransactionId;

                        if (dto.Participants != null)
                        {
                            for (int k = 0; k < dto.Participants.Count; k++) row.Participants.Add(dto.Participants[k]);
                        }

                        _tables.Insert(row);
                    }
                }

                if (parsed.CallLink != null)
                {
                    for (int i = 0; i < parsed.CallLink.Count; i++)
                    {
                        CallLinkRowDto dto = parsed.CallLink[i];
                        if (dto == null || string.IsNullOrEmpty(dto.Token)) continue;

                        var link = new CallLinkRow();
                        link.Token = dto.Token;
                        link.CreatorJid = dto.CreatorJid;
                        link.State = dto.State;
                        link.MediaType = dto.MediaType;
                        link.LobbyToken = dto.LobbyToken;
                        link.WaitingRoomEnabled = dto.WaitingRoom;
                        link.LastUsedTimestamp = dto.LastUsed;
                        link.RandomId = dto.RandomId;
                        link.SharedApp = dto.SharedApp;

                        _tables.InsertLink(link);
                    }
                }

                Diag.Ok("call log: read " + _tables.Count + " calls and " + _tables.LinkCount + " links");
            }
            catch (FileNotFoundException)
            {
                // The first run: a missing log is not a failure.
                _loaded = true;
            }
            catch (Exception ex)
            {
                Diag.Failed("CallLogStore/load", ex);
                _loaded = true;
            }
        }

        public async Task SaveAsync()
        {
            try
            {
                var file = new CallLogFile();
                file.CallLog = new List<CallLogRowDto>();
                file.CallLink = new List<CallLinkRowDto>();

                IList<CallLogRow> rows = _tables.Rows();
                for (int i = 0; i < rows.Count; i++)
                {
                    CallLogRow row = rows[i];
                    var dto = new CallLogRowDto();
                    dto.CallId = row.CallId;
                    dto.UserJid = row.UserJid;
                    dto.Timestamp = row.Timestamp;
                    dto.FromMe = row.FromMe;
                    dto.Video = row.IsVideo;
                    dto.Result = row.Result;
                    dto.Duration = row.DurationSeconds;
                    dto.TelecomUuid = row.TelecomUuid;
                    dto.TransactionId = row.TransactionId;
                    dto.Participants = new List<string>(row.Participants);
                    file.CallLog.Add(dto);
                }

                IList<CallLinkRow> links = _tables.Links();
                for (int i = 0; i < links.Count; i++)
                {
                    CallLinkRow link = links[i];
                    var dto = new CallLinkRowDto();
                    dto.Token = link.Token;
                    dto.CreatorJid = link.CreatorJid;
                    dto.State = link.State;
                    dto.MediaType = link.MediaType;
                    dto.LobbyToken = link.LobbyToken;
                    dto.WaitingRoom = link.WaitingRoomEnabled;
                    dto.LastUsed = link.LastUsedTimestamp;
                    dto.RandomId = link.RandomId;
                    dto.SharedApp = link.SharedApp;
                    file.CallLink.Add(dto);
                }

                var serializer = new DataContractJsonSerializer(typeof(CallLogFile));
                string text;

                using (var stream = new MemoryStream())
                {
                    serializer.WriteObject(stream, file);
                    text = System.Text.Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                StorageFile target = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);

                await FileIO.WriteTextAsync(target, text);
            }
            catch (Exception ex)
            {
                Diag.Failed("CallLogStore/save", ex);
            }
        }

        [DataContract]
        public sealed class CallLogFile
        {
            [DataMember(Name = "call_log")]
            public List<CallLogRowDto> CallLog;

            [DataMember(Name = "call_link")]
            public List<CallLinkRowDto> CallLink;
        }

        [DataContract]
        public sealed class CallLogRowDto
        {
            [DataMember(Name = "call_id")] public string CallId;
            [DataMember(Name = "user_jid")] public string UserJid;
            [DataMember(Name = "timestamp")] public long Timestamp;
            [DataMember(Name = "from_me")] public bool FromMe;
            [DataMember(Name = "video_call")] public bool Video;
            [DataMember(Name = "call_result")] public int Result;
            [DataMember(Name = "duration")] public int Duration;
            [DataMember(Name = "telecom_uuid")] public string TelecomUuid;
            [DataMember(Name = "transaction_id")] public string TransactionId;
            [DataMember(Name = "participants")] public List<string> Participants;
        }

        [DataContract]
        public sealed class CallLinkRowDto
        {
            [DataMember(Name = "token")] public string Token;
            [DataMember(Name = "creator_jid")] public string CreatorJid;
            [DataMember(Name = "state")] public string State;
            [DataMember(Name = "media_type")] public string MediaType;
            [DataMember(Name = "lobby_token")] public string LobbyToken;
            [DataMember(Name = "waiting_room_enabled")] public bool WaitingRoom;
            [DataMember(Name = "last_used_timestamp")] public long LastUsed;
            [DataMember(Name = "random_id")] public string RandomId;
            [DataMember(Name = "shared_app")] public string SharedApp;
        }
    }
}
