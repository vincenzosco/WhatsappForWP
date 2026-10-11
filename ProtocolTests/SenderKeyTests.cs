using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WaProto;
using ProtocolTests.Vectors;

namespace ProtocolTests
{
    /// <summary>
    /// The group sender key, against bytes the reference produced.
    ///
    /// Two assertions here are the difference between a sender key that works and one that
    /// only looks like it does: the reference's own distribution message has to be readable
    /// (`TheReferenceSDistributionIsRead`), and the reference's own ciphertext has to come
    /// back as its plaintext (`TheReferenceSCiphertextIsRead`). A schedule written from the
    /// function names alone gets the iv and the cipher key out of the wrong halves of the
    /// HKDF and passes every test that only talks to itself.
    ///
    /// The reference for each number: `ProtocolTests/Vectors/SenderKeyFixture.cs`.
    /// </summary>
    public static class SenderKeyTests
    {
        [Test]
        public static void TheChainStepsAreTheReferenceOnes()
        {
            byte[] chainKey = Hex(SenderKeyFixture.ChainKey);

            Assert.ByteArraysEqual(Hex(SenderKeyFixture.MessageKeySeed),
                SenderKeyRecord.ChainKeyFor(chainKey, new byte[] { 0x01 }));
            Assert.ByteArraysEqual(Hex(SenderKeyFixture.NextChainKey),
                SenderKeyRecord.ChainKeyFor(chainKey, new byte[] { 0x02 }));
        }

        [Test]
        public static void TheIvAndCipherKeyAreTheReferenceOnes()
        {
            byte[] iv;
            byte[] cipherKey;
            SenderKeyRecord.MessageKey(Hex(SenderKeyFixture.MessageKeySeed), out iv, out cipherKey);

            Assert.ByteArraysEqual(Hex(SenderKeyFixture.Iv), iv);
            Assert.ByteArraysEqual(Hex(SenderKeyFixture.CipherKey), cipherKey);
        }

        [Test]
        public static void TheReferenceSDistributionIsRead()
        {
            SenderKeyDistribution parsed = SenderKeyDistribution.Parse(Hex(SenderKeyFixture.Distribution));

            Assert.AreEqual(SenderKeyFixture.KeyId, parsed.KeyId);
            Assert.AreEqual(0u, parsed.Iteration);
            Assert.ByteArraysEqual(Hex(SenderKeyFixture.ChainKey), parsed.ChainKey);
            Assert.ByteArraysEqual(Hex(SenderKeyFixture.SigningPublic), parsed.SigningPublic);

            Assert.ByteArraysEqual(Hex(SenderKeyFixture.Distribution),
                SenderKeyDistribution.Build(parsed.KeyId, parsed.Iteration, parsed.ChainKey, parsed.SigningPublic));
        }

        [Test]
        public static void TheReferenceSCiphertextIsRead()
        {
            SenderKeyRecord record = SenderKeyRecord.StartAsReceiver(SenderKeyFixture.GroupId,
                Hex(SenderKeyFixture.Distribution));

            SenderKeyMessage message = SenderKeyMessage.Parse(Hex(SenderKeyFixture.SenderKeyMessage));

            Assert.IsTrue(message.Verifies(record.SigningPublic),
                "the message carries the signature of the key the distribution named");
            Assert.AreEqual(SenderKeyFixture.KeyId, message.KeyId);
            Assert.AreEqual(0u, message.Iteration);

            // The decrypt is the assertion that matters: the iv and the cipher key are not
            // exposed, so a schedule that got them out of the wrong halves of the HKDF fails
            // here and nowhere else.
            byte[] plaintext = record.Decrypt(SenderKeyFixture.GroupId, message);

            Assert.ByteArraysEqual(Hex(SenderKeyFixture.Plaintext), plaintext);

            SenderKeyMessage second = SenderKeyMessage.Parse(Hex(SenderKeyFixture.SecondMessage));
            Assert.ByteArraysEqual(Hex(SenderKeyFixture.SecondPlaintext),
                record.Decrypt(SenderKeyFixture.GroupId, second));
        }

