namespace WhatsappApp.Protocol.WaProto
{
    /// <summary>
    /// `waCompanionReg.DeviceProps`: what kind of client this is, as the account's
    /// device list and the history sync engine see it.
    ///
    /// The field numbers are the ones in the reference's `WACompanionReg.proto` (os 1,
    /// version 2, platformType 3, requireFullSync 4, historySyncConfig 5), and the
    /// values are the ones a companion that wants a usable history sends: the same
    /// switches the reference sets, with a storage quota and a thumbnail window it
    /// also sends. `platformType` is UWP, which the enum really carries and which is
    /// what a Windows Phone client is; the pairing QR's client type is derived from
    /// the same word (`PairingQr.ClientTypeUwp`).
    /// </summary>
    public static class DeviceProps
    {
        public const int OsField = 1;
        public const int VersionField = 2;
        public const int PlatformTypeField = 3;
        public const int RequireFullSyncField = 4;
        public const int HistorySyncConfigField = 5;

        /// <summary>`DeviceProps.PlatformType.UWP` in the reference's enum.</summary>
        public const int PlatformTypeUwp = 21;

        /// <summary>
        /// What the device list shows as this client's operating system. The phone is
        /// a Windows Phone 8.1 device and says so rather than claiming a browser.
        /// </summary>
        public const string OsName = "Windows Phone";

        /// <summary>
        /// A `DeviceProps` with the history sync settings the reference sends. The
        /// booleans are the sync features that have to be advertised for the server
        /// to send the corresponding parts of the history; a client that omits them
        /// gets a chat list with no history in it.
        /// </summary>
        public static byte[] Encode(uint versionPrimary, uint versionSecondary, uint versionTertiary)
        {
            byte[] version = new ProtoWriter()
                .Varint(1, versionPrimary)
                .Varint(2, versionSecondary)
                .Varint(3, versionTertiary)
                .ToArray();

            byte[] historySyncConfig = new ProtoWriter()
                .Varint(3, HistoryStorageQuotaMb)          // storageQuotaMb
                .Varint(4, 1)                              // inlineInitialPayloadInE2EeMsg
                .Varint(6, 1)                              // supportCallLogHistory
                .Varint(7, 1)                              // supportBotUserAgentChatHistory
                .Varint(8, 1)                              // supportCagReactionsAndPolls
                .Varint(9, 1)                              // supportBizHostedMsg
                .Varint(10, 1)                             // supportRecentSyncChunkMessageCountTuning
                .Varint(11, 1)                             // supportHostedGroupMsg
                .Varint(12, 1)                             // supportFbidBotChatHistory
                .Varint(14, 1)                             // supportMessageAssociation
                .Varint(15, 1)                             // supportGroupHistory
                .Varint(19, HistoryThumbnailSyncDays)      // thumbnailSyncDaysLimit
                .Varint(21, 1)                             // supportManusHistory
                .Varint(22, 1)                             // supportHatchHistory
                .ToArray();

            return new ProtoWriter()
                .Text(OsField, OsName)
                .Bytes(VersionField, version)
                .Varint(PlatformTypeField, PlatformTypeUwp)
                .Varint(RequireFullSyncField, 0)
                .Bytes(HistorySyncConfigField, historySyncConfig)
                .ToArray();
        }

        /// <summary>The quota the reference asks for: 10 GB.</summary>
        public const uint HistoryStorageQuotaMb = 10240;

        /// <summary>Thumbnails for messages this old are not synced, as the reference has it.</summary>
        public const uint HistoryThumbnailSyncDays = 60;
    }
}
