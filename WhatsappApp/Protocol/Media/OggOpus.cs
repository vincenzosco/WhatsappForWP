using System;
using System.Collections.Generic;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// Ogg/Opus: the container WhatsApp plays a voice note out of.
    ///
    /// Why it is written here and not by a library: the phone has no Ogg muxer either (the
    /// platform decodes Ogg, it does not write it), and the whole format this app needs is
    /// two header packets and a page header per group of frames - about a hundred lines,
    /// against a native container library that would have to be built for ARM too.
    ///
    /// The shape is the specification's, and the three things it is easy to get wrong are
    /// the three pinned by `ProtocolTests/OggOpusTests.cs`:
    ///
    /// - **The granule is counted at 48 kHz**, whatever the input rate was: Opus decodes to
    ///   48 kHz and the position in the container is in those samples. A note recorded at
    ///   16 kHz with a 16 kHz granule plays at a third of its length.
    /// - **The CRC is Ogg's own**, not zlib's: polynomial 0x04c11db7, no reflection, no
    ///   final xor, and the field itself counts as four zero bytes while it is computed.
    ///   A page with a zlib CRC is a page every player rejects.
    /// - **A packet of 255 bytes takes two lacing values** (255 and 0), because 255 means
    ///   "the packet goes on".
    /// </summary>
    public static class OggOpus
    {
        /// <summary>
        /// The pre-skip libopus' own tooling writes: 312 samples at 48 kHz, the encoder's
        /// own start-up delay, which the player must drop instead of playing.
        /// </summary>
        public const int DefaultPreSkip = 312;

        /// <summary>Opus decodes to 48 kHz, and every granule is counted in those samples.</summary>
        private const int OpusRate = 48000;

        /// <summary>
        /// How many frames share a page. Not a rule of the format (one page per frame is
        /// legal too), but 25 frames is half a second at 20 ms, which is the granularity a
        /// player seeks at - and it keeps the page count of a three-minute note sane.
        /// </summary>
        private const int PacketsPerPage = 25;

        private const string Vendor = "WhatsAppForWP";

        /// <summary>
        /// The whole file: the two header packets, then the frames.
        /// </summary>
        /// <param name="packets">The Opus packets, in order, as the encoder produced them.</param>
        /// <param name="inputSampleRate">The rate the PCM was recorded at (8000..48000).</param>
        /// <param name="channels">1 or 2.</param>
        /// <param name="serial">The stream's serial number: any 32 bits, the same for every page of one file.</param>
        public static byte[] Build(IList<byte[]> packets, int inputSampleRate, int channels, int serial)
        {
            if (packets == null || packets.Count == 0)
                throw new ProtocolException("a voice note needs at least one Opus packet");
            if (inputSampleRate <= 0) throw new ProtocolException("a voice note needs the rate it was recorded at");
            if (channels != 1 && channels != 2) throw new ProtocolException("a voice note has one or two channels, not " + channels);

            var pages = new List<byte>(packets.Count * 80);

            // The two header packets are pages of their own, and the first one is the
            // stream's beginning: a player looks for `OpusHead` there and nowhere else.
            Page(pages, 0x02, 0, serial, 0, new[] { Head(inputSampleRate, channels) });
            Page(pages, 0x00, 0, serial, 1, new[] { Tags() });

            // Every packet is 20 ms, so the granule of a page is the number of frames
            // before it, converted to 48 kHz samples.
            long samples = 0;
            long perPacket = (long)inputSampleRate / 50 * OpusRate / inputSampleRate;
            int sequence = 2;

            for (int start = 0; start < packets.Count; start += PacketsPerPage)
            {
                int count = Math.Min(PacketsPerPage, packets.Count - start);
                var group = new byte[count][];
                for (int i = 0; i < count; i++) group[i] = packets[start + i];

                samples += perPacket * count;
                bool last = start + count >= packets.Count;

                Page(pages, last ? 0x04 : 0x00, samples, serial, sequence, group);
                sequence++;
            }

            return pages.ToArray();
        }

        /// <summary>`OpusHead`: the identification header, 19 bytes for one or two channels.</summary>
        public static byte[] Head(int inputSampleRate, int channels)
        {
            var head = new byte[19];
            Text(head, 0, "OpusHead");
            head[8] = 1;                                  // version
            head[9] = (byte)channels;
            Put(head, 10, (ulong)DefaultPreSkip, 2);      // pre-skip, little endian
            Put(head, 12, (ulong)inputSampleRate, 4);     // the rate the source was recorded at
            Put(head, 16, 0, 2);                          // output gain, dB in Q7.8
            head[18] = 0;                                 // channel mapping family

            return head;
        }

        /// <summary>`OpusTags`, with the vendor string and no comments.</summary>
        public static byte[] Tags()
        {
            byte[] vendor = System.Text.Encoding.UTF8.GetBytes(Vendor);
            var tags = new byte[8 + 4 + vendor.Length + 4];

            Text(tags, 0, "OpusTags");
            Put(tags, 8, (ulong)vendor.Length, 4);
            Buffer.BlockCopy(vendor, 0, tags, 12, vendor.Length);
            Put(tags, 12 + vendor.Length, 0, 4);          // no user comments

            return tags;
        }

        /// <summary>
        /// One page: the header, the lacing table, the packets, and the CRC over all of it
        /// with its own field counted as zero.
        /// </summary>
        private static void Page(List<byte> pages, int flags, long granule, int serial, int sequence,
            IList<byte[]> packets)
        {
            var lacing = new List<byte>(packets.Count);

            for (int i = 0; i < packets.Count; i++)
            {
                int length = packets[i].Length;

                // 255 is "this packet continues", so a packet of exactly 255 bytes carries a
                // 255 and then a 0 - and a packet of 256 carries 255, 1.
                while (length >= 255)
                {
                    lacing.Add(255);
                    length -= 255;
                }

                lacing.Add((byte)length);
            }

            if (lacing.Count > 255)
                throw new ProtocolException("too many packets in one Ogg page: " + lacing.Count + " lacing values");

            int headerLength = 27 + lacing.Count;
            int payloadLength = 0;
            for (int i = 0; i < packets.Count; i++) payloadLength += packets[i].Length;

            var page = new byte[headerLength + payloadLength];

            Text(page, 0, "OggS");
            page[4] = 0;                                   // stream structure version
            page[5] = (byte)flags;
            Put(page, 6, (ulong)granule, 8);               // -1 means "no packet finishes on this page"
            Put(page, 14, (ulong)(uint)serial, 4);
            Put(page, 18, (ulong)(uint)sequence, 4);
            Put(page, 22, 0, 4);                           // the CRC, filled in below
            page[26] = (byte)lacing.Count;

            for (int i = 0; i < lacing.Count; i++) page[27 + i] = lacing[i];

            int at = headerLength;
            for (int i = 0; i < packets.Count; i++)
            {
                Buffer.BlockCopy(packets[i], 0, page, at, packets[i].Length);
                at += packets[i].Length;
            }

            Put(page, 22, (ulong)Crc(page), 4);

            pages.AddRange(page);
        }

        /// <summary>Ogg's CRC-32: polynomial 0x04c11db7, high bit first, no final inversion.</summary>
        private static uint Crc(byte[] page)
        {
            uint crc = 0;

            for (int i = 0; i < page.Length; i++)
            {
                crc ^= (uint)page[i] << 24;

                for (int bit = 0; bit < 8; bit++)
                {
                    bool carry = (crc & 0x80000000u) != 0;
                    crc <<= 1;
                    if (carry) crc ^= 0x04c11db7u;
                }
            }

            return crc;
        }

        private static void Text(byte[] into, int at, string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Buffer.BlockCopy(bytes, 0, into, at, bytes.Length);
        }

        /// <summary>
        /// A little-endian integer of one, two, four or eight bytes.
        ///
        /// A `ulong` and not a `uint`: a shift of a 32-bit value by 32 is a shift by zero,
        /// so an eight-byte field written from a `uint` writes its low half twice - which is
        /// a granule of 2880 arriving as 12369505815360 and a note that claims to be four
        /// million seconds long.
        /// </summary>
        private static void Put(byte[] into, int at, ulong value, int bytes)
        {
            for (int i = 0; i < bytes; i++) into[at + i] = (byte)((value >> (8 * i)) & 0xff);
        }
    }
}
