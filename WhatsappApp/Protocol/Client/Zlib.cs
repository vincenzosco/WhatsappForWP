using System;
using System.IO;
using System.IO.Compression;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// The inflate the inline history bootstrap arrives compressed with.
    ///
    /// Why by hand: the payload is deflate inside a zlib wrapper (`0x78 ...`, the two-byte
    /// header a `zlib.deflate` writes, with a four-byte Adler-32 trailer), and the .NET
    /// class is a *raw* deflate. Feeding the wrapper to `DeflateStream` as-is answers
    /// "invalid data" - the first two bytes it sees are a header it reads as a block.
    ///
    /// So the header is skipped and the trailer is left alone: `DeflateStream` stops at the
    /// end of the deflate stream and never reads them. The header test is the format's own
    /// (the low nibble of the first byte is the compression method, and 8 is deflate), so
    /// the same call also takes a payload that turns out to be raw deflate after all,
    /// instead of refusing a payload that would have worked.
    /// </summary>
    public static class Zlib
    {
        /// <summary>The bytes a zlib header starts with: the method in the low nibble.</summary>
        private const int DeflateMethod = 8;

        public static byte[] Inflate(byte[] data)
        {
            if (data == null) throw new ProtocolException("there is no payload to inflate");

            int start = 0;
            if (data.Length > 2 && (data[0] & 0x0f) == DeflateMethod) start = 2;

            try
            {
                using (var input = new MemoryStream(data, start, data.Length - start))
                using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    int read;

                    while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                    }

                    return output.ToArray();
                }
            }
            catch (Exception error)
            {
                // Named rather than left to the caller: a payload that will not inflate is a
                // history sync that cannot be read, and the caller's answer to that is a
                // sentence in the log, not a stack trace on the chat list.
                throw new ProtocolException("the history payload is not deflate: " + error.Message, error);
            }
        }
    }
}
