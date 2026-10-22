using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The tile card: a background of the app's teal, the picture in the middle at half the
    /// tile, and nothing magnified. The three things that can be wrong are the colour, the
    /// place, and the compositing of a glyph's transparent edge.
    /// </summary>
    public static class TileCanvasTests
    {
        private static byte[] Pixel(byte b, byte g, byte r, byte a)
        {
            return new byte[] { b, g, r, a };
        }

        private static byte[] At(byte[] canvas, int x, int y)
        {
            int i = (y * TileCanvas.Size + x) * 4;
            return new byte[] { canvas[i], canvas[i + 1], canvas[i + 2], canvas[i + 3] };
        }

        [Test]
        public static void ACardIsTheAppsTealAndItIsOpaque()
        {
            byte[] card = TileCanvas.Card();

            Assert.AreEqual(TileCanvas.Size * TileCanvas.Size * 4, card.Length,
                "BGRA, one card's worth");

            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255), At(card, 0, 0), "the corner");
            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255), At(card, TileCanvas.Size - 1, TileCanvas.Size - 1),
                "the far corner");
        }

        [Test]
        public static void ThePictureIsCentredAndHalfTheTile()
        {
            byte[] card = TileCanvas.Card();
            var white = new byte[TileCanvas.Picture * TileCanvas.Picture * 4];
            for (int i = 0; i < white.Length; i += 4)
            {
                white[i] = 0xFF;
                white[i + 1] = 0xFF;
                white[i + 2] = 0xFF;
                white[i + 3] = 0xFF;
            }

            TileCanvas.DrawPicture(card, white, TileCanvas.Picture, TileCanvas.Picture);

            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255), At(card, 0, 0), "the margin stays the card");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                At(card, TileCanvas.Inset, TileCanvas.Inset), "the picture's first pixel");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                At(card, TileCanvas.Inset + TileCanvas.Picture - 1, TileCanvas.Inset + TileCanvas.Picture - 1),
                "the last one, so the whole square is drawn");
            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255),
                At(card, TileCanvas.Inset + TileCanvas.Picture, TileCanvas.Inset), "and not a pixel past it");
            Assert.IsTrue(TileCanvas.Inset * 2 + TileCanvas.Picture == TileCanvas.Size,
                "a picture of half the card leaves the same margin on both sides");
        }

        [Test]
        public static void ATransparentPixelLeavesTheCardUnderIt()
        {
            byte[] card = TileCanvas.Card();

            TileCanvas.Draw(card, Pixel(0xFF, 0x00, 0xFF, 0), 1, 1, 5, 5);

            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255), At(card, 5, 5),
                "the app mark's transparent margin must not become a black square on a JPEG");
        }

        [Test]
        public static void AHalfTransparentPixelIsBlendedWithTheCard()
        {
            byte[] card = TileCanvas.Card();

            // White at half opacity over #075E54: half way to white, rounded down.
            TileCanvas.Draw(card, Pixel(0xFF, 0xFF, 0xFF, 128), 1, 1, 0, 0);

            byte[] blended = At(card, 0, 0);
            Assert.AreEqual((byte)((0xFF * 128 + 0x54 * 127) / 255), blended[0], "the blue channel");
            Assert.AreEqual((byte)((0xFF * 128 + 0x5E * 127) / 255), blended[1], "the green one");
            Assert.AreEqual((byte)((0xFF * 128 + 0x07 * 127) / 255), blended[2], "and the red one");
            Assert.AreEqual((byte)255, blended[3], "the card is a JPEG: it has no transparency to keep");
        }

        [Test]
        public static void APictureThatDoesNotFitIsRefused()
        {
            byte[] card = TileCanvas.Card();

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.Draw(card, Pixel(1, 2, 3, 255), 1, 1, TileCanvas.Size, 0); },
                "a picture drawn past the edge would read outside the canvas");
        }
    }
}
