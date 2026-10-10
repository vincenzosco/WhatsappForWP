using System;
using System.Collections.Generic;
using System.Text;

namespace WhatsappApp.Protocol.Qr
{
    /// <summary>How much of the symbol is given to error correction.</summary>
    public enum QrErrorCorrection
    {
        Low,
        Medium,
        Quartile,
        High
    }

    /// <summary>
    /// A QR encoder, written here because the pairing screen has to draw its own code.
    ///
    /// Until the server left, the QR arrived as a PNG that the adapter had made. Now the
    /// string comes out of `PairingQr` and the phone has to turn it into modules itself,
    /// with nothing to draw them with but the `WriteableBitmap` the rest of the app uses -
    /// so this class returns a matrix of booleans and knows nothing about pixels.
    ///
    /// It encodes **byte mode** at any of the four levels, versions 1..40. Byte mode is
    /// the only mode it needs: the payload is a URL with a mixed-case ref in it, and the
    /// reference itself picks byte mode for exactly that. An all-uppercase input, which
    /// the reference would shrink into alphanumeric mode, is encoded in byte mode here -
    /// a bigger symbol, and the same string on the other side of a scan.
    ///
    /// Correctness is pinned by `ProtocolTests/Vectors/QrFixture.cs`, which holds two
    /// symbols printed from a reference implementation and checked row for row, mask
    /// included. Which mask wins is a penalty score, and where two scores tie the
    /// reference's number is the one this code returns: a decoder does not care, a
    /// fixture does, and matching it is what makes the fixture an oracle.
    /// </summary>
    public static class QrCode
    {
        /// <summary>
        /// The blank margin a scanner needs around the symbol, in modules. The matrix this
        /// class returns has none: the drawer adds it, because only the drawer knows how
        /// many pixels a module is.
        /// </summary>
        public const int QuietZone = 4;

        /// <summary>The byte-mode indicator: `0100`.</summary>
        private const int ByteMode = 0x4;

        /// <summary>
        /// Encodes `text` and returns the modules: `[row, column]`, `true` is dark.
        ///
        /// The size follows the version the data needs, `17 + 4 * version` - 21 for the
        /// shortest input, 65 for the pairing payload.
        /// </summary>
        public static bool[,] Encode(string text, QrErrorCorrection level)
        {
            if (string.IsNullOrEmpty(text)) throw new ProtocolException("there is nothing to encode");

            byte[] data = Encoding.UTF8.GetBytes(text);
            int version = SmallestVersion(data.Length, level);
            int size = Size(version);

            bool[,] modules = new bool[size, size];
            bool[,] reserved = new bool[size, size];

            // The function patterns first: they are what the data is placed around, and
            // every one of them is reserved so no later step writes over it.
            FinderPatterns(modules, reserved);
            TimingPattern(modules, reserved);
            AlignmentPatterns(modules, reserved, version);

            // The format area is reserved now, with placeholder bits, because the data
            // placement has to skip it - the real bits are written once the mask is known.
            FormatInformation(modules, reserved, level, 0);

            if (version >= 7) VersionInformation(modules, reserved, version);

            PlaceData(modules, reserved, Codewords(data, version, level));

            int mask = BestMask(modules, reserved, level);
            ApplyMask(modules, reserved, mask);
            FormatInformation(modules, reserved, level, mask);

            return modules;
        }

        /// <summary>The side of a symbol of this version, in modules.</summary>
        public static int Size(int version)
        {
            if (version < 1 || version > QrTables.MaxVersion)
            {
                throw new ProtocolException("a QR version is 1.." + QrTables.MaxVersion + ", not " + version);
            }

            return version * 4 + 17;
        }

        /// <summary>
        /// The smallest version that holds this many bytes at this level. The ceiling is
        /// the last version, and an input past it is refused rather than encoded into a
        /// symbol that would drop the end of it.
        /// </summary>
        public static int SmallestVersion(int byteCount, QrErrorCorrection level)
        {
            for (int version = 1; version <= QrTables.MaxVersion; version++)
            {
                if (byteCount <= Capacity(version, level)) return version;
            }

            throw new ProtocolException(byteCount + " byte(s) do not fit in a QR symbol at " + level);
        }

