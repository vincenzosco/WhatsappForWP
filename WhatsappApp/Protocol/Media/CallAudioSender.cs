using System;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The outgoing half of a call's audio on its twenty-millisecond clock: PCM from the
    /// microphone becomes one Opus packet per frame, and a packet the path refuses is dropped
    /// rather than queued.
    ///
    /// `CallMediaSession` seals a packet and `CallMediaSocket.SendAudio` refuses one whose
    /// handshake is not trusted; what a call was still missing is the clock between them, and
    /// the clock is also a framer. A stream does not arrive in frames - `MediaCapture` hands
    /// over whatever the buffer held - so a thousand bytes can arrive where 1280 are wanted,
    /// and the two halves of one sixteen-bit sample can be split across two calls. The carry
    /// here is what turns that back into frames of exactly `SamplesPerFrame` samples, which is
    /// the count the RTP timestamp advances by.
    ///
    /// Muted is not stopped: a muted call still sends frames, made of zero samples, because a
    /// peer that stops receiving audio decides the call has gone away.
    /// </summary>
    public sealed class CallAudioSender
    {
        /// <summary>The rate this phone's recorder and its encoder produce.</summary>
        public const int SampleRate = CallMediaSession.SampleRate;

        /// <summary>One frame, as WhatsApp's own calls write it.</summary>
        public const int FrameMilliseconds = CallMediaSession.FrameMilliseconds;

        /// <summary>320 samples at 16 kHz: what one packet carries.</summary>
        public const int SamplesPerFrame = CallMediaSession.SamplesPerFrame;

        /// <summary>Sixteen-bit mono, so one frame is 640 bytes of PCM.</summary>
        public const int FrameBytes = SamplesPerFrame * 2;

        /// <summary>One frame of PCM, as the encoder takes it. Null means the encoder refused.</summary>
        public delegate byte[] Encode(short[] samples, int count);

        /// <summary>One Opus packet, offered to the path. False means the path did not take it.</summary>
        public delegate bool Send(byte[] opusFrame);

        private readonly Encode _encode;
        private readonly Send _send;

        private readonly short[] _samples = new short[SamplesPerFrame];
        private readonly short[] _zeros = new short[SamplesPerFrame];

        /// <summary>The bytes of a frame that has not arrived in full yet.</summary>
        private byte[] _carry = new byte[FrameBytes * 2];

        private int _held;

        public CallAudioSender(Encode encode, Send send)
        {
            if (encode == null) throw new ArgumentNullException("encode");
            if (send == null) throw new ArgumentNullException("send");

            _encode = encode;
            _send = send;
        }

        /// <summary>True while the microphone is muted, which still sends frames.</summary>
        public bool Muted { get; set; }

        /// <summary>How many frames reached the encoder, muted ones included.</summary>
        public int Frames { get; private set; }

        /// <summary>How many packets the path took.</summary>
        public int Sent { get; private set; }

        /// <summary>How many packets the path refused: a handshake that is not trusted, a socket that is gone.</summary>
        public int Refused { get; private set; }

        /// <summary>How many frames the encoder refused, so nothing was sent for them.</summary>
        public int Dropped { get; private set; }

        /// <summary>How many bytes of the next frame are already held.</summary>
        public int Held
        {
            get { return _held; }
        }

        /// <summary>
        /// PCM as it arrives, in whatever sizes it arrives in. Returns how many frames this
        /// call completed, each one encoded and offered to the path before it returns.
        /// </summary>
        public int Push(byte[] pcm, int length)
        {
            if (pcm == null || length <= 0) return 0;
            if (length > pcm.Length) length = pcm.Length;

            if (_held + length > _carry.Length) Grow(_held + length);

            Array.Copy(pcm, 0, _carry, _held, length);
            _held += length;

            int frames = 0;

            while (_held >= FrameBytes)
            {
                Emit();

                // The rest of the carry is the beginning of the next frame: 640 bytes went
                // out, and whatever came after them stays.
                Array.Copy(_carry, FrameBytes, _carry, 0, _held - FrameBytes);
                _held -= FrameBytes;

                frames++;
            }

            return frames;
        }

        /// <summary>The call ended or its keys changed: the half frame held is not this call's.</summary>
        public void Reset()
        {
            _held = 0;
            Array.Clear(_carry, 0, _carry.Length);
        }

        private void Grow(int wanted)
        {
            int size = _carry.Length;
            while (size < wanted) size *= 2;

            var bigger = new byte[size];
            Array.Copy(_carry, 0, bigger, 0, _held);
            _carry = bigger;
        }

        /// <summary>
        /// One frame out of the front of the carry, encoded and offered to the path. The sample
        /// pairing is the WAV one this phone records: sixteen bits, little endian, mono.
        /// </summary>
        private void Emit()
        {
            for (int i = 0; i < SamplesPerFrame; i++)
            {
                int at = i * 2;
                _samples[i] = (short)(_carry[at] | (_carry[at + 1] << 8));
            }

            Frames++;

            byte[] packet = _encode(Muted ? _zeros : _samples, SamplesPerFrame);

            if (packet == null || packet.Length == 0)
            {
                // A frame the encoder refused is a frame this call never sends: there is
                // nothing to retry with, and the next one is already being recorded.
                Dropped++;
                return;
            }

            if (_send(packet)) Sent++;
            else Refused++;
        }
    }
}
