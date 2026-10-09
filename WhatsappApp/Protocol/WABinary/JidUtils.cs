using System;

namespace WhatsappApp.Protocol.WABinary
{
    /// <summary>A JID split into the parts the binary format cares about.</summary>
    public struct DecodedJid
    {
        /// <summary>False when the string had no `@` and is not a JID at all.</summary>
        public bool IsJid;

        public string User;
        public string Server;

        /// <summary>The domain, which decides the server on the way back.</summary>
        public int DomainType;

        /// <summary>False when the JID carried no `:device` segment.</summary>
        public bool HasDevice;

        public int Device;
    }

    /// <summary>
    /// The two JID helpers the binary format needs, matching `WABinary/jid-utils.ts`.
    ///
    /// A JID is `user[:device]@server`. The binary format encodes it as a pair
    /// (`user` and `server` written as strings) or, when a device is present, as an
    /// "AD JID": a domain byte, a device byte and the user. Which form goes out is
    /// decided by <see cref="DecodedJid.HasDevice"/>, so `device 0` and `no device`
    /// have to stay distinguishable - that is the one thing this type does not do
    /// the obvious way.
    /// </summary>
    public static class JidUtils
    {
        public const string WhatsappServer = "s.whatsapp.net";

        public const int DomainWhatsApp = 0;
        public const int DomainLid = 1;
        public const int DomainHosted = 128;
        public const int DomainHostedLid = 129;

        /// <summary>Splits a JID; <see cref="DecodedJid.IsJid"/> says whether it was one.</summary>
        public static DecodedJid Decode(string jid)
        {
            var decoded = new DecodedJid();
            if (string.IsNullOrEmpty(jid)) return decoded;

            int at = jid.IndexOf('@');
            if (at < 0) return decoded;

            decoded.IsJid = true;
            decoded.Server = jid.Substring(at + 1);

            string userAndDevice = jid.Substring(0, at);

            // The reference destructures `split(':')` and `split('_')`, so only the
            // first segment after each separator counts: "a:b:c" is a=user, b=device.
            string[] devicePair = FirstTwoSegments(userAndDevice, ':');
            string[] agentPair = FirstTwoSegments(devicePair[0], '_');

            decoded.User = agentPair[0];
            string agent = agentPair[1];
            string deviceText = devicePair[1];

            decoded.HasDevice = !string.IsNullOrEmpty(deviceText);
            decoded.Device = decoded.HasDevice ? ParseInt(deviceText) : 0;

            if (decoded.Server == "lid") decoded.DomainType = DomainLid;
            else if (decoded.Server == "hosted") decoded.DomainType = DomainHosted;
            else if (decoded.Server == "hosted.lid") decoded.DomainType = DomainHostedLid;
            else if (!string.IsNullOrEmpty(agent)) decoded.DomainType = ParseInt(agent);
            else decoded.DomainType = DomainWhatsApp;

            return decoded;
        }

        /// <summary>
        /// Joins the parts back. A device of zero is left out, exactly as the
        /// reference does with `!!device`, so `user:0@server` does not survive a
        /// round trip: it becomes `user@server`.
        /// </summary>
        public static string Encode(string user, string server, int device)
        {
            string devicePart = device != 0 ? ":" + device.ToString() : string.Empty;
            return (user ?? string.Empty) + devicePart + "@" + server;
        }

        /// <summary>The server a domain byte names, for an AD JID on the way in.</summary>
        public static string ServerForDomain(int domainType)
        {
            if (domainType == DomainLid) return "lid";
            if (domainType == DomainHosted) return "hosted";
            if (domainType == DomainHostedLid) return "hosted.lid";
            return WhatsappServer;
        }

        /// <summary>
        /// The first two segments of a `split` on a separator, as a two-element array:
        /// `{ before, after }`, and `{ text, null }` when the separator is absent.
        ///
        /// It returns an array rather than using an `out` parameter because the C# 5
        /// guard reads `out string x` in a signature as C# 6's inline declaration, and
        /// a guard rule is not worth weakening for a helper this small.
        /// </summary>
        private static string[] FirstTwoSegments(string text, char separator)
        {
            int first = text.IndexOf(separator);
            if (first < 0) return new string[] { text, null };

            int second = text.IndexOf(separator, first + 1);
            string rest = second < 0
                ? text.Substring(first + 1)
                : text.Substring(first + 1, second - first - 1);

            return new string[] { text.Substring(0, first), rest };
        }

        private static int ParseInt(string text)
        {
            int value;
            return int.TryParse(text, out value) ? value : 0;
        }
    }
}
