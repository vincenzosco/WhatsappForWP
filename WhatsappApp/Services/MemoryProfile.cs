using System;
using Windows.System;

namespace WhatsappApp.Services
{
    /// <summary>What kind of phone this is running on, as far as the budget goes.</summary>
    public enum MemoryTier
    {
        /// <summary>A 512 MB device: the limit is about 185 MB.</summary>
        Low,

        /// <summary>A 1 GB device: the limit is about 390 MB.</summary>
        Medium,

        /// <summary>A 2 GB device: the limit is about 780 MB.</summary>
        High,

        /// <summary>Anything with more room, and the answer when the platform does not say.</summary>
        Full
    }

    /// <summary>
    /// The memory budget of the phone, and the work that scales with it.
    ///
    /// `MemoryWatcher` reacts once the system says the app is under pressure,
    /// which is *after* the damage - the list has already decoded two hundred pictures and the
    /// GC has already been asked for what it cannot give back. On a cramped phone the useful
    /// moment is earlier: the limit is known before the first frame
    /// (`AppMemoryUsageLimit`), so the same list can do less work instead of the same work
    /// and a trim.
    ///
    /// Why four classes and not two: `AppMemoryUsageLimit` is the *app's* share, not the
    /// phone's RAM, and it is roughly 185 MB on a 512 MB device, 390 MB on a 1 GB one and
    /// 780 MB on a 2 GB one. A single threshold - "600 MB or less is small" - therefore
    /// called a 1 GB phone small and a 2 GB phone normal, which is one class too few in
    /// both directions: the 2 GB phone did a 1 GB phone's work, and the 1 GB phone did the
    /// 512 MB one's. The four classes are those three phones plus whatever comes after, and
    /// the numbers below are per class, not one number bolted onto a condition.
    ///
    /// What it does **not** do: it does not free anything and it does not decide when a page
    /// draws. It answers what the callers ask - how many pictures to decode, at what width,
    /// how many chats to fetch them for, how much of a conversation the list binds, and how
    /// long a conversation is held in memory - and the callers that already exist
    /// (`DataService.RestoreAvatars`, `AvatarEnricher`, `Contact.LoadAvatarAsync`,
    /// `ChatPage`) use them. One place to read a budget, instead of a conditional per page.
    /// </summary>
    public sealed class MemoryProfile
    {
        /// <summary>
        /// The highest app limit still treated as a 512 MB phone. The limit on that class is
        /// about 185 MB, so 200 covers it with room for the system's own accounting.
        /// </summary>
        public const ulong LowTierLimitBytes = 200UL * 1024UL * 1024UL;

        /// <summary>The highest app limit still treated as a 1 GB phone (about 390 MB there).</summary>
        public const ulong MediumTierLimitBytes = 420UL * 1024UL * 1024UL;

        /// <summary>The highest app limit still treated as a 2 GB phone (about 780 MB there).</summary>
        public const ulong HighTierLimitBytes = 900UL * 1024UL * 1024UL;

        /// <summary>
        /// How many pictures the list decodes at once: one screenful on a 512 MB phone (24),
        /// three on a 1 GB one (70), the whole list `AvatarCache` covers on 2 GB and above.
        /// </summary>
        public const int LowDecodedAvatars = 24;

        public const int MediumDecodedAvatars = 70;

        public const int HighDecodedAvatars = 140;

        public const int FullDecodedAvatars = 200;

        /// <summary>
        /// The width a picture of the list is decoded at: the row box is 52 px, so 2 GB and
        /// above decode at 68 (the box plus its border), 1 GB at the box itself, and a 512 MB
        /// phone at 40 - a third of the pixels, and the difference is not visible at that size.
        /// </summary>
        public const int LowAvatarDecodePx = 40;

        public const int MediumAvatarDecodePx = 52;

        public const int HighAvatarDecodePx = 68;

        public const int FullAvatarDecodePx = 68;

        /// <summary>
        /// Messages of a conversation the list binds at once, and how much older each pull
        /// towards the top adds: 30 rows on a 512 MB phone, 60 on 1 GB, 100 on 2 GB and above.
        /// </summary>
        public const int LowMessagePageSize = 30;

        public const int MediumMessagePageSize = 60;

        public const int HighMessagePageSize = 100;

        public const int FullMessagePageSize = 100;

        /// <summary>
        /// How long a conversation is kept in memory, in messages: 300 rows on a 512 MB phone,
        /// 1500 on the phones with room. A text row is a few hundred bytes, so even the
        /// largest figure is a couple of MB - but it is a per-chat ceiling, and the list holds
        /// one conversation open at a time, so the smallest phone is the one that pays.
        /// </summary>
        public const int LowMessagesPerChat = 300;

