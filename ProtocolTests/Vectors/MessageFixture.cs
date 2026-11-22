namespace ProtocolTests
{
    /// <summary>
    /// The reference's own bytes for a `Message`, and the command that prints them.
    ///
    /// Printed from a temporary installation of the reference outside this repository:
    ///
    /// mkdir -p /tmp/m4ref && cd /tmp/m4ref && npm i @whiskeysockets/baileys
    /// node -e "const p=require('@whiskeysockets/baileys').proto;
    /// const hex=m=&gt;Buffer.from(p.Message.encode(m).finish()).toString('hex');
    /// console.log('text ', hex({conversation:'Hello, WhatsApp!'}));
    /// console.log('extended ', hex({extendedTextMessage:{text:'Hi'}}));
    /// console.log('skdm ', hex({senderKeyDistributionMessage:{groupId:'g',
    /// axolotlSenderKeyDistributionMessage:Buffer.from([1,2,3])}}));
    /// console.log('protocol ', hex({protocolMessage:{type:0,
    /// key:{id:'ID', remoteJid:'r@s.whatsapp.net'}}}));
    /// console.log('deviceSent ', hex({deviceSentMessage:{destinationJid:'r@s.whatsapp.net',
    /// message:{conversation:'Hello, WhatsApp!'}}}));"
    ///
    /// and the five lines it printed, in that order:
    ///
    /// text 0a1048656c6c6f2c20576861747341707021
    /// extended 32040a024869
    /// skdm 12080a01671203010203
    /// protocol 621a0a160a107240732e77686174736170702e6e65741a0249441000
    /// deviceSent fa01260a107240732e77686174736170702e6e657412120a1048656c6c6f2c20576861747341707021
    /// reaction f202200a180a107240732e77686174736170702e6e657410011a0249441204f09f918d
    /// reply 32310a0248698a012a0a02494412107040732e77686174736170702e6e65741a120a1048656c6c6f2c20576861747341707021
    ///
    /// The field numbers are the reference's arithmetic, not anyone's memory of them:
    /// 0x0a is field 1, 0x12 field 2, 0x32 field 6, 0x62 field 12, and 0xfa 0x01 is the
    /// varint 250 that is field 31 with wire type 2. Inside the device-sent form,
    /// `destinationJid` is field 1 and the wrapped message is field 2. The reaction's
    /// 0xf2 0x02 is the varint 370: field 46 with wire type 2, whose `key` is field 1
    /// and whose `text` is field 2 - the emoji's four bytes f0 9f 91 8d are its UTF-8.
    /// The reply's 0x8a 0x01 is the varint 138: `contextInfo` at field 17, with the id
    /// of the message answered at field 1, its author at field 2 and its words at field 3.
    ///
    /// Nothing here was typed.
    /// </summary>
    public static class MessageFixture
    {
        public const string TextBody = "Hello, WhatsApp!";

        public const string TextEncoded = "0a1048656c6c6f2c20576861747341707021";

        public const string ExtendedTextEncoded = "32040a024869";

        public const string SenderKeyDistributionEncoded = "12080a01671203010203";

        public const string ProtocolEncoded =
            "621a0a160a107240732e77686174736170702e6e65741a0249441000";

        public const string DeviceSentEncoded =
            "fa01260a107240732e77686174736170702e6e657412120a1048656c6c6f2c20576861747341707021";

        /// <summary>The JID inside <see cref="DeviceSentEncoded"/>'s `destinationJid`.</summary>
        public const string DeviceSentTo = "r@s.whatsapp.net";

        /// <summary>
        /// The emoji the reaction line answered `ID` with, and the three names of the
        /// message it answered.
        /// </summary>
        public const string ReactionEmoji = "\ud83d\udc4d";
        public const string ReactionTo = "ID";
        public const string ReactionChat = "r@s.whatsapp.net";

        public const string ReactionEncoded =
            "f202200a180a107240732e77686174736170702e6e657410011a0249441204f09f918d";

        /// <summary>The words of the reply line and of the message it answers.</summary>
        public const string ReplyBody = "Hi";
        public const string ReplyQuotedBody = "Hello, WhatsApp!";
        public const string ReplyQuotedId = "ID";
        public const string ReplyQuotedAuthor = "p@s.whatsapp.net";

        public const string ReplyEncoded =
            "32310a0248698a012a0a02494412107040732e77686174736170702e6e65741a120a1048656c6c6f2c20576861747341707021";
    }
}
