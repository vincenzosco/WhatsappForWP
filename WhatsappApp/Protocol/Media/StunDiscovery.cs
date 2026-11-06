using System;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// Server-reflexive discovery: the one question an ICE agent asks a STUN service to learn the
    /// address the network puts in front of this phone.
    ///
    /// `CallCandidates` could only name the address the phone is attached to, and
    /// that address is not the one a peer behind a carrier NAT can reach. The answer to a Binding
    /// request is the mapped address - the reflexive candidate - and without it a call placed
    /// from a mobile network offers a private address nobody can send to. The lite agent already
    /// reads the mapped address of a *peer's* check; this is the same question asked about
    /// ourselves.
    ///
    /// Not an ICE agent, a socket, or a candidate list. `Request` writes the bytes and
    /// `Read` reads the answer, so the question is exercised without a network, and
    /// `CallCandidates.ServerReflexive` is what turns a mapped address into something an offer
    /// can carry.
    ///
    /// No MESSAGE-INTEGRITY: a STUN service that is not part of this call has no password to
    /// check one with, and RFC 5389 section 10 answers an unauthenticated Binding request with
    /// the mapped address all the same. That also means the answer proves nothing about who sent
    /// it, which is why this class hands back an address and the caller decides whether to put
    /// it in an offer.
    /// </summary>
    public static class StunDiscovery
    {
        /// <summary>How long a STUN transaction id is (RFC 5389, section 6).</summary>
        public const int TransactionIdLength = 12;

        /// <summary>What a discovery question came back with: an address, or a reason it did not.</summary>
        public sealed class Answer
        {
            internal Answer(StunEndpoint mapped, int errorCode, string failure)
            {
                Mapped = mapped;
                ErrorCode = errorCode;
                Failure = failure;
            }

            /// <summary>The address the service saw, or null when the question was refused.</summary>
            public StunEndpoint Mapped { get; private set; }

            /// <summary>The error code the service answered, or zero.</summary>
            public int ErrorCode { get; private set; }

            /// <summary>Why no address came back, or null.</summary>
            public string Failure { get; private set; }
        }

        /// <summary>
        /// One Binding request, ready for the socket. The transaction id is the caller's: it is
        /// what tells the answer to this question from the answer to an earlier one, and a class
        /// that made one up could not be tested against a fixed script.
        /// </summary>
        public static byte[] Request(byte[] transactionId)
        {
            if (transactionId == null || transactionId.Length != TransactionIdLength)
            {
                throw new ProtocolException("a discovery question needs "
                    + TransactionIdLength + " bytes of transaction id");
            }

            var request = new StunMessage(StunMethod.Binding, StunClass.Request, transactionId);
            request.AddText(StunAttributeType.Software, "whatsapp-wp/1");

            return request.ToBytes();
        }

        /// <summary>
        /// The answer to `Request`, or null when the datagram is not an answer to that question -
        /// unreadable, a request, or a reply that names another transaction.
        ///
        /// A refused question is an `Answer` with no address and the service's own code, not a
        /// null: "the service said 401" and "nothing arrived" are two different problems.
        /// </summary>
        public static Answer Read(byte[] datagram, byte[] transactionId)
        {
            if (datagram == null || transactionId == null) return null;
            if (!StunMessage.Looks(datagram, 0, datagram.Length)) return null;

            StunMessage message;
            try
            {
                message = StunMessage.Parse(datagram);
            }
            catch (ProtocolException)
            {
                return null;
            }

            if (message.Class != StunClass.SuccessResponse
                && message.Class != StunClass.ErrorResponse)
            {
                return null;
            }

            // A delayed answer to an earlier question is not an answer to this one (RFC 5389,
            // section 7.3.3): the transaction id is the only thing that tells them apart.
            if (!Same(message.TransactionId, transactionId)) return null;

            if (message.Class == StunClass.ErrorResponse)
            {
                StunAttribute error = message.Find(StunAttributeType.ErrorCode);
                int code = error == null ? 0 : error.ErrorCode;

                return new Answer(null, code, "the STUN service answered " + code);
            }

            StunAttribute mapped = message.Find(StunAttributeType.XorMappedAddress);
            if (mapped == null) mapped = message.Find(StunAttributeType.MappedAddress);

            if (mapped == null)
            {
                return new Answer(null, 0, "the STUN answer named no address");
            }

            StunEndpoint endpoint = mapped.ReadAddress(message.TransactionId);
            if (endpoint == null)
            {
                return new Answer(null, 0, "the STUN answer named an address it could not read");
            }

            return new Answer(endpoint, 0, null);
        }

        /// <summary>A relay probe is usable only when the answer supplied a mapped address.</summary>
        public static bool HasMappedAddress(Answer answer)
        {
            return answer != null && answer.Mapped != null;
        }

        private static bool Same(byte[] left, byte[] right)
        {
            if (left == null || right == null) return false;
            if (left.Length != right.Length) return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i]) return false;
            }

            return true;
        }
    }
}
