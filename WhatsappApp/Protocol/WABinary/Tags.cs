namespace WhatsappApp.Protocol.WABinary
{
    /// <summary>
    /// The tag bytes of the WhatsApp binary format. They are the values of `TAGS`
    /// in the reference; the names are the ones the format is discussed with.
    ///
    /// `ListEmpty` doubles as the tag that means "no string here", and
    /// <see cref="Dictionary0"/>..<see cref="Dictionary3"/> are also the selectors
    /// of the two-byte token dictionaries - which is why the single-byte token table
    /// stops at 236 entries and the reader must test the table before the switch.
    /// </summary>
    public static class Tags
    {
        public const int ListEmpty = 0;
        public const int Dictionary0 = 236;
        public const int Dictionary1 = 237;
        public const int Dictionary2 = 238;
        public const int Dictionary3 = 239;
        public const int InteropJid = 245;
        public const int FbJid = 246;
        public const int AdJid = 247;
        public const int List8 = 248;
        public const int List16 = 249;
        public const int JidPair = 250;
        public const int Hex8 = 251;
        public const int Binary8 = 252;
        public const int Binary20 = 253;
        public const int Binary32 = 254;
        public const int Nibble8 = 255;

        /// <summary>The longest string that may be nibble- or hex-packed.</summary>
        public const int PackedMax = 127;
    }
}
