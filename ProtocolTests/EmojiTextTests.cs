using System;
using System.Collections.Generic;
using WhatsappApp.Protocol.Client;

namespace ProtocolTests
{
    /// <summary>
    /// The emoji rule: what this phone draws, what it is given instead, and what goes.
    ///
    /// Why these cases: every one of them is a row of the account this was written for -
    /// `Benny🩵`, `robotica🦾🦾🦿`, `I bro di hip hop 🪐✨🪐` - and the rule exists because
    /// those code points are ones the font of Windows Phone 8.1 has never had a glyph for.
    /// </summary>
    public static class EmojiTextTests
    {
        /// <summary>Text without emoji is not copied: it is the same string, handed back.</summary>
        [Test]
        public static void PlainTextComesBackAsItIs()
        {
            const string text = "ci vediamo alle 8, porta il pane";

            Assert.IsTrue(ReferenceEquals(text, EmojiText.Compatible(text)),
                "a message with no emoji allocated a second string for every row of the list");
        }

        /// <summary>An emoji this phone has is left alone: the rule is not a filter.</summary>
        [Test]
        public static void AnOldEmojiIsKept()
        {
            // Every one of these is 6.0: pizza, bikini, tongue, moai, grin.
            const string text = "Calcio \U0001F355\U0001F459\U0001F61C\U0001F5FF\U0001F600";

            Assert.AreEqual(text, EmojiText.Compatible(text));
        }

        /// <summary>The two that were boxes on the phone, and what they become.</summary>
        [Test]
        public static void TheNewestEmojiBecomeTheOlderOnes()
        {
            Assert.AreEqual("Benny\U0001F499", EmojiText.Compatible("Benny\U0001FA75"),
                "the light blue heart of 15.0 has no glyph here; the blue heart of 6.0 does");

            Assert.AreEqual("robotica\U0001F4AA\U0001F4AA\U0001F463",
                EmojiText.Compatible("robotica\U0001F9BE\U0001F9BE\U0001F9BF"));
        }

        /// <summary>A ringed planet is a planet with a ring this font cannot draw.</summary>
        [Test]
        public static void TheRingedPlanetBecomesTheGlobe()
        {
            Assert.AreEqual("I bro di hip hop \U0001F30D\u2728\U0001F30D",
                EmojiText.Compatible("I bro di hip hop \U0001FA90\u2728\U0001FA90"));
        }

        /// <summary>
        /// An emoji with no older cousin is taken out. That is the one case where the message
        /// loses something, and what it loses is a box: the troll of 14.0 was never drawn here.
        /// </summary>
        [Test]
        public static void AnEmojiWithNoOlderCousinIsDropped()
        {
            Assert.AreEqual("ciao ", EmojiText.Compatible("ciao \U0001F9CC"));
        }

        /// <summary>
        /// A joiner is what makes one glyph out of several, and no font older than 9.0 has
        /// one: without it the family is three emoji, and those are drawn.
        /// </summary>
        [Test]
        public static void AJoinedSequenceBecomesItsParts()
        {
            Assert.AreEqual("\U0001F468\U0001F469\U0001F467",
                EmojiText.Compatible("\U0001F468\u200D\U0001F469\u200D\U0001F467"));
        }

        /// <summary>A skin tone is a modifier, and the font has none: the person stays.</summary>
        [Test]
        public static void ASkinToneIsDroppedAndTheHandStays()
        {
            Assert.AreEqual("\U0001F44D", EmojiText.Compatible("\U0001F44D\U0001F3FD"));
        }

        /// <summary>
        /// Half a surrogate is not a character: it used to reach the renderer as the
        /// replacement mark, and the message read as a broken one.
        /// </summary>
        [Test]
        public static void HalfASurrogateGoes()
        {
            Assert.AreEqual("ciao", EmojiText.Compatible("ciao\uD83D"));
            Assert.AreEqual("ciao", EmojiText.Compatible("ciao\uDE00"));
        }

        /// <summary>
        /// The rule of the table: every replacement is an emoji this phone draws, so that
        /// applying the rule to a replacement answers the replacement itself. A replacement
        /// that needed replacing would be a box in place of a box.
        /// </summary>
        [Test]
        public static void EveryReplacementIsAnEmojiThisPhoneDraws()
        {
            IList<KeyValuePair<int, string>> entries = EmojiText.Entries();
            Assert.IsTrue(entries.Count > 100, "the table is the feature: it cannot be a stub");

            for (int i = 0; i < entries.Count; i++)
            {
                int codePoint = entries[i].Key;
                string older = entries[i].Value;

                Assert.AreEqual(older, EmojiText.Compatible(older),
                    "the replacement of U+" + codePoint.ToString("X")
                    + " is not an emoji this phone draws, so the box only moved");
            }
        }

