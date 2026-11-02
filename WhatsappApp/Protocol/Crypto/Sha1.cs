using System;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// SHA-1 (FIPS 180-4), managed.
    ///
    /// Why this hash and not the SHA-256 the rest of the client uses: SRTP authenticates
    /// with **HMAC-SHA1 truncated to 80 bits** (RFC 3711, section 4.2), and its key
    /// derivation is built on SHA-1's own block size. WhatsApp's media does not get to
    /// choose a different hash, so this file exists; nothing else in this client should use
    /// it - SHA-1 is not collision-resistant and is here for interoperability, not security.
    ///
    /// Managed for the same reason as every other hash here: the phone and the desktop test
    /// host have to run the same code, and the phone's own implementation is a WinRT type
    /// the test host cannot reach.
    /// </summary>
    public static class Sha1
    {
        public const int DigestLength = 20;

        /// <summary>The digest of the whole input.</summary>
        public static byte[] Hash(byte[] data)
        {
            if (data == null) data = new byte[0];

            return Hash(data, 0, data.Length);
        }

        /// <summary>
        /// The digest of a slice: SRTP authenticates a header and a payload held in the same
        /// buffer, and copying them into a third one per packet is a copy a call cannot pay
        /// sixty times a second.
        /// </summary>
        public static byte[] Hash(byte[] data, int offset, int count)
        {
            if (data == null) throw new ProtocolException("SHA-1 needs data to hash");
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ProtocolException("SHA-1 was given a range outside its buffer");

            uint h0 = 0x67452301;
            uint h1 = 0xEFCDAB89;
            uint h2 = 0x98BADCFE;
            uint h3 = 0x10325476;
            uint h4 = 0xC3D2E1F0;

            // The padding is built as a buffer and not block by block, and that is the fix
            // for a real bug: a message whose length is 56 modulo 64 has no room left in its
            // last block for the `1` bit **and** the eight length bytes, so the length goes in
            // a block of its own. Writing the two into one block ran the length over the
            // `0x80`, which reads as a correct implementation until the message is exactly
            // that long - the FIPS example is, and it failed.
            long bits = (long)count * 8;
            int padded = count + 1 + 8;
            while (padded % 64 != 0) padded++;

            var buffer = new byte[padded];
            Buffer.BlockCopy(data, offset, buffer, 0, count);
            buffer[count] = 0x80;

            // The length in bits as a 64-bit big-endian number, at the very end.
            for (int i = 0; i < 8; i++)
            {
                buffer[padded - 1 - i] = (byte)((bits >> (8 * i)) & 0xff);
            }

            var block = new byte[64];
            for (int at = 0; at < padded; at += 64)
            {
                Buffer.BlockCopy(buffer, at, block, 0, 64);
                Compress(block, ref h0, ref h1, ref h2, ref h3, ref h4);
            }

            var digest = new byte[DigestLength];
            Put(digest, 0, h0);
            Put(digest, 4, h1);
            Put(digest, 8, h2);
            Put(digest, 12, h3);
            Put(digest, 16, h4);

            return digest;
        }

        /// <summary>One 64-byte block: the eighty rounds of the specification.</summary>
        private static void Compress(byte[] block, ref uint h0, ref uint h1, ref uint h2, ref uint h3, ref uint h4)
        {
            var w = new uint[80];

            for (int i = 0; i < 16; i++)
            {
                w[i] = ((uint)block[i * 4] << 24) | ((uint)block[i * 4 + 1] << 16)
                    | ((uint)block[i * 4 + 2] << 8) | block[i * 4 + 3];
            }

            for (int i = 16; i < 80; i++)
            {
                w[i] = Rotate(w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16], 1);
            }

            uint a = h0, b = h1, c = h2, d = h3, e = h4;

            for (int i = 0; i < 80; i++)
            {
                uint f;
                uint k;

                if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
                else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
                else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
                else { f = b ^ c ^ d; k = 0xCA62C1D6; }

                uint temp = Rotate(a, 5) + f + e + k + w[i];
                e = d;
                d = c;
                c = Rotate(b, 30);
                b = a;
                a = temp;
            }

            h0 += a;
            h1 += b;
            h2 += c;
            h3 += d;
            h4 += e;
        }

        private static uint Rotate(uint value, int bits)
        {
            return (value << bits) | (value >> (32 - bits));
        }

        private static void Put(byte[] into, int at, uint value)
        {
            into[at] = (byte)(value >> 24);
            into[at + 1] = (byte)(value >> 16);
            into[at + 2] = (byte)(value >> 8);
            into[at + 3] = (byte)value;
        }
    }
}
