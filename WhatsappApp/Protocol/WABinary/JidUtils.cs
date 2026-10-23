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

        /// <summary>
        /// The placeholder device the server puts in a device list: device 99 is not a phone,
        /// and the reference leaves it out of every fan-out (`device !== 99`). The device sits
        /// inside the JID (`name:99@server`), which is why a check on the front of the string
        /// never matched one.
        /// </summary>
        public static bool IsPlaceholderDevice(string jid)
        {
            DecodedJid decoded = Decode(jid);
            return decoded.IsJid && decoded.HasDevice && decoded.Device == 99;
        }

        /// <summary>
        /// The JID with the device taken off, which is the JID of the *chat*.
        ///
        /// The reference does exactly this before it decides which conversation a message
        /// belongs to - `whatsmeow`, `message.go`, `parseMessageSource`,
        /// `source.Chat = from.ToNonAD()`, and Baileys' `jidNormalizedUser`, which drops the
        /// device and rewrites `c.us` to `s.whatsapp.net`. It matters because a message written
        /// from a linked device arrives with the sender's device in `from`
        /// (`15551230000:5@s.whatsapp.net`), and a chat keyed on the raw string would give that
        /// contact a second chat row named after their device.
        ///
        /// The device itself is still needed - it is what a Signal session is stored under -
        /// so this is only for the chat and for anything shown to a person.
        /// </summary>
        public static string Normalize(string jid)
        {
            DecodedJid decoded = Decode(jid);

            // Not a JID at all (an empty string, a group already bare, a test's placeholder):
            // there is nothing to strip and guessing one would lose it.
            if (!decoded.IsJid) return jid;

            string server = decoded.Server == "c.us" ? WhatsappServer : decoded.Server;

            return Encode(decoded.User, server, 0);
        }

        /// <summary>
        /// Whether a stanza's `from` names a conversation this app draws a row for.
        ///
        /// Why it is a rule and not a check at each caller: the chat list is built from
        /// whatever arrives, so anything the server names becomes a row - and two of the
        /// addresses WhatsApp uses are not people. `status@broadcast` is the status list
        /// (its updates have their own screen), and `@newsletter` is a channel, which this
        /// client does not read at all. `0@s.whatsapp.net` is the third and the one the
        /// device of 2026-10-22 put on screen: it is the placeholder the server writes for
        /// "no account" (the `from` of an offline marker), and a list with a row called
        /// "0" in it is a list that says something WhatsApp never did.
        ///
        /// A group, a `@lid`, a `@bot` and a `hosted` address are all conversations: the
        /// rule refuses shapes, never names.
        /// </summary>
        public static bool IsConversation(string jid)
        {
            DecodedJid decoded = Decode(jid);
            if (!decoded.IsJid) return false;

            if (decoded.Server == "broadcast" || decoded.Server == "newsletter") return false;

            // The three addresses a person can have a chat with. Anything else that is a JID
            // (`g.us`, `bot`, `hosted`) is a conversation; an `s.whatsapp.net` whose user part
            // is all zeros is the placeholder and not an account.
            if (decoded.Server == WhatsappServer || decoded.Server == "c.us" || decoded.Server == "lid")
            {
                return !AllZeros(decoded.User);
            }

            return true;
        }

        /// <summary>
        /// Whether this address is a LID: the identifier WhatsApp uses for an account whose
        /// phone number this phone does not know yet.
        ///
        /// Why it is a rule and not a check at each caller: a LID's user part is a long run
        /// of digits, so anything that prints a JID's user as a number prints a phone number
        /// that belongs to nobody - `+258212345678901` in the chat list, in the chat header
        /// and on the contact page. `DisplayNameForJid` learned it the hard way: it had the
        /// `@lid` guard written, but below the `user.Length >= 8 && all digits` branch, which
        /// every real LID satisfies - a guard that could not fire.
        /// </summary>
        public static bool IsLid(string jid)
        {
            DecodedJid decoded = Decode(jid);
            if (!decoded.IsJid) return false;

            return decoded.DomainType == DomainLid || decoded.DomainType == DomainHostedLid;
        }

        /// <summary>True for an empty user part and for one made only of zeros.</summary>
        private static bool AllZeros(string user)
        {
            if (string.IsNullOrEmpty(user)) return true;

            for (int i = 0; i < user.Length; i++)
            {
                if (user[i] != '0') return false;
            }

            return true;
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
