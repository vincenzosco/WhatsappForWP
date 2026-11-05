using System;
using System.Collections.Generic;
using System.Text;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The other half of ICE (RFC 8445, section 2.5): the side that *starts* the traversal, checks
    /// every candidate it was offered, and nominates the pair the media goes on.
    ///
    /// Why it exists: `IceLiteAgent` answers and never asks, which is the right role for a phone
    /// that publishes one address and waits - but a call placed from behind a carrier NAT needs
    /// the other role, because a lite agent cannot open a path, only accept one. The connection
    /// that has to be opened is this one.
    ///
    /// What it does, in the order RFC 8445 requires:
    /// - a check names the peer's ufrag first and this side's second (`UserName`), carries a
    ///   PRIORITY, says ICE-CONTROLLING with this agent's tie-breaker, and is signed with the
    ///   peer's password;
    /// - USE-CANDIDATE travels in every check (aggressive nomination), so the first pair that
    ///   answers is the pair, and the answerer never has to be asked twice;
    /// - a success is only a success when it carries the check's transaction id, comes from the
    ///   candidate it was sent to, and verifies under the peer's password - anything else is
    ///   counted and ignored;
    /// - a 487 means the peer also believes it is controlling: that candidate is refused and the
    ///   next one is tried, which is what a lite peer sends when the roles are backwards;
    /// - an unanswered candidate is retried on RFC 8445's schedule (half a second, doubling) and
    ///   then abandoned, and an agent whose every candidate went unanswered says so instead of
    ///   waiting for a user to give up.
    ///
    /// What it is not: a socket, a gatherer, or a clock. `Start` writes the first checks, `Receive`
    /// takes one datagram, `Tick` is asked by the caller's timer - so a test drives the whole
    /// traversal against `IceLiteAgent` in memory, which is exactly the pairing a call uses.
    ///
    /// The transaction ids come from the caller (`Func&lt;byte[]&gt;`) for the same reason: sixteen
    /// bytes of randomness in a class that a fixed script has to reproduce would make the test
    /// assert on its own input.
    /// </summary>
    public sealed class IceControllingAgent
    {
        /// <summary>The transaction id length STUN fixes (RFC 5389, section 6).</summary>
        private const int TransactionIdLength = 12;

        /// <summary>How long the first retransmission waits: half a second, as RFC 8445 suggests.</summary>
        public const int RetransmissionIntervalMs = 500;

        /// <summary>
        /// How many times a check is sent again before that candidate is given up on. Four
        /// resends at 0.5, 1, 2 and 4 seconds is seven and a half seconds per candidate, which is
        /// longer than a call's ringing phase and shorter than a person's patience.
        /// </summary>
        public const int MaxRetransmissions = 4;

        /// <summary>
        /// The PRIORITY every check carries. RFC 8445 section 7.1.1 makes it a number the peer
        /// ranks pairs by; this agent has one candidate type and no preference between peers, so
        /// it is the type-preferred value with the local preference at its maximum.
        /// </summary>
        public const uint CheckPriority = 0x7e0000ff;

        /// <summary>
        /// How many candidates one call is checked against. An offer carries one per local
        /// interface - two or three on this phone - and a peer that named fifty would otherwise
        /// turn one call into fifty datagrams a retransmission.
        /// </summary>
        public const int MaxCandidates = 8;

        /// <summary>One outstanding check: where it went, and when it is due again.</summary>
        private sealed class Check
        {
            public StunEndpoint To;
            public byte[] TransactionId;
            public byte[] Datagram;
            public long DueMs;
            public int Sent;
            public bool Answered;
        }

        private readonly string _localUfrag;
        private readonly string _localPassword;
        private readonly string _remoteUfrag;
        private readonly string _remotePassword;
        private readonly ulong _tieBreaker;
        private readonly Func<byte[]> _transactionId;
        private readonly List<Check> _checks = new List<Check>();

        private StunEndpoint _selected;
        private string _failure;

        /// <summary>
        /// One agent for one call. `localUfrag`/`localPassword` are this side's ICE credentials,
        /// `remoteUfrag`/`remotePassword` the peer's, and `tieBreaker` the number a 487 compares.
        /// </summary>
        public IceControllingAgent(string localUfrag, string localPassword, string remoteUfrag,
            string remotePassword, ulong tieBreaker, Func<byte[]> transactionId)
        {
            if (string.IsNullOrEmpty(localUfrag) || string.IsNullOrEmpty(localPassword))
                throw new ProtocolException("an ICE agent needs its own ufrag and password");
            if (string.IsNullOrEmpty(remoteUfrag) || string.IsNullOrEmpty(remotePassword))
                throw new ProtocolException("an ICE agent needs the peer's ufrag and password");
            if (transactionId == null)
                throw new ProtocolException("an ICE controlling agent needs a source of transaction ids");

            _localUfrag = localUfrag;
            _localPassword = localPassword;
            _remoteUfrag = remoteUfrag;
            _remotePassword = remotePassword;
            _tieBreaker = tieBreaker;
            _transactionId = transactionId;
        }

        /// <summary>
        /// The ICE username a check carries: the peer's ufrag first, this side's second (RFC 8445,
        /// section 7.2.2). The peer compares it against its own "local:remote", which is the same
        /// string, and that is the whole point of the order.
        /// </summary>
        public string UserName
        {
            get { return _remoteUfrag + ":" + _localUfrag; }
        }

        /// <summary>The pair the media goes on, once a check answered. Null before that.</summary>
        public StunEndpoint Selected
        {
            get { return _selected; }
        }

        public bool Nominated
        {
            get { return _selected != null; }
        }

        /// <summary>Why the traversal stopped, or null while it is still running.</summary>
        public string Failure
        {
            get { return _failure; }
        }

        /// <summary>Checks the peer refused, for the diagnostics line.</summary>
        public int RefusedCount { get; private set; }

        /// <summary>Datagrams that were not an answer to a check of this agent's.</summary>
        public int IgnoredCount { get; private set; }

        /// <summary>Candidates still being checked.</summary>
        public int Outstanding
        {
            get
            {
                int left = 0;
                for (int i = 0; i < _checks.Count; i++)
                {
                    if (!_checks[i].Answered) left++;
                }

                return left;
            }
        }

        /// <summary>
        /// The first checks, one per candidate, in the order the offer named them: the highest
        /// priority first, because that is the order a pair is tried in.
        /// </summary>
        public IList<byte[]> Start(IList<StunEndpoint> candidates)
        {
            if (candidates == null)
                throw new ProtocolException("an ICE controlling agent needs candidates to check");
            if (_checks.Count > 0)
                throw new ProtocolException("an ICE controlling agent starts once");

            var outbound = new List<byte[]>();

            for (int i = 0; i < candidates.Count && i < MaxCandidates; i++)
            {
                StunEndpoint to = candidates[i];
                if (to == null) continue;
                if (!IsUsable(to))
                {
                    // A candidate with no address, or with the any-address of a phone that has
                    // not attached to a network yet, is one nothing can be sent to.
                    IgnoredCount++;
                    continue;
                }

                byte[] transactionId = _transactionId();
                if (transactionId == null || transactionId.Length != TransactionIdLength)
                {
                    throw new ProtocolException("an ICE controlling agent needs "
                        + TransactionIdLength + "-byte transaction ids");
                }

                var check = new Check();
                check.To = to;
                check.TransactionId = transactionId;
                check.Datagram = Build(to, transactionId);
                check.Sent = 1;

                _checks.Add(check);
                outbound.Add(check.Datagram);
            }

            if (_checks.Count == 0) Fail("no candidate to check");

            return outbound;
        }

        /// <summary>
        /// One datagram from `from`. A response to a check is read and the pair nominated; nothing
        /// is sent back, because a response has no transaction to answer. Anything else is counted
        /// and ignored: this agent never answers a request, it only sends them.
        /// </summary>
        public void Receive(byte[] datagram, StunEndpoint from)
        {
            if (datagram == null || from == null)
            {
                IgnoredCount++;
                return;
            }

            if (!StunMessage.Looks(datagram, 0, datagram.Length))
            {
                IgnoredCount++;
                return;
            }

            StunMessage message;
            try
            {
                message = StunMessage.Parse(datagram);
            }
            catch (ProtocolException)
            {
                IgnoredCount++;
                return;
            }

            if (message.Class != StunClass.SuccessResponse
                && message.Class != StunClass.ErrorResponse)
            {
                IgnoredCount++;
                return;
            }

            Check check = Find(message.TransactionId);
            if (check == null || !Same(check.To, from))
            {
                // A response from an address the check was not sent to is not an answer to it:
                // the transaction id comes from the datagram, and anybody can write one.
                IgnoredCount++;
                return;
            }

            if (message.Class == StunClass.ErrorResponse)
            {
                // 487 is a role conflict: the peer says it is controlling too. This agent keeps
                // the role (it is the one that opened the path) and moves on to the next
                // candidate, which is what a lite peer's answer leaves to do.
                check.Answered = true;
                RefusedCount++;

                if (Outstanding == 0 && _selected == null)
                {
                    Fail("every candidate answered with a refusal, the last one "
                        + (message.Find(StunAttributeType.ErrorCode) == null
                            ? "an error"
                            : message.Find(StunAttributeType.ErrorCode).ErrorCode.ToString()));
                }

                return;
            }

            if (!message.VerifyIntegrity(Utf8(_remotePassword)))
            {
                // A success that does not verify under the peer's password was not written by a
                // party that knows it, and nominating on it would hand the media to a stranger.
                IgnoredCount++;
                return;
            }

            check.Answered = true;

            if (_selected == null) _selected = from;
        }

        /// <summary>
        /// The clock, asked by the caller's timer: the check to send again, or null.
        ///
        /// The schedule is RFC 8445's - half a second, then doubling - and it exists because a
        /// check has no acknowledgement of its own until the peer answers: a datagram lost on a
        /// phone's network and a peer that is thinking look the same for half a second, and only
        /// a second attempt tells them apart. `nowMs` is the caller's monotonic count; nothing
        /// here reads a clock.
        /// </summary>
        public byte[] Tick(long nowMs)
        {
            if (_selected != null || _failure != null) return null;

            for (int i = 0; i < _checks.Count; i++)
            {
                Check check = _checks[i];
                if (check.Answered) continue;

                if (check.DueMs == 0)
                {
                    // The first ask starts the schedule at a known point instead of resending a
                    // check the moment it was written.
                    check.DueMs = nowMs + RetransmissionIntervalMs;
                    return null;
                }

                if (nowMs < check.DueMs) return null;

                if (check.Sent > MaxRetransmissions)
                {
                    check.Answered = true;
                    continue;
                }

                check.Sent++;
                check.DueMs = nowMs + ((long)RetransmissionIntervalMs << (check.Sent - 1));

                return check.Datagram;
            }

            if (Outstanding == 0 && _selected == null)
            {
                Fail("the ICE checks went unanswered on every candidate");
            }

            return null;
        }

        private byte[] Build(StunEndpoint to, byte[] transactionId)
        {
            var request = new StunMessage(StunMethod.Binding, StunClass.Request, transactionId);

            request.AddText(StunAttributeType.UserName, UserName);
            request.AddUInt32(StunAttributeType.Priority, CheckPriority);
            request.Add(StunAttribute.UInt64(StunAttributeType.IceControlling, _tieBreaker));
            request.Add(StunAttribute.Flag(StunAttributeType.UseCandidate));

            return request.Seal(Utf8(_remotePassword));
        }

        private Check Find(byte[] transactionId)
        {
            if (transactionId == null) return null;

            for (int i = 0; i < _checks.Count; i++)
            {
                if (Same(_checks[i].TransactionId, transactionId)) return _checks[i];
            }

            return null;
        }

        private void Fail(string reason)
        {
            if (_failure == null) _failure = reason;
        }

        /// <summary>
        /// Whether a candidate is worth a datagram: an address of the right length, and not the
        /// any-address a phone reports before it has attached to a network.
        /// </summary>
        private static bool IsUsable(StunEndpoint endpoint)
        {
            byte[] address = endpoint.Address;
            if (address == null) return false;
            if (address.Length != 4 && address.Length != 16) return false;

            for (int i = 0; i < address.Length; i++)
            {
                if (address[i] != 0) return true;
            }

            return false;
        }

        private static bool Same(StunEndpoint left, StunEndpoint right)
        {
            if (left == null || right == null) return false;
            if (left.Port != right.Port) return false;

            byte[] a = left.Address;
            byte[] b = right.Address;

            if (a == null || b == null || a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
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

        private static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }
    }
}
