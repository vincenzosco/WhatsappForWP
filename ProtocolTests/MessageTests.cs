using System;
using WhatsappApp.Protocol;
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
        /// <summary>
        /// A tag whose field number does not fit 29 bits is not protobuf. Truncating it to
        /// `int` made a corrupt frame read as a real field with a meaning nobody sent, so it
        /// is refused like every other malformed value in the reader.
        /// </summary>
        [Test]
        public static void AProtobufFieldNumberThatDoesNotFitIsRefused()
        {
            // field = 2^29, wire type 0: one past the format's own ceiling.
            byte[] tooBig = { 0x80, 0x80, 0x80, 0x80, 0x10 };
            Assert.Throws<ProtocolException>(delegate { new ProtoReader(tooBig).ReadField(); },
                "a field number over 2^29-1 is not a field");

            byte[] zero = { 0x00 };
            Assert.Throws<ProtocolException>(delegate { new ProtoReader(zero).ReadField(); },
                "and field zero does not exist in protobuf");
        }

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

        /// <summary>
        /// The walk that replaced the throw-per-guess: it says where the protobuf ends, and
        /// it never throws doing it.
        ///
        /// Why this is a test and not a detail: `DecodePadded` used to answer "is this the
        /// padding" with a parse inside a `try`, so a plaintext that did not open on the first
        /// guess cost an exception per guess - sixteen of them, seventeen with the final read.
        /// A debugger prints every first-chance exception, and on 24 October 2026 the debugger
        /// output for one run was a column of `Eccezione generata: ProtocolException` for
        /// messages that read correctly in the end.
        /// </summary>
        [Test]
        public static void MeasureSaysWhereTheProtobufEndsWithoutThrowing()
        {
            byte[] message = Message.Text(MessageFixture.TextBody).Encode();

            Assert.AreEqual(message.Length, ProtoReader.Measure(message),
                "a whole protobuf is as long as it parses");
            Assert.AreEqual(message.Length, ProtoReader.Measure(message, message.Length),
                "and the same when it is asked not to read past its own end");

            var cut = new byte[message.Length - 2];
            Buffer.BlockCopy(message, 0, cut, 0, cut.Length);
            Assert.IsTrue(ProtoReader.Measure(cut) < cut.Length,
                "a last field the buffer does not hold is where the protobuf ends");

            Assert.AreEqual(0, ProtoReader.Measure(null), "nothing is not a protobuf");

            // Arbitrary bytes: the walk answers something inside them and never throws, which
            // is the whole reason it exists.
            var random = new Random(24);
            for (int i = 0; i < 200; i++)
            {
                var noise = new byte[1 + (i % 37)];
                random.NextBytes(noise);

                int measured = ProtoReader.Measure(noise);
                Assert.IsTrue(measured >= 0 && measured <= noise.Length,
                    "a walk of arbitrary bytes stays inside them");
            }
        }

        /// <summary>
        /// A plaintext with no padding whose last byte happens to be a small number: the
        /// declared count does not fit, so it is not the count, and the message still reads.
        /// </summary>
        [Test]
        public static void ALastByteThatOnlyLooksLikeACountIsNotTakenForOne()
        {
            byte[] message = Message.Text(MessageFixture.TextBody).Encode();

            var plaintext = new byte[message.Length + 3];
            Buffer.BlockCopy(message, 0, plaintext, 0, message.Length);
            plaintext[message.Length] = 0xe1;
            plaintext[message.Length + 1] = 0xe2;
            plaintext[message.Length + 2] = 0x05;

            Assert.AreEqual(MessageFixture.TextBody, Message.DecodePadded(plaintext).Conversation,
                "five bytes of padding are not there, so the protobuf ends where it ends");
        }

        /// <summary>
        /// A damaged plaintext still fails, and names itself: the exception is the one worth
        /// seeing, and a reader that swallowed it would put an empty message in the chat.
        /// </summary>
        [Test]
        public static void ADamagedPlaintextStillNamesItsOwnFailure()
        {
            byte[] message = Message.Text(MessageFixture.TextBody).Encode();

            var truncated = new byte[message.Length - 2];
            Buffer.BlockCopy(message, 0, truncated, 0, truncated.Length);

            string said = null;
            try
            {
                Message.DecodePadded(truncated);
            }
            catch (ProtocolException error)
            {
                said = error.Message;
            }

            Assert.IsTrue(said != null, "a truncated plaintext is not a message");
            Assert.IsTrue(said.IndexOf("announces", StringComparison.Ordinal) >= 0
                || said.IndexOf("runs past", StringComparison.Ordinal) >= 0,
                "the failure is the bytes' own: " + said);
        }

        /// <summary>
        /// Bytes that start no protobuf at all are refused, not read as an empty message: a
        /// silent empty `Message` is a chat row with nothing in it and no line in the log.
        /// </summary>
        [Test]
        public static void BytesThatStartNoProtobufAreRefused()
        {
            byte[] garbage = { 0xff, 0xff, 0xff, 0xff };

            Assert.AreEqual(0, ProtoReader.Measure(garbage), "nothing here starts a protobuf");
            Assert.Throws<ProtocolException>(delegate { Message.DecodePadded(garbage); },
                "and the decode says so instead of handing back an empty message");
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
