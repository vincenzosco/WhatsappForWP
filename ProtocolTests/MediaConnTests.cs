using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The media query and its answer, against the reference's own shape.
    ///
    /// `@whiskeysockets/baileys`, `Socket/messages-send.js`, lines 40 to 57:
    ///
    /// ```js
    /// const result = await query({
    ///     tag: 'iq',
    ///     attrs: { type: 'set', xmlns: 'w:m', to: S_WHATSAPP_NET },
    ///     content: [{ tag: 'media_conn', attrs: {} }]
    /// });
    /// const mediaConnNode = getBinaryNodeChild(result, 'media_conn');
    /// const node = {
    ///     hosts: getBinaryNodeChildren(mediaConnNode, 'host').map(({ attrs }) => ({
    ///         hostname: attrs.hostname,
    ///         maxContentLengthBytes: +attrs.maxContentLengthBytes
    ///     })),
    ///     auth: mediaConnNode.attrs.auth,
    ///     ttl: +mediaConnNode.attrs.ttl,
    ///     fetchDate: new Date()
    /// };
    /// ```
    ///
    /// Two things in there are worth pinning and are easy to get wrong by memory: the query
    /// is a **`set`** (not a `get`, which is what every other `iq` in this client is), and
    /// the size ceiling is on each **`host`**, not on the `media_conn` node.
    /// </summary>
    public static class MediaConnTests
    {
        [Test]
        public static void TheQueryIsTheIqTheServerAnswers()
        {
            BinaryNode query = MediaConn.Query("MEDIA0001");

            Assert.AreEqual("iq", query.Tag);
            Assert.AreEqual("set", query.Attrs["type"], "the media query is a set, as the reference sends it");
            Assert.AreEqual("w:m", query.Attrs["xmlns"]);
            Assert.AreEqual("MEDIA0001", query.Attrs["id"], "the id is what the answer is waited on");
            Assert.AreEqual(PairingSession.ServerJid, query.Attrs["to"]);

            IList<BinaryNode> children = query.Children();
            Assert.IsTrue(children != null && children.Count == 1, "one child, the media_conn");
            Assert.AreEqual("media_conn", children[0].Tag);

            Assert.Throws<ProtocolException>(delegate { MediaConn.Query(null); });
        }

        [Test]
        public static void TheAnswerCarriesHostAuthAndLimit()
        {
            MediaConnInfo info = MediaConn.Read(Answer());

            Assert.AreEqual("tok3n", info.Auth);
            Assert.AreEqual(3L, info.Ttl);
            Assert.IsTrue(info.Hosts.Count == 2, "two hosts, in the order the server named them");
            Assert.AreEqual("media-a.whatsapp.net", info.Hosts[0]);
            Assert.AreEqual("media-b.whatsapp.net", info.Hosts[1]);
            Assert.AreEqual(8000000L, info.MaxContentLength,
                "the smallest ceiling wins: sending the larger one gets a file refused by the host that named the smaller");
        }

        [Test]
        public static void AConnectionWithoutATokenIsRefusedByName()
        {
            BinaryNode answer = Answer();
            answer.Child("media_conn").Attrs.Remove("auth");

            ProtocolException failed = null;

            try
            {
                MediaConn.Read(answer);
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "a tokenless media_conn is not a host list");
            Assert.IsTrue(failed.Message.Contains("auth"), "and the failure names the missing token: <" + failed.Message + ">");
        }

        [Test]
        public static void AConnectionWithoutAHostIsRefusedByName()
        {
            BinaryNode answer = Answer();
            IList<BinaryNode> children = answer.Child("media_conn").Children();

            for (int i = children.Count - 1; i >= 0; i--)
            {
                if (children[i].Tag == "host") children.RemoveAt(i);
            }

            ProtocolException failed = null;

            try
            {
                MediaConn.Read(answer);
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "an upload with nowhere to go is refused before anything is encrypted");
            Assert.IsTrue(failed.Message.Contains("host"), "and the sentence names the host: <" + failed.Message + ">");
        }

        /// <summary>The answer as the server sends it: a token, a ttl and two hosts with their own ceilings.</summary>
        private static BinaryNode Answer()
        {
            var conn = new BinaryNode("media_conn");
            conn.Attrs.Add("auth", "tok3n");
            conn.Attrs.Add("ttl", "3");

            var small = new BinaryNode("host");
            small.Attrs.Add("hostname", "media-a.whatsapp.net");
            small.Attrs.Add("maxContentLengthBytes", "8000000");

            var large = new BinaryNode("host");
            large.Attrs.Add("hostname", "media-b.whatsapp.net");
            large.Attrs.Add("maxContentLengthBytes", "64000000");

            conn.Content = new List<BinaryNode> { small, large };

            var answer = new BinaryNode("iq");
            answer.Attrs.Add("type", "result");
            answer.Content = new List<BinaryNode> { conn };

            return answer;
        }
    }
}
