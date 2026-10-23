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
