using System;
using Windows.System;

namespace WhatsappApp.Services
{
    /// <summary>What kind of phone this is running on, as far as the budget goes.</summary>
    public enum MemoryTier
    {
        /// <summary>A 512 MB device, or anything the app should treat like one.</summary>
        Low,

        /// <summary>Room for the whole list: the default, and what a 1 GB phone gets.</summary>
        Normal
    }

    /// <summary>
    /// The memory budget of the phone, and the work that scales with it.
    ///
    /// Why it exists: `MemoryWatcher` reacts once the system says the app is under pressure,
    /// which is *after* the damage - the list has already decoded two hundred pictures and the
    /// GC has already been asked for what it cannot give back. On a 512 MB phone the useful
    /// moment is earlier: the limit is known before the first frame (`AppMemoryUsageLimit`),
    /// so the same list can do less work instead of the same work and a trim.
    ///
    /// What it does **not** do: it does not free anything and it does not decide when a page
    /// draws. It answers four numbers - how many pictures to decode, at what width, how many
    /// chats to fetch them for, and how much of a conversation to show - and the callers that
    /// already exist (`DataService.RestoreAvatars`, `AvatarEnricher`) use them. One place to
    /// read a budget, instead of a conditional per page.
    ///
    /// The threshold: 600 MB covers a 512 MB phone with room for the system's own accounting,
    /// and a 1 GB phone is nowhere near it. The number is a class, not a measurement: the
    /// platform reports the app's limit, which is what this app can spend.
    /// </summary>
    public sealed class MemoryProfile
    {
        /// <summary>The highest limit still treated as a low-memory phone.</summary>
        public const ulong LowTierLimitBytes = 600UL * 1024UL * 1024UL;

        /// <summary>How many pictures the list decodes on a low-memory phone (one screenful).</summary>
        public const int LowDecodedAvatars = 24;

        /// <summary>And on any other phone: `AvatarCache.MaxChats`, the list it can cover.</summary>
        public const int NormalDecodedAvatars = 200;

        /// <summary>A picture of the list is drawn 40 px wide here and 68 px on a bigger phone.</summary>
        public const int LowAvatarDecodePx = 40;

        public const int NormalAvatarDecodePx = 68;

        private static readonly MemoryProfile InstanceHolder = new MemoryProfile();

        private bool _known;
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

        /// <summary>True on the 512 MB class: the callers that care ask this, not the numbers.</summary>
        public bool IsLow
        {
            get { return Tier == MemoryTier.Low; }
        }

        /// <summary>How many chat pictures are decoded at once.</summary>
        public int MaxDecodedAvatars
        {
            get { return IsLow ? LowDecodedAvatars : NormalDecodedAvatars; }
        }

        /// <summary>How many chats one pass of `AvatarEnricher` asks about, pictures or not.</summary>
        public int AvatarsToEnrich
        {
            get { return IsLow ? LowDecodedAvatars : NormalDecodedAvatars; }
        }

        /// <summary>The width a profile picture is decoded at.</summary>
        public int AvatarDecodePx
        {
            get { return IsLow ? LowAvatarDecodePx : NormalAvatarDecodePx; }
        }

        /// <summary>
        /// How many messages of a conversation the list binds when it opens, and how many
        /// older ones each pull towards the top adds.
        ///
        /// Why a page and not the collection: `DataService` keeps the whole conversation
        /// (ceilinged by `MaxMessagesPerChat`), and the list used to bind all of it before
        /// showing the newest row. On a 512 MB phone a long chat is the most expensive thing
        /// a page can ask XAML to measure, and the reader only ever looks at a screenful.
        /// </summary>
        public int MessagePageSize
        {
            get { return IsLow ? 30 : 100; }
        }

        /// <summary>
        /// Reads the limit once. Every failure is a normal phone: a platform that refuses the
        /// question must not be read as a 512 MB device, because the lower budget is a cut in
        /// what the app shows and guessing it wrong makes a phone do less than it can.
        /// </summary>
        private void Detect()
        {
            if (_known) return;
            _known = true;

            try
            {
                ulong limit = MemoryManager.AppMemoryUsageLimit;
                _tier = limit > 0 && limit <= LowTierLimitBytes ? MemoryTier.Low : MemoryTier.Normal;

                Diag.Ok("memory profile: " + _tier + " (limit " + (limit / (1024UL * 1024UL)) + " MB, "
                    + (IsLow ? "the list decodes " + LowDecodedAvatars + " pictures at " + LowAvatarDecodePx + " px"
                        : "the whole list") + ")");
            }
            catch (Exception ex)
            {
                _tier = MemoryTier.Normal;
                Diag.Failed("MemoryProfile.Detect", ex);
            }
        }
    }
}
