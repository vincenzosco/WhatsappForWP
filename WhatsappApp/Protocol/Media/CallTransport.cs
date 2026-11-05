using System;
using System.Collections.Generic;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// Where one call's media path is: waiting for the peer's first connectivity check, running
    /// the handshake that makes the keys, or carrying audio.
    /// </summary>
    public enum CallTransportState
    {
        /// <summary>Nothing has arrived yet. An ICE-lite agent sends no check of its own.</summary>
        Waiting = 0,

        /// <summary>A check nominated a pair and the handshake is running on it.</summary>
        Handshaking = 1,

        /// <summary>The handshake finished and the keys are in use: audio goes through `Media`.</summary>
        Ready = 2,

        /// <summary>The handshake failed. Nothing more will be sent or read.</summary>
        Failed = 3
    }

    /// <summary>
    /// The two shapes of a media handshake, as `CallTransport` needs them: a first datagram, one
    /// datagram in and the answer out, and the four pieces of key material at the end.
    ///
    /// Why an interface and not `DtlsClient` directly: which end starts the handshake is decided
    /// by the call's signalling, not by this file, and the two roles are two different state
    /// machines. `DtlsClientHandshake` is the client half that already exists; the server half is
    /// the piece still missing, and when it is written it arrives here as one more implementation
    /// instead of a rewrite of the transport.
    ///
    /// `Local` is the material this phone writes with, `Remote` the material it reads with,
    /// whichever end opened the handshake: the transport installs them without a role.
    /// </summary>
    public interface IMediaHandshake
    {
        /// <summary>The first datagram, or null when this side waits for the peer to speak.</summary>
        byte[] Start();

        /// <summary>One datagram in; the datagram to send back, or null.</summary>
        byte[] Receive(byte[] datagram);

        /// <summary>True when the handshake finished and the key material below is usable.</summary>
        bool IsComplete { get; }

        /// <summary>
        /// Whether the peer proved the identity the signalling named. A handshake can be
        /// complete and untrusted; the caller decides whether audio may go over it.
        /// </summary>
        bool IsTrusted { get; }

        /// <summary>Why the handshake stopped, or null.</summary>
        string Failure { get; }

        byte[] LocalKey { get; }

        byte[] LocalSalt { get; }

        byte[] RemoteKey { get; }

        byte[] RemoteSalt { get; }
    }

    /// <summary>
    /// `DtlsClient` as the transport's handshake: this phone opens it, and writes with the client
    /// key the exporter produced.
    /// </summary>
    public sealed class DtlsClientHandshake : IMediaHandshake
    {
        private readonly DtlsClient _client;

        /// <summary>
        /// `privateKey` is 32 bytes of X25519, `clientRandom` 32 bytes of the caller's
        /// randomness, and `expectedCertificateSha256` the fingerprint from the signalling (or
        /// null, which makes the result complete but not trusted).
        /// </summary>
        public DtlsClientHandshake(byte[] privateKey, byte[] clientRandom,
            byte[] expectedCertificateSha256)
        {
            _client = new DtlsClient(privateKey, clientRandom, expectedCertificateSha256);
        }

        public byte[] Start()
        {
            return _client.Start();
        }

        public byte[] Receive(byte[] datagram)
        {
            return _client.Receive(datagram);
        }

        public bool IsComplete
        {
            get { return _client.IsComplete; }
        }

        public bool IsTrusted
        {
            get { return _client.IsTrusted; }
        }

        public string Failure
        {
            get { return _client.Failure; }
        }

        /// <summary>The client writes with the client key, so it is this side's.</summary>
        public byte[] LocalKey
        {
            get { return _client.SrtpClientKey; }
        }

        public byte[] LocalSalt
        {
            get { return _client.SrtpClientSalt; }
        }

        public byte[] RemoteKey
        {
            get { return _client.SrtpServerKey; }
        }

        public byte[] RemoteSalt
        {
            get { return _client.SrtpServerSalt; }
        }
    }

    /// <summary>
    /// One call's media path as a value: datagrams in, datagrams out, and the audio once the
    /// handshake made the keys.
    ///
    /// Why this file exists: `IceLiteAgent`, `DtlsClient` and `CallMediaSession` were each
    /// written and tested alone, and a call needs the three driven by the same datagrams. The
    /// seam between them is a classification, not a protocol: a media port carries STUN until a
    /// pair is nominated, the handshake on that pair next, and RTP after it, and every one of
    /// the three is told apart by its first byte. Doing that in one place is what keeps the
    /// socket - which WP8.1 owns and this project cannot fake - a loop of four lines.
    ///
    /// What it is not: a socket, a timer, a retransmission schedule or a candidate gatherer.
    /// `Receive` is handed the datagram and the address it came from and returns the datagrams
    /// to send, so the whole path is exercised in memory; `Resend` is the handshake's datagram
    /// again, for a caller whose own timer decided the answer was lost (there is no clock here,
    /// and a class that read one could not be tested against a fixed script).
    ///
    /// After nomination nothing is read from any other address: an ICE-lite agent selects the
    /// pair the controlling side nominated, and a call that accepted media from elsewhere would
    /// let a third party speak into a call it is not on.
    /// </summary>
    public sealed class CallTransport
    {
        private readonly IceLiteAgent _ice;
        private readonly IMediaHandshake _handshake;
        private readonly uint _localSsrc;
        private readonly uint _remoteSsrc;

        private StunEndpoint _selected;
        private CallMediaSession _media;
        private byte[] _lastOutbound;
        private CallTransportState _state;
        private string _failure;

        /// <summary>
        /// `ice` is this call's ICE-lite agent, `handshake` the state machine that will make the
        /// keys, and the two SSRCs the ones the call's key material names.
        /// </summary>
        public CallTransport(IceLiteAgent ice, IMediaHandshake handshake,
            uint localSsrc, uint remoteSsrc)
        {
            if (ice == null) throw new ProtocolException("a call transport needs an ICE agent");
            if (handshake == null) throw new ProtocolException("a call transport needs a handshake");

            _ice = ice;
            _handshake = handshake;
            _localSsrc = localSsrc;
            _remoteSsrc = remoteSsrc;
            _state = CallTransportState.Waiting;
        }

        /// <summary>Where the path is, in the shape a page draws it.</summary>
        public CallTransportState State
        {
            get { return _state; }
        }

        /// <summary>
        /// The address the peer nominated, which is where everything now goes. Null until a check
        /// nominates a pair.
        /// </summary>
        public StunEndpoint Peer
        {
            get { return _selected; }
        }

        /// <summary>The audio, once `Ready`; null before that.</summary>
        public CallMediaSession Media
        {
            get { return _media; }
        }

        /// <summary>
        /// True when the handshake finished and the peer's identity verified against the
        /// fingerprint the signalling named. False on an untrusted path, and the caller says so
        /// rather than sending audio over a handshake that proved nothing.
        /// </summary>
        public bool IsTrusted
        {
            get { return _state == CallTransportState.Ready && _handshake.IsTrusted; }
        }

        /// <summary>Why the path stopped, or null.</summary>
        public string Failure
        {
            get { return _failure; }
        }

        /// <summary>Datagrams thrown away: unreadable, or after the handshake failed.</summary>
        public int Dropped { get; private set; }

        /// <summary>Datagrams from an address that is not the nominated pair.</summary>
        public int NotFromPeer { get; private set; }

        /// <summary>Media that arrived before the keys existed, which is one packet to lose.</summary>
        public int MediaBeforeReady { get; private set; }

        /// <summary>Media the SRTP session refused: one packet to drop, not an exception.</summary>
        public int MediaRefused { get; private set; }

        /// <summary>
        /// One datagram in from `from`, and the datagrams to send back in answer - the ICE
        /// response, the handshake's own flight, both, or none.
        ///
        /// A STUN datagram is always handed to the agent, because a check is how a pair is
        /// discovered in the first place; everything else waits for a nomination and then has to
        /// come from the nominated address.
        /// </summary>
        public IList<byte[]> Receive(byte[] datagram, StunEndpoint from)
        {
            var outbound = new List<byte[]>();

            if (datagram == null || datagram.Length == 0 || from == null)
            {
                Dropped++;
                return outbound;
            }

            if (_state == CallTransportState.Failed)
            {
                Dropped++;
                return outbound;
            }

            if (StunMessage.Looks(datagram, 0, datagram.Length))
            {
                byte[] answer = _ice.Handle(datagram, from);
                if (answer != null) outbound.Add(answer);

                if (_ice.Nominated && _state == CallTransportState.Waiting)
                {
                    BeginHandshake(outbound);
                }

                return outbound;
            }

            if (!_ice.Nominated)
            {
                // Nothing is nominated, so there is no path a handshake or a frame could belong
                // to. A lite agent cannot start one; it waits.
                Dropped++;
                return outbound;
            }

            if (_selected == null) _selected = _ice.Selected;

            if (!Same(_selected, from))
            {
                NotFromPeer++;
                return outbound;
            }

            if (datagram[0] >= Dtls.TypeChangeCipherSpec
                && datagram[0] <= Dtls.TypeApplicationData)
            {
                return Handshake(datagram, outbound);
            }

            if ((datagram[0] & 0xc0) == 0x80)
            {
                ReadMedia(datagram);
                return outbound;
            }

            Dropped++;
            return outbound;
        }

        /// <summary>
        /// One Opus frame, sealed and ready for the socket, or null when the path is not up yet.
        /// The 20 ms of the frame is what advances the RTP clock.
        /// </summary>
        public byte[] Send(byte[] opusFrame)
        {
            return Send(opusFrame, CallMediaSession.FrameMilliseconds);
        }

        /// <summary>One frame whose audio lasts `milliseconds` (a repacketized one).</summary>
        public byte[] Send(byte[] opusFrame, int milliseconds)
        {
            if (_state != CallTransportState.Ready || _media == null) return null;

            return _media.Seal(opusFrame, milliseconds);
        }

        /// <summary>
        /// The handshake's last datagram again, while it is still running: a datagram lost on the
        /// way to the peer is answered by the caller's own retransmission timer, and this class
        /// keeps no clock.
        /// </summary>
        public byte[] Resend()
        {
            if (_state != CallTransportState.Handshaking) return null;

            return _lastOutbound;
        }

        private void BeginHandshake(IList<byte[]> outbound)
        {
            _selected = _ice.Selected;

            byte[] first = _handshake.Start();

            if (first == null || first.Length == 0)
            {
                Fail("the handshake produced no first datagram");
                return;
            }

            _state = CallTransportState.Handshaking;
            _lastOutbound = first;
            outbound.Add(first);
        }

        private IList<byte[]> Handshake(byte[] datagram, IList<byte[]> outbound)
        {
            if (_state == CallTransportState.Ready)
            {
                // A second handshake on a live path is renegotiation, and this project does not
                // do it. Dropping the record leaves the call up, which is the safe direction.
                Dropped++;
                return outbound;
            }

            byte[] reply = _handshake.Receive(datagram);

            if (reply != null && reply.Length > 0)
            {
                _lastOutbound = reply;
                outbound.Add(reply);
            }

            string failure = _handshake.Failure;
            if (failure != null)
            {
                Fail(failure);
                return outbound;
            }

            if (_handshake.IsComplete) BecomeReady();

            return outbound;
        }

        private void BecomeReady()
        {
            byte[] localKey = _handshake.LocalKey;
            byte[] localSalt = _handshake.LocalSalt;
            byte[] remoteKey = _handshake.RemoteKey;
            byte[] remoteSalt = _handshake.RemoteSalt;

            if (localKey == null || localSalt == null || remoteKey == null || remoteSalt == null)
            {
                Fail("the handshake finished without key material");
                return;
            }

            _media = new CallMediaSession(localKey, localSalt, _localSsrc,
                remoteKey, remoteSalt, _remoteSsrc);
            _state = CallTransportState.Ready;
        }

        private void ReadMedia(byte[] datagram)
        {
            if (_state != CallTransportState.Ready || _media == null)
            {
                MediaBeforeReady++;
                return;
            }

            if (!_media.Receive(datagram, datagram.Length)) MediaRefused++;
        }

        private void Fail(string reason)
        {
            if (_failure == null) _failure = reason;
            _state = CallTransportState.Failed;
        }

        private static bool Same(StunEndpoint left, StunEndpoint right)
        {
            if (left == null || right == null) return false;
            if (left.Port != right.Port) return false;

            byte[] a = left.Address;
            byte[] b = right.Address;

            if (a.Length != b.Length) return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }

            return true;
        }
    }
}
