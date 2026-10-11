using System.Collections.Generic;
using WhatsappApp.Protocol.WABinary;

namespace WhatsappApp.Protocol.Client
{
    /// <summary>
    /// Where an upload may go, and with what. WhatsApp answers the media query with one
    /// `media_conn` node: an `auth` token, a `ttl` in seconds telling how long that token
    /// lives, and one `host` child per host that accepts the file, each with its own
    /// maximum size.
    ///
    /// The reference reads exactly those four things (`@whiskeysockets/baileys`,
    /// `Socket/messages-send.js`, lines 40 to 57: `hosts`, `auth`, `ttl`, `fetchDate`; the
    /// size is `maxContentLengthBytes` on each `host`).
    /// </summary>
    public sealed class MediaConnInfo
    {
        /// <summary>The bearer token the upload carries as `?auth=`.</summary>
        public string Auth;

        /// <summary>The largest file any host accepts. The smallest host's ceiling wins.</summary>
        public long MaxContentLength;

        /// <summary>How long the token lives, in seconds.</summary>
        public long Ttl;

        /// <summary>The hosts, in the order the server named them.</summary>
        public IList<string> Hosts = new List<string>();
    }

    /// <summary>
    /// The `iq` that asks for an upload host, and the reader of its answer.
    ///
    /// The query is `type="set"` with `xmlns="w:m"` to `s.whatsapp.net` and a single
    /// `media_conn` child - a `set` and not a `get`, which is the reference's shape
    /// (`Socket/messages-send.js:44-49`) and not a typo.
    /// </summary>
    public static class MediaConn
    {
        /// <summary>The namespace of the media query. Also the guard for the answer.</summary>
        public const string NameSpace = "w:m";

        /// <summary>
        /// The query, with the id the caller waits on: the answer comes back on the same
        /// socket as the messages, so the id is the only thing that ties one to the other.
        /// </summary>
        public static BinaryNode Query(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ProtocolException("a media query needs an id to be answered by");

            var iq = new BinaryNode("iq");
            iq.Attrs.Add("id", id);
            iq.Attrs.Add("type", "set");
            iq.Attrs.Add("xmlns", NameSpace);
            iq.Attrs.Add("to", PairingSession.ServerJid);

            iq.Content = new List<BinaryNode> { new BinaryNode("media_conn") };

            return iq;
        }

        /// <summary>
        /// The answer: the token, the ceilings and the hosts. A `media_conn` with no `auth`
        /// is refused by name rather than carried on as an empty token, because the upload
        /// that followed would fail at the far end with a sentence nobody can act on.
        /// </summary>
        public static MediaConnInfo Read(BinaryNode answer)
        {
            if (answer == null) throw new ProtocolException("the server answered nothing to the media query");

            BinaryNode conn = answer.Child("media_conn");
            if (conn == null)
            {
                throw new ProtocolException("the media query answer carries no media_conn: " + answer);
            }

            string auth = conn.Attrs["auth"];
            if (string.IsNullOrEmpty(auth))
            {
                throw new ProtocolException("the media_conn node carries no auth token: " + conn);
            }

            var info = new MediaConnInfo();
            info.Auth = auth;
            info.Ttl = Number(conn.Attrs["ttl"]);

            IList<BinaryNode> children = conn.Children();
            if (children != null)
            {
                for (int i = 0; i < children.Count; i++)
                {
                    if (children[i].Tag != "host") continue;

                    string hostname = children[i].Attrs["hostname"];
                    if (string.IsNullOrEmpty(hostname)) continue;

                    info.Hosts.Add(hostname);

                    long max = Number(children[i].Attrs["maxContentLengthBytes"]);
                    if (max <= 0) continue;

                    // The smallest ceiling is the one every host has to accept: taking the
                    // largest would send a file one host refuses and turn a working upload
                    // into a retry.
                    if (info.MaxContentLength == 0 || max < info.MaxContentLength) info.MaxContentLength = max;
                }
            }

            if (info.Hosts.Count == 0)
            {
                throw new ProtocolException("the media_conn node names no upload host: " + conn);
            }

            return info;
        }

        private static long Number(string text)
        {
            long value;
            if (string.IsNullOrEmpty(text) || !long.TryParse(text, out value)) return 0;

            return value;
        }
    }
}
