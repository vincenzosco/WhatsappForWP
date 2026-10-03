using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The frame contract with the adapter, in one place:
    /// [4-byte UInt32LE payload length][payload].
    ///
    /// It used to live inside CommunicationService, scattered among the socket,
    /// the cipher and the dispatcher. Here the reader, the writer, the ceiling and
    /// both byte orders are one file, so a new frame site cannot quietly choose a
    /// different one - which is exactly what the framing guard scans for.
    ///
    /// Nothing here decides what a payload means: the caller encrypts before
    /// <see cref="WriteFrameAsync"/> and decrypts after <see cref="ReadFrameAsync"/>.
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>
        /// Largest frame we accept. The 4-byte prefix is the only thing the other
        /// end checks: if the reader is misaligned that length is a piece of JSON,
        /// that is a huge number. Without a limit LoadAsync used it as the size and
        /// the app ended up in OutOfMemoryException (0x8007000E) instead of closing
        /// the connection. Eight mebibytes let an image through in base64 and stay
        /// far from the memory of a WP8.1 phone. The adapter's MAX_FRAME_LENGTH is
        /// the same number; change one and you must change the other.
        /// </summary>
        public const uint MaxFrameLength = 8 * 1024 * 1024;

        /// <summary>
        /// A DataReader for a network stream, with the byte order stated explicitly.
        /// The WinRT default is not little-endian, and the adapter writes the frame
        /// length with writeUInt32LE: on the device a 289-byte frame (0x00000121)
        /// was read as 0x21010000 = 553713664, that is a frame that does not exist,
        /// and the connection closed before receiving the state and the QR code.
        /// Reading and writing go through CreateFrameReader/CreateFrameWriter: they
        /// are the only place the byte order is chosen, so they can no longer
        /// diverge.
        /// </summary>
        public static DataReader CreateFrameReader(IInputStream stream)
        {
            var reader = new DataReader(stream);
            reader.InputStreamOptions = InputStreamOptions.Partial;
            reader.ByteOrder = ByteOrder.LittleEndian;
            return reader;
        }

        /// <summary>The same pact as CreateFrameReader, on the writing side.</summary>
        public static DataWriter CreateFrameWriter(IOutputStream stream)
        {
            var writer = new DataWriter(stream);
            writer.ByteOrder = ByteOrder.LittleEndian;
            return writer;
        }

        /// <summary>The write: [4-byte UInt32LE payload length][payload].</summary>
        public static async Task WriteFrameAsync(DataWriter writer, byte[] payload)
        {
            writer.WriteUInt32((uint)payload.Length);
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }

        /// <summary>
        /// Reads one complete frame: [4-byte length][payload].
        /// Returns null when the connection is closed or the frame is not
        /// acceptable (a length outside 1..MaxFrameLength is a fault, not a
        /// payload: it is recorded and the connection is dropped).
        /// </summary>
        public static async Task<byte[]> ReadFrameAsync(DataReader reader)
        {
            if (!await LoadAtLeastAsync(reader, 4)) return null;

            uint payloadLength = reader.ReadUInt32();

            if (payloadLength == 0 || payloadLength > MaxFrameLength)
            {
                Diag.Failed("ReadFrameAsync/length",
                    new InvalidDataException("frame length out of range: " + payloadLength));
                return null;
            }

            if (!await LoadAtLeastAsync(reader, payloadLength)) return null;

            byte[] payload = new byte[payloadLength];
            reader.ReadBytes(payload);
            return payload;
        }

        /// <summary>
        /// Fills the reader buffer until it has at least <paramref name="count"/>
        /// unconsumed bytes. With InputStreamOptions.Partial a LoadAsync can return
        /// fewer than requested: the length prefix read with a single LoadAsync(4)
        /// was split in the middle of a frame and the connection dropped on a frame
        /// that had only arrived in two pieces.
        /// </summary>
        private static async Task<bool> LoadAtLeastAsync(DataReader reader, uint count)
        {
            while (reader.UnconsumedBufferLength < count)
            {
                uint loaded = await reader.LoadAsync(count - reader.UnconsumedBufferLength);
                if (loaded == 0) return false;   // stream closed by the other end
            }
            return true;
        }
    }
}
