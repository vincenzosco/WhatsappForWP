using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Signal;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    /// <summary>
    /// The call key, from the 32 bytes to the `<enc>` child of an offer and back.
    ///
    /// What is pinned here is the part that makes a call answerable: the plaintext is a
    /// `Message` carrying `call.callKey` (field 10, then field 1), the ciphertext is a Signal
    /// message on the session that exists - `msg` when the peer has answered, `pkmsg` while
    /// the first ordinary message is still unanswered, because an offer with no key rings
    /// nowhere - and one device gets one ciphertext. The receiving half is the same file:
    /// the reference's own two sessions, Alice's and Bob's, open what the other sealed.
    /// </summary>
    public static class CallKeyTests
    {
        private const string Peer = "15551230000@s.whatsapp.net";
        private const string CallId = "CALLKEY0001";
        private const string BobDevice = "15551230000.0@s.whatsapp.net";

        /// <summary>Another phone of the same account, whose session must not open Bob's copy.</summary>
        private const string OtherDevice = "15551230000.7@s.whatsapp.net";

        /// <summary>One ordinary message, delivered the way a phone delivers it.</summary>
        private static void Deliver(
            SessionRecord from, SessionRecord to, string text, IRandomSource padding)
        {
            bool preKey = from.PreKeyPending;
            byte[] ciphertext = from.Encrypt(OutgoingMessage.Pad(Message.Text(text).Encode(), padding));

            to.Decrypt(ciphertext, preKey);
        }

        /// <summary>A source that always returns one key, for the fixture's ratchet steps.</summary>
        private sealed class FixedRandom : IRandomSource
        {
            private readonly byte[] _key;

            public FixedRandom(byte[] key)
            {
                _key = key;
            }

            public byte[] Bytes(int count)
            {
                if (_key == null || _key.Length != count)
                {
                    throw new ProtocolException("the fixture key is not the size the session asked for");
                }

                return _key;
            }
        }

        /// <summary>The bytes as hex, so two arrays can be compared as one value.</summary>
        private static string Hex(byte[] bytes)
        {
            if (bytes == null) return null;

            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));

            return text.ToString();
        }

        [Test]
        public static void ACallKeyIsAMessageWithTheKeyInFieldTen()
        {
            byte[] key = new byte[32];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);

            Message message = Message.DecodePadded(Message.ForCall(key).Encode());

            Assert.IsTrue(message.Call != null, "the call is field 10 of the message");
            Assert.AreEqual(Hex(key), Hex(message.CallKey()),
                "field 1 of the call is the key itself");
        }

        [Test]
        public static void AKeyIsThirtyTwoBytesAndFreshEveryTime()
        {
            var random = new SignalSessions.CounterRandom();

            byte[] first = CallKeyExchange.NewCallKey(random);
            byte[] second = CallKeyExchange.NewCallKey(random);

            Assert.AreEqual(32, first.Length);
            Assert.IsTrue(Hex(first) != Hex(second),
                "two calls must not share a media key");
        }

        [Test]
        public static void AKeyIsSealedForADeviceAndOpensOnTheOtherSide()
        {
            var random = new SignalSessions.CounterRandom();
            var padding = new SignalSessions.CounterRandom();

            // The reference's own pair, with the fixture's ratchet keys: the reply that ends
            // the pending state is the fixture's, so the two sessions have to be the ones
            // that transcript was written for (`SignalSessionTests` builds them the same
            // way).
            SessionRecord alice = SignalSessions.Alice(
                new FixedRandom(SignalFixture.X3dh.AliceStepRatchetPrivate));
            SessionRecord bob = SignalSessions.Bob(
                new FixedRandom(SignalFixture.BobSide.SendingRatchetPrivate));

            // The established form of the key: the two sides have already exchanged ordinary
            // messages, the reply is what ends the pending pre-key state - the same sequence
            // the transcript uses - and the offer says `msg`.
            Deliver(alice, bob, SignalFixture.Conversation.Plaintexts[0], padding);
            Deliver(alice, bob, SignalFixture.Conversation.Plaintexts[1], padding);
            alice.Decrypt(SignalFixture.Hex(SignalFixture.Conversation.Received[0]), false);
            Assert.IsFalse(alice.PreKeyPending, "the session is established before the call key");

            byte[] key = CallKeyExchange.NewCallKey(padding);

            var devices = new List<KeyValuePair<string, SessionRecord>>();
            devices.Add(new KeyValuePair<string, SessionRecord>(BobDevice, alice));

            CallKeyOffer offer = CallKeyExchange.Wrap(key, devices, padding);

            Assert.AreEqual(1, offer.Payloads.Count);
            Assert.AreEqual("msg", offer.Payloads[0].Type,
                "an established session, never a pre-key message");
            Assert.AreEqual(CallKeyExchange.KeyVersion, offer.Payloads[0].Version);
            Assert.IsTrue(offer.Payloads[0].Ciphertext.Length > 0, "the ciphertext is the enc content");
            Assert.AreEqual(0, offer.Skipped.Count);
            Assert.AreEqual(BobDevice, offer.DeviceJids[0],
                "the payload names the device it was sealed for: a call that does not ring has "
                + "to be traced to a JID, and a count of pre-key messages names nobody");

            byte[] opened = CallKeyExchange.Unwrap(offer.Payloads[0], bob);

            Assert.AreEqual(Hex(key), Hex(opened),
                "the device that answers reads the same 32 bytes");
        }

        [Test]
        public static void APendingSessionIsKeyedWithAPreKeyMessage()
        {
            var random = new SignalSessions.CounterRandom();

            // Alice's session as it is created: the bundle is known and the peer has not
            // answered, so the key can only be written as a `pkmsg`. The offer carries it
            // anyway: an offer with no key is a phone that never rings, which is the call
            // in the phone's log (`with no key`, then `reason timeout`).
            var devices = new List<KeyValuePair<string, SessionRecord>>();
            devices.Add(new KeyValuePair<string, SessionRecord>(BobDevice, SignalSessions.Alice(random)));

            CallKeyOffer offer = CallKeyExchange.Wrap(CallKeyExchange.NewCallKey(random), devices, random);

            Assert.AreEqual(1, offer.Payloads.Count, "the key goes out");
            Assert.AreEqual(CallKeyExchange.PreKeyType, offer.Payloads[0].Type,
                "a session the peer has not answered is written as a pre-key message");
            Assert.IsTrue(offer.Payloads[0].Ciphertext.Length > 0, "the ciphertext is the enc content");
            Assert.AreEqual(0, offer.Skipped.Count, "nobody is left out");
            Assert.AreEqual(BobDevice, offer.DeviceJids[0],
                "the pre-key offer still says whose phone it was sealed for");
        }

        [Test]
        public static void ADeviceWithNoSessionAtAllIsSkippedAndSaidSo()
        {
            var random = new SignalSessions.CounterRandom();

            var devices = new List<KeyValuePair<string, SessionRecord>>();
            devices.Add(new KeyValuePair<string, SessionRecord>(BobDevice, null));

            Assert.Throws<ProtocolException>(delegate
            {
                CallKeyExchange.Wrap(CallKeyExchange.NewCallKey(random), devices, random);
            });
        }

        /// <summary>
        /// The session that opens the key says which device it came for: the receiving phone is
        /// not told, so it tries the devices it knows and keeps the one whose tag verifies.
        /// </summary>
        [Test]
        public static void TheSessionThatOpensTheKeyNamesTheDeviceItCameFrom()
        {
            var padding = new SignalSessions.CounterRandom();

            SessionRecord alice = SignalSessions.Alice(
                new FixedRandom(SignalFixture.X3dh.AliceStepRatchetPrivate));
            SessionRecord bob = SignalSessions.Bob(
                new FixedRandom(SignalFixture.BobSide.SendingRatchetPrivate));

            Deliver(alice, bob, SignalFixture.Conversation.Plaintexts[0], padding);
            Deliver(alice, bob, SignalFixture.Conversation.Plaintexts[1], padding);
            alice.Decrypt(SignalFixture.Hex(SignalFixture.Conversation.Received[0]), false);

            byte[] key = CallKeyExchange.NewCallKey(padding);

            var sealedFor = new List<KeyValuePair<string, SessionRecord>>();
            sealedFor.Add(new KeyValuePair<string, SessionRecord>(BobDevice, alice));

            CallKeyOffer offer = CallKeyExchange.Wrap(key, sealedFor, padding);

            // Another phone of the same account first: its session is a real one, and the key
            // was not written for it, so it has to be tried and refused before Bob's opens.
            var received = new List<KeyValuePair<string, SessionRecord>>();
            received.Add(new KeyValuePair<string, SessionRecord>(
                OtherDevice, SignalSessions.Bob(new SignalSessions.CounterRandom())));
            received.Add(new KeyValuePair<string, SessionRecord>(BobDevice, bob));

            CallKeyReceived opened = CallKeyExchange.UnwrapFrom(offer.Payloads[0], received);

            Assert.AreEqual(Hex(key), Hex(opened.Key), "the same 32 bytes after the trial");
            Assert.AreEqual(BobDevice, opened.DeviceJid,
                "the device whose session opened it, not the first one tried");
        }

        /// <summary>
        /// A key no stored session opens is refused with the count, not silently null: the
        /// caller of this has one line to write, and the count is what says whether the phone
        /// knew the caller's devices at all.
        /// </summary>
        [Test]
        public static void AKeyNoSessionOpensIsRefusedAndSaysHowManyWereTried()
        {
            var padding = new SignalSessions.CounterRandom();

            SessionRecord alice = SignalSessions.Alice(
                new FixedRandom(SignalFixture.X3dh.AliceStepRatchetPrivate));
            SessionRecord bob = SignalSessions.Bob(
                new FixedRandom(SignalFixture.BobSide.SendingRatchetPrivate));

            Deliver(alice, bob, SignalFixture.Conversation.Plaintexts[0], padding);
            Deliver(alice, bob, SignalFixture.Conversation.Plaintexts[1], padding);
            alice.Decrypt(SignalFixture.Hex(SignalFixture.Conversation.Received[0]), false);

            var sealedFor = new List<KeyValuePair<string, SessionRecord>>();
            sealedFor.Add(new KeyValuePair<string, SessionRecord>(BobDevice, alice));

            CallKeyOffer offer = CallKeyExchange.Wrap(
                CallKeyExchange.NewCallKey(padding), sealedFor, padding);

            var strangers = new List<KeyValuePair<string, SessionRecord>>();
            strangers.Add(new KeyValuePair<string, SessionRecord>(
                OtherDevice, SignalSessions.Bob(new SignalSessions.CounterRandom())));

            Assert.Throws<ProtocolException>(delegate
            {
                CallKeyExchange.UnwrapFrom(offer.Payloads[0], strangers);
            });
        }

        [Test]
        public static void AnOfferCarriesOneEncPerDevice()
        {
            var audio = new List<CallAudio> { new CallAudio { Codec = "opus", Rate = 16000 } };
            var keys = new List<CallKeyPayload>
            {
                new CallKeyPayload { Type = "msg", Version = 2, Ciphertext = new byte[] { 1, 2 } },
                new CallKeyPayload { Type = "msg", Version = 2, Ciphertext = new byte[] { 3, 4 } }
            };

            WhatsappApp.Protocol.WABinary.BinaryNode node =
                Calls.Offer(CallId, Peer, Peer, audio, null, keys, null);
            WhatsappApp.Protocol.WABinary.BinaryNode offer = node.Children()[0];

            int encs = 0;
            for (int i = 0; i < offer.Children().Count; i++)
            {
                if (offer.Children()[i].Tag == "enc") encs++;
            }

            Assert.AreEqual(2, encs, "one ciphertext per companion device");

            CallSignal signal = CallSignaling.Read(node);
            Assert.AreEqual(2, signal.Keys.Count, "and both are read back");
            Assert.AreEqual("msg", signal.Key.Type, "the first one keeps the single-key answer");
        }
    }
}
