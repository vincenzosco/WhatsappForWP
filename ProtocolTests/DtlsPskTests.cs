using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The PSK half of DTLS 1.2.
    ///
    /// Why these tests and no RFC vector: the RFC's PSK examples are for a complete handshake,
    /// which needs a peer this project does not have yet. What is pinned here is the shape RFC
    /// 4279 defines byte for byte - the length, the zero run of `other_secret`, the secret - and
    /// the two properties a caller depends on: the same inputs give the same keys, a different
    /// secret gives different ones.
    /// </summary>
    public static class DtlsPskTests
    {
        private static byte[] Secret()
        {
            return new byte[] { 1, 2, 3, 4 };
        }

        private static byte[] Random(byte seed)
        {
            var bytes = new byte[32];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
            return bytes;
        }

        /// <summary>
        /// `uint16(4) || 00000000 || uint16(4) || 01020304`: the premaster of RFC 4279, section 2.
        /// </summary>
        [Test]
        public static void ThePreMasterIsTheLengthTheZerosAndTheSecret()
        {
            Assert.AreEqual("000400000000000401020304", Hex(DtlsPsk.PreMaster(Secret())));
        }

        [Test]
        public static void TheMasterSecretIsFortyEightBytesAndDeterministic()
        {
            byte[] first = DtlsPsk.MasterSecret(Secret(), Random(0x10), Random(0x40));
            byte[] again = DtlsPsk.MasterSecret(Secret(), Random(0x10), Random(0x40));

            Assert.AreEqual(48, first.Length, "a master secret is 48 bytes");
            Assert.AreEqual(Hex(first), Hex(again), "the same inputs are the same keys");
        }

        [Test]
        public static void AnotherSecretIsAnotherKey()
        {
            byte[] first = DtlsPsk.MasterSecret(Secret(), Random(0x10), Random(0x40));
            byte[] second = DtlsPsk.MasterSecret(new byte[] { 1, 2, 3, 5 }, Random(0x10), Random(0x40));

            Assert.IsTrue(Hex(first) != Hex(second), "the secret is what changes the keys");
        }

        [Test]
        public static void TheClientAndServerKeysDifferAndFillTheBlock()
        {
            byte[] master = DtlsPsk.MasterSecret(Secret(), Random(0x10), Random(0x40));
            byte[] block = DtlsPsk.KeyBlock(master, Random(0x10), Random(0x40));

            Assert.AreEqual(40, block.Length, "two keys and two implicit nonces");

            bool same = true;
            for (int i = 0; i < 16; i++)
            {
                if (block[i] != block[16 + i]) same = false;
            }

            Assert.IsFalse(same, "the two directions do not share a key");
        }

        [Test]
        public static void AnEmptySecretAndAWrongRandomAreRefused()
        {
            Assert.Throws<ProtocolException>(delegate { DtlsPsk.PreMaster(new byte[0]); });
            Assert.Throws<ProtocolException>(delegate { DtlsPsk.PreMaster(null); });
            Assert.Throws<ProtocolException>(delegate
            {
                DtlsPsk.MasterSecret(Secret(), new byte[16], Random(0x40));
            });
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));
            return text.ToString();
        }
    }
}
