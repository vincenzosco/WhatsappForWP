using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Contact and group pictures: the query that asks for one, the node that sets one, and
    /// the reader of the answer.
    ///
    /// Both references agree on the shape and it is worth writing down because the `to` and
    /// the `target` are the opposite way round from every other query in this client
    /// (`@whiskeysockets/baileys`, `Socket/chats.js`, `profilePictureUrl` at 556 and
    /// `updateProfilePicture` at 205; `whatsmeow`, `user.go`, `GetProfilePictureInfo` at 548):
    ///
    ///     iq attrs: { to: "s.whatsapp.net", target: &lt;jid&gt;, type: "get"|"set",
    ///                 xmlns: "w:profile:picture" }
    ///     content:  picture { type: "preview"|"image", query: "url" }
    ///
    /// The `picture` child carries `type` - `preview` for a small one the chat list can keep
    /// for every row, `image` for the full one behind a contact's page - and `query="url"`,
    /// which is what makes the answer a **URL** and not the JPEG itself. The URL is signed
    /// and expires, so it is fetched at once and never stored (see `AvatarCache`, which keeps
    /// the bytes).
    ///
    /// "No picture" is an answer too: the server sends a `picture` node with no `url`, and
    /// that is null here, not an empty array and not an exception. A contact with no picture
    /// is the ordinary case, not a failure.
    /// </summary>
    public static class ProfilePictures
    {
        /// <summary>The namespace of the picture queries.</summary>
        public const string NameSpace = "w:profile:picture";

        /// <summary>The small picture, for the list rows.</summary>
        public const string Preview = "preview";

        /// <summary>The full picture, for a contact's own page.</summary>
        public const string Image = "image";

        /// <summary>
        /// Asks for somebody's picture. `type` is <see cref="Preview"/> or
        /// <see cref="Image"/> and nothing else: the server answers a `type` it does not
        /// know with an empty picture, which reads here as "no picture" and would hide a
        /// typo for as long as nobody looked.
        /// </summary>
        public static BinaryNode Query(string id, string jid, string type)
        {
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a picture query needs an id to be answered by");
            if (string.IsNullOrEmpty(jid)) throw new ProtocolException("a picture query needs somebody to ask about");
            Check(type);

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("id", id);
            iq.Attrs.Add("type", "get");
            iq.Attrs.Add("xmlns", NameSpace);
            iq.Attrs.Add("to", PairingSession.ServerJid);
            iq.Attrs.Add("target", jid);

            var picture = new BinaryNode("picture");
            picture.Attrs.Add("type", type);
            picture.Attrs.Add("query", "url");

            iq.Content = new List<BinaryNode> { picture };

            return iq;
        }

        /// <summary>
        /// Sets our own picture: a `set` whose `picture` child is the JPEG itself, with
        /// `type="image"` (`updateProfilePicture`, `chats.js:218`) - the one place the bytes
        /// travel in the node rather than behind a URL.
        /// </summary>
        public static BinaryNode Encode(string id, byte[] jpeg)
        {
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a picture update needs an id to be answered by");
            if (jpeg == null || jpeg.Length == 0) throw new ProtocolException("there is no picture to set");

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("id", id);
            iq.Attrs.Add("type", "set");
            iq.Attrs.Add("xmlns", NameSpace);
            iq.Attrs.Add("to", PairingSession.ServerJid);

            var picture = new BinaryNode("picture");
            picture.Attrs.Add("type", Image);
            picture.Content = jpeg;

            iq.Content = new List<BinaryNode> { picture };

            return iq;
        }

        /// <summary>
        /// Where the picture is, or null when there is none. Null and not an empty string:
        /// the caller's next step is a download, and a download of nothing is a failure the
        /// log would have to explain.
        /// </summary>
        public static string Read(BinaryNode answer)
        {
            if (answer == null) throw new ProtocolException("the server answered nothing to the picture query");

            BinaryNode picture = answer.Child("picture");
            if (picture == null)
            {
                throw new ProtocolException("the picture query answer carries no picture: " + answer);
            }

            string url = picture.Attrs["url"];

            return string.IsNullOrEmpty(url) ? null : url;
        }

        private static void Check(string type)
        {
            if (type == Preview || type == Image) return;

            throw new ProtocolException("a picture is asked for as \"" + Preview + "\" or \"" + Image
                + "\", not \"" + type + "\"");
        }
    }
}
