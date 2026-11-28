using System;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    /// <summary>
    /// The modern content arrives wrapped. A photo or a document is a `FutureProofMessage`
    /// around the real message - `ephemeralMessage` (field 40), `viewOnceMessage` (37),
    /// `documentWithCaptionMessage` (53), `viewOnceMessageV2` (55), `viewOnceMessageV2Extension`
    /// (59), `editedMessage` (58), `associatedChildMessage` (91), `groupStatusMessage` (96) and
    /// `groupStatusMessageV2` (103) all carry `message = 1` inside - and it is the wrapper that
    /// travels in a plain `Message`. The reference client unwraps them before it reads anything
    /// (`normalizeMessageContent`, `src/Utils/messages.ts`). A decoder that only reads the flat
    /// fields finds no media in a wrapped one: the status that carries a photo is the case the
    /// user saw, an empty screen with no log line at all.
    /// </summary>
    public static class MessageWrapperTests
    {
        [Test]
        public static void APhotoInsideAnEphemeralWrapperIsFound()
        {
            Message message = Message.Decode(Wrap(40, Photo()));

            Assert.IsTrue(message.ImageMessage != null, "the photo is behind the wrapper");
            MediaInfo media = MediaMessages.Of(message);
            Assert.IsTrue(media != null, "and the media walk finds it");
            Assert.IsTrue(media.IsFetchable, "with its key and its place to go");
        }

        [Test]
        public static void ViewOnceWrappersKeepTheirIdentityAfterUnwrapping()
        {
            int[] viewOnceWrappers = { 37, 55, 59 };
            for (int i = 0; i < viewOnceWrappers.Length; i++)
            {
                Message message = Message.Decode(Wrap(viewOnceWrappers[i], Photo()));
                Assert.IsTrue(message.IsViewOnce,
                    "view-once wrapper field " + viewOnceWrappers[i] + " remains identifiable");
                Assert.IsTrue(MediaMessages.Of(message) != null,
                    "the ordinary media decoder still sees wrapped media");
            }

            Message ordinary = Message.Decode(Wrap(40, Photo()));
            Assert.IsFalse(ordinary.IsViewOnce, "ephemeral media is not view-once media");
        }

        [Test]
        public static void EveryWrapperOfTheReferenceListIsLookedInside()
        {
            int[] wrappers = { 37, 40, 53, 55, 58, 59, 91, 96, 103 };
            for (int i = 0; i < wrappers.Length; i++)
            {
                Message message = Message.Decode(Wrap(wrappers[i], Photo()));
                Assert.IsTrue(message.ImageMessage != null,
                    "the photo behind wrapper field " + wrappers[i] + " is found");
            }
        }

        [Test]
        public static void WrappersNestInsideEachOtherAndInsideADeviceSent()
        {
            // The camera app sends exactly this: a view-once photo inside an ephemeral
            // inside the device-sent envelope. Every layer is another look inside.
            byte[] nested = Wrap(40, Wrap(37, Photo()));
            byte[] sent = new ProtoWriter().Bytes(2, nested).ToArray();
            byte[] wire = new ProtoWriter().Bytes(31, sent).ToArray();

            Message outer = Message.Decode(wire);
            Message message = Message.Inner(outer.DeviceSentMessage);

            Assert.IsTrue(message.ImageMessage != null, "three layers down there is still a photo");
        }

        [Test]
        public static void TheWordsOfAWrappedMessageAreStillItsWords()
        {
            byte[] words = new ProtoWriter().Text(1, "buona sera").ToArray();

            Message message = Message.Decode(Wrap(40, words));

            Assert.AreEqual("buona sera", message.Body(),
                "a conversation inside a wrapper is still the conversation");
        }

        [Test]
        public static void AnUnknownWrapperIsSkippedAndTheRestOfTheMessageIsRead()
        {
            // A wrapper this client does not know (field 99, the limit-sharing one) must
            // not swallow the message around it - the same way an unknown flat field does
            // not.
            byte[] wire = new ProtoWriter()
                .Bytes(99, new ProtoWriter().Bytes(1, Photo()).ToArray())
                .Text(1, "ancora qui")
                .ToArray();

            Message message = Message.Decode(wire);

            Assert.IsTrue(message.ImageMessage == null, "the unknown wrapper is not read as content");
            Assert.AreEqual("ancora qui", message.Body(), "and the words beside it survive");
        }

        [Test]
        public static void WhatIsStoredAndReencodedIsTheUnwrappedMessage()
        {
            // The row is kept as the decode found it (`post.Encoded`), so what the app
            // writes back has to be the content itself - a re-encoded wrapped message would
            // store the wrapper as if it were the message.
            Message decoded = Message.Decode(Wrap(40, Photo()));

            Message again = Message.Decode(decoded.Encode());

            Assert.IsTrue(again.ImageMessage != null, "the photo is in the row and comes back");
        }

        private static byte[] Wrap(int field, byte[] inner)
        {
            // `FutureProofMessage { message = 1 }` around the content, in a `Message` field.
            byte[] futureProof = new ProtoWriter().Bytes(1, inner).ToArray();
            return new ProtoWriter().Bytes(field, futureProof).ToArray();
        }

        private static byte[] Photo()
        {
            byte[] mediaKey = new byte[32];
            for (int i = 0; i < mediaKey.Length; i++) mediaKey[i] = (byte)(i + 1);

            // url 1, media key 8, direct path 11: what `MediaMessages` reads to fetch it.
            byte[] image = new ProtoWriter()
                .Bytes(1, new byte[] { 0x68, 0x74, 0x74, 0x70 })
                .Bytes(8, mediaKey)
                .Bytes(11, new byte[] { 0x2f, 0x78 })
                .ToArray();

            // A `Message` carrying that photo in its field 3: what a wrapper wraps.
            return new ProtoWriter().Bytes(3, image).ToArray();
        }
    }
}