using System;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// Server-reflexive discovery: the question this phone asks to learn the address the network
    /// puts in front of it, and the reading of the answer.
    ///
    /// Why it is pinned here: the address in the answer is the only candidate a phone behind a
    /// carrier NAT can offer, and a reader that confused a reply to an earlier question with a
    /// reply to this one, or that accepted an unauthenticated "the address is somewhere else",
    /// would put a wrong address in an offer that a peer then sends media to. Both mistakes are
    /// invisible in a log and cost a whole call.
    /// </summary>
    public static class StunDiscoveryTests
    {
        private static readonly byte[] TransactionId = Hex("0a0b0c0d0e0f101112131415");

        private static readonly StunEndpoint Mapped = StunEndpoint.Parse("203.0.113.7", 62000);

        [Test]
        public static void TheQuestionIsABindingRequestThatCarriesNoKey()
        {
            byte[] request = StunDiscovery.Request(TransactionId);

            Assert.IsTrue(StunMessage.Looks(request, 0, request.Length));

            StunMessage read = StunMessage.Parse(request);
            Assert.AreEqual(StunMethod.Binding, read.Method);
            Assert.AreEqual(StunClass.Request, read.Class);
            Assert.ByteArraysEqual(TransactionId, read.TransactionId);
            Assert.IsTrue(read.Find(StunAttributeType.MessageIntegrity) == null,
                "a STUN service outside this call has no password to check, so it is not asked to");
        }

        [Test]
        public static void TheAddressTheAnswerNamesComesBackAsTheMappedAddress()
        {
            byte[] answer = Answer(TransactionId, Mapped, null);

            StunDiscovery.Answer read = StunDiscovery.Read(answer, TransactionId);

            Assert.IsTrue(read != null);
            Assert.IsTrue(read.Failure == null, "an address came back, so there is nothing to explain");
            Assert.AreEqual(Mapped.Text, read.Mapped.Text);
            Assert.AreEqual(Mapped.Port, read.Mapped.Port);
            Assert.AreEqual(0, read.ErrorCode);
        }

        [Test]
        public static void AnAnswerToAnEarlierQuestionIsNotAReadOfThisOne()
        {
            byte[] answer = Answer(Hex("1f1e1d1c1b1a191817161514"), Mapped, null);

            Assert.IsTrue(StunDiscovery.Read(answer, TransactionId) == null,
                "the transaction id is the only thing that tells one answer from another");
        }

        [Test]
        public static void ARefusalCarriesTheServicesCodeAndNoAddress()
        {
            StunDiscovery.Answer read = StunDiscovery.Read(Answer(TransactionId, null, 401), TransactionId);

            Assert.IsTrue(read != null, "a refusal is an answer, not a silence");
            Assert.IsTrue(read.Mapped == null);
            Assert.AreEqual(401, read.ErrorCode);
            Assert.IsTrue(read.Failure != null && read.Failure.IndexOf("401") >= 0, read.Failure);
        }

        [Test]
        public static void TrafficThatIsNotAStunAnswerIsNotRead()
        {
            Assert.IsTrue(StunDiscovery.Read(Hex("80010102030405060708090a0b0c"), TransactionId) == null,
                "an RTP packet on a media port is not a discovery answer");
            Assert.IsTrue(StunDiscovery.Read(null, TransactionId) == null);
            Assert.IsTrue(StunDiscovery.Read(StunDiscovery.Request(TransactionId), TransactionId) == null,
                "a request is a question, not an answer");
        }

        [Test]
        public static void AQuestionNeedsTwelveBytesOfTransactionId()
        {
            Assert.Throws<ProtocolException>(delegate { StunDiscovery.Request(null); });
            Assert.Throws<ProtocolException>(delegate { StunDiscovery.Request(new byte[11]); });
            Assert.Throws<ProtocolException>(delegate { StunDiscovery.Request(new byte[13]); });
        }

        /// <summary>
        /// A success answer, or a refusal when `error` is given, sealed the way a STUN service
        /// seals one. The signature is not what discovery checks - it has no key to - and the
        /// answer still has one, which is why the reader does not ask.
        /// </summary>
        private static byte[] Answer(byte[] transactionId, StunEndpoint mapped, int? error)
        {
            var message = new StunMessage(StunMethod.Binding,
                error.HasValue ? StunClass.ErrorResponse : StunClass.SuccessResponse, transactionId);

            if (error.HasValue) message.AddError(error.Value, "refused");
            else message.AddXorAddress(mapped);

            return message.Seal(Utf8("a-service-password"));
        }

        private static byte[] Hex(string text)
        {
            string clean = text.Replace(" ", "");
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
    }
}
