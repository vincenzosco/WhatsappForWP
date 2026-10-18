using System;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    /// <summary>
    /// The `Message` protobuf, checked against the reference's own bytes
    /// (<see cref="MessageFixture"/>) rather than against itself: an encoder and a
    /// decoder that agree with each other and with nothing else still ship a symbol
    /// WhatsApp refuses.
    ///
    /// The four field numbers this milestone writes are pinned here too - 1, 2, 6, 12
    /// and 31 - because a hand-written protobuf's field numbers are the one thing the
    /// compiler cannot check.
    /// </summary>
    public static class MessageTests
    {
        [Test]
        public static void TheReferenceTextMessageIsReproducedByteForByte()
        {
            byte[] expected = Hex(MessageFixture.TextEncoded);

            Assert.ByteArraysEqual(expected, Message.Text(MessageFixture.TextBody).Encode());
            Assert.AreEqual(MessageFixture.TextBody, Message.Decode(expected).Conversation);
        }

        [Test]
        public static void EachFieldDecodesFromTheReferencesOwnBytes()
        {
            Assert.AreEqual(MessageFixture.TextBody,
                Message.Decode(Hex(MessageFixture.TextEncoded)).Conversation);

            // Field 2: groupId 1, the distribution message 2.
            Assert.AreEqual(8,
                Message.Decode(Hex(MessageFixture.SenderKeyDistributionEncoded))
                    .SenderKeyDistributionMessage.Length);

            // Field 6: ExtendedText, whose own text is field 1.
            Assert.AreEqual(4,
                Message.Decode(Hex(MessageFixture.ExtendedTextEncoded))
                    .ExtendedTextMessage.Length);

            // Field 12: ProtocolMessage, key 1 and type 2.
            Assert.AreEqual(26,
                Message.Decode(Hex(MessageFixture.ProtocolEncoded))
                    .ProtocolMessage.Length);

            // Field 31: DeviceSentMessage, destinationJid 1 and message 2.
            Assert.AreEqual(38,
                Message.Decode(Hex(MessageFixture.DeviceSentEncoded))
                    .DeviceSentMessage.Length);
        }

        [Test]
        public static void TheDeviceSentFormCarriesTheDestinationAndTheText()
        {
            byte[] expected = Hex(MessageFixture.DeviceSentEncoded);

            Assert.ByteArraysEqual(expected,
                Message.DeviceSent(MessageFixture.DeviceSentTo, Message.Text(MessageFixture.TextBody)).Encode());

            // The fixture is the whole message with field 31 in it, and `Destination`
            // and `Inner` read that field's own payload - which is the shape the
            // receive path has in hand when it asks whether a copy is our own echo.
            Message wrapping = Message.Decode(expected);
            Assert.AreEqual(MessageFixture.DeviceSentTo, Message.Destination(wrapping.DeviceSentMessage));
            Assert.AreEqual(MessageFixture.TextBody, Message.Inner(wrapping.DeviceSentMessage).Conversation);
        }

        [Test]
        public static void ARoundTripKeepsEveryField()
        {
            var message = new Message();
            message.Conversation = "c";
            message.SenderKeyDistributionMessage = new byte[] { 1 };
            message.ExtendedTextMessage = new byte[] { 2, 2 };
            message.ProtocolMessage = new byte[] { 3, 3, 3 };
            message.DeviceSentMessage = new byte[] { 4, 4, 4, 4 };

            Message again = Message.Decode(message.Encode());

            Assert.AreEqual("c", again.Conversation);
            Assert.ByteArraysEqual(new byte[] { 1 }, again.SenderKeyDistributionMessage);
            Assert.ByteArraysEqual(new byte[] { 2, 2 }, again.ExtendedTextMessage);
            Assert.ByteArraysEqual(new byte[] { 3, 3, 3 }, again.ProtocolMessage);
            Assert.ByteArraysEqual(new byte[] { 4, 4, 4, 4 }, again.DeviceSentMessage);
        }

        [Test]
        public static void AnUnknownFieldIsSkippedNotFatal()
        {
            byte[] clean = Hex(MessageFixture.TextEncoded);

            // A peer that knows a newer field than this client: 100, length-delimited,
            // three bytes. The text in front of it still has to read.
            var newer = new byte[clean.Length + 6];
            Buffer.BlockCopy(clean, 0, newer, 0, clean.Length);
            newer[clean.Length] = 0xa2;      // 100 << 3 | 2
            newer[clean.Length + 1] = 0x06;
            newer[clean.Length + 2] = 0x03;  // three bytes
            newer[clean.Length + 3] = 0x09;
            newer[clean.Length + 4] = 0x08;
            newer[clean.Length + 5] = 0x07;

            Assert.AreEqual(MessageFixture.TextBody, Message.Decode(newer).Conversation);
        }

        /// <summary>
        /// A plaintext as a phone sends it: the protobuf with WhatsApp's padding behind it,
        /// whose **last byte is how many bytes of it there are**.
        ///
        /// This is not the cipher's block padding, and it is not a detail: the reference strips
        /// it from every decrypted message (`unpadRandomMax16`, `unpadMessage`), and a reader
        /// that hands the padded bytes to the protobuf parser reads a length field out of its
        /// tail - the `a protobuf field announces 12095 bytes with 52 left` the phone logged on
        /// a status it had decrypted correctly.
        /// </summary>
        [Test]
        public static void ThePaddingAPhoneAppendsIsStrippedBeforeTheProtobufIsRead()
        {
            byte[] message = Message.Text(MessageFixture.TextBody).Encode();

            var padded = new byte[message.Length + 6];
            Buffer.BlockCopy(message, 0, padded, 0, message.Length);
            for (int i = message.Length; i < padded.Length; i++) padded[i] = (byte)(0x51 + i);
            padded[padded.Length - 1] = 6;

            Assert.AreEqual(MessageFixture.TextBody, Message.DecodePadded(padded).Conversation,
                "the words are in front of the padding, and the padding is not part of them");

            Assert.AreEqual(MessageFixture.TextBody, Message.DecodePadded(message).Conversation,
                "and a plaintext that carries no padding still reads");
        }

        private static byte[] Hex(string text)
        {
            var bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            }

            return bytes;
        }
    }
}
