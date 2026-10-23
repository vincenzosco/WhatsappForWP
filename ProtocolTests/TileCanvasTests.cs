using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The tile card: a background of the app's teal, a chat's picture covering the whole tile,
    /// the app mark at half of it, and nothing magnified. The things that can be wrong are the
    /// colour, the place, and the compositing of a glyph's transparent edge.
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

        /// <summary>
        /// The crop that makes a picture square: the shorter side, centred, and nothing
        /// stretched.
        ///
        /// Why the arithmetic is here and not only in `TileImage`: a crop off by a few pixels
        /// is a face moved sideways in the tile, and it is the one part of the picture nobody
        /// can check once it is on a phone.
        /// </summary>
        [Test]
        public static void TheCropIsTheCentreSquareOfThePicture()
        {
            TileCrop square = TileCanvas.CentreSquare(96, 96);
            Assert.AreEqual(0, square.X, "a square picture loses nothing");
            Assert.AreEqual(0, square.Y, "on either axis");
            Assert.AreEqual(96, square.Side, "and is itself");

            TileCrop wide = TileCanvas.CentreSquare(400, 300);
            Assert.AreEqual(50, wide.X, "a landscape picture loses half of the extra width on each side");
            Assert.AreEqual(0, wide.Y, "and nothing vertically");
            Assert.AreEqual(300, wide.Side, "the shorter side is what survives");

            TileCrop tall = TileCanvas.CentreSquare(300, 400);
            Assert.AreEqual(0, tall.X, "a portrait picture loses nothing horizontally");
            Assert.AreEqual(50, tall.Y, "and the extra height is split");
            Assert.AreEqual(300, tall.Side, "around the shorter side");

            TileCrop odd = TileCanvas.CentreSquare(101, 100);
            Assert.AreEqual(0, odd.X, "an odd extra pixel goes on the right, not outside");
            Assert.IsTrue(odd.X + odd.Side <= 101, "the crop never runs past the source");
        }

        [Test]
        public static void APictureWithNoPixelsHasNoCrop()
        {
            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.CentreSquare(0, 100); }, "a column of no pixels is no picture");
            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.CentreSquare(100, 0); }, "and neither is a row of none");
        }

        /// <summary>A white square of `side` pixels, fully opaque, as straight BGRA.</summary>
        private static byte[] White(int side)
        {
            var square = new byte[side * side * 4];
            for (int i = 0; i < square.Length; i += 4)
            {
                square[i] = 0xFF;
                square[i + 1] = 0xFF;
                square[i + 2] = 0xFF;
                square[i + 3] = 0xFF;
            }

            return square;
        }

        /// <summary>
        /// A chat's picture covers the tile: no border anywhere.
        ///
        /// Why it is the rule: the phone of 2026-10-22 showed every group and every person
        /// inside a teal frame - the picture at half the tile - and a frame around a picture is
        /// exactly what the person asking for "the image at the size of the tile" is pointing at.
        /// </summary>
        [Test]
        public static void APictureOfTheWholeTileLeavesNoEdge()
        {
            byte[] card = TileCanvas.Card();

            TileCanvas.DrawPicture(card, White(TileCanvas.Size), TileCanvas.Size, TileCanvas.Size);

            byte[] white = Pixel(0xFF, 0xFF, 0xFF, 255);

            Assert.ByteArraysEqual(white, At(card, 0, 0), "the top-left corner is the picture");
            Assert.ByteArraysEqual(white, At(card, TileCanvas.Size - 1, TileCanvas.Size - 1),
                "the far corner too, so the whole square is drawn");
            Assert.ByteArraysEqual(white, At(card, TileCanvas.Size / 2, 0),
                "the middle of the top edge: no margin on any side");
            Assert.ByteArraysEqual(white, At(card, 0, TileCanvas.Size / 2),
                "and none on the left one");
        }

        /// <summary>
        /// The app mark keeps its margin, at half the tile: a glyph with air around it is a mark,
        /// and one cropped by the wide tile's shape is a mistake.
        /// </summary>
        [Test]
        public static void TheAppMarkIsCentredAndHalfTheTile()
        {
            byte[] card = TileCanvas.Card();

            TileCanvas.DrawPicture(card, White(TileCanvas.Picture), TileCanvas.Picture, TileCanvas.Picture);

            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255), At(card, 0, 0), "the margin stays the card");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                At(card, TileCanvas.Inset, TileCanvas.Inset), "the mark's first pixel");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                At(card, TileCanvas.Inset + TileCanvas.Picture - 1, TileCanvas.Inset + TileCanvas.Picture - 1),
                "the last one, so the whole square is drawn");
            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255),
                At(card, TileCanvas.Inset + TileCanvas.Picture, TileCanvas.Inset), "and not a pixel past it");
            Assert.IsTrue(TileCanvas.Inset * 2 + TileCanvas.Picture == TileCanvas.Size,
                "a mark of half the card leaves the same margin on both sides");
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

        /// <summary>One pixel of a square BGRA buffer of any side, not just the card's.</summary>
        private static byte[] AtSide(byte[] picture, int side, int x, int y)
        {
            int i = (y * side + x) * 4;
            return new byte[] { picture[i], picture[i + 1], picture[i + 2], picture[i + 3] };
        }

        /// <summary>
        /// The crash of 23 October 2026, at the arithmetic level: the decoder answered 96x96
        /// for a picture the card asked for at 336, and the read ran off the end of it.
        ///
        /// Why it is a test and not a comment: `Draw` reads `width` pixels per row, so a
        /// buffer that is shorter than the side it is drawn at is an out-of-bounds read, and
        /// the only thing that made it a silent one was that nothing measured it.
        /// </summary>
        [Test]
        public static void APictureShorterThanTheSideItIsDrawnAtIsRefused()
        {
            byte[] card = TileCanvas.Card();

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.Draw(card, White(96), TileCanvas.Size, TileCanvas.Size, 0, 0); },
                "96x96 bytes drawn as 336x336 read past the end of the buffer");

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.ResizeSquare(new byte[10], 2, 4); },
                "a buffer that is not even two row's worth is not a 2x2 picture");
        }

        /// <summary>
        /// The buffer knows its own side. This is the measurement the tile was missing: the
        /// decoder is asked for a side, and what it returns is what the card has to compose.
        /// </summary>
        [Test]
        public static void TheSideOfABufferIsMeasured()
        {
            Assert.AreEqual(1, TileCanvas.SideOf(4), "one pixel is one pixel");
            Assert.AreEqual(96, TileCanvas.SideOf(96 * 96 * 4), "a 96x96 preview knows it is 96");
            Assert.AreEqual(TileCanvas.Size, TileCanvas.SideOf(TileCanvas.Size * TileCanvas.Size * 4),
                "and a card knows it is the card");

            Assert.AreEqual(0, TileCanvas.SideOf(0), "nothing is not a square");
            Assert.AreEqual(0, TileCanvas.SideOf(96), "nor is a run that is not a whole number of pixels");
            Assert.AreEqual(0, TileCanvas.SideOf(40), "nor ten pixels, which are not a square");
        }

        /// <summary>
        /// The whole path the phone takes after the fix: the decoder's answer is measured,
        /// scaled to the card's side, and then drawn - and the tile is the picture, not a
        /// crash and not a margin.
        /// </summary>
        [Test]
        public static void ADecodedPictureReachesTheCardAtTheSideItIsDrawn()
        {
            byte[] decoded = White(96);

            int side = TileCanvas.SideOf(decoded.Length);
            Assert.AreEqual(96, side, "the decoder's own side");

            byte[] scaled = TileCanvas.ResizeSquare(decoded, side, TileCanvas.Size);
            Assert.AreEqual(TileCanvas.Size * TileCanvas.Size * 4, scaled.Length,
                "the card's side, exactly what Draw reads");

            byte[] card = TileCanvas.Card();
            TileCanvas.DrawPicture(card, scaled, TileCanvas.Size, TileCanvas.Size);

            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255), At(card, 0, 0),
                "a white picture fills the tile from its first pixel");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                At(card, TileCanvas.Size - 1, TileCanvas.Size - 1), "to its last one");
        }

        /// <summary>
        /// A glyph scaled up keeps its colour: the app mark is one opaque shape on a
        /// transparent margin, and a straight-alpha average over that margin pulls every
        /// edge towards black - a halo around the mark on the card.
        /// </summary>
        [Test]
        public static void ScalingAGlyphDoesNotDarkenItsEdge()
        {
            // A 2x2 picture whose only opaque pixel is red; the other three are the margin.
            var glyph = new byte[2 * 2 * 4];
            glyph[0] = 0x00;
            glyph[1] = 0x00;
            glyph[2] = 0xFF;
            glyph[3] = 0xFF;

            byte[] scaled = TileCanvas.ResizeSquare(glyph, 2, 8);

            Assert.ByteArraysEqual(Pixel(0x00, 0x00, 0xFF, 255), AtSide(scaled, 8, 0, 0),
                "the corner the opaque pixel fills stays red and opaque");
            Assert.ByteArraysEqual(Pixel(0x00, 0x00, 0x00, 0), AtSide(scaled, 8, 7, 7),
                "the far corner is the margin, and stays transparent");

            byte[] blended = AtSide(scaled, 8, 3, 0);
            Assert.AreEqual((byte)0xFF, blended[2], "a partly covered pixel is still red, not red-turned-black");
            Assert.IsTrue(blended[3] > 0 && blended[3] < 255,
                "and it is partly covered, which is what upscaling between two source pixels means");
        }

        /// <summary>Shrinking is the other half: an app mark of 480 becomes the 168 the tile draws.</summary>
        [Test]
        public static void ShrinkingAPictureKeepsItsColour()
        {
            byte[] scaled = TileCanvas.ResizeSquare(White(480), 480, TileCanvas.Picture);

            Assert.AreEqual(TileCanvas.Picture * TileCanvas.Picture * 4, scaled.Length, "exactly the mark's side");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255), AtSide(scaled, TileCanvas.Picture, 0, 0),
                "white stays white");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                AtSide(scaled, TileCanvas.Picture, TileCanvas.Picture - 1, TileCanvas.Picture - 1),
                "all the way across");
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
