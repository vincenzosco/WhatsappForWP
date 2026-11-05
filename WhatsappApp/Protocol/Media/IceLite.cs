using System;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// An ICE-lite agent (RFC 8445, section 2.5): it answers connectivity checks and never
    /// sends one.
    ///
    /// Why this shape: a call's media socket is behind whatever NAT the phone is behind, and
    /// ICE is how the two ends find a path through it. A lite agent is the half that costs
    /// nothing on a phone - no check timer, no candidate pairs to keep warm, no state beyond
    /// the address that won - and it is the role that fits an endpoint that publishes one
    /// candidate and answers whoever asks. Both ends may be lite; what a lite agent cannot do
    /// is start a traversal, so it is the answerer here, never the caller.
    ///
    /// What it does, in the order RFC 8445 requires:
    /// - a Binding request is accepted only with the ICE username of the pair
    ///   (`remoteUfrag:localUfrag`) and a MESSAGE-INTEGRITY made with the peer's password;
    /// - PRIORITY has to be there (a check without it is not a check, section 7.3.1.1);
    /// - a peer that says it is ICE-CONTROLLED is told 487 Role Conflict, because a lite
    ///   agent is always the controlled side;
    /// - the source address of the check **is** the peer-reflexive candidate, so the answer
    ///   names it in XOR-MAPPED-ADDRESS and the pair is usable from that moment;
    /// - USE-CANDIDATE nominates that pair, and the first nomination wins.
    ///
    /// What it is not: the controlling agent's side of ICE - no checks of its own, no
    /// gathering, no TURN. And it is not yet **on** a socket: `Handle` takes the datagram and
    /// the address it came from and answers the bytes to send back, so the caller keeps the
    /// transport and this class stays testable without one.
    /// </summary>
    public sealed class IceLiteAgent
    {
        private readonly string _localUfrag;
        private readonly string _localPassword;
        private readonly string _remoteUfrag;
        private readonly string _remotePassword;

        private StunEndpoint _selected;

        /// <summary>
        /// One agent for one call. The two ufrags and the two passwords are the ones the
        /// offer and the answer carried: the peer's are what a check is checked against, and
        /// ours are what the answer is signed with.
        /// </summary>
        public IceLiteAgent(string localUfrag, string localPassword,
            string remoteUfrag, string remotePassword)
        {
            if (string.IsNullOrEmpty(localUfrag) || string.IsNullOrEmpty(localPassword))
                throw new ProtocolException("an ICE agent needs its own ufrag and password");
            if (string.IsNullOrEmpty(remoteUfrag) || string.IsNullOrEmpty(remotePassword))
                throw new ProtocolException("an ICE agent needs the peer's ufrag and password");

            _localUfrag = localUfrag;
            _localPassword = localPassword;
            _remoteUfrag = remoteUfrag;
            _remotePassword = remotePassword;
        }

        /// <summary>
        /// The ICE username a check for this agent has to carry: this side's ufrag first, the
        /// peer's second (RFC 8445, section 7.2.2).
        ///
        /// The order is not cosmetic. A check is written by the peer as "the receiver's ufrag,
        /// then the sender's", so the string the peer sends and the string the receiver expects
        /// are the same one - and a receiver that compared them the other way round would answer
        /// 401 to every check a correct agent sent, on a call whose signalling had worked
        /// perfectly. This project's own two roles now agree on it: `IceControllingAgent.UserName`
        /// writes the same order from the sending side.
        /// </summary>
        public string ExpectedUserName
        {
            get { return _localUfrag + ":" + _remoteUfrag; }
        }

        /// <summary>
        /// The address the call's media goes to, once a check nominated it. Null until then:
        /// a pair that was only validated is not a pair the media may use.
        /// </summary>
        public StunEndpoint Selected
        {
            get { return _selected; }
        }

        public bool Nominated
        {
            get { return _selected != null; }
        }

        /// <summary>Checks that arrived and were refused, for the diagnostics line.</summary>
        public int RefusedCount { get; private set; }

        /// <summary>Datagrams that were not STUN at all, or were not readable as one.</summary>
        public int IgnoredCount { get; private set; }

        /// <summary>
        /// Reads one datagram from `from` and answers the bytes to send back, or null when
        /// there is nothing to say. A check that is not authenticated is answered with 401
        /// and never nominates anything.
        /// </summary>
        public byte[] Handle(byte[] datagram, StunEndpoint from)
        {
            if (datagram == null || from == null) return null;
            if (!StunMessage.Looks(datagram, 0, datagram.Length))
            {
                IgnoredCount++;
                return null;
            }

            StunMessage message;
            try
            {
                message = StunMessage.Parse(datagram);
            }
            catch (ProtocolException)
            {
                // A malformed datagram on a media port is normal traffic: a peer's keepalive
                // from an older stack, a stray packet, an attack. It is counted, not logged
                // per packet, and it never stops the socket.
                IgnoredCount++;
                return null;
            }

            // A Binding indication is a keepalive: it has no transaction to answer and no
            // pair to validate. A lite agent stays silent, which is what the peer expects.
            if (message.Class == StunClass.Indication) return null;

            // A response is not this agent's business: it never sent a check.
            if (message.Class != StunClass.Request) return null;

            if (message.Method != StunMethod.Binding)
                return Refuse(message, 400, "Bad Request");

            // A fingerprint that does not match means the datagram was damaged or made up.
            // RFC 5389 section 8 says drop it; answering would be answering bytes nobody sent.
            if (message.Find(StunAttributeType.Fingerprint) != null && !message.VerifyFingerprint())
            {
                IgnoredCount++;
                return null;
            }

            if (!string.Equals(message.UserName, ExpectedUserName, StringComparison.Ordinal))
                return Refuse(message, 401, "Unauthorized");

            // The check is signed with the receiver's password, which is this side's own (RFC 8445,
            // section 7.2.2, short-term credentials). It used to be read the other way round, so a
            // correct controlling agent that signed with our password was answered 401 on a pair
            // that had been offered perfectly - the same inversion the username had.
            if (!message.VerifyIntegrity(System.Text.Encoding.UTF8.GetBytes(_localPassword)))
                return Refuse(message, 401, "Unauthorized");

            if (message.Find(StunAttributeType.Priority) == null)
                return Refuse(message, 400, "Bad Request");

            // A lite agent is always controlled; a peer that claims to be controlled too is
            // the one that has to switch (RFC 8445, section 7.3.1.1).
            if (message.Find(StunAttributeType.IceControlled) != null)
                return Refuse(message, 487, "Role Conflict");

            bool nomination = message.Find(StunAttributeType.UseCandidate) != null;
            if (nomination && _selected == null) _selected = from;

            return Answer(message, from);
        }

        private byte[] Answer(StunMessage request, StunEndpoint from)
        {
            var answer = new StunMessage(StunMethod.Binding, StunClass.SuccessResponse,
                request.TransactionId);

            answer.AddXorAddress(from);
            answer.AddText(StunAttributeType.Software, "whatsapp-wp/1");

            return answer.Seal(System.Text.Encoding.UTF8.GetBytes(_localPassword));
        }

        private byte[] Refuse(StunMessage request, int code, string reason)
        {
            RefusedCount++;

            var answer = new StunMessage(StunMethod.Binding, StunClass.ErrorResponse,
                request.TransactionId);

            answer.AddError(code, reason);
            answer.AddText(StunAttributeType.Software, "whatsapp-wp/1");

            return answer.Seal(System.Text.Encoding.UTF8.GetBytes(_localPassword));
        }
    }
}
