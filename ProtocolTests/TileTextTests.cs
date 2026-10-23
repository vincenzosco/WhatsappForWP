using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// The two lines a tile has room for: the person on the large line and the message under
    /// it - or how much is waiting when there is nothing to quote.
    ///
    /// The bug these pin: the tile of 23 October 2026 put the count on the large line and the
    /// name under it, so the message never reached a 150 px tile and every turn of the queue
    /// read the same, whatever had been said.
    /// </summary>
    public static class TileTextTests
    {
        [Test]
        public static void ThePersonIsTheLargeLineAndTheMessageIsUnderIt()
        {
            TileLines lines = TileText.For("Sasha", "ci vediamo alle otto?", "6 unread messages");

            Assert.AreEqual("Sasha", lines.First, "the name takes the large line");
            Assert.AreEqual("ci vediamo alle otto?", lines.Second, "and the message the wrapping one");
        }

        [Test]
        public static void WithNothingSaidTheSecondLineIsHowMuchIsWaiting()
        {
            TileLines lines = TileText.For("Sasha", "", "6 unread messages");

            Assert.AreEqual("Sasha", lines.First);
            Assert.AreEqual("6 unread messages", lines.Second,
                "a chat with no preview still says what it is worth opening for");
        }

        [Test]
        public static void WithNobodyNamedTheCountTakesTheLargeLineAndOnlyOnce()
        {
            TileLines lines = TileText.For(null, null, "2 unread messages");

            Assert.AreEqual("2 unread messages", lines.First, "the count is the only true line");
            Assert.AreEqual("", lines.Second, "and it is not said twice");
        }

        [Test]
        public static void ANameAndAMessageAreCutToWhatTheTemplateHolds()
        {
            string name = new string('n', TileText.NameLength + 10);
            string body = new string('b', TileText.BodyLength + 10);

            TileLines lines = TileText.For(name, body, "1 unread message");

            Assert.AreEqual(TileText.NameLength, lines.First.Length, "the large line stops at the limit");
            Assert.AreEqual(TileText.BodyLength, lines.Second.Length, "and so does the wrapping one");
            Assert.IsTrue(lines.First.EndsWith("\u2026"), "with an ellipsis of ours, not half a word");
            Assert.IsTrue(lines.Second.EndsWith("\u2026"));
        }

        [Test]
        public static void AShortLineIsLeftExactlyAsItIs()
        {
            TileLines lines = TileText.For("Anna", "ciao", "1 unread message");

            Assert.AreEqual("Anna", lines.First);
            Assert.AreEqual("ciao", lines.Second);
        }
    }
}