        /// <summary>
        /// The emoticons, which is the other half of the request of 29 October 2026:
        /// a `:)` typed on a keyboard that does not make faces still leaves as a face.
        /// </summary>
        [Test]
        public static void ATypedSmileBecomesTheFaceTheKeyboardWouldHaveMade()
        {
            Assert.AreEqual("ciao \U0001F60A", EmojiText.Emoticons("ciao :)"));
            Assert.AreEqual("ciao \U0001F60A", EmojiText.Emoticons("ciao :-)"));
            Assert.AreEqual("\U0001F622", EmojiText.Emoticons(":'("));
            Assert.AreEqual("\U0001F609", EmojiText.Emoticons(";)"));
            Assert.AreEqual("\U0001F61E", EmojiText.Emoticons(":("));
            Assert.AreEqual("\U0001F604", EmojiText.Emoticons(":D"));
            Assert.AreEqual("\U0001F61B", EmojiText.Emoticons(":P"));
            Assert.AreEqual("\U0001F62E", EmojiText.Emoticons(":O"));
            Assert.AreEqual("\U0001F610", EmojiText.Emoticons(":|"));
            Assert.AreEqual("\u2764\uFE0F", EmojiText.Emoticons("<3"));
            Assert.AreEqual("\U0001F494", EmojiText.Emoticons("</3"));
        }

        /// <summary>
        /// Where people actually put them: at the end of a word with no space, which is why
        /// nothing is asked of what comes before the colon, and inside a sentence with a
        /// period right after, which is why the word in front of a face is what resumes.
        /// </summary>
        [Test]
        public static void ASmileCanCloseTheSentenceOrTheWord()
        {
            Assert.AreEqual("ci vediamo\U0001F60A", EmojiText.Emoticons("ci vediamo:)"));
            Assert.AreEqual("va bene \U0001F60A, ci sentiamo",
                EmojiText.Emoticons("va bene :), ci sentiamo"));
            Assert.AreEqual("perfetto!\U0001F604!", EmojiText.Emoticons("perfetto!:D!"));
        }

        /// <summary>
        /// And where they are not emoticons: a word that begins with `:D`, a time, a link.
        /// </summary>
        [Test]
        public static void AWordIsNotAFace()
        {
            Assert.AreEqual(":Domani parto", EmojiText.Emoticons(":Domani parto"));
            Assert.AreEqual("alle 10:30", EmojiText.Emoticons("alle 10:30"));
            Assert.AreEqual("https://example.com/a:b", EmojiText.Emoticons("https://example.com/a:b"));
            Assert.AreEqual("C:\\Users\\me", EmojiText.Emoticons("C:\\Users\\me"));
        }

        /// <summary>Text with nothing an emoticon could match is handed back, not copied.</summary>
        [Test]
        public static void TextWithNoEmoticonIsNotCopied()
        {
            const string text = "ci vediamo alle 8, porta il pane";

            Assert.IsTrue(ReferenceEquals(text, EmojiText.Emoticons(text)),
                "a message with no emoticon allocated a second string on every send");
        }

        /// <summary>
        /// The rule of the emoticon table, the same one the emoji table has: every face on
        /// the right is one this phone draws, and one that is already a face stays itself -
        /// so the conversion can run twice and nothing moves.
        /// </summary>
        [Test]
        public static void EveryFaceIsOneThisPhoneDrawsAndTheConversionIsIdempotent()
        {
            IList<KeyValuePair<string, string>> faces = EmojiText.EmoticonEntries();
            Assert.IsTrue(faces.Count > 10, "the emoticons are the feature: they cannot be a stub");

            for (int i = 0; i < faces.Count; i++)
            {
                string face = faces[i].Value;

                Assert.AreEqual(face, EmojiText.Compatible(face),
                    "the face for " + faces[i].Key + " is one this phone has no glyph for");

                Assert.AreEqual(face, EmojiText.Emoticons(face),
                    "the face for " + faces[i].Key + " is changed again by a second pass");
            }
        }

        /// <summary>
        /// And what the table is keyed on is an emoji the phone cannot draw: a key the rule
        /// would keep is a line that never runs and an emoji that showed as a box.
        /// </summary>
        [Test]
        public static void EveryKeyIsAnEmojiThisPhoneLacks()
        {
            IList<KeyValuePair<int, string>> entries = EmojiText.Entries();

            for (int i = 0; i < entries.Count; i++)
            {
                string self = char.ConvertFromUtf32(entries[i].Key);

                Assert.IsFalse(Equals(self, EmojiText.Compatible(self)),
                    "U+" + entries[i].Key.ToString("X") + " is in the table and is drawn by "
                    + "this phone: the line replaces an emoji that was fine");
            }
        }
    }
}
