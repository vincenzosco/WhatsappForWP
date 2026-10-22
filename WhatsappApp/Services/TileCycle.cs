using System.Collections.Generic;

namespace WhatsappApp.Services
{
    /// <summary>
    /// One conversation waiting on the tile: who wrote, what they said, and the file
    /// of their picture. It is what one notification of the queue shows.
    /// </summary>
    public sealed class TileEntry
    {
        /// <summary>The chat the message arrived in. It is the identity of the entry.</summary>
        public string ChatId;

        /// <summary>The name of the person who wrote, as the tile writes it.</summary>
        public string Sender;

        /// <summary>The message, which is the wide tile's second line.</summary>
        public string Body;

        /// <summary>The stored picture, or null when there is none and the app mark is used.</summary>
        public string Image;
    }

    /// <summary>
    /// Which conversations the live tile cycles through, and in what order.
    ///
    /// Why it exists: the tile used to be one notification, replaced in place, so it named
    /// one person and stayed on them until somebody else wrote. Windows has a notification
    /// queue that rotates what a tile shows, and the reference client uses it: with four
    /// people waiting, the tile is four faces rather than the last one forever.
    ///
    /// What went wrong the first time the queue was used - and what this rule avoids - is
    /// that every message pushed one more tile, each carrying the unread count of its own
    /// moment. The tile on screen kept showing a count nobody had verified since. Here the
    /// queue holds **one entry per conversation**, the newest first, and the count is drawn
    /// from the live total on every render, so rotating never shows a number that is not
    /// the truth right now.
    ///
    /// It is pure and it lives here, outside `NotificationService`, because the WinRT half
    /// cannot be run off the phone and this is the part that was wrong: `TileCycleTests`
    /// exercises exactly these two methods.
    /// </summary>
    public static class TileCycle
    {
        /// <summary>
        /// How many notifications the queue may hold. Five is the ceiling WP8.1 accepts:
        /// past it the oldest is dropped by the shell, so a sixth entry would only push
        /// out the one that was about to be shown.
        /// </summary>
        public const int MaxEntries = 5;

        /// <summary>
        /// The list with this conversation in front: a person who wrote twice is one entry
        /// again, moved to the top, and a sixth conversation pushes out the oldest.
        ///
        /// A new list is returned rather than the one that came in, so the caller's list is
        /// never mutated while another thread is drawing it.
        /// </summary>
        public static IList<TileEntry> Remember(IList<TileEntry> entries, TileEntry entry)
        {
            var next = new List<TileEntry>();

            if (entry != null && !string.IsNullOrEmpty(entry.ChatId)) next.Add(entry);

            if (entries != null)
            {
                for (int i = 0; i < entries.Count && next.Count < MaxEntries; i++)
                {
                    TileEntry kept = entries[i];
                    if (kept == null) continue;
                    if (entry != null && kept.ChatId == entry.ChatId) continue;

                    next.Add(kept);
                }
            }

            return next;
        }

        /// <summary>
        /// The list without one conversation: the chat was read, so its message is no
        /// longer waiting and must not keep a turn on the tile.
        /// </summary>
        public static IList<TileEntry> Forget(IList<TileEntry> entries, string chatId)
        {
            var next = new List<TileEntry>();

            if (entries == null) return next;

            for (int i = 0; i < entries.Count; i++)
            {
                TileEntry kept = entries[i];
                if (kept == null) continue;
                if (!string.IsNullOrEmpty(chatId) && kept.ChatId == chatId) continue;

                next.Add(kept);
            }

            return next;
        }
    }
}
