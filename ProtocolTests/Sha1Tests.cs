using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Crypto;

namespace ProtocolTests
{
    /// <summary>
    /// SHA-1 against the published vectors (FIPS 180-4's own examples and the two
    /// canonical ones every implementation is checked with).
    ///
    /// Why vectors and not a round trip: a hash has no inverse, so the only way to know it is
    /// right is to compare it with what the specification says. SRTP authenticates with this
    /// digest, and a digest that is wrong by one round still produces plausible-looking tags
    /// that no peer will accept.
    /// </summary>
    public static class Sha1Tests
    {
        [Test]
        public static void TheEmptyMessageHashesToItsVector()
        {
            Assert.AreEqual("da39a3ee5e6b4b0d3255bfef95601890afd80709",
                Hex(Sha1.Hash(new byte[0])));
        }

        [Test]
        public static void AbcHashesToItsVector()
        {
            Assert.AreEqual("a9993e364706816aba3e25717850c26c9cd0d89d",
                Hex(Sha1.Hash(Encoding.UTF8.GetBytes("abc"))));
        }

        [Test]
        public static void TheLongStandardsMessageHashesToItsVector()
        {
            // The 56-byte message of FIPS 180-4's example: one block plus its length, which
            // is the case where the padding splits a block in two.
            string message = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";

            Assert.AreEqual("84983e441c3bd26ebaae4aa1f95129e5e54670f1",
                Hex(Sha1.Hash(Encoding.UTF8.GetBytes(message))));
        }

        /// <summary>
        /// The one property SRTP relies on beyond correctness: hashing a slice of a buffer
        /// has to equal hashing the same bytes copied out of it, because that is how a packet
        /// whose header and payload sit together is authenticated without a copy.
        /// </summary>
        [Test]
        public static void ASliceHashesAsItsOwnBytesDo()
        {
            byte[] whole = Encoding.UTF8.GetBytes("xxxxabcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopqyyyy");
            byte[] middle = new byte[56];
            System.Buffer.BlockCopy(whole, 4, middle, 0, 56);

            Assert.AreEqual(Hex(Sha1.Hash(middle)), Hex(Sha1.Hash(whole, 4, 56)));
        }

        private static string Hex(byte[] bytes)
        {
            var text = new System.Text.StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("x2"));
            return text.ToString();
        }
    }
}
