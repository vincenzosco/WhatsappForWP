using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The audio of one call, as a value: frames out sealed in SRTP, packets in opened,
    /// decrypted and put in the order they will be played in.
    ///
    /// The pieces were written one at a time and each one was tested alone -
    /// `RtpPacket` (the header), `SrtpSession` (the encryption), `RtpJitterBuffer` (the order),
    /// `CallKeys` (the key material) - and a call needed the four wired together with the
    /// numbering and the counters that make them one thing. That wiring is small but it is
    /// not nothing: the sequence number and the timestamp have to move together, the
    /// timestamp moves at **48 kHz even for a 16 kHz encoder** (RFC 7587), a packet whose tag
    /// does not verify is one packet to drop, and a gap is only lost when the playout says so.
    ///
    /// Out of scope, deliberately:
    /// - **not a socket.** Nothing here knows an address. The transport that carries the
    /// bytes is the caller's, which is what lets this be tested on a desktop against
    /// itself and lets a relay be added later without touching this file.
    /// - **not the key agreement.** The two directions' master key and salt arrive as
    /// parameters. Where they come from - the reference expands the call's `enc_key` into
    /// 46 bytes and how those split is decided by its native engine - is documented in
    /// `CallKeys` and in the calls design document, and it is not invented here.
    /// - **not an encoder.** The payload is an Opus packet this class never looks inside.
    ///
    /// The clock: `Seal` advances the timestamp by the frame's own duration, so a caller that
    /// sends one packet per 20 ms frame does not have to know the 48 kHz arithmetic, and one
    /// that concatenates frames says so with the duration it actually used.
    /// </summary>
    public sealed class CallMediaSession
    {
        /// <summary>What this client's recorder and its Opus encoder produce.</summary>
        public const int SampleRate = 16000;

        /// <summary>One frame, as the recorder and WhatsApp's own calls write it.</summary>
        public const int FrameMilliseconds = 20;

        /// <summary>Samples per channel in one frame: 320 at 16 kHz.</summary>
        public const int SamplesPerFrame = SampleRate / 1000 * FrameMilliseconds;

        private readonly SrtpSession _out;
        private readonly SrtpSession _in;
        private readonly RtpJitterBuffer _jitter;
        private readonly uint _localSsrc;

        /// <summary>
        /// One thread at a time on the receiving half. The socket hands a packet in while the
        /// player asks for the next one, and the jitter buffer is an ordered list: two threads
        /// in it at once is one list read while another writes it, which on a call is a
        /// decoder handed a packet from a slot that moved. `Seal` is not under this lock - a
        /// call sends from one clock only.
        /// </summary>
        private readonly object _gate = new object();

        private int _sequence;
        private uint _timestamp;

        /// <summary>
        /// One call's audio. `sendKey`/`sendSalt`/`localSsrc` are this phone's direction;
        /// `receiveKey`/`receiveSalt`/`remoteSsrc` are the other side's, which the peer
        /// derived from the same secret - the two sessions are separate on purpose, because
        /// SRTP keys are per direction and a shared one would let a reflection read itself.
        /// </summary>
        public CallMediaSession(byte[] sendKey, byte[] sendSalt, uint localSsrc,
                                byte[] receiveKey, byte[] receiveSalt, uint remoteSsrc)
        {
            _out = new SrtpSession(sendKey, sendSalt, localSsrc);
            _in = new SrtpSession(receiveKey, receiveSalt, remoteSsrc);

            _jitter = new RtpJitterBuffer();
            _localSsrc = localSsrc;
            _sequence = 0;
            _timestamp = 0;
        }

        /// <summary>How many frames this phone has sent.</summary>
        public int Sent { get; private set; }

        /// <summary>How many packets arrived with a tag that verified.</summary>
        public int Received { get; private set; }

        /// <summary>
        /// How many packets were refused: a tag that did not verify, or bytes that are not an
        /// RTP packet. Both are one packet to drop on a call, and neither is an exception.
        /// </summary>
        public int Refused { get; private set; }

        /// <summary>How many arrived too late or twice and were thrown away (`RtpJitterBuffer`).</summary>
        public int Dropped
        {
            get { return _jitter.Dropped; }
        }

        /// <summary>How many the playout gave up waiting for.</summary>
        public int Lost
        {
            get { return _jitter.Lost; }
        }

        /// <summary>How many are held, waiting for their turn.</summary>
        public int Pending
        {
            get { return _jitter.Pending; }
        }

        /// <summary>The sequence number the next frame will carry, modulo 65536.</summary>
        public int NextSequence
        {
            get { return _sequence; }
        }

        /// <summary>The timestamp the next frame will carry, at the 48 kHz RTP clock.</summary>
        public uint NextTimestamp
        {
            get { return _timestamp; }
        }

        /// <summary>
        /// One frame, ready for the socket: the RTP packet under it, sealed in SRTP.
        ///
        /// The timestamp advances by the frame's duration **whether or not the frame carried
        /// that much audio**, which is the caller's promise: a payload that is not 20 ms of
        /// Opus turns the other end's playout clock into a lie, and nothing here can tell.
        /// </summary>
        public byte[] Seal(byte[] opusFrame)
        {
            return Seal(opusFrame, FrameMilliseconds);
        }

        /// <summary>
        /// One frame whose audio lasts `milliseconds`: a packet that concatenates Opus frames
        /// (the reference's own repacketizer does) advances the clock by all of them.
        /// </summary>
        public byte[] Seal(byte[] opusFrame, int milliseconds)
        {
            if (opusFrame == null) throw new ProtocolException("a call sends a frame");
            if (milliseconds <= 0) milliseconds = FrameMilliseconds;

            byte[] plain = RtpPacket.Opus(_sequence, _timestamp, _localSsrc, opusFrame).ToBytes();

            _sequence = (_sequence + 1) & 0xffff;
            _timestamp = _timestamp + (uint)(RtpPacket.OpusClockRate / 1000 * milliseconds);

            byte[] sealedPacket = _out.Seal(plain, plain.Length);
            Sent++;

            return sealedPacket;
        }

        /// <summary>
        /// One packet off the socket. True when it was accepted, false when it was refused or
        /// dropped - in both cases the playout is untouched and the caller keeps receiving.
        ///
        /// Accepted is not the same as playable: the packet waits in the jitter buffer until
        /// `Take` reaches it.
        /// </summary>
        public bool Receive(byte[] packet, int length)
        {
            if (packet == null) return false;

            byte[] plain = _in.Open(packet, length);

            RtpPacket rtp = plain == null ? null : RtpPacket.FromBytes(plain, plain.Length);

            if (rtp == null)
            {
                Refused++;
                return false;
            }

            lock (_gate)
            {
                Received++;
                return _jitter.Push(rtp);
            }
        }

        /// <summary>
        /// The next payload to decode and play, or null when it is not this packet's turn yet.
        /// The playout calls this on its own twenty-millisecond clock, not on arrival
        /// (`RtpJitterBuffer.Next`, named apart from LINQ's `Take`).
        /// </summary>
        public byte[] Take()
        {
            lock (_gate)
            {
                RtpPacket packet = _jitter.Next();
                return packet == null ? null : packet.Payload;
            }
        }

        /// <summary>
        /// The playout's decision that the packet it is waiting for has waited long enough:
        /// the gap is counted as lost and what is behind it becomes next.
        /// </summary>
        public bool SkipGap()
        {
            lock (_gate)
            {
                return _jitter.SkipGap();
            }
        }

        /// <summary>The call is over, or its keys changed: nothing held will be played.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                _jitter.Reset();
            }
        }
    }
}
