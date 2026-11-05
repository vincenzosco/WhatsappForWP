using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The media path of one call: the three layers driven by the same datagrams.
    ///
    /// Why a scripted handshake and not a DTLS one: `DtlsClient` needs a server to answer it,
    /// and the server half of DTLS is the piece this project does not have yet. What can be
    /// checked without one is the seam - which datagram goes to which layer, when the handshake
    /// starts, that media is refused before the keys exist, and that nothing is read from an
    /// address that is not the nominated pair - and that is what these tests pin. The handshake
    /// itself is pinned by `DtlsTests` against recorded flights.
    /// </summary>
    public static class CallTransportTests
    {
        private const string LocalUfrag = "Lc0f";

        private const string LocalPassword = "local-password";

        private const string RemoteUfrag = "Rv7x";

        private const string RemotePassword = "remote-password";

        private static readonly StunEndpoint PeerA = StunEndpoint.Parse("192.0.2.5", 50000);

        private static readonly StunEndpoint PeerB = StunEndpoint.Parse("192.0.2.6", 50001);

        [Test]
        public static void ACheckThatDoesNotNominateStartsNoHandshake()
        {
            var handshake = new ScriptedHandshake();
            CallTransport transport = Transport(handshake);

            IList<byte[]> outbound = transport.Receive(Check(false), PeerA);

            Assert.AreEqual(1, outbound.Count, "a check is answered");
            Assert.IsTrue(StunMessage.Looks(outbound[0], 0, outbound[0].Length),
                "the answer is a STUN message, not a handshake");
            Assert.AreEqual(CallTransportState.Waiting, transport.State,
                "a validated pair that was not nominated is not a path the media may use");
            Assert.AreEqual(0, handshake.Starts, "the handshake waits for the nomination");
        }

        [Test]
        public static void ANominationAnswersTheCheckAndOpensTheHandshake()
        {
            var handshake = new ScriptedHandshake();
            CallTransport transport = Transport(handshake);

            IList<byte[]> outbound = transport.Receive(Check(true), PeerA);

            Assert.AreEqual(2, outbound.Count, "the answer to the check and the handshake's first flight");
            Assert.IsTrue(StunMessage.Looks(outbound[0], 0, outbound[0].Length));

            StunMessage answer = StunMessage.Parse(outbound[0]);
            Assert.AreEqual(StunClass.SuccessResponse, answer.Class);
            Assert.AreEqual("client hello", Encoding.UTF8.GetString(outbound[1]),
                "the handshake's own first datagram follows the answer");

            Assert.AreEqual(CallTransportState.Handshaking, transport.State);
            Assert.AreEqual(PeerA.Text, transport.Peer.Text, "the nominated address is the peer");
            Assert.IsTrue(transport.Resend() != null, "a datagram to repeat while the answer is missing");
            Assert.IsTrue(transport.Media == null, "no keys before the handshake finishes");
        }

        [Test]
        public static void TheHandshakesAnswerOnTheNominatedPairMakesTheAudio()
        {
            var handshake = new ScriptedHandshake();
            CallTransport transport = Transport(handshake);

            transport.Receive(Check(true), PeerA);
            IList<byte[]> outbound = transport.Receive(Dtls(TypeHandshake, Message("server flight")), PeerA);

            Assert.AreEqual(1, outbound.Count, "the handshake's own answer goes back");
            Assert.AreEqual(CallTransportState.Ready, transport.State);
            Assert.IsTrue(transport.Media != null, "the keys exist, so the audio does");
            Assert.IsTrue(transport.IsTrusted, "the scripted handshake is a trusted one");
            Assert.IsTrue(transport.Resend() == null, "nothing to repeat once the path is up");

            byte[] packet = transport.Send(Message("opus frame"));

            Assert.IsTrue(packet != null, "a call that is ready sends");
            Assert.AreEqual(0x80, packet[0] & 0xc0, "what goes out is an RTP packet under SRTP");
        }

        [Test]
        public static void MediaBeforeTheKeysIsCountedAndDropped()
        {
            var handshake = new ScriptedHandshake();
            CallTransport transport = Transport(handshake);

            transport.Receive(Check(true), PeerA);
            IList<byte[]> outbound = transport.Receive(Rtp(), PeerA);

            Assert.AreEqual(0, outbound.Count, "a frame with no keys has nothing to answer");
            Assert.AreEqual(1, transport.MediaBeforeReady);
            Assert.AreEqual(CallTransportState.Handshaking, transport.State);
            Assert.IsTrue(transport.Send(Message("opus frame")) == null,
                "a call that is not up yet sends nothing");
        }

        [Test]
        public static void AFrameFromAnotherAddressIsNotReadIntoTheCall()
        {
            var handshake = new ScriptedHandshake();
            CallTransport transport = Transport(handshake);

            transport.Receive(Check(true), PeerA);
            transport.Receive(Dtls(TypeHandshake, Message("server flight")), PeerA);

            IList<byte[]> outbound = transport.Receive(Rtp(), PeerB);

            Assert.AreEqual(0, outbound.Count);
            Assert.AreEqual(1, transport.NotFromPeer,
                "a third address cannot speak into a call it is not on");
            Assert.AreEqual(0, transport.Media.Received, "nothing was read from it");
        }

        [Test]
        public static void AHandshakeThatFailsStopsThePathAndKeepsTheReason()
        {
            var handshake = new ScriptedHandshake();
            handshake.Refuse = "the server's Finished does not verify";
            CallTransport transport = Transport(handshake);

            transport.Receive(Check(true), PeerA);
            transport.Receive(Dtls(TypeHandshake, Message("server flight")), PeerA);

            Assert.AreEqual(CallTransportState.Failed, transport.State);
            Assert.AreEqual("the server's Finished does not verify", transport.Failure);
            Assert.IsTrue(transport.Media == null);
            Assert.AreEqual(0, transport.Receive(Rtp(), PeerA).Count, "a failed path reads nothing");
        }

        [Test]
        public static void AnUnreadableDatagramIsCountedAndNotAnException()
        {
            var handshake = new ScriptedHandshake();
            CallTransport transport = Transport(handshake);

            IList<byte[]> before = transport.Receive(new byte[] { 0x01, 0x02, 0x03 }, PeerA);

            Assert.AreEqual(0, before.Count);
            Assert.AreEqual(1, transport.Dropped, "there is no path yet, so this is dropped");

            transport.Receive(Check(true), PeerA);
            transport.Receive(Dtls(TypeHandshake, Message("server flight")), PeerA);

            transport.Receive(new byte[] { 0x01, 0x02, 0x03, 0x04 }, PeerA);
            Assert.AreEqual(2, transport.Dropped);
            Assert.AreEqual(CallTransportState.Ready, transport.State, "one stray packet does not end a call");
        }

        [Test]
        public static void ATransportWithoutItsPartsIsNotBuilt()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                new CallTransport(null, new ScriptedHandshake(), 1u, 2u);
            });

            Assert.Throws<ProtocolException>(delegate
            {
                new CallTransport(Agent(), null, 1u, 2u);
            });
        }

        private const byte TypeHandshake = 22;

        private static CallTransport Transport(IMediaHandshake handshake)
        {
            return new CallTransport(Agent(), handshake, 0x11223344u, 0x55667788u);
        }

        private static IceLiteAgent Agent()
        {
            return new IceLiteAgent(LocalUfrag, LocalPassword, RemoteUfrag, RemotePassword);
        }

        private static byte[] Check(bool useCandidate)
        {
            var check = new StunMessage(StunMethod.Binding, StunClass.Request,
                Hex("0a0b0c0d0e0f101112131415"));

            check.AddText(StunAttributeType.UserName, RemoteUfrag + ":" + LocalUfrag);
            check.AddUInt32(StunAttributeType.Priority, 0x7e0000ff);
            check.Add(StunAttribute.UInt64(StunAttributeType.IceControlling, 0x4d3c2b1a5e6f7081ul));

            if (useCandidate) check.Add(StunAttribute.Flag(StunAttributeType.UseCandidate));

            return check.Seal(Utf8(RemotePassword));
        }

        /// <summary>A DTLS record header in front of the body, so the transport classifies it.</summary>
        private static byte[] Dtls(byte type, byte[] body)
        {
            var record = new byte[13 + body.Length];
            record[0] = type;
            record[1] = 0xfe;
            record[2] = 0xfd;
            record[11] = (byte)(body.Length >> 8);
            record[12] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, record, 13, body.Length);
            return record;
        }

        /// <summary>An RTP packet: version 2, no padding, no extension, one CSRC, PT 111.</summary>
        private static byte[] Rtp()
        {
            return new byte[] { 0x80, 0x6f, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
                0x11, 0x22, 0x33, 0x44, 0xde, 0xad, 0xbe, 0xef };
        }

        private static byte[] Message(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        private static byte[] Utf8(string text)
        {
            return Encoding.UTF8.GetBytes(text);
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

        /// <summary>
        /// A handshake in the shape the transport expects, with the flights a test wrote: one
        /// datagram out, one in, complete - and a way to fail instead.
        /// </summary>
        private sealed class ScriptedHandshake : IMediaHandshake
        {
            private bool _complete;

            public int Starts;

            public string Refuse;

            public byte[] Start()
            {
                Starts++;
                return Message("client hello");
            }

            public byte[] Receive(byte[] datagram)
            {
                if (Refuse != null) return null;

                _complete = true;
                return Message("client finished");
            }

            public bool IsComplete
            {
                get { return _complete; }
            }

            public bool IsTrusted
            {
                get { return _complete; }
            }

            public string Failure
            {
                get { return Refuse; }
            }

            public byte[] LocalKey
            {
                get { return new byte[16]; }
            }

            public byte[] LocalSalt
            {
                get { return new byte[14]; }
            }

            public byte[] RemoteKey
            {
                get { return new byte[16]; }
            }

            public byte[] RemoteSalt
            {
                get { return new byte[14]; }
            }
        }
    }
}
