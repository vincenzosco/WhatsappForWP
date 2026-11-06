using System;
using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Media;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The peer's certificate fingerprint in the call signalling: the value, and the node that
    /// carries it.
    ///
    /// Why these tests: the fingerprint is the one pin that turns a finished DTLS handshake into
    /// an authenticated call, and the APK shows it travelling with the transport negotiation
    /// (`Web P2P: sending type 13 (., has_fp=%d)`) rather than anywhere the Java code names.
    /// So the format is ours to choose from the SDP one, and a round trip is the only thing that
    /// says the two ends will read the same bytes.
    /// </summary>
    public static class CallFingerprintTests
    {
        private const string CallId = "3EB0C767D097B7C7C030";

        [Test]
        public static void TheValueRoundTripsThroughItsText()
        {
            byte[] digest = Digest();
            string text = CallFingerprint.Format(digest);

            Assert.IsTrue(text.StartsWith("sha-256 ", StringComparison.Ordinal),
                "the algorithm comes first: " + text);

            byte[] parsed = CallFingerprint.Parse(text);

            Assert.IsTrue(parsed != null, "a formatted fingerprint parses");
            Assert.ByteArraysEqual(digest, parsed);
        }

        [Test]
        public static void LowercaseHexAndNoColonsAreTheSameFingerprint()
        {
            byte[] digest = Digest();
            string text = CallFingerprint.Format(digest).Replace(":", "").ToLowerInvariant();

            Assert.ByteArraysEqual(digest, CallFingerprint.Parse(text));
        }

        [Test]
        public static void AnotherAlgorithmIsNotAPin()
        {
            Assert.AreEqual(null, CallFingerprint.Parse("sha-1 00112233445566778899AABBCCDDEEFF00112233"),
                "a digest this side cannot compute is not a fingerprint to compare");
        }

        [Test]
        public static void AShortOrBrokenValueIsRefused()
        {
            Assert.AreEqual(null, CallFingerprint.Parse("sha-256 00112233"));
            Assert.AreEqual(null, CallFingerprint.Parse("sha-256 zz112233"));
            Assert.AreEqual(null, CallFingerprint.Parse(null));
            Assert.AreEqual(null, CallFingerprint.Parse(""));
        }

        [Test]
        public static void TheFingerprintOfACertificateIsItsSha256()
        {
            byte[] certificate = new byte[] { 0x30, 0x82, 0x01, 0x02, 0x07 };

            Assert.ByteArraysEqual(WhatsappApp.Protocol.Crypto.Sha256.Hash(certificate),
                CallFingerprint.Of(certificate));
        }

        [Test]
        public static void AnAcceptCarriesTheFingerprintAndReadsItBack()
        {
            var audio = new List<CallAudio>();
            audio.Add(new CallAudio { Codec = "opus", Rate = 16000 });

            var candidates = new List<CallCandidate>();
            candidates.Add(CallCandidates.Host("192.168.0.108", 8585, 1));

            string fingerprint = CallFingerprint.Format(Digest());

            BinaryNode call = CallSignaling.Accept(CallId, "15551230000@s.whatsapp.net",
                audio, null, candidates, fingerprint);

            BinaryNode destination = call.Children()[0].Child("destination");

            Assert.IsTrue(destination != null, "the fingerprint rides on the candidates' node");
            Assert.AreEqual(fingerprint, destination.Attrs["fingerprint"],
                "in the form the peer's engine logs");

            // And the reader takes it off the same node, which is what makes it a pin: a value
            // written and not read is a field nobody uses.
            CallSignal signal = CallSignaling.Read(call);

            Assert.IsTrue(signal != null, "the answer is a call node");
            Assert.AreEqual(fingerprint, signal.Fingerprint,
                "the reader gives the peer's fingerprint back");
        }

        [Test]
        public static void AnAcceptWithoutAFingerprintHasNoSuchAttribute()
        {
            BinaryNode accept = CallSignaling.Accept(CallId, "15551230000@s.whatsapp.net",
                null, null, new List<CallCandidate>()).Children()[0];

            Assert.IsTrue(accept.Child("destination") == null,
                "no candidates and no fingerprint is no destination node");
        }

        private static byte[] Digest()
        {
            var digest = new byte[CallFingerprint.DigestLength];
            for (int i = 0; i < digest.Length; i++) digest[i] = (byte)(i * 11 + 3);
            return digest;
        }
    }
}
