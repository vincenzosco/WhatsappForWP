using System;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The picture a tile shows, drawn at the size the tile shows it.
    ///
    /// Why it exists: a WP8.1 tile template that carries an image draws it **over the whole
    /// tile**, and a profile picture is 96x96 - the preview WhatsApp hands out. Handing that
    /// file to the tile is what made the phone of 2026-10-22 look like it was showing a
    /// magnified face, and the same for the app mark: `TileIcon.png` is a glyph with a
    /// transparent margin, so drawing it edge to edge is a logo enlarged until it is cropped.
    ///
    /// The answer is not a different template - none of them draws an image in a frame - but
    /// an image that is already a card: the tile's own background, the picture at half the
    /// tile, centred, with a margin around it. The shell then draws it 1:1 whatever the size,
    /// so nothing is magnified and nothing is cropped.
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

        /// <summary>Side of the picture inside the card: half the card, so it reads as a face.</summary>
        public const int Picture = 168;

        /// <summary>Where the picture starts: the card is symmetric around it.</summary>
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
        /// The picture drawn into the card at (75, 75) - the centred position - over whatever
        /// is there. `picture` is BGRA with straight (not premultiplied) alpha, which is what
        /// the decoder is asked for: the app mark is a glyph with a transparent margin, and
        /// compositing it over the card is what makes it a mark on a background rather than a
        /// black square.
        /// </summary>
        public static void DrawPicture(byte[] canvas, byte[] picture, int width, int height)
        {
            Draw(canvas, picture, width, height, Inset, Inset);
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
