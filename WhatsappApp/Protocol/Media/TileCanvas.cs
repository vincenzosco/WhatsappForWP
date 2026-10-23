using System;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The square a decoder crops out of a picture: its side, and where it starts.
    ///
    /// A struct rather than three `out` numbers because the three are one answer - the crop
    /// that makes a picture square without stretching it - and because the C# 5 guard reads
    /// an `out` parameter in a signature as C# 6's inline declaration.
    /// </summary>
    public struct TileCrop
    {
        /// <summary>Where the square starts, in the source's own pixels.</summary>
        public int X;
        public int Y;

        /// <summary>How many pixels on a side: the source's shorter side.</summary>
        public int Side;
    }

    /// <summary>
    /// The picture a tile shows, drawn at the size the tile shows it.
    ///
    /// Why it exists: a WP8.1 tile template that carries an image draws it **over the whole
    /// tile**, and the file this app used to hand it was the 96x96 preview WhatsApp sends -
    /// so the shell stretched it across 150 or 310 pixels and the phone of 2026-10-22 showed a
    /// magnified face. The answer is an image the shell has nothing to stretch: a tile-sized
    /// square, already composed.
    ///
    /// Two rules, because there are two pictures:
    ///
    /// - a **chat's own picture fills the tile**. The decoder crops it to its centre square and
    ///   scales it once, and it is drawn edge to edge - no border, which is what a person means
    ///   when they say the picture is not the size of the tile. The crop is why a portrait is
    ///   not stretched into a square; the shell then draws it 1:1 at every size;
    /// - the **app mark keeps a margin**, at half the tile: it is a glyph that goes away if it
    ///   is cropped by the wide tile's 310x150 shape, and a logo with a border is a logo, while
    ///   a cropped one is a mistake.
    ///
    /// It is here, in the protocol core and away from WinRT, because the arithmetic is the
    /// part that can be wrong in a way nobody sees until the phone is in a hand: `TileCanvasTests`
    /// covers it on the desktop.
    /// </summary>
    public static class TileCanvas
    {
        /// <summary>
        /// The side of the card, in pixels: 150 dp at the 240% scale this phone reports.
        /// The shell scales it for the larger sizes and the picture keeps its proportion,
        /// because the margin is part of the picture and not something around it.
        /// </summary>
        public const int Size = 336;

        /// <summary>Side of the app mark: half the card, so a glyph is never cropped by the wide tile.</summary>
        public const int Picture = 168;

        /// <summary>Where the app mark starts: the card is symmetric around it.</summary>
        public const int Inset = (Size - Picture) / 2;

        /// <summary>
        /// The wide tile, in pixels: 310x150 dp at the 240% scale this phone reports. It is
        /// the card's own size and not the square scaled: a square image in a 2.07:1 tile is
        /// scaled by the longer side and cropped, which magnifies the face by more than twice
        /// and takes the top and the chin off it.
        /// </summary>
        public const int WideWidth = 744;
        public const int WideHeight = 360;

        /// <summary>
        /// The card's colour, as BGRA: `#075E54`, the dark teal the app's own header uses.
        /// Dark because the shell writes the tile's two lines in white over the picture, and
        /// a bright card would fight them.
        /// </summary>
        public const byte CardBlue = 0x54;
        public const byte CardGreen = 0x5E;
        public const byte CardRed = 0x07;

        /// <summary>
        /// The centre square of a picture: the rectangle a decoder crops to before it scales.
        ///
        /// The rule it encodes: a picture that is not square loses a slice of its longer side,
        /// evenly at both ends, and is never stretched into one - a face widened to fill the
        /// tile is the distortion this crop exists to avoid.
        /// </summary>
        public static TileCrop CentreSquare(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ProtocolException("a picture with no side is no picture: " + width + "x" + height);
            }

            int side = width < height ? width : height;

            var crop = new TileCrop();
            crop.Side = side;
            crop.X = (width - side) / 2;
            crop.Y = (height - side) / 2;

            return crop;
        }

        /// <summary>
        /// A card with nothing on it yet: `Size` x `Size` BGRA pixels of the card's colour,
        /// fully opaque. Opaque matters: the file is written as JPEG, and a JPEG of a
        /// transparent canvas is a black one.
        /// </summary>
        public static byte[] Card()
        {
            var canvas = new byte[Size * Size * 4];

            for (int i = 0; i < canvas.Length; i += 4)
            {
                canvas[i] = CardBlue;
                canvas[i + 1] = CardGreen;
                canvas[i + 2] = CardRed;
                canvas[i + 3] = 255;
            }

            return canvas;
        }

        /// <summary>
        /// The wide card: the tile's teal, with the square card in the middle of it, 1:1.
        ///
        /// Why not hand the square card to the wide binding: the shell fills the tile with
        /// the image it is given - scaled by the wider side, cropped top and bottom - so a
        /// 336 square becomes 744 wide by 744 tall and the middle 360 rows of it are all that
        /// is left: a face magnified more than twice, with its forehead and its chin gone.
        /// The phone of 23 October 2026 showed exactly that ("le foto vengono zoommate nella
        /// tile grande"). Here the picture keeps the size it was composed at, and the card
        /// grows around it, which is also where the template's two lines have room.
        /// </summary>
        public static byte[] WideCard(byte[] square)
        {
            if (square == null) throw new ArgumentNullException("square");

            int side = SideOf(square.Length);
            if (side == 0 || side > WideHeight || side > WideWidth)
            {
                throw new ProtocolException("a picture of " + square.Length + " byte(s) is not "
                    + "a square that fits the wide card: " + WideWidth + "x" + WideHeight);
            }

            var wide = new byte[WideWidth * WideHeight * 4];

            for (int i = 0; i < wide.Length; i += 4)
            {
                wide[i] = CardBlue;
                wide[i + 1] = CardGreen;
                wide[i + 2] = CardRed;
                wide[i + 3] = 255;
            }

            int left = (WideWidth - side) / 2;
            int top = (WideHeight - side) / 2;

            for (int y = 0; y < side; y++)
            {
                Buffer.BlockCopy(square, y * side * 4, wide, ((top + y) * WideWidth + left) * 4, side * 4);
            }

            return wide;
        }

        /// <summary>
        /// The side a square BGRA buffer holds, or 0 when it is not a square of pixels.
        ///
        /// Why it exists: a decoder is *asked* for a side, and the tile of 23 October 2026
        /// found that the answer to that request is not always the answer - a 96x96 buffer
        /// went to `Draw` as 336x336 and the read ran past its end
        /// (`IndexOutOfRangeException`, `TileCanvas.Draw`). A byte count is not a wish: it
        /// can be measured, so the card measures it instead of trusting the request.
        /// </summary>
        public static int SideOf(int byteCount)
        {
            if (byteCount <= 0 || byteCount % 4 != 0) return 0;

            int pixels = byteCount / 4;
            int side = (int)Math.Sqrt(pixels);

            while (side > 0 && (long)side * side > pixels) side--;
            while ((long)(side + 1) * (side + 1) <= pixels) side++;

            return side * side == pixels ? side : 0;
        }

        /// <summary>
        /// A square picture at another side, by bilinear interpolation over premultiplied
        /// alpha.
        ///
        /// Why it is here and not left to the decoder: the decoder is the only resampler on
        /// this phone worth using, and it is also the component whose answer the tile of
        /// 23 October 2026 could not trust - asked for a scaled picture it returned the crop
        /// at its own size. The card is composed from a buffer whose side is known, so what
        /// reaches `Draw` is always exactly the side it is drawn at.
        ///
        /// Premultiplied because the app mark is a glyph on transparency: averaging straight
        /// alpha across a transparent neighbour darkens every edge into a halo.
        /// </summary>
        public static byte[] ResizeSquare(byte[] picture, int from, int to)
        {
            if (picture == null) throw new ArgumentNullException("picture");
            if (to <= 0) throw new ProtocolException("a picture with no side is no picture: " + to);

            if (from <= 0)
            {
                throw new ProtocolException("a picture of " + picture.Length + " byte(s) is "
                    + "not a square of pixels, so it cannot be scaled to " + to + "x" + to);
            }

            if ((long)from * from * 4 > picture.Length)
            {
                throw new ProtocolException("a picture of " + picture.Length + " byte(s) is not "
                    + from + "x" + from + " pixels, so it cannot be scaled to " + to + "x" + to);
            }

            var scaled = new byte[to * to * 4];

            for (int y = 0; y < to; y++)
            {
                // Half a pixel in, so the first and last output rows are not sampled from
                // exactly the edge and the grid stays centred on the source.
                double sourceY = (y + 0.5) * from / to - 0.5;
                if (sourceY < 0) sourceY = 0;
                if (sourceY > from - 1) sourceY = from - 1;

                int top = (int)sourceY;
                int bottom = top < from - 1 ? top + 1 : top;
                double fy = sourceY - top;

                for (int x = 0; x < to; x++)
                {
                    double sourceX = (x + 0.5) * from / to - 0.5;
                    if (sourceX < 0) sourceX = 0;
                    if (sourceX > from - 1) sourceX = from - 1;

                    int left = (int)sourceX;
                    int right = left < from - 1 ? left + 1 : left;
                    double fx = sourceX - left;

                    int atTopLeft = (top * from + left) * 4;
                    int atTopRight = (top * from + right) * 4;
                    int atBottomLeft = (bottom * from + left) * 4;
                    int atBottomRight = (bottom * from + right) * 4;

                    double wTopLeft = (1 - fx) * (1 - fy);
                    double wTopRight = fx * (1 - fy);
                    double wBottomLeft = (1 - fx) * fy;
                    double wBottomRight = fx * fy;

                    // Premultiplied, so a transparent corner cannot pull the colour of the
                    // opaque one towards black: the weight is the alpha as well.
                    double alpha = picture[atTopLeft + 3] * wTopLeft
                        + picture[atTopRight + 3] * wTopRight
                        + picture[atBottomLeft + 3] * wBottomLeft
                        + picture[atBottomRight + 3] * wBottomRight;

                    int at = (y * to + x) * 4;
                    if (alpha <= 0)
                    {
                        scaled[at + 3] = 0;
                        continue;
                    }

                    double blue = picture[atTopLeft] * picture[atTopLeft + 3] * wTopLeft
                        + picture[atTopRight] * picture[atTopRight + 3] * wTopRight
                        + picture[atBottomLeft] * picture[atBottomLeft + 3] * wBottomLeft
                        + picture[atBottomRight] * picture[atBottomRight + 3] * wBottomRight;

                    double green = picture[atTopLeft + 1] * picture[atTopLeft + 3] * wTopLeft
                        + picture[atTopRight + 1] * picture[atTopRight + 3] * wTopRight
                        + picture[atBottomLeft + 1] * picture[atBottomLeft + 3] * wBottomLeft
                        + picture[atBottomRight + 1] * picture[atBottomRight + 3] * wBottomRight;

                    double red = picture[atTopLeft + 2] * picture[atTopLeft + 3] * wTopLeft
                        + picture[atTopRight + 2] * picture[atTopRight + 3] * wTopRight
                        + picture[atBottomLeft + 2] * picture[atBottomLeft + 3] * wBottomLeft
                        + picture[atBottomRight + 2] * picture[atBottomRight + 3] * wBottomRight;

                    scaled[at] = Channel(blue / alpha);
                    scaled[at + 1] = Channel(green / alpha);
                    scaled[at + 2] = Channel(red / alpha);
                    scaled[at + 3] = Channel(alpha);
                }
            }

            return scaled;
        }

        /// <summary>One channel, rounded and held inside the range a byte has.</summary>
        private static byte Channel(double value)
        {
            if (value <= 0) return 0;
            if (value >= 255) return 255;

            return (byte)Math.Round(value);
        }

        /// <summary>
        /// The picture drawn into the card, centred: a picture of the whole tile covers it edge
        /// to edge, and the app mark - half the tile across - is the one that keeps a margin.
        ///
        /// `picture` is BGRA with straight (not premultiplied) alpha, which is what the decoder
        /// is asked for: the app mark is a glyph with a transparent margin, and compositing it
        /// over the card is what makes it a mark on a background rather than a black square.
        /// </summary>
        public static void DrawPicture(byte[] canvas, byte[] picture, int width, int height)
        {
            Draw(canvas, picture, width, height, (Size - width) / 2, (Size - height) / 2);
        }

        /// <summary>The same, at a place of the caller's choosing. Used by the tests.</summary>
        public static void Draw(byte[] canvas, byte[] picture, int width, int height, int left, int top)
        {
            if (canvas == null) throw new ArgumentNullException("canvas");
            if (picture == null) throw new ArgumentNullException("picture");
            if (width <= 0 || height <= 0) throw new ProtocolException("a picture with no side is no picture");

            // The buffer is read `width` pixels per row, so it has to be at least that big.
            // The tile of 23 October 2026 handed this method 96x96 bytes as 336x336 and the
            // read ran off the end: an IndexOutOfRangeException with nothing in it that
            // names the picture. A short buffer is a protocol error with a number in it.
            if (picture.Length < width * height * 4)
            {
                throw new ProtocolException("a picture of " + width + "x" + height + " is "
                    + (width * height * 4) + " byte(s) and this one is " + picture.Length
                    + ": the side it is drawn at is not the side it has");
            }

            if (left < 0 || top < 0 || left + width > Size || top + height > Size)
            {
                throw new ProtocolException("the picture does not fit the card: " + width + "x" + height
                    + " at " + left + "," + top);
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int from = (y * width + x) * 4;
                    int to = ((top + y) * Size + (left + x)) * 4;

                    int alpha = picture[from + 3];
                    if (alpha == 255)
                    {
                        canvas[to] = picture[from];
                        canvas[to + 1] = picture[from + 1];
                        canvas[to + 2] = picture[from + 2];
                        continue;
                    }

                    if (alpha == 0) continue;

                    // Source over: what a transparent edge of a glyph has to become on a card
                    // of a known colour, with integer arithmetic so it cannot drift.
                    canvas[to] = (byte)((picture[from] * alpha + canvas[to] * (255 - alpha)) / 255);
                    canvas[to + 1] = (byte)((picture[from + 1] * alpha + canvas[to + 1] * (255 - alpha)) / 255);
                    canvas[to + 2] = (byte)((picture[from + 2] * alpha + canvas[to + 2] * (255 - alpha)) / 255);
                }
            }
        }
    }
}