        /// <summary>The most whole bytes a symbol of this version holds at this level.</summary>
        public static int Capacity(int version, QrErrorCorrection level)
        {
            int usableBits = DataBits(version, level) - 4 - CharacterCountBits(version);
            return usableBits / 8;
        }

        /// <summary>How many bits the character count takes: eight below version 10, sixteen from there.</summary>
        private static int CharacterCountBits(int version)
        {
            return version < 10 ? 8 : 16;
        }

        /// <summary>The data area of a symbol, in bits: everything that is not error correction.</summary>
        private static int DataBits(int version, QrErrorCorrection level)
        {
            int total = QrTables.TotalCodewords[version];
            int ec = QrTables.EcCodewordsPerVersion[version - 1, LevelIndex(level)];
            if (ec >= total) throw new ProtocolException("version " + version + " has no room for data");

            return (total - ec) * 8;
        }

        private static int DataCodewords(int version, QrErrorCorrection level)
        {
            return DataBits(version, level) / 8;
        }

        /// <summary>Low, Medium, Quartile, High is how the tables are laid out.</summary>
        private static int LevelIndex(QrErrorCorrection level)
        {
            if (level == QrErrorCorrection.Low) return 0;
            if (level == QrErrorCorrection.Medium) return 1;
            if (level == QrErrorCorrection.Quartile) return 2;
            return 3;
        }

        /// <summary>
        /// The two bits the format information carries for a level. They are not the
        /// enum's order: `L` is 1, `M` is 0, `Q` is 3 and `H` is 2, which is the
        /// specification's numbering and not anybody's convenience.
        /// </summary>
        private static int LevelBits(QrErrorCorrection level)
        {
            if (level == QrErrorCorrection.Low) return 1;
            if (level == QrErrorCorrection.Medium) return 0;
            if (level == QrErrorCorrection.Quartile) return 3;
            return 2;
        }

        // ---------------------------------------------------------------- the bits

        /// <summary>
        /// The codewords of one message: mode and length, the bytes, a terminator, and the
        /// pad bytes the standard prescribes.
        /// </summary>
        private static byte[] Data(BitBuffer buffer, int version, QrErrorCorrection level)
        {
            int capacityBits = DataBits(version, level);

            if (buffer.Length + 4 <= capacityBits) buffer.Put(0, 4);
            while (buffer.Length % 8 != 0) buffer.PutBit(false);

            int padding = (capacityBits - buffer.Length) / 8;
            for (int i = 0; i < padding; i++) buffer.Put(i % 2 == 0 ? 0xec : 0x11, 8);

            return buffer.ToBytes();
        }

        /// <summary>
        /// The data codewords with the error correction ones appended: the message is cut
        /// into the blocks the standard prescribes, each block gets its own Reed-Solomon
        /// parity, and the blocks are interleaved.
        ///
        /// The interleaving is not decoration. A decoder reads the codewords column by
        /// column; write the blocks one after another and a scratch across the symbol
        /// lands inside a single block, which is the one thing error correction cannot
        /// repair.
        /// </summary>
        private static byte[] Codewords(byte[] data, int version, QrErrorCorrection level)
        {
            var buffer = new BitBuffer();
            buffer.Put(ByteMode, 4);
            buffer.Put(data.Length, CharacterCountBits(version));

            for (int i = 0; i < data.Length; i++) buffer.Put(data[i], 8);

            byte[] dataCodewords = Data(buffer, version, level);

            int total = QrTables.TotalCodewords[version];
            int blockCount = QrTables.BlocksPerVersion[version - 1, LevelIndex(level)];
            int ecCodewords = QrTables.EcCodewordsPerVersion[version - 1, LevelIndex(level)];
            if (blockCount < 1 || ecCodewords % blockCount != 0)
            {
                throw new ProtocolException("version " + version + " has an inconsistent block table");
            }

            int ecPerBlock = ecCodewords / blockCount;
            int shortBlocks = blockCount - (total % blockCount);
            int shortBlockLength = total / blockCount;
            int shortDataLength = dataCodewords.Length / blockCount;

            var blocks = new byte[blockCount][];
            var parity = new byte[blockCount][];
            int offset = 0;
            int longestData = 0;

            for (int b = 0; b < blockCount; b++)
            {
                int length = b < shortBlocks ? shortDataLength : shortDataLength + 1;
                blocks[b] = new byte[length];
                Buffer.BlockCopy(dataCodewords, offset, blocks[b], 0, length);
                parity[b] = ReedSolomon.Parity(blocks[b], ecPerBlock);

                offset += length;
                if (length > longestData) longestData = length;
            }

            var interleaved = new byte[total];
            int index = 0;

            for (int i = 0; i < longestData; i++)
            {
                for (int b = 0; b < blockCount; b++)
                {
                    if (i < blocks[b].Length) interleaved[index++] = blocks[b][i];
                }
            }

            for (int i = 0; i < ecPerBlock; i++)
            {
                for (int b = 0; b < blockCount; b++) interleaved[index++] = parity[b][i];
            }

            if (index != total) throw new ProtocolException("the interleaving wrote " + index + " of " + total + " codewords");
            return interleaved;
        }

