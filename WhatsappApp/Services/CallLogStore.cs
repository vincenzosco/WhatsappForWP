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
    /// The call log on this phone: `call-log.json` in `LocalFolder`, holding the same
    /// rows the official app keeps in `call_log`, `call_log_participant_v2` and
    /// `call_link`.
    ///
    /// Why a file instead of SQLite: Windows Phone 8.1 has no SQLite built in, and
    /// there are only a few thousand rows here. Keeping the lookups in memory (see
    /// `CallLogTables`) is enough, and this class is just the file around them. The
    /// names written into the file are the official ones (`call_id`, `user_jid`,
    /// `token`), so anyone who knows that database can read this one.
    ///
    /// Writes go through a `SerialQueue` because calls end while the app is busy: two
    /// writes to the same file at once and one of them is lost.
    /// </summary>
    public sealed class CallLogStore
    {
        private const string FileName = "call-log.json";

        private readonly SerialQueue _writes = new SerialQueue();
        private CallLogTables _tables = new CallLogTables();

        private bool _loadStarted;
        private bool _loaded;

        /// <summary>The rows themselves, for reading rather than writing.</summary>
        public CallLogTables Tables
        {
            get
            {
                EnsureLoaded();
                return _tables;
            }
        }

        /// <summary>
        /// Reads the file once per run, in the background.
        ///
        /// If the read fails we log it and carry on. A call log that could not be loaded
        /// is still a call log that works for the calls made from now on.
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
        /// Adds one finished call.
        ///
        /// The row keeps the call id it had on the network, which is what makes a second
        /// copy of the same call one row instead of two.
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
        /// Turns the word we use for a call's outcome into the number the official log
        /// stores: `CONNECTED` 0, `REJECTED` 1, `CANCELLED` 2, `MISSED` 4.
        ///
        /// A call we made that nobody answered is a timeout on the network, and the
        /// official log files that under `MISSED`. That is why "outgoing" is checked
        /// before the missed cases.
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
        /// Writes the file, through the queue so two calls ending at once cannot write
        /// over each other.
        /// </summary>
        private void SaveSoon()
        {
            _writes.RunAsync(() => SaveAsync());
        }

        /// <summary>Resets this process's call history after a new account is linked.</summary>
        public void Reset()
        {
            _tables = new CallLogTables();
            _loadStarted = false;
            _loaded = false;
        }

        public async Task LoadAsync()
        {
            try
            {
                IStorageItem item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName);
                if (item == null)
                {
                    _loaded = true;
                    return;
                }

                string text = await FileIO.ReadTextAsync((StorageFile)item);
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

        /// <summary>What the file contains: the two tables, by their official names.</summary>
        [DataContract]
        public sealed class CallLogFile
        {
            [DataMember(Name = "call_log")]
            public List<CallLogRowDto> CallLog;

            [DataMember(Name = "call_link")]
            public List<CallLinkRowDto> CallLink;
        }

        /// <summary>One `call_log` row as it is written to the file.</summary>
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

        /// <summary>One `call_link` row as it is written to the file.</summary>
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