        [Test]
        public static void AMovedCiphertextByteIsRefusedByName()
        {
            SenderKeyRecord record = SenderKeyRecord.StartAsReceiver(SenderKeyFixture.GroupId,
                Hex(SenderKeyFixture.Distribution));

            byte[] bytes = Hex(SenderKeyFixture.SenderKeyMessage);
            bytes[20] = (byte)(bytes[20] ^ 0xff);

            SenderKeyMessage moved = SenderKeyMessage.Parse(bytes);

            ProtocolException failed = null;

            try
            {
                record.Decrypt(SenderKeyFixture.GroupId, moved);
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "a body whose bytes moved is not accepted");
            Assert.IsTrue(failed.Message.Contains("signature"),
                "and the failure names the signature, not the cipher: <" + failed.Message + ">");
        }

        [Test]
        public static void ADistributionFromAnotherGroupIsRefused()
        {
            SenderKeyRecord record = SenderKeyRecord.StartAsReceiver("120363000000000000@g.us",
                Hex(SenderKeyFixture.Distribution));

            ProtocolException failed = null;

            try
            {
                record.Decrypt("120363111111111111@g.us",
                    SenderKeyMessage.Parse(Hex(SenderKeyFixture.SenderKeyMessage)));
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "a record is not usable with another group");
            Assert.IsTrue(failed.Message.Contains("120363000000000000@g.us")
                && failed.Message.Contains("120363111111111111@g.us"),
                "and the sentence names both groups: <" + failed.Message + ">");
        }

        [Test]
        public static void TwoMembersOfAGroupReadEachOther()
        {
            SenderKeyRecord mine = SenderKeyRecord.Create(SenderKeyFixture.GroupId, new CountingRandom());
            SenderKeyRecord theirs = SenderKeyRecord.StartAsReceiver(SenderKeyFixture.GroupId,
                mine.Distribution(SenderKeyFixture.GroupId));

            byte[] first = mine.Encrypt(SenderKeyFixture.GroupId, Bytes("ciao gruppo"));
            Assert.ByteArraysEqual(Bytes("ciao gruppo"), theirs.Decrypt(SenderKeyFixture.GroupId,
                SenderKeyMessage.Parse(first)));

            byte[] second = mine.Encrypt(SenderKeyFixture.GroupId, Bytes("e ancora"));
            Assert.ByteArraysEqual(Bytes("e ancora"), theirs.Decrypt(SenderKeyFixture.GroupId,
                SenderKeyMessage.Parse(second)));

            ProtocolException refused = null;

            try
            {
                theirs.Encrypt(SenderKeyFixture.GroupId, Bytes("non posso"));
            }
            catch (ProtocolException error)
            {
                refused = error;
            }

            Assert.IsTrue(refused != null, "a record that only received cannot sign, and says so");
            Assert.IsTrue(refused.Message.Contains("read"), "<" + refused.Message + ">");
        }

        [Test]
        public static void AMessageThatArrivesOutOfOrderIsStillRead()
        {
            SenderKeyRecord mine = SenderKeyRecord.Create(SenderKeyFixture.GroupId, new CountingRandom());
            SenderKeyRecord theirs = SenderKeyRecord.StartAsReceiver(SenderKeyFixture.GroupId,
                mine.Distribution(SenderKeyFixture.GroupId));

            byte[] first = mine.Encrypt(SenderKeyFixture.GroupId, Bytes("uno"));
            byte[] second = mine.Encrypt(SenderKeyFixture.GroupId, Bytes("due"));

            Assert.ByteArraysEqual(Bytes("due"), theirs.Decrypt(SenderKeyFixture.GroupId,
                SenderKeyMessage.Parse(second)));
            Assert.ByteArraysEqual(Bytes("uno"), theirs.Decrypt(SenderKeyFixture.GroupId,
                SenderKeyMessage.Parse(first)));
        }

