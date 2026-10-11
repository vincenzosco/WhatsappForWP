using System.Collections.Generic;
using WhatsappApp.Protocol.WaProto;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The chat list as a rule: history chunks and live messages in, ordered rows out.
    ///
    /// Why it is separate from the page that binds it: what a chat list *is* - a row per
    /// conversation, newest first, a message counted once, the words of a chat in the order
    /// they were written - is the part worth a test, and none of it needs a phone. The store
    /// the pages bind (`Services/DataService`) keeps its unread counts, its pin and mute
    /// preferences and its caches; this is the input side of it, and it holds no WinRT type
    /// so the desktop host compiles it.
    ///
    /// The dedupe is the reference's own rule and not an optimisation: WhatsApp re-delivers
    /// history chunks and messages after a reconnect, and the reference keeps a bounded set
    /// of the ids it has already processed (`SeenMessages` here, `messages.upsert` there).
    /// Without it a reconnect shows the same conversation twice.
    /// </summary>
    public sealed class NativeChats
    {
        /// <summary>How many message ids are remembered. A day of a busy chat, and bounded.</summary>
        public const int SeenLimit = 512;

        private readonly SeenMessages _seen = new SeenMessages(SeenLimit);
        private readonly List<HistoryConversation> _rows = new List<HistoryConversation>();

        /// <summary>The rows, newest conversation first.</summary>
        public IList<HistoryConversation> Rows
        {
            get { return _rows; }
        }

        /// <summary>
        /// Whether the server has said the history is all here.
        ///
        /// The rule is the reference's (`Socket/chats.js`): an initial bootstrap fires the
        /// list immediately, and a later chunk with `progress === 100` is the explicit end
        /// of the sync. Until one of those arrives the list is a list that is still being
        /// filled, and a page that says "no chats" over it would be wrong.
        /// </summary>
        public bool IsSyncComplete { get; private set; }

        /// <summary>
        /// One chunk of history: the conversations it carries, merged into the rows.
        ///
        /// The unread count is *set*, not added: the server's number is the truth about a
        /// chat's unread messages at the moment of the sync, and adding it to what a previous
        /// chunk already said would double it. A live message is the case that counts up
        /// (see <see cref="AddIncoming"/>).
        /// </summary>
        public void ApplyHistory(HistorySync sync)
        {
            if (sync == null) return;

            for (int i = 0; i < sync.Conversations.Count; i++)
            {
                HistoryConversation chunk = sync.Conversations[i];
                if (chunk.Jid == null) continue;

                HistoryConversation row = EnsureRow(chunk.Jid);

                row.UnreadCount = chunk.UnreadCount;
                row.Archived = row.Archived || chunk.Archived;
                if (chunk.Name != null) row.Name = chunk.Name;
                if (chunk.LastMessageTimestamp > row.LastMessageTimestamp) row.LastMessageTimestamp = chunk.LastMessageTimestamp;
                if (chunk.ConversationTimestamp > row.ConversationTimestamp) row.ConversationTimestamp = chunk.ConversationTimestamp;

                for (int m = 0; m < chunk.Messages.Count; m++) AddMessage(row, chunk.Messages[m]);
            }

            for (int i = 0; i < sync.PushNames.Count; i++)
            {
                HistoryPushName name = sync.PushNames[i];
                HistoryConversation row = Find(name.Jid);
                if (row != null && name.Name != null) row.Name = name.Name;
            }

            if (sync.SyncType == HistorySync.InitialBootstrap || sync.Progress >= 100) IsSyncComplete = true;

            Order();
        }

        /// <summary>
        /// A message that has just arrived. False when it is one this client has already
        /// seen, which is the answer that keeps a re-delivery out of the chat.
        /// </summary>
        public bool AddIncoming(HistoryMessage message)
        {
            if (message == null || message.Jid == null) return false;

            HistoryConversation row = EnsureRow(message.Jid);
            if (!AddMessage(row, message)) return false;

            // An incoming message is unread until someone reads it, and the row is the newest
            // one now by definition: it was just written.
            row.UnreadCount = row.UnreadCount + 1;
            Touch(row, message.Timestamp);
            Order();

            return true;
        }

        /// <summary>
        /// A message this phone sent. It moves its chat to the top - writing to someone is
        /// what makes that chat the current one - and it does not count as unread.
        /// </summary>
        public bool AddOutgoing(string jid, string id, string text, long timestamp)
        {
            if (jid == null) return false;

            var message = new HistoryMessage();
            message.Jid = jid;
            message.Id = id;
            message.Text = text;
            message.IsFromMe = true;
            message.Timestamp = timestamp;

            HistoryConversation row = EnsureRow(jid);
            AddMessage(row, message);
            Touch(row, timestamp);
            Order();

            return true;
        }

        /// <summary>The row for a chat, or null when the chat list has never heard of it.</summary>
        public HistoryConversation Find(string jid)
        {
            if (jid == null) return null;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Jid == jid) return _rows[i];
            }

            return null;
        }

        private HistoryConversation EnsureRow(string jid)
        {
            HistoryConversation row = Find(jid);
            if (row != null) return row;

            row = new HistoryConversation();
            row.Jid = jid;
            _rows.Add(row);

            return row;
        }

        /// <summary>
        /// Adds a message to a row unless its id has been seen. A message with no id at all is
        /// kept: an id is what a re-delivery is recognised by, and a client that dropped
        /// everything without one would drop protocol messages whose shape it does not know.
        /// </summary>
        private bool AddMessage(HistoryConversation row, HistoryMessage message)
        {
            if (message == null) return false;
            if (message.Id != null && !_seen.IsNew(message.Id)) return false;

            row.Messages.Add(message);
            if (message.Timestamp > row.ConversationTimestamp) row.ConversationTimestamp = message.Timestamp;

            return true;
        }

        private void Touch(HistoryConversation row, long timestamp)
        {
            if (timestamp > row.ConversationTimestamp) row.ConversationTimestamp = timestamp;
            if (timestamp > row.LastMessageTimestamp) row.LastMessageTimestamp = timestamp;
        }

        /// <summary>
        /// Newest conversation first, and stable for equal timestamps: a chat that did not
        /// move keeps the place it had instead of swapping with the row beside it on every
        /// sync, which is what an unstable sort would do to a list the user is looking at.
        /// </summary>
        private void Order()
        {
            for (int i = 1; i < _rows.Count; i++)
            {
                HistoryConversation row = _rows[i];
                int at = i - 1;

                while (at >= 0 && _rows[at].ConversationTimestamp < row.ConversationTimestamp)
                {
                    _rows[at + 1] = _rows[at];
                    at--;
                }

                _rows[at + 1] = row;
            }
        }
    }
}
