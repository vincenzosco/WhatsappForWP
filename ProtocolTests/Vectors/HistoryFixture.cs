namespace ProtocolTests.Vectors
{
    /// <summary>
    /// A history bootstrap and a history notification, as the reference itself encodes them.
    ///
    /// Regenerated with, inside `/tmp/m4ref` (Baileys 7.x, a temporary checkout outside this
    /// repository - a fixture nobody can regenerate is a fixture nobody can trust):
    ///
    ///     node -e "const p=require('@whiskeysockets/baileys').proto, zlib=require('zlib'); \
    ///       const s=p.HistorySync.encode({syncType:2, conversations:[ ... ]}).finish(); \
    ///       console.log(s.toString('hex')); console.log(zlib.deflateSync(Buffer.from(s)).toString('hex'));"
///
/// The keys of the `md-msg-hist` blob come from the same checkout, over a media key of 32
/// bytes of `0x07`:
///
///     node --input-type=module -e "import * as m from
///       './node_modules/@whiskeysockets/baileys/lib/Utils/messages-media.js';
///       console.log(m.hkdfInfoKey('md-msg-hist'));
///       const k = await m.getMediaKeys(Buffer.alloc(32, 7), 'md-msg-hist');
///       console.log(Buffer.from(k.iv).toString('hex')); ..."
///
/// The blob behind a download is printed by the same checkout
/// (`/tmp/m4ref/gen-history-blob.mjs`), over a media key of 32 bytes of `0x07`: `Plain`
/// deflated to 253 bytes, encrypted with the history cipher key and MAC-ed to 266. The
/// notification's `fileSha256` and `fileLength` are taken over the *compressed* payload,
/// because that is what the media path hashes and measures before the inflate.
    ///
    /// The field numbers below are not read off the protocol documentation: they are the
    /// reference's own, printed by encoding one field at a time and reading the tag varint
    /// back out (`/tmp/m4ref/history-tags.js`):
    ///
    ///     HistorySync        syncType 1, conversations 2, chunkOrder 5, progress 6,
    ///                        pushnames 7, callLogRecords 13
    ///     Conversation       id 1, messages 2, lastMsgTimestamp 5, unreadCount 6,
    ///                        conversationTimestamp 12, name 13, archived 16,
    ///                        participant 20, pinned 24, displayName 38, pnJid 39
    ///     HistorySyncMsg     message 1, msgOrderId 2
    ///     WebMessageInfo     key 1, message 2, messageTimestamp 3, status 4,
    ///                        participant 5, pushName 19
    ///     MessageKey         remoteJid 1, fromMe 2, id 3, participant 4
    ///     CallLogRecord      callResult 1, duration 4, startTime 5, isIncoming 6,
    ///                        isVideo 7, callId 11, callCreatorJid 12, groupJid 13,
    ///                        participants 14
    ///     Message.HistorySyncNotification
    ///                        fileSha256 1, fileLength 2, mediaKey 3, fileEncSha256 4,
    ///                        directPath 5, syncType 6, chunkOrder 7, originalMessageId 8,
    ///                        initialHistBootstrapInlinePayload 11
    ///
    /// The bootstrap carries two conversations on purpose: one person with two messages
    /// (one of them ours) and one group with one, so the order, the two directions and the
    /// group case are all in the same vector.
    /// </summary>
    public static class HistoryFixture
    {
        /// <summary>Two conversations, a push name and a call log record, 471 bytes.</summary>
        public const string Plain =
            "080212b9010a1b33393330303030303030303040732e77686174736170702e6e657412440a400a2d0a1b3339333030303030" +
            "3030303040732e77686174736170702e6e657410001a0c33454230414141413030303112090a076369207365693f1880cba6" +
            "d606100112450a410a2d0a1b33393330303030303030303040732e77686174736170702e6e657410011a0c33454230414141" +
            "4130303032120a0a08736f6e6f2071756918bccba6d606100230026080cba6d6066a054d6172636f12b1010a173132303336" +
            "3330303030303030303030303040672e75731285010a80010a460a1731323033363330303030303030303030303040672e75" +
            "7310001a0c334542304242424230303031221b33393331313131313131313140732e77686174736170702e6e6574120c0a0a" +
            "62756f6e67696f726e6f18f0fca5d6062a1b33393331313131313131313140732e77686174736170702e6e65749a01044c75" +
            "63611003300060f0fca5d6066a0647727570706f30323a2a0a1b33393330303030303030303040732e77686174736170702e" +
            "6e6574120b4d6172636f20526f7373696a350801202a28e0aea5d606300138005a0843414c4c30303031621b333933303030" +
            "30303030303040732e77686174736170702e6e6574";

        /// <summary>The same bootstrap, deflated the way the inline payload travels.</summary>
        public const string Inline =
            "789ce36012dac9c8256d6c696c00030ec57ae5198925c58905057a79a925422e5c0e5cba7895083048f118bb3a19383a3a3a" +
            "1a1818180a7172b127672a14a766da4b349c5e768d4d8051c895cb9190298c28a6180971717114e7e7e52b1496664aec011b" +
            "c364c0940036308bd537b128395f68232397b8a19181b119c25490c1e97aa5c542ad8c5c0d8c5c6e3815c05cede4e4e40472" +
            "b512c8718630801e0a3c5c5c49a5f979e999f94579f9121ffe2cbdc6a6854fc32c46169fd2e4440166038604b0ea2c36f7a2" +
            "d282827c03232b2dfc01ce0df69b42507e717166962907a38296c683754bafb119305a304471383bfaf8809c9b84cf0c0078" +
            "0280da";

        /// <summary>A notification pointing at that payload: 32-byte keys of 1, 2 and 3.</summary>
        public const string Notification =
            "0a20010101010101010101010101010101010101010101010101010101010101010110d2091a200202020202020202020202" +
            "0202020202020202020202020202020202020202022220030303030303030303030303030303030303030303030303030303" +
            "03030303032a082f762f7436322f783002380142083345423043434343";

        /// <summary>The info string the history keys are expanded with: `hkdfInfoKey('md-msg-hist')`.</summary>
        public const string HistoryInfo = "WhatsApp History Keys";

        /// <summary>The three keys for a media key of 32 bytes of `0x07`, as the reference derives them.</summary>
        public const string HistoryIv = "8c5afed3583347a1449e12ab5f2d8deb";
        public const string HistoryCipherKey =
            "37af562252ffce1c9ebf55e1cf225cd87484873e854e5fd5ef2c7ae4071d4509";
        public const string HistoryMacKey =
            "1c532784afa512320359296081cb8e6f055e43ec9f672a218e9c3ae161808504";

        /// <summary>
        /// The notification that hides the history behind a download: a 32-byte media key of
        /// `0x07`, `directPath` `/v/t62/hist`, and the hashes of the compressed payload.
        /// </summary>
        public const string DownloadedNotification =
            "0a205349dde6616bf60377e20c596431f0e807f24dbf9c2fc9bfb9dd0fe68d2c" +
            "a94110fd011a2007070707070707070707070707070707070707070707070707" +
            "070707070707072220ee8c02a85455fae9812c520e51067595bd0236e73eda44" +
            "2097de980fbbfc50d42a0b2f762f7436322f686973743002";

        /// <summary>The body that path serves: 266 bytes, ciphertext and its ten-byte MAC.</summary>
        public const string EncryptedBody =
            "70533d3a21d57869bba232a1b4c3a7067159a3611c1fb966ae8e9bec0278c363" +
            "d9dd5841a457d85a3dd6681050c27149af3ef9736535ec421748245239148fb5" +
            "196dd471f368bbf841561a18d763763ea87c0be5ed25b49c0db02dcc71df0c7f" +
            "4521d6a935bb4a05b1afbf3d7578b570d6a3cc49eca17d5b5a9f2bd1dc01e67d" +
            "f503729011bf8afb2816e68178c9b5303859da8033f94a7dd5c3ee4756fb69da" +
            "2526d12b9ce72fb71386d57b17b9bcbb0f592ba38f31462f959aef970002a2ee" +
            "89c66c102b1a7f694a395991f1d6339319c1bdf02d9446f3001959f2f19d6ea1" +
            "af9daf26101bfea30d6c6ef4cc30d37da1089b9cdfd17985bb1b2b6821d13689" +
            "ee1c1ab78ed1d16cd58f";
    }
}
