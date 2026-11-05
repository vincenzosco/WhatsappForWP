using System;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The four values a call's media needs before a datagram can move: two ICE credentials and
    /// two SSRCs, derived from the call key both ends hold.
    ///
    /// Why these tests: the two phones never exchange these values - there is no field for them
    /// in the call stanza - so the only thing that can make a call work is the two derivations
    /// agreeing. That agreement is exactly what these pin: the caller's local credentials are
    /// the callee's remote ones, the caller's SSRC is the callee's remote one, and two calls
    /// never share either.
    /// </summary>
    public static class CallMediaSetupTests
    {
        private const string CallId = "3EB0C767D097B7C7C030";

        [Test]
        public static void TheTwoEndsAgreeOnTheSameCall()
        {
            byte[] key = Key();

            CallMediaSetup caller = CallMediaSetup.Derive(key, CallId, true);
            CallMediaSetup callee = CallMediaSetup.Derive(key, CallId, false);

            Assert.AreEqual(caller.LocalUfrag, callee.RemoteUfrag,
                "the caller's ufrag is the one the callee expects in a check");
            Assert.AreEqual(caller.LocalPassword, callee.RemotePassword,
                "and its password is the one the check's integrity is made with");
            Assert.AreEqual(caller.RemoteUfrag, callee.LocalUfrag,
                "and the other direction is the mirror");
            Assert.AreEqual(caller.RemotePassword, callee.LocalPassword);

            Assert.IsTrue(caller.LocalSsrc == callee.RemoteSsrc,
                "the caller sends with the SSRC the callee reads");
            Assert.IsTrue(caller.RemoteSsrc == callee.LocalSsrc,
                "and reads with the one it sends");
        }

        [Test]
        public static void TheSameKeyAndCallGiveTheSameSetup()
        {
            CallMediaSetup first = CallMediaSetup.Derive(Key(), CallId, true);
            CallMediaSetup second = CallMediaSetup.Derive(Key(), CallId, true);

            Assert.AreEqual(first.LocalUfrag, second.LocalUfrag, "a derivation, not a random draw");
            Assert.AreEqual(first.LocalPassword, second.LocalPassword);
            Assert.IsTrue(first.LocalSsrc == second.LocalSsrc);
        }

        [Test]
        public static void AnotherCallIsAnotherPair()
        {
            byte[] key = Key();

            CallMediaSetup first = CallMediaSetup.Derive(key, CallId, true);
            CallMediaSetup second = CallMediaSetup.Derive(key, "3EB0C767D097B7C7C031", true);

            Assert.IsTrue(first.LocalUfrag != second.LocalUfrag,
                "the call id is in the derivation, so two calls cannot share a pair");
            Assert.IsTrue(first.LocalPassword != second.LocalPassword);
            Assert.IsTrue(first.LocalSsrc != second.LocalSsrc);
        }

        [Test]
        public static void TheTwoSidesOfOneCallDoNotShareAnSsrc()
        {
            CallMediaSetup setup = CallMediaSetup.Derive(Key(), CallId, true);

            Assert.IsTrue(setup.LocalSsrc != setup.RemoteSsrc,
                "two streams with one SSRC are one stream the receiver cannot tell apart");
        }

        [Test]
        public static void TheCredentialsArePrintableAndLongEnough()
        {
            CallMediaSetup setup = CallMediaSetup.Derive(Key(), CallId, false);

            IsHex(setup.LocalUfrag);
            IsHex(setup.LocalPassword);
            IsHex(setup.RemoteUfrag);
            IsHex(setup.RemotePassword);

            // RFC 8445 wants a ufrag of at least four characters and a password of at least
            // twenty-two; both are satisfied by eight and thirty-two hex characters.
            Assert.IsTrue(setup.LocalUfrag.Length == 8, "a ufrag of eight characters");
            Assert.IsTrue(setup.LocalPassword.Length == 32, "a password of thirty-two");
        }

        [Test]
        public static void ACallWithoutAKeyIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { CallMediaSetup.Derive(null, CallId, true); });
            Assert.Throws<ProtocolException>(delegate { CallMediaSetup.Derive(new byte[31], CallId, true); });
            Assert.Throws<ProtocolException>(delegate { CallMediaSetup.Derive(Key(), null, true); });
            Assert.Throws<ProtocolException>(delegate { CallMediaSetup.Derive(Key(), "", true); });
        }

        private static void IsHex(string value)
        {
            Assert.IsTrue(value != null && value.Length > 0, "a credential is not empty");

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                Assert.IsTrue(hex, "a credential is lowercase hex, not " + c);
            }
        }

        private static byte[] Key()
        {
            var key = new byte[CallKeys.CallKeyLength];
            for (int i = 0; i < key.Length; i++) key[i] = (byte)(i * 7 + 1);
            return key;
        }
    }
}