        // -------------------------------------------------------- the function patterns

        private static void FinderPatterns(bool[,] modules, bool[,] reserved)
        {
            int size = modules.GetLength(0);
            int[] rows = new int[] { 0, 0, size - 7 };
            int[] columns = new int[] { 0, size - 7, 0 };

            for (int i = 0; i < rows.Length; i++)
            {
                for (int r = -1; r <= 7; r++)
                {
                    int row = rows[i] + r;
                    if (row < 0 || row >= size) continue;

                    for (int c = -1; c <= 7; c++)
                    {
                        int column = columns[i] + c;
                        if (column < 0 || column >= size) continue;

                        bool dark =
                            (r >= 0 && r <= 6 && (c == 0 || c == 6)) ||
                            (c >= 0 && c <= 6 && (r == 0 || r == 6)) ||
                            (r >= 2 && r <= 4 && c >= 2 && c <= 4);

                        Set(modules, reserved, row, column, dark);
                    }
                }
            }
        }

        private static void TimingPattern(bool[,] modules, bool[,] reserved)
        {
            int size = modules.GetLength(0);

            for (int r = 8; r < size - 8; r++)
            {
                bool dark = r % 2 == 0;
                Set(modules, reserved, r, 6, dark);
                Set(modules, reserved, 6, r, dark);
            }
        }

