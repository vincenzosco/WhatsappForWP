using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The Ogg/Opus writer, read back the way a player reads it.
    ///
    /// Why these five assertions and not a round trip through this client's own reader: the
    /// reader is written by the same hand and would agree with any mistake. What is pinned
    /// here is the format - the two headers in the first two pages, the granule at 48 kHz,
    /// Ogg's own CRC, the lacing of a packet of exactly 255 bytes, and the end-of-stream
    /// flag - because those are the five things a wrong muxer gets wrong while still
    /// producing something that looks like an Ogg file.
    /// </summary>
    public static class OggOpusTests
    {
        private const int Rate = 16000;
        private const int Frame = Rate / 50;      // 20 ms

        [Test]
        public static void TheFirstTwoPagesAreTheTwoHeaders()
        {
            byte[] file = Build(3, Frame);

            List<Page> pages = Read(file);
            Assert.AreEqual("OpusHead", Text(pages[0].Body, 0, 8));
            Assert.AreEqual("OpusTags", Text(pages[1].Body, 0, 8));
            Assert.AreEqual(0x02, pages[0].Flags & 0x02, "the first page is the stream's beginning");
            Assert.AreEqual(0L, pages[0].Granule, "and it holds no audio");
            Assert.AreEqual(0, pages[0].Sequence, "the first page is page zero");
            Assert.AreEqual(1, pages[1].Sequence);
        }

        [Test]
        public static void TheGranuleCountsAtFortyEightKilohertz()
        {
            // Three frames of 20 ms at 16 kHz. Opus counts in 48 kHz samples, so the page
            // says 3 x 960 and not 3 x 320: the shorter number plays the note at a third
            // of its length.
            byte[] file = Build(3, Frame);
            List<Page> pages = Read(file);

            Page audio = pages[pages.Count - 1];
            Assert.AreEqual(2880L, audio.Granule);
        }

        [Test]
        public static void TheCrcIsOggsOwnAndNotZlibs()
        {
            byte[] file = Build(2, Frame);

            foreach (Page page in Read(file))
            {
                // Zeroed, as the specification says the field is while it is computed.
                var copy = new byte[page.Length];
                System.Buffer.BlockCopy(file, page.Offset, copy, 0, page.Length);
                copy[22] = 0;
                copy[23] = 0;
                copy[24] = 0;
                copy[25] = 0;

                Assert.AreEqual(Crc(copy), page.Crc, "page " + page.Sequence + " carries Ogg's CRC");
            }
        }

        [Test]
        public static void APacketOfTwoHundredFiftyFiveBytesTakesTwoLacingValues()
        {
            // 255 means "this packet goes on", so a packet of exactly 255 bytes is 255 and
            // then 0. Written as one 255 it would swallow the next packet.
            var packets = new List<byte[]>();
            packets.Add(new byte[255]);
            packets.Add(new byte[1]);

            byte[] file = OggOpus.Build(packets, Rate, 1, 7);
            List<Page> pages = Read(file);

            Page audio = pages[pages.Count - 1];
            Assert.AreEqual(3, audio.Lacing.Length, "255 and 0 for the long packet, 1 for the short one");
            Assert.AreEqual((byte)255, audio.Lacing[0]);
            Assert.AreEqual((byte)0, audio.Lacing[1]);
            Assert.AreEqual((byte)1, audio.Lacing[2]);

            // An encoder would never emit a packet of exactly 255 bytes in one page with a
            // second one behind it, but the arithmetic has to be the arithmetic: 255 + 0 is
            // a 255-byte packet, and the one after it is the byte that follows.
            List<byte[]> split = audio.PayloadSplit(audio.Lacing);
            Assert.AreEqual(2, split.Count, "two packets, not one long one");
            Assert.AreEqual(255, split[0].Length);
            Assert.AreEqual(1, split[1].Length);
        }

        [Test]
        public static void TheLastPageSaysTheStreamIsOver()
        {
            byte[] file = Build(30, Frame);      // more than one page of frames
            List<Page> pages = Read(file);

            Assert.IsTrue(pages.Count > 3, "thirty frames make more than one audio page");
            Assert.AreEqual(0, pages[pages.Count - 1].Flags & 0x02, "only the first page begins a stream");
            Assert.IsTrue((pages[pages.Count - 1].Flags & 0x04) != 0, "the last page ends it");
        }

        [Test]
        public static void TheHeadCarriesTheRateTheChannelsAndThePreSkip()
        {
            byte[] head = OggOpus.Head(16000, 1);

            Assert.AreEqual(19, head.Length);
            Assert.AreEqual(1, (int)head[8], "version 1");
            Assert.AreEqual(1, (int)head[9], "mono");
            Assert.AreEqual((long)OggOpus.DefaultPreSkip, Little(head, 10, 2));
            Assert.AreEqual(16000L, Little(head, 12, 4), "the rate the note was recorded at");
            Assert.AreEqual(0L, Little(head, 16, 2), "no output gain");
            Assert.AreEqual(0, (int)head[18], "one channel, one stream: mapping family 0");
        }

        [Test]
        public static void AnEmptyNoteIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate
            {
                OggOpus.Build(new List<byte[]>(), Rate, 1, 1);
            });
        }

        private static byte[] Build(int frames, int samplesPerFrame)
        {
            var packets = new List<byte[]>();

            for (int i = 0; i < frames; i++)
            {
                var packet = new byte[40];
                for (int b = 0; b < packet.Length; b++) packet[b] = (byte)(i + b);
                packets.Add(packet);
            }

            return OggOpus.Build(packets, Rate, 1, 4242);
        }

        /// <summary>One page, as the reader found it.</summary>
        private sealed class Page
        {
            public int Offset;
            public int Length;
            public int Flags;
            public long Granule;
            public int Sequence;
            public uint Crc;
            public byte[] Lacing;
            public byte[] Body;

            /// <summary>The bytes of each packet the page carries, from the lacing table.</summary>
            public List<byte[]> PayloadSplit(byte[] lacing)
            {
                var packets = new List<byte[]>();
                int at = 0;

                for (int i = 0; i < lacing.Length; i++)
                {
                    int length = lacing[i];
                    int start = at;

                    while (lacing[i] == 255 && i + 1 < lacing.Length)
                    {
                        i++;
                        length += lacing[i];
                    }

                    var packet = new byte[length];
                    System.Buffer.BlockCopy(Body, start, packet, 0, length);
                    packets.Add(packet);
                    at += length;
                }

                return packets;
            }
        }

        /// <summary>The pages of a file, in order, or a failure naming the page that did not read.</summary>
        private static List<Page> Read(byte[] file)
        {
            var pages = new List<Page>();
            int at = 0;

            while (at < file.Length)
            {
                Assert.AreEqual("OggS", Text(file, at, 4), "a page begins with its magic");

                int segments = file[at + 26];
                int header = 27 + segments;
                int payload = 0;
                for (int i = 0; i < segments; i++) payload += file[at + 27 + i];

                var page = new Page();
                page.Offset = at;
                page.Length = header + payload;
                page.Flags = file[at + 5];
                page.Granule = Little(file, at + 6, 8);
                page.Sequence = (int)Little(file, at + 18, 4);
                page.Crc = (uint)Little(file, at + 22, 4);
                page.Lacing = new byte[segments];
                System.Buffer.BlockCopy(file, at + 27, page.Lacing, 0, segments);
                page.Body = new byte[payload];
                System.Buffer.BlockCopy(file, at + header, page.Body, 0, payload);

                pages.Add(page);
                at += page.Length;
            }

            return pages;
        }

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

        private static string Text(byte[] bytes, int at, int length)
        {
            return System.Text.Encoding.UTF8.GetString(bytes, at, length);
        }

        private static long Little(byte[] bytes, int at, int length)
        {
            long value = 0;
            for (int i = length - 1; i >= 0; i--) value = (value << 8) | bytes[at + i];

            return value;
        }
    }
}
