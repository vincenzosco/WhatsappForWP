using System;

namespace ProtocolTests
{
    /// <summary>
    /// The reference conversation, frozen. Printed by a script in a temporary
    /// directory outside this repository, from libsignal-protocol's own
    /// test/testvectors.js - the same file its own test suite runs against.
    ///
    /// The command that regenerates it, from a directory outside this repository:
    ///
    ///   mkdir -p /tmp/signalref && cd /tmp/signalref && npm init -y
    ///   npm install libsignal-protocol
    ///   node emit-fixture.js                 # the script is in the plan's notes
    ///
    /// Nothing in it was typed: a constant copied by eye is a bug the fixture cannot
    /// catch - which is how a mangled certificate constant once cost an afternoon.
    /// </summary>
    public static class SignalFixture
    {
        public static class X3dh
        {
            /// <summary>Alice's private halves, in the clear, because a fixture is not a phone.</summary>
            public static readonly byte[] AliceIdentityPrivate = Hex("d83d8141aad5f1d62d78a1af09ffbe61f2d3458eeb887a047a58a07565d24463");
            public static readonly byte[] AliceBasePrivate = Hex("2060fe31b041d28127ac35cbfe790e2a25f92d2e21eb2251690ae75e732f5c4d");
            public static readonly byte[] AliceEphemeralPrivate = Hex("082e6391deb7154bd0375df3fc07f87020a3b0fd7a8c6c90e73f0e054bc2bf5d");
            public static readonly uint AliceRegistrationId = 10290;

            /// <summary>Bob's half, as the server hands it out in a pre-key bundle.</summary>
            public static readonly byte[] BobIdentityPublic = Hex("059c2197be51bae703ae2edd26b6ff2b03d589ef4851be33a3f8d923ad86a6b439");
            public static readonly uint BobRegistrationId = 0xD00D;
            public static readonly uint BobPreKeyId = 4611143;
            public static readonly byte[] BobPreKeyPublic = Hex("052cd5004a4c31dd7b89b7fc80cc3e62abcf9cf1af014c93ec4589f7ca3e79e65c");
            public static readonly uint BobSignedPreKeyId = 14983230;
            public static readonly byte[] BobSignedPreKeyPublic = Hex("05a9ecf666ec55fc27988ecc417db0d62dd5e1fa751da1f7a2dd2eca0d14c8bd46");
            public static readonly byte[] BobSignedPreKeySignature = Hex("0b46fdb238f1e2df7b28a94ba575e58b0aa1d377bb843602cc8c2a7cd33770fdd741f65a240f7c3086f00f31dc4f3b8ceeab498356f8d5e4bfe6f2dd3eeca98f");
        }

        /// <summary>The conversation: what Alice sent, and what came back.</summary>
        public static class Conversation
        {
            public static readonly string[] Plaintexts = new string[] { "A", "B" };
            public static readonly string[] Ciphertexts = new string[]
            {
                "3308c7b899021221058a49fa8a94224aaa8f5873404e01710ff9ef02169a75f90af4fbbc600796e0521a21050a6cf5e075c9970f14862db8a703a6c761f50b5182d17874908940556a22372222d301330a2105883ab58b3eb6db93b32bf91899a5b5175e7b21e96fff2cec02c83dff16ba1b271000180022a0013c5d070d1b75c418cef769bd7378a58969537a00e0ff60cbb99defb486fcfb43384264da4ea9821c1336f02d988da38944453331c4b30181704cbcec5a792ab87c5ccff256e0b4d61ba6a30a6964783875018882e66bfbd9445ac44fee9dc67edc2ad9de78adbe0eb7e9cb990272183ce5fac682ee5106f67d732cd16dfb731239590ba67dc827e849c49a9fb5ed8eed41d85d5e6de3294e74f3524c6489c2f25482ff52f9ea29c928b25030bec09207",
                "3308c7b899021221058a49fa8a94224aaa8f5873404e01710ff9ef02169a75f90af4fbbc600796e0521a21050a6cf5e075c9970f14862db8a703a6c761f50b5182d17874908940556a22372222d301330a2105883ab58b3eb6db93b32bf91899a5b5175e7b21e96fff2cec02c83dff16ba1b271001180022a001256aae85babf8c0808f75e08bf10a63f7f3aea97324c2583d777f609df493d7d45232c8883c3e1118fbc29b6318a3091ae57fed4f1c54458c6bb832fbb35f24933cb79765d00f4a161e2877a5a21a26592cdb0aa8a2f70f5fbe8c601ecdff0bef1b733d7fd0cb7b7d8fc1e45f79c016c8f90449239ca1a04b374538f2760eef43127ddc9a6439c6ceca5faf5962fb26d7248257d4d5ee3fe4cf8795acc555718558e5317f618828328b25030bec09207"
            };

            /// <summary>Bob's replies: the first arrives with a new ratchet key.</summary>
            public static readonly string[] Received = new string[]
            {
                "330a2105bc81f1348a1d065b2bd2776edb9f29bc4150399db35c1d87dc258b94894bc57a1000180022a001c93af1107634d9eaa1516a4f8e95c6a454c27313b38830709eb863608f08f2f3a598ff8f558645427f7b6ea8e182e40f7b4a92ce0325f2e22f76f36f6954f6f391dd21d2cad12e5b620e75b991e69df8821ab0e826e3cb2ae1c7a1fb8ed72213e36fc508ca1f0a92ebe2089535b5d5e1b34eae5f91497bd072de47de3291ba78a6fd67d3f8f3f20d04ab3a1159df8f36ef7e4696847e32ce6be07edb93763a2226c87feff8cc4827",
                "330a2105bc81f1348a1d065b2bd2776edb9f29bc4150399db35c1d87dc258b94894bc57a1001180022a001eb52c72c7bb6b8878c96398cc05810382d29fc17644f88bdc8d57509e8a734626620ae243cb740466806ee3c64bbf12957d5ac0452a17aba6c0e10e2a82626a986df0c4e5cadebb9ce824f1af4fac85cf7d1b9b7cf37f5df06d77b901d0e2aaa772b49f838ec92a67d13b4d7908cf91f7e0a54ad031b2aa4a954180b652f0696350e4f286592e24cc83091b196f2d48397241e33acaf6f65be27af12f1a8af91fd1daf2c01bdfaaa"
            };
            public static readonly string[] ReceivedTexts = new string[] { "C", "D" };
            public static readonly string[] ReceivedEphemeralKeys = new string[]
            {
                "d04f334799ea1272eff64c5267e28274f54b91b3b11372879303eb7a8cd52763",
                "undefined"
            };
        }

        /// <summary>Hex, the way the vectors write it.</summary>
        public static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }
    }
}
