using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The controlling side of ICE: what its checks say, and that a traversal against this
    /// project's own lite answerer ends with both ends on the same pair.
    ///
    /// Why the second half matters more than the first: a lite agent cannot open a path, so a
    /// call placed from a mobile network needs exactly this agent, and the two roles only work
    /// together if they agree on three things the specifications put in three different places -
    /// the username (receiver first), the password a check is signed with (the receiver's), and
    /// which side nominates. Each is checked alone in `IceLiteTests`; the pairing is what a call
    /// uses, and a seam between two correct halves is where a call goes silent.
    /// </summary>
    public static class IceControllingTests
    {
        private const string LocalUfrag = "Cc01";

        private const string LocalPassword = "caller-password";

        private const string RemoteUfrag = "An02";

        private const string RemotePassword = "answerer-password";

        private const ulong TieBreaker = 0x4d3c2b1a5e6f7081ul;

        private static readonly StunEndpoint Answerer = StunEndpoint.Parse("192.0.2.5", 50000);

        private static readonly StunEndpoint Elsewhere = StunEndpoint.Parse("192.0.2.6", 50001);

        [Test]
        public static void TheCheckNamesTheAnswererFirstAndAsksForTheNomination()
        {
            IceControllingAgent agent = Agent();

            IList<byte[]> checks = agent.Start(new List<StunEndpoint> { Answerer });

            Assert.AreEqual(1, checks.Count);

            StunMessage check = StunMessage.Parse(checks[0]);

            Assert.AreEqual(StunMethod.Binding, check.Method);
            Assert.AreEqual(StunClass.Request, check.Class);
            Assert.AreEqual("An02:Cc01", check.UserName,
                "the answerer's ufrag first: the two ends then name the pair the same way");
            Assert.AreEqual(IceControllingAgent.CheckPriority,
                check.Find(StunAttributeType.Priority).AsUInt32);
            Assert.IsTrue(check.Find(StunAttributeType.IceControlling) != null,
                "a controlling agent says so, or the answerer cannot sort out the roles");
            Assert.IsTrue(check.Find(StunAttributeType.UseCandidate) != null,
                "aggressive nomination: the first pair that answers is the pair");
            Assert.IsTrue(check.VerifyIntegrity(Utf8(RemotePassword)),
                "a check is signed with the peer's password, which is the one it knows");
        }

        [Test]
        public static void ATraversalAgainstTheLiteAnswererNominatesTheSamePairOnBothEnds()
        {
            var answerer = new IceLiteAgent(RemoteUfrag, RemotePassword, LocalUfrag, LocalPassword);
            IceControllingAgent caller = Agent();

            IList<byte[]> checks = caller.Start(new List<StunEndpoint> { Answerer });

            for (int i = 0; i < checks.Count; i++)
            {
                byte[] answer = answerer.Handle(checks[i], Answerer);
                Assert.IsTrue(answer != null, "the lite agent answered the check");

                caller.Receive(answer, Answerer);
            }

            Assert.IsTrue(caller.Nominated, "the check that answered nominated the pair");
            Assert.AreEqual(Answerer.Text, caller.Selected.Text);
            Assert.AreEqual(Answerer.Port, caller.Selected.Port);

            Assert.IsTrue(answerer.Nominated, "and the answerer nominated the pair it answered on");
            Assert.AreEqual(Answerer.Port, answerer.Selected.Port);
            Assert.AreEqual(0, answerer.RefusedCount, "nothing was refused, so the ufrag order agrees");
        }

        [Test]
        public static void AnAnswerFromSomewhereElseNominatesNothing()
        {
            IceControllingAgent agent = Agent();
            byte[] check = agent.Start(new List<StunEndpoint> { Answerer })[0];

            StunMessage answer = StunMessage.Parse(check);
            var reply = new StunMessage(StunMethod.Binding, StunClass.SuccessResponse,
                answer.TransactionId);
            reply.AddXorAddress(Elsewhere);

            agent.Receive(reply.Seal(Utf8(RemotePassword)), Elsewhere);

            Assert.IsFalse(agent.Nominated,
                "the transaction id travels in the datagram, so the address is what identifies the pair");
            Assert.AreEqual(1, agent.IgnoredCount);
        }

        [Test]
        public static void ASuccessThatDoesNotVerifyHandsTheMediaToNobody()
        {
            IceControllingAgent agent = Agent();
            byte[] check = agent.Start(new List<StunEndpoint> { Answerer })[0];
            byte[] transactionId = StunMessage.Parse(check).TransactionId;

            var reply = new StunMessage(StunMethod.Binding, StunClass.SuccessResponse, transactionId);
            reply.AddXorAddress(Answerer);

            agent.Receive(reply.Seal(Utf8("not-the-password")), Answerer);

            Assert.IsFalse(agent.Nominated);
            Assert.AreEqual(1, agent.IgnoredCount);
        }

        [Test]
        public static void AnUnansweredCheckIsRetriedOnTheRfcScheduleAndThenGivesUp()
        {
            IceControllingAgent agent = Agent();
            agent.Start(new List<StunEndpoint> { Answerer });

            Assert.IsTrue(agent.Tick(0) == null, "the first ask starts the schedule");
            Assert.IsTrue(agent.Tick(IceControllingAgent.RetransmissionIntervalMs - 1) == null,
                "half a second is not yet late");

            Assert.IsTrue(agent.Tick(IceControllingAgent.RetransmissionIntervalMs) != null,
                "the first retransmission");
            Assert.IsTrue(agent.Tick(1499) == null, "the second waits a second from there");
            Assert.IsTrue(agent.Tick(1500) != null);
            Assert.IsTrue(agent.Tick(3499) == null);
            Assert.IsTrue(agent.Tick(3500) != null);
            Assert.IsTrue(agent.Tick(7499) == null);
            Assert.IsTrue(agent.Tick(7500) != null, "the fourth and last");

            Assert.IsTrue(agent.Tick(15500) == null);
            Assert.IsTrue(agent.Failure != null, "an unanswered traversal says so");
            Assert.IsFalse(agent.Nominated);
        }

        [Test]
        public static void ARoleConflictOnOneCandidateStillLetsTheNextOneNominate()
        {
            var answerer = new IceLiteAgent(RemoteUfrag, RemotePassword, LocalUfrag, LocalPassword);
            IceControllingAgent agent = Agent();

            IList<byte[]> checks = agent.Start(new List<StunEndpoint> { Elsewhere, Answerer });

            Assert.AreEqual(2, checks.Count);

            for (int i = 0; i < checks.Count; i++)
            {
                StunMessage sent = StunMessage.Parse(checks[i]);
                StunEndpoint to = i == 0 ? Elsewhere : Answerer;

                // The first candidate is checked against a peer that claims the controlled role,
                // which is what 487 is for; the second answers normally.
                byte[] answer = i == 0
                    ? Refusal(sent, 487, "Role Conflict")
                    : answerer.Handle(checks[i], Answerer);

                agent.Receive(answer, to);
            }

            Assert.AreEqual(1, agent.RefusedCount);
            Assert.IsTrue(agent.Nominated, "a refused candidate is not the end of the traversal");
            Assert.AreEqual(Answerer.Port, agent.Selected.Port);
        }

        [Test]
        public static void ARoleConflictOnEveryCandidateIsAFailureThatNamesIt()
        {
            IceControllingAgent agent = Agent();
            IList<byte[]> checks = agent.Start(new List<StunEndpoint> { Answerer });

            agent.Receive(Refusal(StunMessage.Parse(checks[0]), 487, "Role Conflict"), Answerer);

            Assert.IsFalse(agent.Nominated);
            Assert.IsTrue(agent.Failure != null && agent.Failure.IndexOf("487") >= 0, agent.Failure);
            Assert.AreEqual(0, agent.Outstanding);
        }

        [Test]
        public static void AnAgentWithoutBothSidesOrWithoutTransactionIdsIsNotBuilt()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                new IceControllingAgent("", LocalPassword, RemoteUfrag, RemotePassword, TieBreaker,
                    Ids());
            });

            Assert.Throws<ProtocolException>(delegate
            {
                new IceControllingAgent(LocalUfrag, LocalPassword, RemoteUfrag, RemotePassword,
                    TieBreaker, null);
            });
        }

        [Test]
        public static void TransactionIdsThatAreNotTwelveBytesStopTheStart()
        {
            var agent = new IceControllingAgent(LocalUfrag, LocalPassword, RemoteUfrag,
                RemotePassword, TieBreaker, delegate { return new byte[11]; });

            Assert.Throws<ProtocolException>(delegate
            {
                agent.Start(new List<StunEndpoint> { Answerer });
            });
        }

        /// <summary>One agent with the pair above: the caller of every test in this file.</summary>
        private static IceControllingAgent Agent()
        {
            return new IceControllingAgent(LocalUfrag, LocalPassword, RemoteUfrag, RemotePassword,
                TieBreaker, Ids());
        }

        /// <summary>
        /// A counter, not a random source: a test that asserts on a transaction id has to be able
        /// to say which one it expected.
        /// </summary>
        private static Func<byte[]> Ids()
        {
            int next = 0;

            return delegate
            {
                next++;
                var id = new byte[12];
                id[0] = (byte)(next >> 24);
                id[1] = (byte)(next >> 16);
                id[2] = (byte)(next >> 8);
                id[3] = (byte)next;
                return id;
            };
        }

        private static byte[] Refusal(StunMessage check, int code, string reason)
        {
            var answer = new StunMessage(StunMethod.Binding, StunClass.ErrorResponse,
                check.TransactionId);

            answer.AddError(code, reason);

            return answer.Seal(Utf8(RemotePassword));
        }

        private static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }
    }
}