        private static void AlignmentPatterns(bool[,] modules, bool[,] reserved, int version)
        {
            int[] positions = AlignmentCoordinates(version);
            int last = positions.Length - 1;
            int size = modules.GetLength(0);

            for (int i = 0; i < positions.Length; i++)
            {
                for (int j = 0; j < positions.Length; j++)
                {
                    // The three corners belong to the finder patterns.
                    if ((i == 0 && j == 0) || (i == 0 && j == last) || (i == last && j == 0)) continue;

                    for (int r = -2; r <= 2; r++)
                    {
                        for (int c = -2; c <= 2; c++)
                        {
                            bool dark = r == -2 || r == 2 || c == -2 || c == 2 || (r == 0 && c == 0);

                            int row = positions[i] + r;
                            int column = positions[j] + c;
                            if (row < 0 || row >= size || column < 0 || column >= size) continue;

                            Set(modules, reserved, row, column, dark);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Where the alignment patterns are centred, from the version: the last coordinate
        /// is always `size - 7`, the first is always 6, and the rest are evenly spaced.
        /// </summary>
        private static int[] AlignmentCoordinates(int version)
        {
            if (version == 1) return new int[0];

            int count = version / 7 + 2;
            int size = Size(version);
            int interval = size == 145
                ? 26
                : ((size - 13 + (2 * count - 2) - 1) / (2 * count - 2)) * 2;

            // Ascending, because that is the order the reference ends up with: it builds
            // them downwards and reverses. The first is 6, the last is size - 7, and the
            // ones between are one interval apart.
            var coordinates = new int[count];
            coordinates[count - 1] = size - 7;
            coordinates[0] = 6;

            for (int i = count - 2; i >= 1; i--)
            {
                coordinates[i] = coordinates[i + 1] - interval;
            }

            return coordinates;
        }

        private static void VersionInformation(bool[,] modules, bool[,] reserved, int version)
        {
            int size = modules.GetLength(0);
            int bits = VersionBits(version);

            for (int i = 0; i < 18; i++)
            {
                int row = i / 3;
                int column = (i % 3) + size - 11;
                bool dark = ((bits >> i) & 1) == 1;

                Set(modules, reserved, row, column, dark);
                Set(modules, reserved, column, row, dark);
            }
        }

        private static void FormatInformation(bool[,] modules, bool[,] reserved,
            QrErrorCorrection level, int mask)
        {
            int size = modules.GetLength(0);
            int bits = FormatBits(level, mask);

            for (int i = 0; i < 15; i++)
            {
                bool dark = ((bits >> i) & 1) == 1;

                // Down the left of the top-left finder, then along the bottom.
                if (i < 6) Set(modules, reserved, i, 8, dark);
                else if (i < 8) Set(modules, reserved, i + 1, 8, dark);
                else Set(modules, reserved, size - 15 + i, 8, dark);

                // And along the top, then down the right of the top-left finder.
                if (i < 8) Set(modules, reserved, 8, size - i - 1, dark);
                else if (i < 9) Set(modules, reserved, 8, 15 - i, dark);
                else Set(modules, reserved, 8, 15 - i - 1, dark);
            }

            // The one module that is always dark, and has no other job.
            Set(modules, reserved, size - 8, 8, true);
        }

        /// <summary>
        /// The five data bits - level and mask - with ten BCH check bits, the whole thing
        /// exclusive-ored with a fixed pattern so no combination can come out all zeros.
        /// </summary>
        private static int FormatBits(QrErrorCorrection level, int mask)
        {
            int data = (LevelBits(level) << 3) | mask;
            int remainder = data << 10;
            int generator = 0x537; // 10100110111

            while (BitLength(remainder) - BitLength(generator) >= 0)
            {
                remainder ^= generator << (BitLength(remainder) - BitLength(generator));
            }

            return ((data << 10) | remainder) ^ 0x5412;
        }

        /// <summary>Six version bits with twelve Golay check bits, for the symbols that carry them.</summary>
        private static int VersionBits(int version)
        {
            int remainder = version << 12;
            int generator = 0x1f25; // 1111100100101

            while (BitLength(remainder) - BitLength(generator) >= 0)
            {
                remainder ^= generator << (BitLength(remainder) - BitLength(generator));
            }

            return (version << 12) | remainder;
        }

        private static int BitLength(int value)
        {
            int length = 0;
            while (value != 0)
            {
                length++;
                value = (int)((uint)value >> 1);
            }

            return length;
        }

        // ------------------------------------------------------------- the data area

        /// <summary>
        /// Writes the codewords into everything the function patterns left free: two
        /// columns at a time, from the bottom right, going up then down, skipping the
        /// timing column.
        ///
        /// The order is the specification's and it is what a decoder reverses, so a
        /// "nicer" traversal here is a symbol nobody can read.
        /// </summary>
        private static void PlaceData(bool[,] modules, bool[,] reserved, byte[] codewords)
        {
            int size = modules.GetLength(0);
            int step = -1;
            int row = size - 1;
            int bit = 7;
            int index = 0;

            for (int column = size - 1; column > 0; column -= 2)
            {
                if (column == 6) column--;

                while (true)
                {
                    for (int c = 0; c < 2; c++)
                    {
                        int at = column - c;
                        if (reserved[row, at]) continue;

                        bool dark = false;
                        if (index < codewords.Length) dark = ((codewords[index] >> bit) & 1) == 1;

                        modules[row, at] = dark;

                        bit--;
                        if (bit == -1)
                        {
                            index++;
                            bit = 7;
                        }
                    }

                    row += step;
                    if (row < 0 || row >= size)
                    {
                        row -= step;
                        step = -step;
                        break;
                    }
                }
            }
        }

        // ------------------------------------------------------------------ the mask

        private static int BestMask(bool[,] modules, bool[,] reserved, QrErrorCorrection level)
        {
            int best = 0;
            int lowest = int.MaxValue;

            for (int pattern = 0; pattern < 8; pattern++)
            {
                // The penalty is scored on the finished symbol, format bits included, and
                // both are undone before the next pattern is tried.
                FormatInformation(modules, reserved, level, pattern);
                ApplyMask(modules, reserved, pattern);

                int penalty = Penalty1(modules) + Penalty2(modules) + Penalty3(modules) + Penalty4(modules);

                ApplyMask(modules, reserved, pattern);

                if (penalty < lowest)
                {
                    lowest = penalty;
                    best = pattern;
                }
            }

            return best;
        }

        private static void ApplyMask(bool[,] modules, bool[,] reserved, int pattern)
        {
            int size = modules.GetLength(0);

            for (int column = 0; column < size; column++)
            {
                for (int row = 0; row < size; row++)
                {
                    if (reserved[row, column]) continue;
                    if (Masked(pattern, row, column)) modules[row, column] = !modules[row, column];
                }
            }
        }

        private static bool Masked(int pattern, int row, int column)
        {
            switch (pattern)
            {
                case 0: return (row + column) % 2 == 0;
                case 1: return row % 2 == 0;
                case 2: return column % 3 == 0;
                case 3: return (row + column) % 3 == 0;
                case 4: return (row / 2 + column / 3) % 2 == 0;
                case 5: return (row * column) % 2 + (row * column) % 3 == 0;
                case 6: return ((row * column) % 2 + (row * column) % 3) % 2 == 0;
                case 7: return ((row * column) % 3 + (row + column) % 2) % 2 == 0;
                default: throw new ProtocolException("there is no mask " + pattern);
            }
        }

        /// <summary>Runs of five or more same-coloured modules, in both directions.</summary>
        private static int Penalty1(bool[,] modules)
        {
            int size = modules.GetLength(0);
            int points = 0;

            for (int row = 0; row < size; row++)
            {
                int runColumn = 0;
                int runRow = 0;
                bool? lastColumn = null;
                bool? lastRow = null;

                for (int column = 0; column < size; column++)
                {
                    bool across = modules[row, column];
                    if (across == lastColumn) runColumn++;
                    else
                    {
                        if (runColumn >= 5) points += 3 + (runColumn - 5);
                        lastColumn = across;
                        runColumn = 1;
                    }

                    bool down = modules[column, row];
                    if (down == lastRow) runRow++;
                    else
                    {
                        if (runRow >= 5) points += 3 + (runRow - 5);
                        lastRow = down;
                        runRow = 1;
                    }
                }

                if (runColumn >= 5) points += 3 + (runColumn - 5);
                if (runRow >= 5) points += 3 + (runRow - 5);
            }

            return points;
        }

        /// <summary>Two by two blocks of one colour: three points each.</summary>
        private static int Penalty2(bool[,] modules)
        {
            int size = modules.GetLength(0);
            int points = 0;

            for (int row = 0; row < size - 1; row++)
            {
                for (int column = 0; column < size - 1; column++)
                {
                    bool first = modules[row, column];
                    if (first == modules[row, column + 1]
                        && first == modules[row + 1, column]
                        && first == modules[row + 1, column + 1])
                    {
                        points++;
                    }
                }
            }

            return points * 3;
        }

        /// <summary>
        /// The finder-like `1 1 1 1 1 0 1 0 1 1 1` pattern anywhere, which a decoder would
        /// try to use as a finder: forty points each.
        /// </summary>
        private static int Penalty3(bool[,] modules)
        {
            int size = modules.GetLength(0);
            int points = 0;

            for (int row = 0; row < size; row++)
            {
                int across = 0;
                int down = 0;

                for (int column = 0; column < size; column++)
                {
                    across = ((across << 1) & 0x7ff) | (modules[row, column] ? 1 : 0);
                    if (column >= 10 && (across == 0x5d0 || across == 0x05d)) points++;

                    down = ((down << 1) & 0x7ff) | (modules[column, row] ? 1 : 0);
                    if (column >= 10 && (down == 0x5d0 || down == 0x05d)) points++;
                }
            }

            return points * 40;
        }

        /// <summary>How far the dark proportion is from half, in five-percent steps.</summary>
        private static int Penalty4(bool[,] modules)
        {
            int size = modules.GetLength(0);
            int dark = 0;

            for (int row = 0; row < size; row++)
            {
                for (int column = 0; column < size; column++)
                {
                    if (modules[row, column]) dark++;
                }
            }

            // The reference rounds the whole ratio up before it counts the steps; rounding
            // the percentage first and dividing by five after it scores a different step
            // on some symbols, which is a different mask, which is a different symbol.
            int ratio = (int)Math.Ceiling(dark * 100.0 / (size * size) / 5.0);
            int steps = ratio - 10;
            if (steps < 0) steps = -steps;

            return steps * 10;
        }

        private static void Set(bool[,] modules, bool[,] reserved, int row, int column, bool dark)
        {
            modules[row, column] = dark;
            reserved[row, column] = true;
        }

        /// <summary>
        /// Bits, accumulated in the order the standard puts them in - the order is the
        /// format, so this is a list rather than anything cleverer.
        /// </summary>
        private sealed class BitBuffer
        {
            private readonly List<bool> _bits = new List<bool>(512);

            public int Length
            {
                get { return _bits.Count; }
            }

            /// <summary>`count` bits of `value`, most significant first.</summary>
            public void Put(int value, int count)
            {
                for (int i = count - 1; i >= 0; i--) PutBit(((value >> i) & 1) == 1);
            }

            public void PutBit(bool bit)
            {
                _bits.Add(bit);
            }

            public byte[] ToBytes()
            {
                if (_bits.Count % 8 != 0) throw new ProtocolException("the bit stream is not whole bytes");

                var bytes = new byte[_bits.Count / 8];
                for (int i = 0; i < _bits.Count; i++)
                {
                    if (_bits[i]) bytes[i / 8] = (byte)(bytes[i / 8] | (1 << (7 - (i % 8))));
                }

                return bytes;
            }
        }
    }

    /// <summary>
    /// Reed-Solomon over GF(256) with the QR standard's polynomial `x^8 + x^4 + x^3 + x^2 + 1`
    /// (0x11d): what turns a block of data codewords into the parity a scanner needs to
    /// repair a scratch.
    /// </summary>
    internal static class ReedSolomon
    {
        private static readonly byte[] Exponents = new byte[512];
        private static readonly byte[] Logarithms = new byte[256];

        static ReedSolomon()
        {
            int value = 1;
            for (int i = 0; i < 255; i++)
            {
                Exponents[i] = (byte)value;
                Logarithms[value] = (byte)i;

                value <<= 1;
                if ((value & 0x100) != 0) value ^= 0x11d;
            }

            // The table is doubled so a multiplication's index never needs the modulo.
            for (int i = 255; i < 512; i++) Exponents[i] = Exponents[i - 255];
        }

        private static byte Multiply(byte left, byte right)
        {
            if (left == 0 || right == 0) return 0;

            return Exponents[Logarithms[left] + Logarithms[right]];
        }

        /// <summary>The parity codewords for one block: the remainder of the block divided by the generator polynomial.</summary>
        public static byte[] Parity(byte[] block, int degree)
        {
            if (degree < 1) throw new ProtocolException("error correction needs a degree");

            byte[] generator = Generator(degree);
            var work = new byte[block.Length + degree];
            Buffer.BlockCopy(block, 0, work, 0, block.Length);

            for (int i = 0; i < block.Length; i++)
            {
                byte factor = work[i];
                if (factor == 0) continue;

                for (int j = 0; j < generator.Length; j++)
                {
                    work[i + j] = (byte)(work[i + j] ^ Multiply(generator[j], factor));
                }
            }

            var parity = new byte[degree];
            Buffer.BlockCopy(work, block.Length, parity, 0, degree);
            return parity;
        }

        /// <summary>`(x - a^0)(x - a^1)...(x - a^(degree-1))`, as its coefficients.</summary>
        private static byte[] Generator(int degree)
        {
            var polynomial = new byte[] { 1 };

            for (int i = 0; i < degree; i++)
            {
                var next = new byte[polynomial.Length + 1];
                byte root = Exponents[i];

                for (int j = 0; j < polynomial.Length; j++)
                {
                    next[j] = (byte)(next[j] ^ polynomial[j]);
                    next[j + 1] = (byte)(next[j + 1] ^ Multiply(polynomial[j], root));
                }

                polynomial = next;
            }

            return polynomial;
        }
    }
}
