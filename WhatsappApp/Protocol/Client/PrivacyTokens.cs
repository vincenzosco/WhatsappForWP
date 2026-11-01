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
        public string Jid;
    }

    public static class PrivacyTokens
    {
        public const string Namespace = "privacy";
        public const string Type = "trusted_contact";

        /// <summary>
        /// The privacy endpoint has accepted both spellings over time. Try the LID first
        /// when it is known, then the phone-number JID, without asking the same address twice.
        /// </summary>
        public static IList<string> QueryJids(string phoneJid, string lidJid)
        {
            var result = new List<string>();
            AddQueryJid(result, lidJid);
            AddQueryJid(result, phoneJid);
            return result;
        }

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

            return ReadFromChildren(answer);
        }

        /// <summary>Reads a token pushed in a privacy_token notification.</summary>
        public static PrivacyToken ReadNotification(BinaryNode notification)
        {
            if (notification == null) return null;

            PrivacyToken token = ReadFromChildren(notification);
            if (token == null) return null;
            if (string.IsNullOrEmpty(token.Jid)) token.Jid = notification.Attrs["from"];
            return token;
        }

        private static PrivacyToken ReadFromChildren(BinaryNode parent)
        {
            if (parent == null) return null;

            BinaryNode tokens = parent.Tag == "tokens" ? parent : parent.Child("tokens");
            if (tokens != null)
            {
                PrivacyToken token = ReadTokenList(tokens.Children());
                if (token != null) return token;
            }

            PrivacyToken direct = ReadToken(parent.Child("token"));
            if (direct != null) return direct;

            return ReadTokenList(parent.Children());
        }

        private static PrivacyToken ReadTokenList(IList<BinaryNode> children)
        {
            if (children == null) return null;

            for (int i = 0; i < children.Count; i++)
            {
                PrivacyToken token = ReadToken(children[i]);
                if (token != null) return token;
            }

            return null;
        }

        private static PrivacyToken ReadToken(BinaryNode token)
        {
            if (token == null || token.Tag != "token") return null;
            if (token.Attrs["type"] != Type) return null;

            byte[] bytes = token.Bytes();
            string timestamp = token.Attrs["t"];
            if (bytes == null || bytes.Length == 0 || string.IsNullOrEmpty(timestamp)) return null;

            var result = new PrivacyToken();
            result.Bytes = bytes;
            result.Timestamp = timestamp;
            result.Jid = token.Attrs["jid"];
            return result;
        }

        private static void AddQueryJid(IList<string> result, string jid)
        {
            if (string.IsNullOrEmpty(jid)) return;

            for (int i = 0; i < result.Count; i++)
            {
                if (string.Equals(result[i], jid, StringComparison.OrdinalIgnoreCase)) return;
            }

            result.Add(jid);
        }
    }
}
