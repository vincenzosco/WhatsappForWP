using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Trusted-contact privacy tokens used by current one-to-one message relays.
    /// </summary>
    public sealed class PrivacyToken
    {
        public byte[] Bytes;
        public string Timestamp;
    }

    public static class PrivacyTokens
    {
        public const string Namespace = "privacy";
        public const string Type = "trusted_contact";

        /// <summary>Asks WhatsApp to issue a trusted-contact token for one person.</summary>
        public static BinaryNode Query(string jid, string id, uint timestamp)
        {
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a privacy token needs a contact");
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a privacy token query needs an id");

            var token = new BinaryNode("token");
            token.Attrs.Add("jid", jid);
            token.Attrs.Add("t", timestamp.ToString());
            token.Attrs.Add("type", Type);

            var tokens = new BinaryNode("tokens");
            tokens.Content = new List<BinaryNode> { token };

            var query = new BinaryNode("iq");
            query.Attrs.Add("to", "@s.whatsapp.net");
            query.Attrs.Add("type", "set");
            query.Attrs.Add("xmlns", Namespace);
            query.Attrs.Add("id", id);
            query.Content = new List<BinaryNode> { tokens };
            return query;
        }

        /// <summary>Reads the first trusted-contact token in a privacy result.</summary>
        public static PrivacyToken Read(BinaryNode answer)
        {
            if (answer == null) return null;

            BinaryNode tokens = answer.Child("tokens");
            if (tokens == null) return null;

            IList<BinaryNode> children = tokens.Children();
            if (children == null) return null;

            for (int i = 0; i < children.Count; i++)
            {
                BinaryNode token = children[i];
                if (token == null || token.Tag != "token") continue;
                if (token.Attrs["type"] != Type) continue;

                byte[] bytes = token.Bytes();
                string timestamp = token.Attrs["t"];
                if (bytes == null || bytes.Length == 0 || string.IsNullOrEmpty(timestamp)) continue;

                var result = new PrivacyToken();
                result.Bytes = bytes;
                result.Timestamp = timestamp;
                return result;
            }

            return null;
        }
    }
}
