using System;
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
        private readonly NameBook _names;

        /// <summary>
        /// A chat list with nobody's names: the shape a test wants when it is about the order
        /// of the rows and not about what they are called.
        /// </summary>
        public NativeChats()
            : this(new NameBook())
        {
        }

        /// <summary>
        /// The same, with the names and the LID pairs it has to apply. One book is shared with
        /// the connection and the cache, so a name learned from a live message is the name the
        /// cached rows are drawn with after the next restart (see `Services/NameCache`).
        /// </summary>
        public NativeChats(NameBook names)
        {
            if (names == null) throw new ProtocolException("a chat list needs a place to keep names");

            _names = names;
        }

        /// <summary>The names and LID pairs behind the rows.</summary>
        public NameBook Names
        {
            get { return _names; }
        }

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
        /// One chunk of history: the conversations it carries, merged into the rows, and the
        /// messages the chunk actually added - which is what the app beside it draws.
        ///
        /// The unread count is *set*, not added: the server's number is the truth about a
        /// chat's unread messages at the moment of the sync, and adding it to what a previous
        /// chunk already said would double it. A live message is the case that counts up
        /// (see <see cref="AddIncoming"/>).
        ///
        /// The answer is the messages and not the chunk: it has the dedupe applied, so a chunk
        /// WhatsApp re-delivers hands the app nothing twice, and it carries only what this
        /// client can draw (<see cref="Shows"/>). A row whose messages were never handed over
        /// is the conversation a person opens to find empty, which is the failure this answer
        /// exists to end.
        /// </summary>
        public IList<HistoryMessage> ApplyHistory(HistorySync sync)
        {
            var applied = new List<HistoryMessage>();
            if (sync == null) return applied;

            Learn(sync);
            FoldMappedRows();

            for (int i = 0; i < sync.Conversations.Count; i++)
            {
                HistoryConversation chunk = sync.Conversations[i];

                // The address of the conversation, in the order the fields exist: `jid`
                // (field 1) is the one the sync usually writes, and `pnJID` (39) and
                // `lidJID` (42) name the same account from the two sides of the LID pair.
                // A chunk that fills only the pair is a conversation all the same, and
                // skipping it is a chat list that stops at the rows whose `jid` happened to
                // be filled in - which is what a list that loses most of its chats looks
                // like from the outside.
                if (chunk.Jid == null) chunk.Jid = chunk.PnJid;
                if (chunk.Jid == null) chunk.Jid = chunk.LidJid;
                if (chunk.Jid == null) continue;

                // The chat is keyed on its canonical address: a `@lid` whose phone number this
                // sync named is the same conversation as that number, and two rows for one
                // person is what the canonical address exists to prevent (NameBook.Canonical).
                chunk.Jid = _names.Canonical(chunk.Jid);
                if (chunk.Jid == null) continue;

                // The history carries a `status@broadcast` conversation with every status of
                // the last day inside it. None of it is a chat, so none of it becomes a row.
                if (StatusFeed.IsStatus(chunk.Jid)) continue;

                HistoryConversation row = EnsureRow(chunk.Jid);

                // The count is the server's, but only on a chunk that brought something new.
                // WhatsApp re-delivers chunks after a reconnect (`SeenMessages` is why this
                // is safe), and a chunk whose every message is already here must not put back
                // a count the reader has cleared.
                bool fresh = false;
                row.Archived = row.Archived || chunk.Archived;
                if (chunk.Name != null) row.Name = chunk.Name;
                if (string.IsNullOrEmpty(row.Name)) row.Name = _names.NameFor(chunk.Jid);
                if (chunk.LastMessageTimestamp > row.LastMessageTimestamp) row.LastMessageTimestamp = chunk.LastMessageTimestamp;
                if (chunk.ConversationTimestamp > row.ConversationTimestamp) row.ConversationTimestamp = chunk.ConversationTimestamp;

                for (int m = 0; m < chunk.Messages.Count; m++)
                {
                    HistoryMessage message = chunk.Messages[m];

                    // The conversation is the chat, even when the key did not carry one: a
                    // message with no chat of its own would reach the app as a conversation of
                    // its own, next to the person it belongs to.
                    if (string.IsNullOrEmpty(message.Jid)) message.Jid = chunk.Jid;
                    message.Jid = _names.Canonical(message.Jid);

                    if (!AddMessage(row, message)) continue;

                    fresh = true;
                    message.IsHistory = true;
                    if (Shows(message)) applied.Add(message);
                }

                if (fresh) row.UnreadCount = chunk.UnreadCount;
            }

            for (int i = 0; i < sync.PushNames.Count; i++)
            {
                HistoryPushName name = sync.PushNames[i];
                HistoryConversation row = Find(name.Jid);
                if (row == null) continue;

                // A push name never overwrites a saved one: the address book is the person's
                // own word for the contact and the push name is the contact's word for
                // themselves. The book already resolves that order, so the row asks it.
                string known = _names.NameFor(row.Jid);
                if (known != null) row.Name = known;
                else if (name.Name != null) row.Name = name.Name;
            }

            if (sync.SyncType == HistorySync.InitialBootstrap || sync.Progress >= 100) IsSyncComplete = true;

            Order();

            return applied;
        }

        /// <summary>
        /// Whether a history message carries something this client can draw: words, or media it
        /// reads.
        ///
        /// The live path decides the same thing in the same words (`NativeConnection.ReadMediaAsync`
        /// asks `MediaMessages.Of` and leaves a message that answers null alone), and it is decided
        /// again here because the two inputs are not the same shape: a live message the client
        /// cannot read is one stanza it names and drops, while a history chunk carries hundreds, and
        /// a sticker or a reaction among them would otherwise arrive as one empty bubble per
        /// message - a conversation that looks broken where it used to look short.
        /// </summary>
        private static bool Shows(HistoryMessage message)
        {
            if (message.Text != null) return true;
            if (message.Encoded == null) return false;

            return MediaMessages.Of(Message.Decode(message.Encoded)) != null;
        }

        /// <summary>
        /// A message that has just arrived. False when it is one this client has already
        /// seen, which is the answer that keeps a re-delivery out of the chat.
        /// </summary>
        public bool AddIncoming(HistoryMessage message)
        {
            if (message == null || message.Jid == null) return false;

            // A status is not a chat, and a chat list that held one would show a row called
            // "status" for every contact who posted that morning
            // (`StatusFeed.IsStatus`; Review Focus 5).
            if (StatusFeed.IsStatus(message.Jid)) return false;

            message.Jid = _names.Canonical(message.Jid);
            if (message.Jid == null) return false;

            HistoryConversation row = EnsureRow(message.Jid);
            if (string.IsNullOrEmpty(row.Name)) row.Name = _names.NameFor(message.Jid);
            if (!AddMessage(row, message)) return false;

            // An incoming message is unread until someone reads it, and the row is the newest
            // one now by definition: it was just written.
            row.UnreadCount = row.UnreadCount + 1;
            Touch(row, message.Timestamp);
            Order();

            return true;
        }

        /// <summary>
        /// A stanza from a chat that arrived with nothing this app can draw.
        ///
        /// Why the row and not only the message: a chat exists whether or not this build has a
        /// reader for what was written in it. A sticker, a poll, an edit, a contact card or a
        /// kind of media nobody has written a decoder for yet still says "this conversation
        /// exists and it just moved" - and dropping the row together with the message is how a
        /// group could write ten times and never appear (the phone of 2026-10-23: five chats on
        /// screen, while a group sent ten messages into a list that never heard of it). Real
        /// WhatsApp lists a chat as soon as anything arrives in it.
        ///
        /// No message is added on purpose: an empty bubble is not a message either, so the row
        /// takes the place and the time and says nothing it does not know. False when this is
        /// not an address a chat can have.
        /// </summary>
        public bool NoteIncoming(string jid, long timestamp)
        {
            if (jid == null) return false;

            // The status list and the channels are not conversations, for the same reason as
            // in `AddIncoming`: one is its own screen and the other is never read here.
            if (StatusFeed.IsStatus(jid)) return false;

            jid = _names.Canonical(jid);
            if (jid == null) return false;

            HistoryConversation row = EnsureRow(jid);
            if (row == null) return false;
            if (string.IsNullOrEmpty(row.Name)) row.Name = _names.NameFor(jid);

            Touch(row, timestamp);
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

            jid = _names.Canonical(jid);
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

        /// <summary>
        /// The reader has seen this chat: its count goes back to zero, so the message that
        /// arrives next is one and not the pile that was already read. Without this the row
        /// hands the old count back with every later message, because `ApplyChat` copies it
        /// onto the chat-list row.
        /// </summary>
        public void MarkRead(string jid)
        {
            HistoryConversation row = Find(jid);
            if (row == null) return;

            row.UnreadCount = 0;
        }

        /// <summary>Every conversation at once: the reader cleared the whole list.</summary>
        public void MarkAllRead()
        {
            for (int i = 0; i < _rows.Count; i++) _rows[i].UnreadCount = 0;
        }

        /// <summary>How many messages of this chat are waiting, 0 for a chat never heard of.</summary>
        public int UnreadFor(string jid)
        {
            HistoryConversation row = Find(jid);
            return row == null ? 0 : row.UnreadCount;
        }

        /// <summary>The row for a chat, or null when the chat list has never heard of it.</summary>
        public HistoryConversation Find(string jid)
        {
            jid = _names.Canonical(jid);
            if (jid == null) return null;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Jid == jid) return _rows[i];
            }

            return null;
        }

        /// <summary>
        /// Teaches the chat list that a `@lid` and a phone number are one account, and folds
        /// the row the LID already had into the number's row.
        ///
        /// Why the fold and not only the mapping: a message that arrives before the history
        /// does not know the pair yet, so the row it creates is keyed on the LID. Learning the
        /// pair afterwards has to move that conversation to the number, or every such contact
        /// keeps a row under an address nothing will ever be sent to again.
        ///
        /// Returns true when a row was folded - the caller has a visible chat to merge in the
        /// pages - and false when the pair was already known or is not a pair.
        /// </summary>
        public bool LearnMapping(string lid, string phone)
        {
            // The pair may already be in the book (the caller persisted it first): the fold is
            // still owed to a row that was created before it was known.
            _names.RememberMapping(lid, phone);

            return FoldMappedRows();
        }

        /// <summary>
        /// Gives a row the best name the book has. Called after a live message taught a name
        /// to a row that was created by the history without one.
        /// </summary>
        public void RefreshName(HistoryConversation row)
        {
            if (row == null) return;

            string name = _names.NameFor(row.Jid);
            if (name != null) row.Name = name;
        }

        private HistoryConversation EnsureRow(string jid)
        {
            HistoryConversation row = Find(jid);
            if (row != null) return row;

            jid = _names.Canonical(jid);
            if (jid == null) return null;

            row = new HistoryConversation();
            row.Jid = jid;
            row.Name = _names.NameFor(jid);
            _rows.Add(row);

            return row;
        }

        /// <summary>
        /// The addresses this sync knows about, learned before its conversations are read.
        ///
        /// The order is what makes the fold unnecessary for a sync: the pairs and the saved
        /// names are in hand before the first row is created, so a conversation that arrives
        /// as a LID is keyed on its number from the start.
        /// </summary>
        private void Learn(HistorySync sync)
        {
            for (int i = 0; i < sync.LidMappings.Count; i++)
            {
                LidMapping mapping = sync.LidMappings[i];
                if (mapping == null) continue;

                _names.RememberMapping(mapping.Lid, mapping.PhoneNumber);
            }

            for (int i = 0; i < sync.InlineContacts.Count; i++)
            {
                InlineContact contact = sync.InlineContacts[i];
                if (contact == null) continue;

                if (!string.IsNullOrEmpty(contact.LidJid) && !string.IsNullOrEmpty(contact.PnJid))
                {
                    _names.RememberMapping(contact.LidJid, contact.PnJid);
                }

                string name = contact.FullName;
                if (string.IsNullOrEmpty(name)) name = contact.FirstName;
                if (string.IsNullOrEmpty(name)) continue;

                // The name is written under both addresses: the address book names the pair,
                // and a row that is still keyed on the LID (a sync that carried no pair) has
                // to find it too.
                if (!string.IsNullOrEmpty(contact.PnJid)) _names.RememberSavedName(contact.PnJid, name);
                if (!string.IsNullOrEmpty(contact.LidJid)) _names.RememberSavedName(contact.LidJid, name);
            }

            for (int i = 0; i < sync.Conversations.Count; i++)
            {
                HistoryConversation conversation = sync.Conversations[i];
                if (conversation == null) continue;

                if (!string.IsNullOrEmpty(conversation.LidJid) && !string.IsNullOrEmpty(conversation.PnJid))
                {
                    _names.RememberMapping(conversation.LidJid, conversation.PnJid);
                }
            }

            for (int i = 0; i < sync.PushNames.Count; i++)
            {
                HistoryPushName push = sync.PushNames[i];
                if (push == null) continue;

                _names.RememberPushName(push.Jid, push.Name);
            }
        }

        /// <summary>
        /// Folds every row that a pair learned since the last sync has just made redundant.
        /// A chunk can carry the pair only in a later chunk than the conversation, so the row
        /// exists under the LID before the mapping arrives.
        /// </summary>
        private bool FoldMappedRows()
        {
            var snapshot = new List<HistoryConversation>(_rows);
            bool folded = false;

            for (int i = 0; i < snapshot.Count; i++)
            {
                string jid = snapshot[i].Jid;
                string canonical = _names.Canonical(jid);
                if (canonical == null || canonical == jid) continue;

                if (Fold(jid, canonical)) folded = true;
            }

            return folded;
        }

        /// <summary>
        /// The row under exactly this address, canonicalising nothing: the fold looks for the
        /// row at the address it is leaving, and `Find` would send it to the one it is joining.
        /// </summary>
        private HistoryConversation FindExact(string jid)
        {
            if (jid == null) return null;

            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Jid == jid) return _rows[i];
            }

            return null;
        }

        /// <summary>
        /// Moves everything a row knew to another row and removes it. False when there was
        /// nothing to fold.
        /// </summary>
        private bool Fold(string from, string into)
        {
            if (from == into) return false;

            HistoryConversation gone = FindExact(from);
            if (gone == null) return false;

            HistoryConversation keep = EnsureRow(into);

            for (int i = 0; i < gone.Messages.Count; i++)
            {
                HistoryMessage message = gone.Messages[i];
                message.Jid = into;
                keep.Messages.Add(message);
            }

            keep.UnreadCount = Math.Max(keep.UnreadCount, gone.UnreadCount);
            keep.Archived = keep.Archived || gone.Archived;

            if (gone.LastMessageTimestamp > keep.LastMessageTimestamp) keep.LastMessageTimestamp = gone.LastMessageTimestamp;
            if (gone.ConversationTimestamp > keep.ConversationTimestamp) keep.ConversationTimestamp = gone.ConversationTimestamp;

            if (string.IsNullOrEmpty(keep.Name)) keep.Name = gone.Name;

            _rows.Remove(gone);
            Order();

            return true;
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
