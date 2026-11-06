using System;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The hearing half of a call on its twenty-millisecond clock: one frame of PCM per tick,
    /// taken from `CallMediaSession`, decoded, and filled with silence where the packet has not
    /// arrived.
    ///
    /// `CallMediaSession.Take` answers only when it is the next packet's turn, and it answers
    /// nothing when that packet is late - which is the normal shape of a call over the
    /// internet. A player cannot be handed "nothing": it asks for a second of audio at a time
    /// and a stream that stops producing is a stream that ends. So every tick answers exactly
    /// `FrameBytes` bytes, and what it carries when there is no packet is decided here: three
    /// ticks of silence while the packet is given a chance to arrive, then the gap is called
    /// lost (`SkipGap`) and the audio behind it plays.
    ///
    /// The rate is the phone's own: 16 kHz mono, sixteen bits, the same PCM the microphone
    /// produces and the encoder takes, so a frame that came back from the peer goes out to the
    /// speaker without a conversion anywhere.
    ///
    /// Not a player and not a decoder: `MediaStreamSource` owns the speaker, `CallOpusDecoder`
    /// owns libopus, and this class owns only the decision of what one tick carries.
    /// </summary>
    public sealed class CallAudioPlayout
    {
        /// <summary>One frame, as the recorder and WhatsApp's own calls write it.</summary>
        public const int FrameMilliseconds = CallMediaSession.FrameMilliseconds;

        /// <summary>320 samples at 16 kHz: what one tick carries.</summary>
        public const int SamplesPerFrame = CallMediaSession.SamplesPerFrame;

        /// <summary>Sixteen-bit mono, so one tick carries 640 bytes.</summary>
        public const int FrameBytes = SamplesPerFrame * 2;

        /// <summary>
        /// How many ticks a missing packet is waited for before the gap is called lost: three
        /// is sixty milliseconds, which is the jitter buffer's own pre-roll and short enough
        /// that a sentence does not turn into a pause.
        /// </summary>
        public const int MissesBeforeGap = 3;

        /// <summary>One Opus packet decoded to the PCM of one frame, or null when it was refused.</summary>
        public delegate byte[] DecodeFrame(byte[] opusFrame);

        /// <summary>The next packet that is due, or null when it has not arrived yet.</summary>
        public delegate byte[] TakeFrame();

        /// <summary>The playout's decision that a gap has waited long enough. True when it moved.</summary>
        public delegate bool SkipGap();

        private readonly DecodeFrame _decode;
        private readonly TakeFrame _take;
        private readonly SkipGap _skipGap;

        /// <summary>How many ticks in a row have found nothing to play.</summary>
        private int _misses;

        /// <summary>
        /// A decoder and the two questions `CallMediaSession` already answers, wired so that one
        /// tick is one frame.
        /// </summary>
        public CallAudioPlayout(DecodeFrame decode, TakeFrame take, SkipGap skipGap)
        {
            if (decode == null) throw new ProtocolException("a playout needs a decoder");
            if (take == null) throw new ProtocolException("a playout needs a source of frames");
            if (skipGap == null) throw new ProtocolException("a playout needs to say when a gap is lost");

            _decode = decode;
            _take = take;
            _skipGap = skipGap;
        }

        /// <summary>How many frames went out to the speaker, silence included.</summary>
        public int Played { get; private set; }

        /// <summary>How many ticks carried silence while the next packet was given its chance.</summary>
        public int Waited { get; private set; }

        /// <summary>How many gaps the playout gave up on.</summary>
        public int Skipped { get; private set; }

        /// <summary>How many packets the decoder refused, so the tick carried silence instead.</summary>
        public int Undecodable { get; private set; }

        /// <summary>How many frames were shorter than twenty milliseconds and were padded.</summary>
        public int Padded { get; private set; }

        /// <summary>How many frames were longer than one tick and were cut.</summary>
        public int Truncated { get; private set; }

        /// <summary>How many ticks in a row have found nothing: the jitter buffer's depth, read from here.</summary>
        public int Misses
        {
            get { return _misses; }
        }

        /// <summary>
        /// The frame this tick plays, always exactly `FrameBytes` of PCM. Silence is a real
        /// frame: a source that answered nothing would be a stream the player ends, and the
        /// call would go silent for good after one lost packet.
        /// </summary>
        public byte[] Tick()
        {
            byte[] packet = _take();

            if (packet == null)
            {
                _misses++;

                // One lost packet is not a lost sentence: the frame it should have carried is
                // played as silence while the packet behind it is still in flight.
                if (_misses > MissesBeforeGap)
                {
                    _misses = 0;

                    if (_skipGap())
                    {
                        Skipped++;
                        packet = _take();
                    }
                }

                if (packet == null)
                {
                    Played++;
                    Waited++;

                    return new byte[FrameBytes];
                }
            }
            else
            {
                _misses = 0;
            }

            Played++;

            byte[] pcm = null;

            try
            {
                pcm = _decode(packet);
            }
            catch (Exception)
            {
                // A decoder that throws is one packet: the clock of a call does not stop for it.
                pcm = null;
            }

            if (pcm == null || pcm.Length == 0)
            {
                Undecodable++;

                return new byte[FrameBytes];
            }

            if (pcm.Length == FrameBytes) return pcm;

            var frame = new byte[FrameBytes];

            if (pcm.Length < FrameBytes)
            {
                // A packet that carries less than the twenty milliseconds the clock promised:
                // the rest of the tick is silence, and the next tick starts on time.
                Padded++;
                Array.Copy(pcm, frame, pcm.Length);
            }
            else
            {
                Truncated++;
                Array.Copy(pcm, frame, FrameBytes);
            }

            return frame;
        }

        /// <summary>The call is over, or its keys changed: the next call starts on a clean clock.</summary>
        public void Reset()
        {
            _misses = 0;

            Played = 0;
            Waited = 0;
            Skipped = 0;
            Undecodable = 0;
            Padded = 0;
            Truncated = 0;
        }
    }
}
