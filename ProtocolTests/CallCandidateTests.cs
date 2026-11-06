using System;
using System.Collections.Generic;
using System.Text;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Media;

namespace ProtocolTests
{
    /// <summary>
    /// The `<te>` content of a call offer.
    ///
    /// Why these tests: an offer with no candidates names a phone the other side cannot reach,
    /// and the encoder that produces them is bytes on the wire - the one place where a field
    /// number or a terminator is not a detail. The pinned hex is the whole message for one
    /// candidate, so a change of shape is a failing test rather than a call that silently
    /// carries a transport nobody can parse.
    /// </summary>
    public static class CallCandidateTests
    {
        [Test]
        public static void AHostCandidateIsItsAddressItsPortAndItsPriority()
        {
            CallCandidate candidate = CallCandidates.Host("1.2.3.4", 5, 0);

            // field 1 (id) 0A 09 "1.2.3.4:5"
            // field 2 (ip) 12 04 01 02 03 04
            // field 3 (port) 18 05
            // field 4 (type) 20 00 a host candidate
            // field 5 (prio) 28 00
            // field 6 (proto) 30 00 UDP
            Assert.AreEqual("0A09312E322E332E343A351204010203041805200028003000", Hex(candidate.Data));
            Assert.AreEqual(0, candidate.Priority, "the priority is on the node as well");
        }

        [Test]
        public static void TwoInterfacesGiveTwoCandidatesWithTheirOwnPriority()
        {
            CallCandidate first = CallCandidates.Host("192.168.0.108", 8585, 1);
            CallCandidate second = CallCandidates.Host("10.0.0.4", 8585, 2);

            Assert.AreEqual(1, first.Priority);
            Assert.AreEqual(2, second.Priority);
            Assert.IsTrue(Hex(first.Data) != Hex(second.Data), "two addresses are two candidates");

            // The id carries the endpoint, so the peer's later `transport` can name it.
            Assert.IsTrue(Hex(first.Data).IndexOf(Hex(Encoding.UTF8.GetBytes("192.168.0.108:8585")),
                StringComparison.Ordinal) > 0, "the id is the endpoint as text");
        }

        [Test]
        public static void AnAddressThatIsNotFourBytesIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { CallCandidates.Host("not-an-address", 8585, 1); });
            Assert.Throws<ProtocolException>(delegate { CallCandidates.Host("1.2.3", 8585, 1); });
            Assert.Throws<ProtocolException>(delegate { CallCandidates.Host("1.2.3.300", 8585, 1); });
        }

        [Test]
        public static void APortOutsideTheRangeIsRefused()
        {
            Assert.Throws<ProtocolException>(delegate { CallCandidates.Host("1.2.3.4", 0, 1); });
            Assert.Throws<ProtocolException>(delegate { CallCandidates.Host("1.2.3.4", 65536, 1); });
        }

        /// <summary>
        /// The candidates of an offer reach the node as `te` children of `destination`: the
        /// encoder and the writer are two files, and this is where they meet.
        /// </summary>
        [Test]
        public static void TheCandidateOfAnOfferIsTheEncodedOne()
        {
            var candidates = new List<CallCandidate>();
            candidates.Add(CallCandidates.Host("1.2.3.4", 5, 0));

            WhatsappApp.Protocol.WABinary.BinaryNode offer =
                CallSignaling.Offer("CALL", "39349@s.whatsapp.net", "39389@s.whatsapp.net",
                    null, null, (CallKeyPayload)null, candidates)
                    .Children()[0];

            WhatsappApp.Protocol.WABinary.BinaryNode te = offer.Child("destination").Child("te");
            Assert.IsTrue(te != null, "the offer carries a candidate");
            Assert.AreEqual("0", te.Attrs["priority"]);
            Assert.AreEqual(Hex(candidates[0].Data), Hex(te.Bytes()));
        }

        private static string Hex(byte[] bytes)
        {
            if (bytes == null) return "null";

            var text = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++) text.Append(bytes[i].ToString("X2"));

            return text.ToString();
        }
    }
}
