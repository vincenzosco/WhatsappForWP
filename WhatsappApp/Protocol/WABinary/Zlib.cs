using System;
using System.IO;
using Ionic.Zlib;

namespace WhatsappApp.Protocol.WABinary
{
    /// <summary>
    /// The inflate for the bytes that arrive deflated: the history bootstrap, and - since
    /// `0x02` frames are read instead of refused - the big nodes of the sync.
    ///
    /// The wire payload is deflate inside a zlib wrapper (`0x78.`, a two-byte header
    /// followed by the raw DEFLATE stream and a four-byte Adler-32 trailer). The portable
    /// `DeflateStream` consumes the raw stream, so the zlib header is skipped and the
    /// trailer is left unread. A payload that is already raw DEFLATE is also accepted.
    ///
    /// It lives here, next to the codec, because both the frame reader and the history
    /// bootstrap inflate: a `WABinary` class reaching into `Client` for a byte utility
    /// would put the two layers the wrong way round.
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
