using System.Collections.Generic;
using WhatsappApp.Protocol.Client;

namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// The history payload: the chats and the messages that were already there when this
    /// phone was linked.
    ///
    /// Why it is a payload and not a handful of nodes: WhatsApp does not send a chat list.
    /// It sends one `HistorySync` protobuf - compressed, and either inline in the
    /// notification or behind a media download - and every client parses it into
    /// conversations and messages itself. The reference does exactly that in
    /// `Utils/history.js` (`processHistoryMessage`), and the field numbers here are the
    /// ones its own encoder uses (`ProtocolTests/Vectors/HistoryFixture.cs` carries both
    /// the fixture and the table).
    ///
    /// What arrives is a *chunk*: a full sync is many of these, each with its own
    /// `chunkOrder` and `progress`. Nothing here decides when the sync is over; the caller
    /// does, from the sync type and the progress (see `NativeChats`).
    /// </summary>
    public sealed class HistorySync
    {
        /// <summary>The first, compressed chunk of a new device's history.</summary>
        public const int InitialBootstrap = 0;

        /// <summary>Status updates, as a sync of their own.</summary>
        public const int InitialStatusV3 = 1;

        /// <summary>The whole backlog.</summary>
        public const int Full = 2;

        /// <summary>The last few days.</summary>
        public const int Recent = 3;

        /// <summary>Only push names: no chats, no messages.</summary>
        public const int PushName = 4;

        /// <summary>Non-blocking data (settings and the like).</summary>
        public const int NonBlockingData = 5;

        /// <summary>A chunk asked for by name, on demand.</summary>
        public const int OnDemand = 6;

        public int SyncType;
        public int Progress;

        public IList<HistoryConversation> Conversations = new List<HistoryConversation>();
        public IList<HistoryPushName> PushNames = new List<HistoryPushName>();
        public IList<CallLogRecord> CallLogRecords = new List<CallLogRecord>();

        public static HistorySync Decode(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no history payload to read");

            var sync = new HistorySync();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        sync.SyncType = (int)reader.ReadVarint();
                        break;

                    case 2:
                        sync.Conversations.Add(HistoryConversation.Decode(reader.ReadBytes()));
                        break;

                    case 5:
                        reader.ReadVarint();
                        break;

                    case 6:
                        sync.Progress = (int)reader.ReadVarint();
                        break;

                    case 7:
                        sync.PushNames.Add(HistoryPushName.Decode(reader.ReadBytes()));
                        break;

                    case 13:
                        sync.CallLogRecords.Add(CallLogRecord.Decode(reader.ReadBytes()));
                        break;

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return sync;
        }
    }

    /// <summary>
    /// One chat in the history: who it is with, what it was called, how much was unread,
    /// and the messages that came with it.
    /// </summary>
    public sealed class HistoryConversation
    {
        public string Jid;
        public string Name;
        public int UnreadCount;
        public long ConversationTimestamp;
        public long LastMessageTimestamp;
        public bool Archived;

        public IList<HistoryMessage> Messages = new List<HistoryMessage>();

        /// <summary>
        /// A group is a group by its address and not by a flag: every group JID ends in
        /// `@g.us`, and that is what the rest of this app already keys on.
        /// </summary>
        public bool IsGroup
        {
            get { return Jid != null && Jid.EndsWith("@g.us"); }
        }

        public static HistoryConversation Decode(byte[] bytes)
        {
            var conversation = new HistoryConversation();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        conversation.Jid = Utf8(reader.ReadBytes());
                        break;

                    case 2:
                        conversation.Messages.Add(HistoryMessage.Decode(reader.ReadBytes()));
                        break;

                    case 5:
                        conversation.LastMessageTimestamp = (long)reader.ReadVarint();
                        break;

                    case 6:
                        conversation.UnreadCount = (int)reader.ReadVarint();
                        break;

                    case 12:
                        conversation.ConversationTimestamp = (long)reader.ReadVarint();
                        break;

                    case 13:
                        conversation.Name = Utf8(reader.ReadBytes());
                        break;

                    case 16:
                        conversation.Archived = reader.ReadVarint() != 0;
                        break;

                    // The name the chat shows: `displayName` is the one the server sends for
                    // a chat whose `name` it does not have, and it is the only one a group
                    // with a subject ever carries.
                    case 38:
                        {
                            string display = Utf8(reader.ReadBytes());
                            if (conversation.Name == null) conversation.Name = display;
                            break;
                        }

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return conversation;
        }

        private static string Utf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// One message of the history, already read down to the words.
    ///
    /// <see cref="Encoded"/> is the untouched `Message` blob and not a convenience: a photo,
    /// a voice note or a document arrives here as a message whose text is empty and whose
    /// bytes are the only way to get at it, and dropping it at this point would be dropping
    /// the media (see `Protocol/Media`).
    /// </summary>
    public sealed class HistoryMessage
    {
        public string Jid;
        public string Id;
        public string Text;
        public bool IsFromMe;
        public long Timestamp;
        public string Sender;

        /// <summary>The sender's name as the server reported it: groups only.</summary>
        public string SenderName;

        /// <summary>The encoded `Message`, or null when the row carried none.</summary>
        public byte[] Encoded;

        public static HistoryMessage Decode(byte[] bytes)
        {
            var message = new HistoryMessage();
            var reader = new ProtoReader(bytes);

            // `HistorySyncMsg` is a wrapper of two fields, and the message is the first.
            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == 1) ReadWebMessage(message, reader.ReadBytes());
                else reader.SkipField(field.WireType);
            }

            return message;
        }

        /// <summary>
        /// The `WebMessageInfo` half: the key (who, which way, which id), the timestamp, the
        /// sender for a group, and the message itself.
        /// </summary>
        private static void ReadWebMessage(HistoryMessage target, byte[] bytes)
        {
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        ReadKey(target, reader.ReadBytes());
                        break;

                    case 2:
                        {
                            byte[] encoded = reader.ReadBytes();
                            target.Encoded = encoded;
                            target.Text = Message.Decode(encoded).Body();
                            break;
                        }

                    case 3:
                        target.Timestamp = (long)reader.ReadVarint();
                        break;

                    case 5:
                        target.Sender = Utf8(reader.ReadBytes());
                        break;

                    case 19:
                        target.SenderName = Utf8(reader.ReadBytes());
                        break;

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }
        }

        /// <summary>
        /// The `MessageKey`: the chat it belongs to, which way it went, its id, and - in a
        /// group - who wrote it. The chat comes from the key and not from the conversation,
        /// because a status or a broadcast message leaves the key's `remoteJid` as the only
        /// place its chat is named.
        /// </summary>
        private static void ReadKey(HistoryMessage target, byte[] bytes)
        {
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        target.Jid = Utf8(reader.ReadBytes());
                        break;

                    case 2:
                        target.IsFromMe = reader.ReadVarint() != 0;
                        break;

                    case 3:
                        target.Id = Utf8(reader.ReadBytes());
                        break;

                    case 4:
                        {
                            string participant = Utf8(reader.ReadBytes());
                            if (target.Sender == null) target.Sender = participant;
                            break;
                        }

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }
        }

        private static string Utf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
    }

    /// <summary>One push name as the server reports it: the JID and what that person calls themselves.</summary>
    public sealed class HistoryPushName
    {
        public string Jid;
        public string Name;

        public static HistoryPushName Decode(byte[] bytes)
        {
            var pushName = new HistoryPushName();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        {
                            byte[] jid = reader.ReadBytes();
                            pushName.Jid = System.Text.Encoding.UTF8.GetString(jid, 0, jid.Length);
                            break;
                        }

                    case 2:
                        {
                            byte[] name = reader.ReadBytes();
                            pushName.Name = System.Text.Encoding.UTF8.GetString(name, 0, name.Length);
                            break;
                        }

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return pushName;
        }
    }

    /// <summary>
    /// One call as the history reports it: who, when, how long, and how it ended. It comes
    /// with the history and not on its own, which is why the call list is filled by a sync.
    /// </summary>
    public sealed class CallLogRecord
    {
        public string CallId;
        public string Peer;
        public string Group;
        public bool IsIncoming;
        public bool IsVideo;
        public int Result;
        public long Duration;
        public long StartTime;

        public static CallLogRecord Decode(byte[] bytes)
        {
            var record = new CallLogRecord();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        record.Result = (int)reader.ReadVarint();
                        break;

                    case 4:
                        record.Duration = (long)reader.ReadVarint();
                        break;

                    case 5:
                        record.StartTime = (long)reader.ReadVarint();
                        break;

                    case 6:
                        record.IsIncoming = reader.ReadVarint() != 0;
                        break;

                    case 7:
                        record.IsVideo = reader.ReadVarint() != 0;
                        break;

                    case 11:
                        record.CallId = Utf8(reader.ReadBytes());
                        break;

                    case 12:
                        record.Peer = Utf8(reader.ReadBytes());
                        break;

                    case 13:
                        record.Group = Utf8(reader.ReadBytes());
                        break;

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return record;
        }

        private static string Utf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// Where the payload is: either inline (already compressed, for the first bootstrap) or
    /// behind a download that is an encrypted media message like any other.
    /// </summary>
    public sealed class HistorySyncNotification
    {
        public byte[] MediaKey;
        public byte[] FileSha256;
        public byte[] FileEncSha256;
        public string DirectPath;
        public long FileLength;
        public int SyncType;
        public int ChunkOrder;
        public string OriginalMessageId;

        /// <summary>The compressed payload a bootstrap can carry instead of a download.</summary>
        public byte[] InlinePayload;

        public static HistorySyncNotification Decode(byte[] bytes)
        {
            if (bytes == null) throw new ProtocolException("no history notification to read");

            var notification = new HistorySyncNotification();
            var reader = new ProtoReader(bytes);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                switch (field.Number)
                {
                    case 1:
                        notification.FileSha256 = reader.ReadBytes();
                        break;

                    case 2:
                        notification.FileLength = (long)reader.ReadVarint();
                        break;

                    case 3:
                        notification.MediaKey = reader.ReadBytes();
                        break;

                    case 4:
                        notification.FileEncSha256 = reader.ReadBytes();
                        break;

                    case 5:
                        notification.DirectPath = Utf8(reader.ReadBytes());
                        break;

                    case 6:
                        notification.SyncType = (int)reader.ReadVarint();
                        break;

                    case 7:
                        notification.ChunkOrder = (int)reader.ReadVarint();
                        break;

                    case 8:
                        notification.OriginalMessageId = Utf8(reader.ReadBytes());
                        break;

                    case 11:
                        notification.InlinePayload = reader.ReadBytes();
                        break;

                    default:
                        reader.SkipField(field.WireType);
                        break;
                }
            }

            return notification;
        }

        /// <summary>The notification's field inside a `ProtocolMessage`.</summary>
        public const int ProtocolField = 6;

        /// <summary>
        /// The notification inside a message, or null when the message is not one.
        ///
        /// This is the shape the history actually arrives in: the reference reads it out of
        /// `protocolMessage.historySyncNotification` (`Utils/history.js`, `getHistoryMsg`),
        /// and the protocol message is field 12 of a `Message` (`Message.ProtocolField`).
        ///
        /// The number 6 is the reference's own: encoding a `ProtocolMessage` that carries
        /// only a notification starts with the tag bytes `32 04`, and `0x32 >> 3` is 6. It
        /// was written as 1 first - the guess a nested field invites - and the fixture is
        /// what said otherwise.
        /// </summary>
        public static HistorySyncNotification From(Message message)
        {
            if (message == null || message.ProtocolMessage == null) return null;

            var reader = new ProtoReader(message.ProtocolMessage);

            while (reader.HasMore)
            {
                ProtoField field = reader.ReadField();

                if (field.Number == ProtocolField) return Decode(reader.ReadBytes());

                reader.SkipField(field.WireType);
            }

            return null;
        }

        private static string Utf8(byte[] bytes)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }
    }
}