        public const int MediumMessagesPerChat = 600;

        public const int HighMessagesPerChat = 1000;

        public const int FullMessagesPerChat = 1500;

        private static readonly MemoryProfile InstanceHolder = new MemoryProfile();

        private bool _known;
        private ulong _limit;
        private MemoryTier _tier;

        public static MemoryProfile Instance
        {
            get { return InstanceHolder; }
        }

        /// <summary>Which class this phone is in. Read once, then answered from the log's own line.</summary>
        public MemoryTier Tier
        {
            get
            {
                Detect();
                return _tier;
            }
        }

        /// <summary>The app's own limit in bytes, as the platform reported it (0 when it did not).</summary>
        public ulong LimitBytes
        {
            get
            {
                Detect();
                return _limit;
            }
        }

        /// <summary>True on the 512 MB class: the callers that care ask this, not the numbers.</summary>
        public bool IsLow
        {
            get { return Tier == MemoryTier.Low; }
        }

        /// <summary>
        /// The class of a given limit. Pure, so the four numbers can be read against a phone
        /// that is not the one running this.
        ///
        /// A limit of zero - a platform that refuses the question - is `Full` on purpose: the
        /// lower budgets are a cut in what the app shows, and guessing them wrong makes a
        /// phone do less than it can. A phone that is really small announces itself as `Low`
        /// before the first frame, which is the whole point of reading the limit early.
        /// </summary>
        public static MemoryTier TierFor(ulong limitBytes)
        {
            if (limitBytes == 0) return MemoryTier.Full;
            if (limitBytes <= LowTierLimitBytes) return MemoryTier.Low;
            if (limitBytes <= MediumTierLimitBytes) return MemoryTier.Medium;
            if (limitBytes <= HighTierLimitBytes) return MemoryTier.High;
            return MemoryTier.Full;
        }

        /// <summary>How many chat pictures are decoded at once.</summary>
        public int MaxDecodedAvatars
        {
            get { return PerTier(LowDecodedAvatars, MediumDecodedAvatars, HighDecodedAvatars, FullDecodedAvatars); }
        }

        /// <summary>How many chats one pass of `AvatarEnricher` asks about, pictures or not.</summary>
        public int AvatarsToEnrich
        {
            get { return MaxDecodedAvatars; }
        }

        /// <summary>The width a profile picture is decoded at.</summary>
        public int AvatarDecodePx
        {
            get { return PerTier(LowAvatarDecodePx, MediumAvatarDecodePx, HighAvatarDecodePx, FullAvatarDecodePx); }
        }

        /// <summary>
        /// How many messages of a conversation the list binds when it opens, and how many
        /// older ones each pull towards the top adds.
        ///
        /// Why a page and not the collection: `DataService` keeps the conversation up to
        /// `MessagesPerChat`, and the list used to bind all of it before showing the newest
        /// row. A long chat is the most expensive thing a page can ask XAML to measure, and
        /// the reader only ever looks at a screenful.
        /// </summary>
        public int MessagePageSize
        {
            get { return PerTier(LowMessagePageSize, MediumMessagePageSize, HighMessagePageSize, FullMessagePageSize); }
        }

        /// <summary>How long a conversation is held in memory. `DataService` trims to it.</summary>
        public int MessagesPerChat
        {
            get { return PerTier(LowMessagesPerChat, MediumMessagesPerChat, HighMessagesPerChat, FullMessagesPerChat); }
        }

        /// <summary>The four numbers of one class, in the order Low, Medium, High, Full.</summary>
        private int PerTier(int low, int medium, int high, int full)
        {
            switch (Tier)
            {
                case MemoryTier.Low: return low;
                case MemoryTier.Medium: return medium;
                case MemoryTier.High: return high;
                default: return full;
            }
        }

        /// <summary>
        /// Reads the limit once. Every failure is a full phone: a platform that refuses the
        /// question must not be read as a cramped device, because the lower budget is a cut
        /// in what the app shows and guessing it wrong makes a phone do less than it can.
        /// </summary>
        private void Detect()
        {
            if (_known) return;
            _known = true;

            try
            {
                _limit = MemoryManager.AppMemoryUsageLimit;
                _tier = TierFor(_limit);

                Diag.Ok("memory profile: " + _tier + " (limit " + (_limit / (1024UL * 1024UL)) + " MB, "
                    + "the list decodes " + MaxDecodedAvatars + " pictures at " + AvatarDecodePx + " px, "
                    + MessagePageSize + " messages a page, " + MessagesPerChat + " a chat)");
            }
            catch (Exception ex)
            {
                _limit = 0;
                _tier = MemoryTier.Full;
                Diag.Failed("MemoryProfile.Detect", ex);
            }
        }
    }
}
