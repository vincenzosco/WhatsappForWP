using System;
using System.Collections.Generic;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The order a call plays its voice in.
    ///
    /// Why it is needed: RTP does not promise that packets arrive in order, and a phone
    /// call crosses the internet - one packet overtaking another is ordinary, and playing
    /// them as they land is a sentence with its syllables swapped. This holds a few
    /// packets back so the one that is late has somewhere to go, releases them in sequence
    /// order, and counts what it had to throw away.
    ///
    /// What it is not: a decoder and not a clock. It answers "which packet is next" and
    /// nothing about *when* - the playout that consumes it owns the twenty milliseconds,
    /// asks for the next packet, and decides when a gap has waited long enough to be
    /// called lost (`Skip`).
    ///
    /// Two things here are easy to get wrong and are the reason for the tests:
    ///
    /// - the sequence number is **sixteen bits**, so it wraps every 65536 packets (about
    ///   twenty-two minutes at sixty a second). Taken as an integer, packet 0 after 65535
    ///   reads as a jump *backwards*, and a naive buffer discards the whole rest of the
    ///   call. `Extend` reads it as the nearest value to what came before, which is what
    ///   makes the wrap disappear.
    /// - a packet that arrives **after** its slot has been played is not deliverable: it
    ///   belongs to audio the user already heard, and playing it now would replay it out
    ///   of place. It is dropped and counted, never buffered.
    /// </summary>
    public sealed class RtpJitterBuffer
    {
        /// <summary>
        /// Three packets: sixty milliseconds at one every twenty. One packet of slack
        /// would not cover a reorder, and ten would be a fifth of a second of delay added
        /// to every sentence - on a phone call that is the difference between talking and
        /// talking over each other.
        /// </summary>
        public const int DefaultPreroll = 3;

        private readonly int _preroll;
        private readonly List<RtpPacket> _packets = new List<RtpPacket>();
        private readonly List<long> _sequence = new List<long>();

        /// <summary>The highest sequence seen, as an extended (non-wrapping) count.</summary>
        private long _highest = -1;

        /// <summary>The sequence the playout wants next, as an extended count, or -1 before the first packet.</summary>
        private long _next = -1;

        /// <summary>Whether the pre-roll has been paid: it gates the first release and nothing after it.</summary>
        private bool _started;

        public RtpJitterBuffer()
            : this(DefaultPreroll)
        {
        }

        /// <summary>
        /// A buffer that waits for `preroll` packets before it releases the first one. One
        /// is a buffer that only reorders what it can (it never waits), which is what the
        /// tests use to check the ordering apart from the waiting.
        /// </summary>
        public RtpJitterBuffer(int preroll)
        {
            if (preroll < 1) throw new ProtocolException("a jitter buffer holds at least one packet");

            _preroll = preroll;
        }

        /// <summary>How many packets are held, waiting for their turn.</summary>
        public int Pending
        {
            get { return _packets.Count; }
        }

        /// <summary>How many packets the playout gave up on (`Skip`), as a whole.</summary>
        public int Lost { get; private set; }

        /// <summary>How many arrived too late or twice, and were thrown away.</summary>
        public int Dropped { get; private set; }

        /// <summary>
        /// Offers one packet. Answers whether it was kept.
        ///
        /// False means the packet is one the playout can never use - a duplicate, or one
        /// older than the slot the playout is waiting on - and the caller does not try
        /// again with it. A malformed packet is refused before this point, by
        /// `RtpPacket.FromBytes`, because that is where the bytes are.
        /// </summary>
        public bool Push(RtpPacket packet)
        {
            if (packet == null) return false;

            long sequence = Extend(packet.SequenceNumber);

            if (_highest < 0)
            {
                // The first packet decides where the call starts: there is nothing to be
                // late for yet, so whatever it is, it is the one the playout wants.
                _highest = sequence;
                _next = sequence;
            }
            else if (sequence > _highest)
            {
                _highest = sequence;
            }

            if (sequence < _next)
            {
                // Already played, or the playout moved past it while it was in flight.
                Dropped++;
                return false;
            }

            if (IndexOf(sequence) >= 0)
            {
                // A retransmission, or a relay that delivered it twice.
                Dropped++;
                return false;
            }

            int at = _sequence.Count;
            while (at > 0 && _sequence[at - 1] > sequence) at--;

            _sequence.Insert(at, sequence);
            _packets.Insert(at, packet);

            return true;
        }

        /// <summary>
        /// The next packet in order, or null when there is nothing to play yet: the pre-roll
        /// has not filled, or the packet the playout wants has not arrived (and the playout
        /// has not yet decided to call it lost).
        /// </summary>
        public RtpPacket Next()
        {
            if (_packets.Count == 0) return null;

            // The pre-roll is paid once, by the first frame. Applying it to every call of
            // this method instead would stop the buffer the moment it released anything:
            // with three held and one played, two are left, and two is "still filling".
            if (!_started)
            {
                // Each packet held here buys one more chance for a late one to arrive in
                // its place, and no later frame pays for it.
                if (_packets.Count < _preroll) return null;

                _started = true;
            }

            if (_sequence[0] != _next) return null;

            RtpPacket packet = _packets[0];
            _packets.RemoveAt(0);
            _sequence.RemoveAt(0);
            _next++;

            return packet;
        }

        /// <summary>
        /// The playout's decision that a gap has waited long enough: the packet at the head
        /// of the wait is called lost, and whatever is held behind it becomes next.
        ///
        /// Answers false when there is nothing to skip - no packet held, or the one the
        /// playout wants is already here (in which case `Take` is the call to make).
        /// </summary>
        public bool SkipGap()
        {
            if (_packets.Count == 0) return false;

            long head = _sequence[0];
            if (head <= _next) return false;

            Lost += (int)(head - _next);
            _next = head;

            return true;
        }

        /// <summary>The call is over, or its keys changed: everything held belongs to audio that will not play.</summary>
        public void Reset()
        {
            _packets.Clear();
            _sequence.Clear();
            _highest = -1;
            _next = -1;
            _started = false;
            Lost = 0;
            Dropped = 0;
        }

        /// <summary>Where a sequence found a home among the ones held, or -1.</summary>
        private int IndexOf(long sequence)
        {
            for (int i = 0; i < _sequence.Count; i++)
            {
                if (_sequence[i] == sequence) return i;
            }

            return -1;
        }

        /// <summary>
        /// A sixteen-bit sequence read as an extended one: the value nearest to the highest
        /// already seen, in either direction.
        ///
        /// This is what makes the wrap invisible. With 65535 last seen, an arriving 0 is
        /// 65536 and not zero - the next packet, not one from twenty-two minutes ago - and
        /// with 0 last seen, an arriving 65535 is -1, which is late and is dropped.
        /// </summary>
        private long Extend(int sequence)
        {
            if (_highest < 0) return sequence;

            long wrap = 65536;
            long baseValue = _highest - (_highest % wrap) + sequence;

            long best = baseValue;
            long bestDistance = Math.Abs(baseValue - _highest);

            long lower = baseValue - wrap;
            long lowerDistance = Math.Abs(lower - _highest);
            if (lowerDistance < bestDistance)
            {
                best = lower;
                bestDistance = lowerDistance;
            }

            long higher = baseValue + wrap;
            if (Math.Abs(higher - _highest) < bestDistance)
            {
                best = higher;
            }

            return best;
        }
    }
}
