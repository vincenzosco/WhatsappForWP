using System.Collections.Generic;
using WhatsappApp.Protocol.WaProto;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// One status, as it arrives: whose it is, which message it is, when it was posted and
    /// what it carries.
    ///
    /// It is the plain shape the two paths share - the history's status conversation and a
    /// live message - so the app reads a status the same way whichever one brought it, which
    /// is the same reason `HistoryMessage` is the plain shape of a chat message.
    /// </summary>
    public sealed class StatusPost
    {
        /// <summary>
        /// The contact who posted it. Not the list: `status@broadcast` is the chat, and the
        /// author is the `participant` (a group's shape, and `whatsmeow` reads it the same
        /// way - `message.go`, `parseMessageSource`).
        /// </summary>
        public string Author;

        /// <summary>The status message's id, which is what the view receipt names.</summary>
        public string Id;

        /// <summary>When it was posted, in seconds.</summary>
        public uint Timestamp;

        /// <summary>The words, or a photo's caption, or null for a plain photo.</summary>
        public string Text;

        /// <summary>
        /// The encoded `Message`, when the status carries a photo or a video. Null for one
        /// that is words alone. It is kept whole for the same reason a chat message's is:
        /// the keys and the address a later fetch needs are inside it.
        /// </summary>
        public byte[] Encoded;
    }

    /// <summary>
    /// Status: somebody's photo or sentence that lives for a day.
    ///
    /// Status is not a chat and every part of this file exists because of that one fact:
    ///
    /// - its JID is `status@broadcast`, a broadcast list and not a person and not a group,
    ///   so `IsStatus` is what keeps a status out of the chat list
    ///   (`whatsmeow`, `send.go`, `types.StatusBroadcastJID`);
    /// - a status is sent to **everybody who should see it**, so it needs the same device
    ///   fan-out a group does (`ListQuery` asks the one `usync` for the whole list, which is
    ///   the reference's `getUSyncDevices`);
    /// - and reading one is not reading a message: it is a `read` receipt sent to the status
    ///   list that names the status (`Viewed`), which is what makes the author's ring fill.
    /// </summary>
    public static class StatusFeed
    {
        /// <summary>The broadcast list every status lives in.</summary>
        public const string BroadcastJid = "status@broadcast";

        /// <summary>
        /// Whether a JID is a status: the broadcast list itself, or one of its devices
        /// (`status:5@broadcast`), which is how a status message's `from` is spelled when it
        /// comes from a device and not the list.
        /// </summary>
        public static bool IsStatus(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return false;

            int at = jid.IndexOf('@');
            if (at < 0) return false;

            string user = jid.Substring(0, at);
            int colon = user.IndexOf(':');
            if (colon >= 0) user = user.Substring(0, colon);

            return user == "status"
                && jid.Substring(at).StartsWith("@broadcast", System.StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The statuses a history sync brought.
        ///
        /// The statuses live in two places and both are read, because WhatsApp uses both:
        ///
        /// - `HistorySync.statusV3Messages` (field 3) is the one a status sync carries - its
        ///   `syncType` is `InitialStatusV3` and this list is the whole payload, so it is what
        ///   fills the feed the first time the app is opened on a phone with a day of
        ///   statuses waiting;
        /// - a `status@broadcast` conversation, for a sync that carried the statuses as a
        ///   chat (`NativeChats.ApplyHistory` skips that same conversation, and this is where
        ///   its contents go instead).
        ///
        /// A status this account posted from another phone is left out: it is not somebody
        /// else's update, and this client has no name of its own to put on it. Whichever
        /// place a status came from, its id is what keeps a re-sent sync from showing it
        /// twice (`NativeConnection.KnownStatus`).
        /// </summary>
        public static IList<StatusPost> FromHistory(HistorySync sync)
        {
            var posts = new List<StatusPost>();
            if (sync == null) return posts;

            for (int m = 0; m < sync.StatusMessages.Count; m++)
            {
                Add(posts, sync.StatusMessages[m], null);
            }

            for (int c = 0; c < sync.Conversations.Count; c++)
            {
                HistoryConversation chunk = sync.Conversations[c];
                if (chunk == null || !IsStatus(chunk.Jid)) continue;

                for (int m = 0; m < chunk.Messages.Count; m++)
                {
                    Add(posts, chunk.Messages[m], chunk.Jid);
                }
            }

            return posts;
        }

        /// <summary>
        /// One message of a status, as a post. `list` is the broadcast JID when the status
        /// came out of a conversation, and null when it came from `statusV3Messages`.
        /// </summary>
        private static void Add(IList<StatusPost> posts, HistoryMessage message, string list)
        {
            if (message == null || message.IsFromMe) return;
            if (string.IsNullOrEmpty(message.Id)) return;

            var post = new StatusPost();
            // The author is the participant, without their device - a status written from a
            // linked phone arrives as `15551230000:5@s.whatsapp.net` and the feed shows a
            // name, not an address. A message that names none falls back to the list it was
            // found in, which leaves it readable instead of dropping it with no name at all.
            post.Author = JidUtils.Normalize(!string.IsNullOrEmpty(message.Sender) ? message.Sender
                : (!string.IsNullOrEmpty(message.Jid) ? message.Jid : list));
            post.Id = message.Id;
            post.Timestamp = message.Timestamp > 0 && message.Timestamp <= uint.MaxValue
                ? (uint)message.Timestamp : 0;
            post.Text = message.Text;
            post.Encoded = message.Encoded;

            if (string.IsNullOrEmpty(post.Author)) return;

            posts.Add(post);
        }

        /// <summary>
        /// Who should see a status: the device list of every name on it, in one `usync`, in
        /// the order given. The same query a group fan-out uses, because it is the same
        /// question - which devices does each of these JIDs have.
        /// </summary>
        public static BinaryNode ListQuery(IList<string> jids, IRandomSource random)
        {
            if (jids == null || jids.Count == 0) throw new ProtocolException("a status list has at least one name on it");

            return DeviceDirectory.Query(jids, random);
        }

        /// <summary>
        /// The receipt that says a status was watched, and the only thing that makes the
        /// author's ring fill: a `read` receipt **to the broadcast list** carrying the
        /// status's id and, as `participant`, whose update it is.
        ///
        /// Why not to the author directly: a status is not a chat. Its message lives in
        /// `status@broadcast`, and the receipts that come back for our own statuses have
        /// exactly this shape - `from="status@broadcast"` plus a `participant` (the phone's
        /// log of 2026-10-22 is full of them, `class="status"`). Addressed to the person
        /// instead, the receipt named a message their one-to-one chat with us did not have:
        /// the ring never filled, and nothing said so.
        /// </summary>
        public static BinaryNode Viewed(string author, string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a status is watched by its id");

            return IncomingMessage.Receipt(BroadcastJid, new List<string> { id }, "read", 0, author);
        }
    }
}
