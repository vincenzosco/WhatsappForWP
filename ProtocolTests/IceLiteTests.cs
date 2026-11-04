using System;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The ICE-lite responder: what it answers, what it refuses, and when it decides that a
    /// pair is the one the call uses.
    ///
    /// Why it is pinned this way and not against a peer: there is no peer to check against
    /// yet, so every test here asks the question the specification answers in words - is an
    /// unauthenticated check answered, does a nomination stick, does a peer that claims the
    /// other role get 487 - and the bytes are built and read by the STUN codec that RFC 5769
    /// pins. That leaves the one thing the tests cannot prove, the wire itself, named where
    /// it belongs: in the call's socket, which does not exist yet.
    /// </summary>
    public static class IceLiteTests
    {
        private const string LocalUfrag = "Lc0f";

        private const string LocalPassword = "local-password";

        private const string RemoteUfrag = "Rv7x";

        private const string RemotePassword = "remote-password";

        private static readonly StunEndpoint PeerA = StunEndpoint.Parse("192.0.2.5", 50000);

        private static readonly StunEndpoint PeerB = StunEndpoint.Parse("192.0.2.6", 50001);

        [Test]
        public static void AnAuthenticatedCheckIsAnsweredAndTheAnswerVerifies()
        {
            IceLiteAgent agent = Agent();
            byte[] check = Check("Rv7x:Lc0f", RemotePassword, true, false, false);

            byte[] reply = agent.Handle(check, PeerA);

            Assert.IsTrue(reply != null, "a valid check has to be answered");

            StunMessage answer = StunMessage.Parse(reply);
            Assert.AreEqual(StunClass.SuccessResponse, answer.Class);
            Assert.ByteArraysEqual(StunMessage.Parse(check).TransactionId, answer.TransactionId,
                "the answer carries the check's transaction id");

            StunEndpoint mapped = answer.Find(StunAttributeType.XorMappedAddress)
                .ReadAddress(answer.TransactionId);

            Assert.AreEqual(PeerA.Text, mapped.Text,
                "the source address of the check is the peer-reflexive candidate");
            Assert.AreEqual(PeerA.Port, mapped.Port);

            Assert.IsTrue(answer.VerifyIntegrity(Utf8(LocalPassword)),
                "the answer is signed with our own password, which is what the peer knows");
            Assert.IsTrue(answer.VerifyFingerprint());
            Assert.IsFalse(agent.Nominated, "a check that did not nominate nominates nothing");
        }

        [Test]
        public static void ACheckSignedWithTheWrongPasswordIsRefusedAndSelectsNothing()
        {
            IceLiteAgent agent = Agent();
            byte[] check = Check("Rv7x:Lc0f", "not-the-password", true, true, false);

            byte[] reply = agent.Handle(check, PeerA);

            StunMessage answer = StunMessage.Parse(reply);
            Assert.AreEqual(StunClass.ErrorResponse, answer.Class);
            Assert.AreEqual(401, answer.Find(StunAttributeType.ErrorCode).ErrorCode);
            Assert.IsTrue(answer.VerifyIntegrity(Utf8(LocalPassword)),
                "even a refusal is signed, or the peer cannot tell it from an attack");
            Assert.IsTrue(agent.Selected == null, "an unauthenticated check must not nominate");
            Assert.AreEqual(1, agent.RefusedCount);
        }

        [Test]
        public static void TheUsernameHasToBeThePairsAndInTheRightOrder()
        {
            IceLiteAgent agent = Agent();
            byte[] reversed = Check("Lc0f:Rv7x", RemotePassword, true, true, false);

            StunMessage answer = StunMessage.Parse(agent.Handle(reversed, PeerA));

            Assert.AreEqual(401, answer.Find(StunAttributeType.ErrorCode).ErrorCode,
                "the username is remote ufrag, a colon, ours (RFC 8445, section 6.1.3)");
            Assert.IsTrue(agent.Selected == null);
        }

        [Test]
        public static void ACheckWithoutPriorityIsNotACheck()
        {
            IceLiteAgent agent = Agent();
            byte[] check = Check("Rv7x:Lc0f", RemotePassword, false, false, false);

            StunMessage answer = StunMessage.Parse(agent.Handle(check, PeerA));

            Assert.AreEqual(400, answer.Find(StunAttributeType.ErrorCode).ErrorCode);
        }

        [Test]
        public static void UseCandidateNominatesTheAddressTheCheckCameFromAndTheFirstOneStays()
        {
            IceLiteAgent agent = Agent();

            agent.Handle(Check("Rv7x:Lc0f", RemotePassword, true, false, false), PeerA);
            Assert.IsTrue(agent.Selected == null, "a validated pair is not yet the nominated one");

            agent.Handle(Check("Rv7x:Lc0f", RemotePassword, true, true, false), PeerA);
            Assert.IsTrue(agent.Nominated);
            Assert.AreEqual(PeerA.Port, agent.Selected.Port);

            agent.Handle(Check("Rv7x:Lc0f", RemotePassword, true, true, false), PeerB);
            Assert.AreEqual(PeerA.Port, agent.Selected.Port,
                "the pair in use is not moved by a later check from somewhere else");
        }

        [Test]
        public static void APeerThatClaimsToBeControlledIsToldRoleConflict()
        {
            IceLiteAgent agent = Agent();
            byte[] check = Check("Rv7x:Lc0f", RemotePassword, true, true, true);

            StunMessage answer = StunMessage.Parse(agent.Handle(check, PeerA));

            Assert.AreEqual(487, answer.Find(StunAttributeType.ErrorCode).ErrorCode,
                "a lite agent is always controlled, so the peer has to switch");
            Assert.IsTrue(agent.Selected == null);
        }

        [Test]
        public static void ABindingIndicationIsNotAnswered()
        {
            IceLiteAgent agent = Agent();

            var indication = new StunMessage(StunMethod.Binding, StunClass.Indication,
                Hex("0f0e0d0c0b0a090807060504"));
            indication.Add(StunAttribute.Flag(StunAttributeType.UseCandidate));

            Assert.IsTrue(agent.Handle(indication.Seal(Utf8(RemotePassword)), PeerA) == null,
                "a keepalive has no transaction to answer");
            Assert.IsTrue(agent.Selected == null);
        }

        [Test]
        public static void ABrokenFingerprintIsDroppedWithoutAnAnswer()
        {
            IceLiteAgent agent = Agent();
            byte[] check = Check("Rv7x:Lc0f", RemotePassword, true, true, false);
            check[check.Length - 1] = (byte)(check[check.Length - 1] ^ 0xFF);

            Assert.IsTrue(agent.Handle(check, PeerA) == null,
                "answering bytes that were damaged tells an attacker the address is live");
            Assert.IsTrue(agent.Selected == null);
            Assert.AreEqual(1, agent.IgnoredCount);
        }

        [Test]
        public static void TrafficThatIsNotStunIsIgnored()
        {
            IceLiteAgent agent = Agent();
            byte[] rtp = Hex("80010102030405060708090a0b0c");

            Assert.IsTrue(agent.Handle(rtp, PeerA) == null);
            Assert.AreEqual(1, agent.IgnoredCount);
            Assert.AreEqual(0, agent.RefusedCount);
        }

        [Test]
        public static void AMethodThisAgentDoesNotAnswerIsRefusedWithFourHundred()
        {
            IceLiteAgent agent = Agent();

            var allocate = new StunMessage(StunMethod.Allocate, StunClass.Request,
                Hex("1112131415161718191a1b1c"));
            allocate.AddText(StunAttributeType.UserName, "Rv7x:Lc0f");
            allocate.AddUInt32(StunAttributeType.Priority, 0x7e0000ff);

            StunMessage answer = StunMessage.Parse(
                agent.Handle(allocate.Seal(Utf8(RemotePassword)), PeerA));

            Assert.AreEqual(400, answer.Find(StunAttributeType.ErrorCode).ErrorCode,
                "this agent answers Binding and nothing else");
        }

        [Test]
        public static void AnAgentWithoutBothSidesUfragsAndPasswordsIsNotBuilt()
        {
            Assert.Throws<ProtocolException>(delegate { new IceLiteAgent("", LocalPassword, RemoteUfrag, RemotePassword); });
            Assert.Throws<ProtocolException>(delegate { new IceLiteAgent(LocalUfrag, LocalPassword, RemoteUfrag, ""); });
        }

        private static IceLiteAgent Agent()
        {
            return new IceLiteAgent(LocalUfrag, LocalPassword, RemoteUfrag, RemotePassword);
        }

        private static byte[] Check(string userName, string password,
            bool priority, bool useCandidate, bool claimsControlled)
        {
            var check = new StunMessage(StunMethod.Binding, StunClass.Request,
                Hex("0a0b0c0d0e0f101112131415"));

            check.AddText(StunAttributeType.UserName, userName);

            if (priority) check.AddUInt32(StunAttributeType.Priority, 0x7e0000ff);

            if (claimsControlled)
                check.Add(StunAttribute.UInt64(StunAttributeType.IceControlled, 0x4d3c2b1a5e6f7081ul));
            else
                check.Add(StunAttribute.UInt64(StunAttributeType.IceControlling, 0x4d3c2b1a5e6f7081ul));

            if (useCandidate) check.Add(StunAttribute.Flag(StunAttributeType.UseCandidate));

            return check.Seal(Utf8(password));
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