        [Test]
        public static void TheRecordSurvivesADiskRoundTrip()
        {
            SenderKeyRecord mine = SenderKeyRecord.Create(SenderKeyFixture.GroupId, new CountingRandom());

            byte[] first = mine.Encrypt(SenderKeyFixture.GroupId, Bytes("prima"));
            SenderKeyRecord reloaded = SenderKeyRecord.Decode(mine.Encode());

            Assert.IsTrue(reloaded != null, "the record reads back");
            Assert.AreEqual(mine.KeyId, reloaded.KeyId);
            Assert.AreEqual(mine.Iteration, reloaded.Iteration);
            Assert.ByteArraysEqual(mine.ChainKey, reloaded.ChainKey);
            Assert.ByteArraysEqual(mine.Distribution(SenderKeyFixture.GroupId),
                reloaded.Distribution(SenderKeyFixture.GroupId));

            SenderKeyRecord theirs = SenderKeyRecord.StartAsReceiver(SenderKeyFixture.GroupId,
                reloaded.Distribution(SenderKeyFixture.GroupId));

            byte[] second = reloaded.Encrypt(SenderKeyFixture.GroupId, Bytes("dopo il disco"));
            Assert.ByteArraysEqual(Bytes("dopo il disco"), theirs.Decrypt(SenderKeyFixture.GroupId,
                SenderKeyMessage.Parse(second)));
            Assert.IsTrue(first.Length > 0);
        }

        [Test]
        public static void ADamagedRecordIsNullAndNotAKey()
        {
            SenderKeyRecord mine = SenderKeyRecord.Create(SenderKeyFixture.GroupId, new CountingRandom());
            byte[] stored = mine.Encode();
            stored[stored.Length - 1] = (byte)(stored[stored.Length - 1] ^ 0xff);

            Assert.IsTrue(SenderKeyRecord.Decode(stored) == null,
                "a record that does not match its digest is not read as one");
        }

        [Test]
        public static void TheDistributionMessageCarriesTheGroupAndTheKey()
        {
            SenderKeyRecord mine = SenderKeyRecord.Create(SenderKeyFixture.GroupId, new CountingRandom());

            byte[] wrapped = SenderKeys.DistributionMessage(SenderKeyFixture.GroupId, mine);

            Assert.AreEqual(SenderKeyFixture.GroupId, SenderKeys.GroupIdOf(wrapped));
            Assert.ByteArraysEqual(mine.Distribution(SenderKeyFixture.GroupId), SenderKeys.DistributionOf(wrapped));
        }

        [Test]
        public static void APaddedGroupMessageStillReads()
        {
            // A phone pads its group plaintext with 1..15 bytes whose value is their count
            // (whatsmeow's `padMessage`); the reader has to step over them.
            byte[] message = Message.Text("ciao").Encode();
            var padded = new byte[message.Length + 7];
            System.Buffer.BlockCopy(message, 0, padded, 0, message.Length);
            for (int i = message.Length; i < padded.Length; i++) padded[i] = 7;

            Assert.AreEqual("ciao", SenderKeys.DecodePadded(padded).Conversation);
            Assert.AreEqual("ciao", SenderKeys.DecodePadded(message).Conversation);
        }

        [Test]
        public static void TheVersionByteIsTheReferenceS()
        {
            SenderKeyRecord mine = SenderKeyRecord.Create(SenderKeyFixture.GroupId, new CountingRandom());

            byte[] encrypted = mine.Encrypt(SenderKeyFixture.GroupId, Bytes("x"));
            byte[] distribution = mine.Distribution(SenderKeyFixture.GroupId);

            Assert.AreEqual((byte)0x33, encrypted[0], "version 3 in both nibbles");
            Assert.AreEqual((byte)0x33, distribution[0]);
            Assert.IsTrue(SenderKeyFixture.Distribution.StartsWith("33"),
                "and the same byte the reference printed starts its own distribution");
        }

        private static byte[] Bytes(string text)
        {
            return System.Text.Encoding.UTF8.GetBytes(text);
        }

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)System.Convert.ToInt32(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        /// <summary>
        /// A random source that is the same every run, so a failing test reads the same
        /// twice. It is not a source of secrecy and is only used by the tests that do not
        /// compare against the reference's own bytes.
        /// </summary>
        private sealed class CountingRandom : IRandomSource
        {
            private byte _next = 0x40;

            public byte[] Bytes(int count)
            {
                var bytes = new byte[count];
                for (int i = 0; i < count; i++)
                {
                    bytes[i] = _next;
                    _next++;
                    if (_next == 0) _next = 1;
                }

                return bytes;
            }
        }
    }
}
