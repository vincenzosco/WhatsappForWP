using System;
using System.Security.Cryptography;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// STUN against the vectors the specification ships (RFC 5769, sections 2.1 to 2.4).
    ///
    /// Why these vectors and not a round trip of our own: MESSAGE-INTEGRITY and FINGERPRINT
    /// are the two things a peer checks before it believes anything, and both are computed
    /// over a message whose length field is patched mid-way - the easiest place in the whole
    /// protocol to be self-consistently wrong. RFC 5769 is four messages with the hashes the
    /// specification itself calculated, so a test that reads them and verifies both hashes
    /// is the difference between "our parser agrees with our writer" and "our parser agrees
    /// with ICE".
    ///
    /// Every vector is the bytes as the RFC prints them, padding included: the padding is a
    /// space in the short-term messages and a zero in the long-term one, and it is covered by
    /// both hashes, which is exactly why verification reads the datagram and not a rebuild.
    /// </summary>
    public static class StunTests
    {
        private const string ShortTermPassword = "VOkJxbRl1RmTxUk/WvJxBt";

        private const string LongTermPassword = "TheMatrIX";

        /// <summary>RFC 5769, section 2.1: a Binding request with short-term credentials.</summary>
        private const string RequestHex =
            "00010058 2112a442 b7e7a701 bc34d686 fa87dfae"
            + "80220010 5354554e 20746573 7420636c 69656e74"
            + "00240004 6e0001ff"
            + "80290008 932ff9b1 51263b36"
            + "00060009 6576746a 3a683676 59202020"
            + "00080014 9aeaa70c bfd8cb56 781ef2b5 b2d3f249 c1b571a2"
            + "80280004 e57a3bcf";

        /// <summary>RFC 5769, section 2.2: an IPv4 success response.</summary>
        private const string ResponseV4Hex =
            "0101003c 2112a442 b7e7a701 bc34d686 fa87dfae"
            + "8022000b 74657374 20766563 746f7220"
            + "00200008 0001a147 e112a643"
            + "00080014 2b91f599 fd9e90c3 8c7489f9 2af9ba53 f06be7d7"
            + "80280004 c07d4c96";

        /// <summary>RFC 5769, section 2.3: the same, for IPv6.</summary>
        private const string ResponseV6Hex =
            "01010048 2112a442 b7e7a701 bc34d686 fa87dfae"
            + "8022000b 74657374 20766563 746f7220"
            + "00200014 0002a147 0113a9fa a5d3f179 bc25f4b5 bed2b9d9"
            + "00080014 a382954e 4be67bf1 1784c97c 8292c275 bfe3ed41"
            + "80280004 c8fb0b4c";

        /// <summary>RFC 5769, section 2.4: long-term credentials, with a nonce and a realm.</summary>
        private const string RequestLongTermHex =
            "00010060 2112a442 78ad3433 c6ad72c0 29da412e"
            + "00060012 e3839ee3 8388e383 aae38383 e382afe3 82b90000"
            + "0015001c 662f2f34 39396b39 35346436 4f4c3334 6f4c3946"
            + "53547679 36347341"
            + "0014000b 6578616d 706c652e 6f726700"
            + "00080014 f6702465 6dd64a3e 02b8e071 2e85c9a2 8ca89666";

        [Test]
        public static void TheShortTermRequestOfRfc5769IsReadAsItWasWritten()
        {
            byte[] data = Hex(RequestHex);
            StunMessage request = StunMessage.Parse(data);

            Assert.AreEqual(StunMethod.Binding, request.Method);
            Assert.AreEqual(StunClass.Request, request.Class);
            Assert.AreEqual("STUN test client", request.Find(StunAttributeType.Software).Text);
            Assert.AreEqual("evtj:h6vY", request.UserName);
            Assert.AreEqual(0x6e0001ffu, request.Find(StunAttributeType.Priority).AsUInt32);
            Assert.AreEqual(0x932ff9b151263b36ul, request.Find(StunAttributeType.IceControlled).AsUInt64,
                "the pseudo-random tie breaker of the vector");
            Assert.ByteArraysEqual(Hex("b7e7a701bc34d686fa87dfae"), request.TransactionId);
        }

        [Test]
        public static void TheShortTermRequestOfRfc5769AuthenticatesWithItsPassword()
        {
            StunMessage request = StunMessage.Parse(Hex(RequestHex));

            Assert.IsTrue(request.VerifyIntegrity(Utf8(ShortTermPassword)),
                "MESSAGE-INTEGRITY is the RFC's own HMAC");
            Assert.IsTrue(request.VerifyFingerprint(), "FINGERPRINT is the RFC's own CRC");
            Assert.IsFalse(request.VerifyIntegrity(Utf8("another password")),
                "a key the message was not signed with must not verify");
        }

        [Test]
        public static void TheIPv4ResponseOfRfc5769NamesItsAddress()
        {
            StunMessage response = StunMessage.Parse(Hex(ResponseV4Hex));

            Assert.AreEqual(StunClass.SuccessResponse, response.Class);

            StunEndpoint mapped = response.Find(StunAttributeType.XorMappedAddress)
                .ReadAddress(response.TransactionId);

            Assert.AreEqual("192.0.2.1", mapped.Text);
            Assert.AreEqual(32853, mapped.Port);

            Assert.IsTrue(response.VerifyIntegrity(Utf8(ShortTermPassword)));
            Assert.IsTrue(response.VerifyFingerprint());
        }

        [Test]
        public static void TheIPv6ResponseOfRfc5769NamesItsAddress()
        {
            StunMessage response = StunMessage.Parse(Hex(ResponseV6Hex));

            StunEndpoint mapped = response.Find(StunAttributeType.XorMappedAddress)
                .ReadAddress(response.TransactionId);

            Assert.ByteArraysEqual(
                Hex("2001 0db8 1234 5678 0011 2233 4455 6677"),
                mapped.Address,
                "the vector's IPv6 address, xored against the cookie and the transaction id");
            Assert.AreEqual(32853, mapped.Port);

            Assert.IsTrue(response.VerifyIntegrity(Utf8(ShortTermPassword)));
            Assert.IsTrue(response.VerifyFingerprint());
        }

        [Test]
        public static void TheLongTermRequestOfRfc5769AuthenticatesWithTheProcessedPassword()
        {
            StunMessage request = StunMessage.Parse(Hex(RequestLongTermHex));

            Assert.AreEqual("example.org", request.Find(StunAttributeType.Realm).Text);
            Assert.AreEqual("f//499k954d6OL34oL9FSTvy64sA",
                request.Find(StunAttributeType.Nonce).Text);

            // The username is six katakana characters: 18 bytes of UTF-8, which the vector
            // pads with two zeros. Read as text it must come back unchanged.
            Assert.ByteArraysEqual(Hex("e3839ee38388e383aae38383e382afe382b9"),
                request.Find(StunAttributeType.UserName).Value,
                "the username's own bytes, not an encoding of them");

            // Long-term credentials do not hash the password: the key is
            // MD5(username:realm:password), which is why the RFC's own HMAC needs the
            // username that came in the message (RFC 5389, section 15.4).
            Assert.IsTrue(request.VerifyIntegrity(
                LongTermKey(request.UserName, "example.org", LongTermPassword)),
                "the password after SASLprep, which is the one the RFC hashed");
            Assert.IsFalse(request.VerifyIntegrity(Utf8(LongTermPassword)),
                "the password alone is the short-term key, not the long-term one");
            Assert.IsFalse(request.VerifyIntegrity(Utf8(ShortTermPassword)));
            Assert.IsTrue(request.Find(StunAttributeType.Fingerprint) == null,
                "this vector ends at MESSAGE-INTEGRITY and carries no FINGERPRINT");
        }

        [Test]
        public static void OneFlippedByteBreaksBothHashes()
        {
            byte[] data = Hex(ResponseV4Hex);

            // The SOFTWARE value: inside the message, before both hashes, and covered by
            // each of them.
            data[24] = (byte)(data[24] ^ 0x01);

            StunMessage damaged = StunMessage.Parse(data);

            Assert.IsFalse(damaged.VerifyIntegrity(Utf8(ShortTermPassword)),
                "the HMAC covers every byte before it");
            Assert.IsFalse(damaged.VerifyFingerprint(), "the CRC covers every byte before it");
        }

        [Test]
        public static void AMessageWeSealedVerifiesWithTheKeyWeSealedItWith()
        {
            var request = new StunMessage(StunMethod.Binding, StunClass.Request, Hex("0102030405060708090a0b0c"));
            request.AddText(StunAttributeType.UserName, "peer:local");
            request.AddUInt32(StunAttributeType.Priority, 0x6e0001ff);
            request.Add(StunAttribute.Flag(StunAttributeType.UseCandidate));

            byte[] wire = request.Seal(Utf8("a password"));
            StunMessage read = StunMessage.Parse(wire);

            Assert.AreEqual(StunMethod.Binding, read.Method);
            Assert.AreEqual(StunClass.Request, read.Class);
            Assert.AreEqual("peer:local", read.UserName);
            Assert.ByteArraysEqual(request.TransactionId, read.TransactionId);
            Assert.IsTrue(read.VerifyIntegrity(Utf8("a password")));
            Assert.IsTrue(read.VerifyFingerprint());
            Assert.IsFalse(read.VerifyIntegrity(Utf8("a password!")));
        }

        [Test]
        public static void AnEndpointSurvivesTheXorInBothDirections()
        {
            var endpoint = StunEndpoint.Parse("203.0.113.9", 49152);
            byte[] transaction = Hex("a1a2a3a4a5a6a7a8a9aaabac");

            var message = new StunMessage(StunMethod.Binding, StunClass.SuccessResponse, transaction);
            message.AddXorAddress(endpoint);

            StunMessage read = StunMessage.Parse(message.Seal(Utf8("k")));
            StunEndpoint mapped = read.Find(StunAttributeType.XorMappedAddress)
                .ReadAddress(read.TransactionId);

            Assert.AreEqual(endpoint.Text, mapped.Text);
            Assert.AreEqual(endpoint.Port, mapped.Port);
        }

        [Test]
        public static void AnUnsealedMessageCarriesNoHashesAtAll()
        {
            var message = new StunMessage(StunMethod.Binding, StunClass.Request, Hex("111111111111111111111111"));
            message.AddText(StunAttributeType.UserName, "a:b");

            StunMessage read = StunMessage.Parse(message.ToBytes());

            Assert.IsFalse(read.VerifyIntegrity(Utf8("a password")),
                "a check that was never signed is not a check that passed");
            Assert.IsFalse(read.VerifyFingerprint());
        }

        [Test]
        public static void ATruncatedAttributeIsRefusedAndNotShortened()
        {
            byte[] data = Hex(ResponseV4Hex);
            byte[] truncated = new byte[data.Length - 4];
            Buffer.BlockCopy(data, 0, truncated, 0, truncated.Length);

            Assert.Throws<ProtocolException>(
                delegate { StunMessage.Parse(truncated); },
                "the header still promises bytes the datagram no longer has");
        }

        [Test]
        public static void EveryMethodAndClassSurviveTheTypeEncoding()
        {
            var methods = new[]
            {
                StunMethod.Binding, StunMethod.Allocate, StunMethod.Refresh,
                StunMethod.Send, StunMethod.Data, StunMethod.CreatePermission,
                StunMethod.ChannelBind
            };
            var classes = new[]
            {
                StunClass.Request, StunClass.Indication,
                StunClass.SuccessResponse, StunClass.ErrorResponse
            };

            for (int m = 0; m < methods.Length; m++)
            {
                for (int c = 0; c < classes.Length; c++)
                {
                    var message = new StunMessage(methods[m], classes[c], Hex("00112233445566778899aabb"));
                    StunMessage read = StunMessage.Parse(message.ToBytes());

                    Assert.AreEqual(methods[m], read.Method, "method " + methods[m]);
                    Assert.AreEqual(classes[c], read.Class, "class " + classes[c]);
                }
            }
        }

        [Test]
        public static void OnlySomethingWithTheCookieIsTreatedAsStun()
        {
            Assert.IsTrue(StunMessage.Looks(Hex(RequestHex), 0, Hex(RequestHex).Length));

            byte[] rtp = Hex("80010102030405060708090a0b0c");
            Assert.IsFalse(StunMessage.Looks(rtp, 0, rtp.Length),
                "an RTP packet starts with two set bits and no cookie");

            byte[] shortData = Hex("000100002112a442");
            Assert.IsFalse(StunMessage.Looks(shortData, 0, shortData.Length),
                "a STUN header is twenty bytes");
        }

        private static byte[] Hex(string text)
        {
            string clean = text.Replace(" ", "").Replace("\n", "").Replace("\r", "");
            var bytes = new byte[clean.Length / 2];

            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(clean.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        private static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        private static byte[] LongTermKey(string userName, string realm, string password)
        {
            using (MD5 md5 = MD5.Create())
            {
                return md5.ComputeHash(Utf8(userName + ":" + realm + ":" + password));
            }
        }
    }
}
