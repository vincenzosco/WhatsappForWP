using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Crypto;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// One whole call's media path, end to end: the peer's check nominates a pair, a real DTLS
    /// handshake runs between this project's client and its server, and an Opus frame sealed on
    /// one end is read back on the other.
    ///
    /// Why this test and not three more of the layers alone: each layer is pinned on its own
    /// (`IceLiteTests`, `DtlsServerTests`, `CallMediaTests`), and the thing that a call needs is
    /// that the three fit - the ICE answer and the handshake flight come back from the same
    /// `Receive`, the keys the handshake exports are the ones the audio uses, and the SSRC the
    /// transport handed to `CallMediaSession` is the one the other end expects. A seam between
    /// two layers that each pass their own tests is exactly where a call goes silent.
    ///
    /// What it still is not: a socket. The datagrams are passed from hand to hand, so this
    /// proves the bytes and the state machine, not the phone's `DatagramSocket` or the network.
    /// </summary>
    public static class CallEndToEndTests
    {
        private const string LocalUfrag = "Lc0f";

        private const string LocalPassword = "local-password";

        private const string RemoteUfrag = "Rv7x";

        private const string RemotePassword = "remote-password";

        private static readonly StunEndpoint Peer = StunEndpoint.Parse("192.0.2.5", 50000);

        private static readonly byte[] ClientEcdheKey = Fill(0x21);

        private static readonly byte[] ServerEcdheKey = Fill(0x15);

        private static readonly byte[] ClientRandom = Fill(0x31);

        private static readonly byte[] ServerRandom = Fill(0x47);

        private static readonly byte[] CertificateScalar = Fill(0x41);

        /// <summary>The SSRC this side sends with, which is the peer's remote one.</summary>
        private const uint ServerSsrc = 0x11223344u;

        /// <summary>And the one the peer sends with, which this side reads.</summary>
        private const uint ClientSsrc = 0x55667788u;

        [Test]
        public static void ACallNominatesItsPairHandshakesAndCarriesAFrameBothWays()
        {
            var identity = new DtlsIdentity(CertificateScalar, "waopus");
            var server = new DtlsServer(identity, ServerEcdheKey, ServerRandom);

            var transport = new CallTransport(
                new IceLiteAgent(LocalUfrag, LocalPassword, RemoteUfrag, RemotePassword),
                new DtlsServerHandshake(server),
                ServerSsrc,
                ClientSsrc);

            var client = new DtlsClient(ClientEcdheKey, ClientRandom,
                Sha256.Hash(identity.Certificate));

            // 1. ICE. The peer's check carries USE-CANDIDATE, so the pair it came from is the
            //    pair, and the handshake starts on it.
            IList<byte[]> outbound = transport.Receive(Check(), Peer);

            Assert.AreEqual(1, outbound.Count,
                "a DTLS server waits for the hello, so only the check's answer goes back");
            Assert.IsTrue(StunMessage.Looks(outbound[0], 0, outbound[0].Length),
                "and it is a STUN answer, not a handshake flight");
            Assert.AreEqual(CallTransportState.Handshaking, transport.State,
                "the nomination opens the handshake");
            Assert.AreEqual(Peer.Text, transport.Peer.Text, "on the address that asked");

            // 2. DTLS, one datagram per hop, the way a socket would carry it.
            byte[] toServer = client.Start();

            for (int i = 0; i < 6 && transport.State != CallTransportState.Ready; i++)
            {
                IList<byte[]> fromServer = transport.Receive(toServer, Peer);

                Assert.IsTrue(fromServer.Count > 0, "the server answers every flight of the client");

                toServer = client.Receive(fromServer[0]);
                if (toServer == null) break;
            }

            Assert.AreEqual(CallTransportState.Ready, transport.State, "the handshake finished");
            Assert.IsTrue(transport.Media != null, "the keys exist, so the audio does");
            Assert.IsTrue(transport.IsTrusted, "the client's Finished proved the peer");
            Assert.IsTrue(client.IsComplete, "and the client verified the server's");
            Assert.IsTrue(client.ServerFinishedVerified);
            Assert.IsTrue(client.IsTrusted, "against the fingerprint it was told to expect");

            // The peer's audio, with the keys the client end of the same handshake exported.
            var peer = new CallMediaSession(client.SrtpClientKey, client.SrtpClientSalt, ClientSsrc,
                client.SrtpServerKey, client.SrtpServerSalt, ServerSsrc);

            // 3. Frames out: sealed by the transport, opened on the other end, and played in the
            //    order they were sent. Three, because the playout holds three before it releases
            //    the first (`RtpJitterBuffer.DefaultPreroll`) - one frame alone would sit in the
            //    pre-roll and prove that it arrived, not that the call plays.
            byte[][] frames = new byte[3][];
            frames[0] = OpusFrame(0x5a);
            frames[1] = OpusFrame(0x11);
            frames[2] = OpusFrame(0x22);

            for (int i = 0; i < frames.Length; i++)
            {
                byte[] sealedPacket = transport.Send(frames[i]);

                Assert.IsTrue(sealedPacket != null, "a call that is up sends");
                Assert.AreEqual(0x80, sealedPacket[0] & 0xc0, "what goes out is RTP under SRTP");
                Assert.IsTrue(peer.Receive(sealedPacket, sealedPacket.Length),
                    "the tag verifies under the client's key");
            }

            Assert.AreEqual(3, peer.Received, "every frame was opened");

            for (int i = 0; i < frames.Length; i++)
            {
                Assert.ByteArraysEqual(frames[i], peer.Take(),
                    "the frame comes back byte for byte, in the order it was sent");
            }

            // 4. And back the other way: frames the peer seals are read by the transport. The
            //    two directions have separate keys and separate SSRCs, so one that works while
            //    the other does not is the failure this half catches.
            byte[][] replies = new byte[3][];
            replies[0] = OpusFrame(0xa5);
            replies[1] = OpusFrame(0x33);
            replies[2] = OpusFrame(0x44);

            for (int i = 0; i < replies.Length; i++)
            {
                IList<byte[]> answers = transport.Receive(peer.Seal(replies[i]), Peer);

                Assert.AreEqual(0, answers.Count, "media is read, not answered");
            }

            Assert.AreEqual(3, transport.Media.Received, "the transport opened the peer's packets");

            for (int i = 0; i < replies.Length; i++)
            {
                Assert.ByteArraysEqual(replies[i], transport.Media.Take(),
                    "and the Opus payloads are the ones the peer sealed");
            }

            Assert.AreEqual(0, transport.MediaRefused, "nothing was refused");
        }

        /// <summary>
        /// The peer's connectivity check, authenticated with the password this side knows and
        /// carrying the nomination: what a controlling agent sends once it has picked a pair.
        /// </summary>
        private static byte[] Check()
        {
            var check = new StunMessage(StunMethod.Binding, StunClass.Request,
                Hex("0a0b0c0d0e0f101112131415"));

            check.AddText(StunAttributeType.UserName, RemoteUfrag + ":" + LocalUfrag);
            check.AddUInt32(StunAttributeType.Priority, 0x7e0000ff);
            check.Add(StunAttribute.UInt64(StunAttributeType.IceControlling, 0x4d3c2b1a5e6f7081ul));
            check.Add(StunAttribute.Flag(StunAttributeType.UseCandidate));

            return check.Seal(Utf8(RemotePassword));
        }

        /// <summary>An Opus payload: the session never looks inside one, so its bytes are a marker.</summary>
        private static byte[] OpusFrame(int seed)
        {
            var frame = new byte[40];
            for (int i = 0; i < frame.Length; i++) frame[i] = (byte)((seed + i * 3) & 0xff);
            return frame;
        }

        private static byte[] Fill(int value)
        {
            var bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((value + i) & 0xff);
            return bytes;
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
    }
}
