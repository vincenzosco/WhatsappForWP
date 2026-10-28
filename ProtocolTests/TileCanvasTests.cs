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

        /// <summary>One pixel of a BGRA buffer whose row is `width` pixels long.</summary>
        private static byte[] PictureAt(byte[] picture, int width, int x, int y)
        {
            int i = (y * width + x) * 4;
            return new byte[] { picture[i], picture[i + 1], picture[i + 2], picture[i + 3] };
        }

        /// <summary>
        /// The crop as pixels, for the decoder that refuses to crop.
        ///
        /// Why it is a test: on 23 October 2026 the full-size profile picture answered
        /// `ArgumentException` (E_INVALIDARG) to the crop, and the tile then fell back to the
        /// raw JPEG - the magnified face again. The whole picture is a request nothing
        /// refuses, so the square is cut here, and it has to be the same square
        /// `CentreSquare` names: the middle, never the corner, and never stretched.
        /// </summary>
        [Test]
        public static void TheCentreSquareOfAPictureIsCutOutOfIt()
        {
            // 4x2, one distinct colour per column, so the crop can be read off the answer.
            var wide = new byte[4 * 2 * 4];
            for (int y = 0; y < 2; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int i = (y * 4 + x) * 4;
                    wide[i] = (byte)(10 + x);
                    wide[i + 3] = 255;
                }
            }

            byte[] square = TileCanvas.CentreSquarePixels(wide, 4, 2);

            Assert.AreEqual(2 * 2 * 4, square.Length, "the shorter side, squared: nothing stretched");
            Assert.ByteArraysEqual(Pixel(11, 0, 0, 255), PictureAt(square, 2, 0, 0),
                "the crop starts where CentreSquare says: one column in");
            Assert.ByteArraysEqual(Pixel(12, 0, 0, 255), PictureAt(square, 2, 1, 0),
                "and it is two columns wide, not four");
            Assert.ByteArraysEqual(Pixel(11, 0, 0, 255), PictureAt(square, 2, 0, 1),
                "the second row is the second row of the picture");
        }

        [Test]
        public static void ASquarePictureIsAlreadyItsOwnCrop()
        {
            byte[] picture = White(3);
            byte[] square = TileCanvas.CentreSquarePixels(picture, 3, 3);

            Assert.AreEqual(3 * 3 * 4, square.Length, "nothing is added around it");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255), PictureAt(square, 3, 2, 2),
                "and nothing is cut off it");

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.CentreSquarePixels(White(2), 3, 3); },
                "a buffer shorter than the picture it claims to be is refused, not read past");
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

        /// <summary>
        /// The wide tile is the card grown around the picture, not the picture stretched into
        /// it: a square filled into a 2.07:1 binding is scaled by the wider side and cropped,
        /// which is the magnified face the phone of 23 October 2026 still showed.
        /// </summary>
        [Test]
        public static void TheWideCardIsTheTealCardAroundThePictureAtItsOwnSize()
        {
            byte[] card = TileCanvas.Card();
            TileCanvas.DrawPicture(card, White(TileCanvas.Size), TileCanvas.Size, TileCanvas.Size);

            byte[] wide = TileCanvas.WideCard(card);

            Assert.AreEqual(TileCanvas.WideWidth * TileCanvas.WideHeight * 4, wide.Length,
                "the wide tile's own shape");

            int left = (TileCanvas.WideWidth - TileCanvas.Size) / 2;
            int top = (TileCanvas.WideHeight - TileCanvas.Size) / 2;

            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255), AtSide(wide, TileCanvas.WideWidth, 0, 0),
                "the side the picture does not reach is the card");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                AtSide(wide, TileCanvas.WideWidth, left, top), "the picture starts where it was placed");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                AtSide(wide, TileCanvas.WideWidth, left + TileCanvas.Size - 1, top + TileCanvas.Size - 1),
                "and ends exactly its own side further on, so nothing was scaled");
            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255),
                AtSide(wide, TileCanvas.WideWidth, left + TileCanvas.Size, top), "not a pixel past it");
        }

        [Test]
        public static void APictureThatIsNotASquareHasNoWideCard()
        {
            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.WideCard(new byte[10]); },
                "ten bytes are not a square of pixels");
        }

        /// <summary>
        /// The card's file name is made of the chat, the rules and the picture.
        ///
        /// Why the picture is in it: a WP8.1 tile caches an image by its URI, so a card
        /// rewritten at one path is the old picture on the Start screen; and two updates of
        /// one chat open that one file at once, which is the `UnauthorizedAccessException`
        /// (E_ACCESSDENIED) the phone showed on 23 October 2026. Two calls for the same
        /// picture must still name the same file, or every message writes a new one.
        /// </summary>
        [Test]
        public static void TheCardNameCarriesThePictureAndTheRules()
        {
            string pin = TileCanvas.CardFileName("pin-393519096405_s_whatsapp_net.jpg", "AAAA");
            string again = TileCanvas.CardFileName("pin-393519096405_s_whatsapp_net.jpg", "AAAA");
            string other = TileCanvas.CardFileName("pin-393519096405_s_whatsapp_net.jpg", "BBBB");

            Assert.AreEqual(pin, again, "the same picture and the same rules are the same file");
            Assert.IsTrue(pin != other, "a new picture is a new file, so no URI changes its bytes");

            Assert.IsTrue(pin.StartsWith("pin-393519096405_s_whatsapp_net-v", System.StringComparison.Ordinal),
                "the chat is still in it: " + pin);
            Assert.IsTrue(pin.EndsWith(".jpg", System.StringComparison.Ordinal), "and so is the extension: " + pin);
            Assert.IsTrue(pin.IndexOf("-v" + TileCanvas.CardVersion + "-", System.StringComparison.Ordinal) > 0,
                "the rules that drew it, so an older card is not reused: " + pin);

            for (int i = 0; i < pin.Length; i++)
            {
                char c = pin[i];
                Assert.IsTrue(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.',
                    "a tile image path holds no other character: " + c);
            }

            Assert.AreEqual("chat-v" + TileCanvas.CardVersion + "-" + TileCanvas.Fingerprint("AAAA") + ".jpg",
                TileCanvas.CardFileName("chat", "AAAA"), "a name with no extension gets one");
            Assert.AreEqual("a-v" + TileCanvas.CardVersion + "-" + TileCanvas.Fingerprint("") + ".png",
                TileCanvas.CardFileName("a.png", null), "and the app mark is a name too");
        }

        /// <summary>
        /// The fingerprint is stable across processes: the foreground and the background task
        /// write the same chat's card, and the shell reads the name one of them wrote. A
        /// `GetHashCode` would differ between the two runs and leave two files per picture.
        /// </summary>
        [Test]
        public static void TheFingerprintOfAPictureIsTheSameInEveryProcess()
        {
            Assert.AreEqual("811c9dc5", TileCanvas.Fingerprint(""),
                "FNV-1a of nothing, which is the app mark's own name");
            Assert.AreEqual(TileCanvas.Fingerprint("aGVsbG8="), TileCanvas.Fingerprint("aGVsbG8="),
                "the same bytes give the same digits");
            Assert.IsTrue(TileCanvas.Fingerprint("aGVsbG8=") != TileCanvas.Fingerprint("aGVsbG9z"),
                "and different ones do not collide here");
            Assert.AreEqual(8, TileCanvas.Fingerprint("aGVsbG8=").Length, "eight digits, always");
        }

        /// <summary>The wide file is the same name with `-wide`: the two cannot drift apart.</summary>
        /// <summary>
        /// The fallback's name is not a card's name.
        ///
        /// Why: `ComposeAsync` skips the work when the card's name is already on the phone, so
        /// a raw JPEG written under that name is a card that never composes again - which is
        /// what the phone of 23 October 2026 kept drawing. The fallback is still named by the
        /// same picture and the same rules, so a second fallback for the same bytes is the same
        /// file, and `IsCurrentCard` accepts it (it carries `-v5-`, and its two variants exist).
        /// </summary>
        [Test]
        public static void TheFallbackHasANameOfItsOwn()
        {
            string chat = "pin-393519096405_s_whatsapp_net.jpg";
            string raw = TileCanvas.FallbackFileName(chat, "AAAA");

            Assert.IsTrue(raw != TileCanvas.CardFileName(chat, "AAAA"),
                "a fallback under the card's name is never composed again: " + raw);
            Assert.IsTrue(raw.IndexOf("-raw-", System.StringComparison.Ordinal) > 0,
                "and it says so: " + raw);
            Assert.AreEqual(raw, TileCanvas.FallbackFileName(chat, "AAAA"),
                "the same picture under the same rules is the same file");
            Assert.IsTrue(raw != TileCanvas.FallbackFileName(chat, "BBBB"),
                "a new picture is a new file");
            Assert.IsTrue(raw.EndsWith(".jpg", System.StringComparison.Ordinal), "the extension is kept: " + raw);
            Assert.IsTrue(raw.IndexOf("-v" + TileCanvas.CardVersion + "-", System.StringComparison.Ordinal) > 0,
                "the rules are in it, so a file of the older ones is not reused: " + raw);
        }

        [Test]
        public static void TheWideNameIsTheSquareNameWithWide()
        {
            Assert.AreEqual("pin-1-v2-ab12cd34-wide.jpg", TileCanvas.WideFileName("pin-1-v2-ab12cd34.jpg"),
                "the marker goes before the extension, not after it");
            Assert.AreEqual("pin-1-wide", TileCanvas.WideFileName("pin-1"),
                "a name with no extension keeps the marker at the end");
        }

        /// <summary>
        /// The 71x71 tile's card: that tile's own side, with the picture filling it.
        ///
        /// Why it is a card of its own: a binding fills the tile with the image it is given and
        /// crops the rest, so the 336 card in a 170 px tile showed the middle of the face at
        /// twice its size - the "l'immagine troppo zoommata e non si adatta alla dimensione
        /// tile" of 23 October 2026.
        /// </summary>
        [Test]
        public static void TheSmallCardIsTheTilesOwnSideAndThePictureFillsIt()
        {
            byte[] card = TileCanvas.Card(TileCanvas.SmallSize);
            Assert.AreEqual(TileCanvas.SmallSize * TileCanvas.SmallSize * 4, card.Length,
                "the 71x71 tile's own shape");

            byte[] picture = TileCanvas.ResizeSquare(White(TileCanvas.Size),
                TileCanvas.Size, TileCanvas.SmallSize);
            Assert.AreEqual(TileCanvas.SmallSize * TileCanvas.SmallSize * 4, picture.Length,
                "the composed card, measured down to the small tile");

            TileCanvas.DrawInto(card, TileCanvas.SmallSize, picture,
                TileCanvas.SmallSize, TileCanvas.SmallSize, 0, 0);

            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                AtSide(card, TileCanvas.SmallSize, 0, 0), "the picture starts at the corner");
            Assert.ByteArraysEqual(Pixel(0xFF, 0xFF, 0xFF, 255),
                AtSide(card, TileCanvas.SmallSize, TileCanvas.SmallSize - 1, TileCanvas.SmallSize - 1),
                "and reaches the far corner, so the shell has nothing to crop");
        }

        /// <summary>
        /// A card walked at another side is refused with a number, not read past its end: the
        /// canvas stride is the card's side, and a 170 card read as 336 is the same
        /// out-of-bounds read the picture's side had (`Draw`).
        /// </summary>
        [Test]
        public static void ACardWalkedAtAnotherSideIsRefused()
        {
            byte[] small = TileCanvas.Card(TileCanvas.SmallSize);

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate
                {
                    TileCanvas.DrawInto(small, TileCanvas.Size, White(TileCanvas.Size),
                        TileCanvas.Size, TileCanvas.Size, 0, 0);
                },
                "a 170 card read as 336 reads past its end");

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate
                {
                    TileCanvas.DrawInto(small, TileCanvas.SmallSize, White(TileCanvas.SmallSize),
                        TileCanvas.SmallSize, TileCanvas.SmallSize, 1, 0);
                },
                "and a picture one pixel past the edge does not fit either");
        }

        [Test]
        public static void TheSmallNameIsTheSquareNameWithSmall()
        {
            Assert.AreEqual("pin-1-v3-ab12cd34-small.jpg", TileCanvas.SmallFileName("pin-1-v3-ab12cd34.jpg"),
                "the marker goes before the extension, not after it");
            Assert.AreEqual("pin-1-small", TileCanvas.SmallFileName("pin-1"),
                "a name with no extension keeps the marker at the end");
        }

        /// <summary>
        /// The theme card's own shape: a background covers the card of **any** proportions, and
        /// the mark drawn on it is the app's logo - which is what the card of 28 October 2026
        /// had lost, a wallpaper edge to edge and nothing else on it.
        /// </summary>
        [Test]
        public static void ABackgroundCoversTheWideCardAndTheMarkSitsOnIt()
        {
            // A 768x768 wallpaper, white: every pixel of both cards is the picture itself, so
            // anything that is not white was drawn by this class - the card's teal, or the mark.
            byte[] background = White(768);

            byte[] square = TileCanvas.CoverPixels(background, 768, 768, TileCanvas.Size, TileCanvas.Size);
            Assert.AreEqual(TileCanvas.Size * TileCanvas.Size * 4, square.Length,
                "the square card is the size the medium and large tiles draw");

            TileCanvas.Dim(square, TileCanvas.ThemeDim);
            Assert.ByteArraysEqual(Pixel(153, 153, 153, 255), At(square, 0, 0),
                "the background is darkened so a white mark reads on it");

            byte[] mark = new byte[4 * 4 * 4];
            for (int i = 0; i < mark.Length; i += 4)
            {
                mark[i] = 0x54;
                mark[i + 1] = 0x5E;
                mark[i + 2] = 0x07;
                mark[i + 3] = 255;
            }

            TileCanvas.DrawOnto(square, TileCanvas.Size, TileCanvas.Size, mark, 4, 4,
                TileCanvas.Inset, TileCanvas.Inset);

            Assert.ByteArraysEqual(Pixel(0x54, 0x5E, 0x07, 255), At(square, TileCanvas.Inset, TileCanvas.Inset),
                "the mark is drawn on the background, in the middle of the card");
            Assert.ByteArraysEqual(Pixel(153, 153, 153, 255), At(square, 2, 2),
                "and the background is around it, not the card's teal");

            byte[] wide = TileCanvas.CoverPixels(background, 768, 768,
                TileCanvas.WideWidth, TileCanvas.WideHeight);
            Assert.AreEqual(TileCanvas.WideWidth * TileCanvas.WideHeight * 4, wide.Length,
                "the wide tile is filled by the background, not by a square floating on teal");

            TileCanvas.Dim(wide, TileCanvas.ThemeDim);
            TileCanvas.DrawOnto(wide, TileCanvas.WideWidth, TileCanvas.WideHeight, mark, 4, 4,
                (TileCanvas.WideWidth - 4) / 2, (TileCanvas.WideHeight - 4) / 2);

            int corner = (0 * TileCanvas.WideWidth + 0) * 4;
            Assert.AreEqual(153, (int)wide[corner], "the far left of the wide card is the background");

            int middle = ((TileCanvas.WideHeight / 2) * TileCanvas.WideWidth + TileCanvas.WideWidth / 2) * 4;
            Assert.AreEqual(0x54, (int)wide[middle], "and the mark is in the middle of it");
        }

        /// <summary>
        /// The crop a background needs: the card's shape, centred, and never stretched. A 4:3
        /// picture in a square card loses its sides, not its proportions.
        /// </summary>
        [Test]
        public static void ABackgroundLosesItsEdgesAndNotItsProportions()
        {
            // Four columns of one colour each, three rows tall, in BGRA.
            var picture = new byte[4 * 3 * 4];
            for (int y = 0; y < 3; y++)
            {
                for (int x = 0; x < 4; x++)
                {
                    int at = (y * 4 + x) * 4;
                    picture[at] = (byte)(x * 40);
                    picture[at + 1] = (byte)(x * 40);
                    picture[at + 2] = (byte)(x * 40);
                    picture[at + 3] = 255;
                }
            }

            byte[] square = TileCanvas.CoverPixels(picture, 4, 3, 3, 3);
            Assert.AreEqual(3 * 3 * 4, square.Length, "a 3x3 card out of a 4x3 picture");

            // The crop keeps the left three of the four columns - a whole-pixel centring, so
            // the odd column goes on the right - and every kept column keeps its colour.
            int middle = (1 * 3 + 1) * 4;
            Assert.AreEqual((byte)40, square[middle], "the second column of the picture is the second of the card");
        }

        /// <summary>
        /// A crop that runs off the picture is a read past the end of its buffer, and it is
        /// refused with the numbers instead: this is the shape the tile of 23 October 2026
        /// crashed on, and a background that covers a card is a crop like any other.
        /// </summary>
        [Test]
        public static void ACropOutsideThePictureIsRefused()
        {
            byte[] picture = White(4);

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.Resize(picture, 4, 3, 0, 4, 4, 8, 8); },
                "four columns read from column 3 are one column and three past the end");

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.Resize(picture, 4, 0, 3, 4, 4, 8, 8); },
                "and a crop below the last row is past the end of the buffer too");

            Assert.Throws<WhatsappApp.Protocol.ProtocolException>(
                delegate { TileCanvas.Resize(new byte[10], 4, 0, 0, 4, 4, 8, 8); },
                "a buffer that is not whole rows has no rows to crop");
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
