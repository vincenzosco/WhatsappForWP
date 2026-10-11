using System.Collections.Generic;
using WhatsappApp.Protocol;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The picture query, its setter and its reader.
    ///
    /// The reference for the shape (`@whiskeysockets/baileys`, `Socket/chats.js`):
    ///
    /// ```js
    /// // get, line 572
    /// const result = await query({
    ///     tag: 'iq',
    ///     attrs: { target: jid, to: S_WHATSAPP_NET, type: 'get', xmlns: 'w:profile:picture' },
    ///     content: buildProfilePictureQueryContent(type, tcTokenContent)   // [{ tag: 'picture', attrs: { type, query: 'url' } }]
    /// });
    /// const child = getBinaryNodeChild(result, 'picture');
    /// return child?.attrs?.url;
    ///
    /// // set, line 210
    /// await query({
    ///     tag: 'iq',
    ///     attrs: { to: S_WHATSAPP_NET, type: 'set', xmlns: 'w:profile:picture', ...(targetJid ? { target: targetJid } : {}) },
    ///     content: [{ tag: 'picture', attrs: { type: 'image' }, content: img }]
    /// });
    /// ```
    ///
    /// Two things in there are easy to get wrong by memory and are pinned here: the JID asked
    /// about is the **`target`** attribute (the `to` is the server), and the answer is a
    /// **URL** - `query="url"` is what asks for it - not the JPEG.
    /// </summary>
    public static class ProfilePictureTests
    {
        public const string Contact = "15551230000@s.whatsapp.net";

        [Test]
        public static void TheQueryIsThePictureIq()
        {
            BinaryNode query = ProfilePictures.Query("PIC00001", Contact, ProfilePictures.Preview);

            Assert.AreEqual("iq", query.Tag);
            Assert.AreEqual("get", query.Attrs["type"]);
            Assert.AreEqual("w:profile:picture", query.Attrs["xmlns"]);
            Assert.AreEqual(PairingSession.ServerJid, query.Attrs["to"], "the query goes to the server");
            Assert.AreEqual(Contact, query.Attrs["target"], "and names who it asks about in `target`");
            Assert.AreEqual("PIC00001", query.Attrs["id"]);

            IList<BinaryNode> children = query.Children();
            Assert.IsTrue(children != null && children.Count == 1, "one child, the picture");
            Assert.AreEqual("picture", children[0].Tag);
            Assert.AreEqual("preview", children[0].Attrs["type"]);
            Assert.AreEqual("url", children[0].Attrs["query"], "without query=url the answer is not a URL");

            Assert.Throws<ProtocolException>(delegate
            {
                ProfilePictures.Query("PIC00002", Contact, "full");
            });
        }

        [Test]
        public static void AnEmptyPictureIsNotAPicture()
        {
            var picture = new BinaryNode("picture");
            var answer = new BinaryNode("iq");
            answer.Attrs.Add("type", "result");
            answer.Content = new List<BinaryNode> { picture };

            Assert.IsTrue(ProfilePictures.Read(answer) == null,
                "a contact with no picture is the ordinary case: null, not an empty URL and not an exception");
        }

        [Test]
        public static void TheAnswerYieldsTheUrl()
        {
            var picture = new BinaryNode("picture");
            picture.Attrs.Add("url", "https://pps.whatsapp.net/v/t61/x.jpg");
            picture.Attrs.Add("id", "123");

            var answer = new BinaryNode("iq");
            answer.Attrs.Add("type", "result");
            answer.Content = new List<BinaryNode> { picture };

            Assert.AreEqual("https://pps.whatsapp.net/v/t61/x.jpg", ProfilePictures.Read(answer));
        }

        [Test]
        public static void ASettingNodeCarriesTheImage()
        {
            var jpeg = new byte[] { 0xff, 0xd8, 0xff };

            BinaryNode set = ProfilePictures.Encode("PIC00003", jpeg);

            Assert.AreEqual("set", set.Attrs["type"]);
            Assert.AreEqual(PairingSession.ServerJid, set.Attrs["to"]);

            IList<BinaryNode> children = set.Children();
            Assert.IsTrue(children != null && children.Count == 1, "one child, the picture");
            Assert.AreEqual("image", children[0].Attrs["type"]);

            byte[] carried = children[0].Bytes();
            Assert.IsTrue(carried != null && carried.Length == jpeg.Length,
                "the JPEG itself is the child's content: the one place the bytes travel in the node");
            Assert.ByteArraysEqual(jpeg, carried);

            Assert.Throws<ProtocolException>(delegate { ProfilePictures.Encode("PIC00004", null); });
        }

        [Test]
        public static void AnAnswerWithNoPictureIsRefusedByName()
        {
            var answer = new BinaryNode("iq");
            answer.Attrs.Add("type", "error");

            ProtocolException failed = null;

            try
            {
                ProfilePictures.Read(answer);
            }
            catch (ProtocolException error)
            {
                failed = error;
            }

            Assert.IsTrue(failed != null, "an answer that carries no picture node is not an answer");
            Assert.IsTrue(failed.Message.Contains("picture"), "<" + failed.Message + ">");
        }
    }
}
