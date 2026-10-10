using System;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Qr;

namespace ProtocolTests
{
    /// <summary>
    /// The QR encoder against two symbols frozen from a reference implementation, row for
    /// row and mask for mask.
    ///
    /// The comparison is the whole matrix on purpose. A test that checked "the three
    /// finder patterns are there" and "the size is right" passes for an encoder that
    /// writes the data in the wrong order, which is the one mistake here that a scanner
    /// reports as nothing at all - it simply never reads a code.
    /// </summary>
    public static class QrCodeTests
    {
        [Test]
        public static void The_short_input_matches_the_reference_symbol()
        {
            bool[,] matrix = QrCode.Encode(QrFixture.HelloText, QrErrorCorrection.Medium);

            Assert.AreEqual(QrCode.Size(QrFixture.HelloVersion), matrix.GetLength(0));
            Assert.AreEqual(QrCode.Size(QrFixture.HelloVersion), matrix.GetLength(1));

            AssertSameSymbol(QrFixture.HelloRows, matrix, "Hello, WhatsApp!");
        }

        [Test]
        public static void The_real_pairing_payload_matches_the_reference_symbol()
        {
            bool[,] matrix = QrCode.Encode(QrFixture.PairingText, QrErrorCorrection.Medium);

            // 277 bytes: past the 1..11 the plan budgeted, and the reason the tables carry
            // every version. Version 12 is what the reference picks, and a wrong version
            // would still produce a plausible square.
            Assert.IsTrue(QrFixture.PairingVersion == 12,
                "the fixture is version 12; a different one means it was regenerated");
            Assert.AreEqual(65, matrix.GetLength(0));
            Assert.AreEqual(65, matrix.GetLength(1));

            AssertSameSymbol(QrFixture.PairingRows, matrix, "the pairing payload");
        }

        [Test]
        public static void A_payload_past_the_short_versions_is_not_refused()
        {
            // 300 characters is version 13 at level Medium, and the plan's check that the
            // encoder grows rather than throws.
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < 300; i++) text.Append(i % 10 == 0 ? 'a' : 'b');

            bool[,] matrix = QrCode.Encode(text.ToString(), QrErrorCorrection.Medium);

            Assert.AreEqual(13, QrCode.SmallestVersion(300, QrErrorCorrection.Medium));
            Assert.AreEqual(69, matrix.GetLength(0));
            Assert.AreEqual(69, matrix.GetLength(1));
        }

        [Test]
        public static void An_input_that_cannot_fit_is_refused_rather_than_truncated()
        {
            // Version 40 at level High holds about 1 200 bytes; past that there is no
            // symbol, and a truncated one would scan into half a message.
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < 5000; i++) text.Append('x');

            Assert.Throws<ProtocolException>(delegate
            {
                QrCode.Encode(text.ToString(), QrErrorCorrection.High);
            });

            Assert.Throws<ProtocolException>(delegate { QrCode.Encode(string.Empty, QrErrorCorrection.Medium); });
        }

        [Test]
        public static void Every_capacity_is_consistent_with_the_version_it_comes_from()
        {
            // The versions the fixture does not reach are pinned by their own shape: the
            // size follows the version, the capacity grows with it, and the symbol the
            // encoder returns is exactly as wide as its version says.
            int previousCapacity = 0;

            for (int version = 1; version <= 12; version++)
            {
                int capacity = QrCode.Capacity(version, QrErrorCorrection.Medium);

                Assert.IsTrue(capacity > previousCapacity, "version " + version + " must hold more than " + (version - 1));
                previousCapacity = capacity;

                var text = new System.Text.StringBuilder();
                for (int i = 0; i < capacity; i++) text.Append('a');

                bool[,] matrix = QrCode.Encode(text.ToString(), QrErrorCorrection.Medium);
                Assert.AreEqual(version, QrCode.SmallestVersion(capacity, QrErrorCorrection.Medium));
                Assert.AreEqual(QrCode.Size(version), matrix.GetLength(0));
                Assert.AreEqual(QrCode.Size(version), matrix.GetLength(1));
            }
        }

        private static void AssertSameSymbol(string[] expectedRows, bool[,] matrix, string what)
        {
            int size = matrix.GetLength(0);
            if (expectedRows.Length != size)
            {
                throw new AssertionException(what + ": the reference has " + expectedRows.Length
                    + " rows and this encoder produced " + size);
            }

            for (int row = 0; row < size; row++)
            {
                string expected = expectedRows[row];
                for (int column = 0; column < size; column++)
                {
                    bool dark = matrix[row, column];
                    if ((expected[column] == '1') != dark)
                    {
                        throw new AssertionException(what + ": module (" + row + "," + column + ") differs"
                            + "\n  reference row " + expected
                            + "\n  ours          " + Describe(matrix, row, size));
                    }
                }
            }
        }

        private static string Describe(bool[,] matrix, int row, int size)
        {
            var text = new System.Text.StringBuilder(size);
            for (int column = 0; column < size; column++) text.Append(matrix[row, column] ? '1' : '0');
            return text.ToString();
        }
    }
}
