using System.Collections.Generic;
using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// The rotation of the live tile: which conversations it cycles through.
    ///
    /// The bug these pin: the tile named one person and stayed on them until somebody
    /// else wrote. Windows rotates a tile through its notification queue, so the queue
    /// has to hold one entry per waiting conversation - and only one per conversation,
    /// or a person who writes twice takes two turns out of five and the count on screen
    /// belongs to a moment that has passed.
    /// </summary>
    public static class TileCycleTests
    {
        [Test]
        public static void EveryWaitingPersonGetsATurnOnTheTile()
        {
            IList<TileEntry> queue = new List<TileEntry>();

            queue = TileCycle.Remember(queue, Entry("anna@s.whatsapp.net", "Anna", "ciao"));
            queue = TileCycle.Remember(queue, Entry("bruno@s.whatsapp.net", "Bruno", "ci sei?"));
            queue = TileCycle.Remember(queue, Entry("carla@s.whatsapp.net", "Carla", "domani"));

            Assert.AreEqual(3, queue.Count, "three people waiting are three tiles to rotate");

            Assert.AreEqual("carla@s.whatsapp.net", queue[0].ChatId, "the newest came last and shows first");
            Assert.AreEqual("bruno@s.whatsapp.net", queue[1].ChatId);
            Assert.AreEqual("anna@s.whatsapp.net", queue[2].ChatId);
        }

        [Test]
        public static void TheSamePersonTwiceIsOneEntryMovedToTheFront()
        {
            IList<TileEntry> queue = new List<TileEntry>();

            queue = TileCycle.Remember(queue, Entry("anna@s.whatsapp.net", "Anna", "ciao"));
            queue = TileCycle.Remember(queue, Entry("bruno@s.whatsapp.net", "Bruno", "ehi"));
            queue = TileCycle.Remember(queue, Entry("anna@s.whatsapp.net", "Anna", "ci sei?"));

            Assert.AreEqual(2, queue.Count, "two conversations, not three turns");
            Assert.AreEqual("anna@s.whatsapp.net", queue[0].ChatId);
            Assert.AreEqual("ci sei?", queue[0].Body, "the newer message is the one the tile says");
            Assert.AreEqual("bruno@s.whatsapp.net", queue[1].ChatId);
        }

        [Test]
        public static void TheSixthConversationPushesOutTheOldest()
        {
            IList<TileEntry> queue = new List<TileEntry>();

            for (int i = 1; i <= TileCycle.MaxEntries + 1; i++)
            {
                queue = TileCycle.Remember(queue, Entry("c" + i + "@s.whatsapp.net", "C" + i, "m" + i));
            }

            Assert.AreEqual(TileCycle.MaxEntries, queue.Count,
                "five is what WP8.1's queue holds");
            Assert.AreEqual("c" + (TileCycle.MaxEntries + 1) + "@s.whatsapp.net", queue[0].ChatId);
            Assert.AreEqual("c2@s.whatsapp.net", queue[queue.Count - 1].ChatId,
                "the first conversation is the one that fell out");
        }

        [Test]
        public static void AReadChatLeavesTheQueue()
        {
            IList<TileEntry> queue = new List<TileEntry>();

            queue = TileCycle.Remember(queue, Entry("anna@s.whatsapp.net", "Anna", "ciao"));
            queue = TileCycle.Remember(queue, Entry("bruno@s.whatsapp.net", "Bruno", "ehi"));

            queue = TileCycle.Forget(queue, "anna@s.whatsapp.net");

            Assert.AreEqual(1, queue.Count, "a chat that was read is no longer waiting");
            Assert.AreEqual("bruno@s.whatsapp.net", queue[0].ChatId);
        }

        [Test]
        public static void ForgettingSomethingThatIsNotThereChangesNothing()
        {
            IList<TileEntry> queue = new List<TileEntry>();
            queue = TileCycle.Remember(queue, Entry("anna@s.whatsapp.net", "Anna", "ciao"));

            Assert.AreEqual(1, TileCycle.Forget(queue, "nessuno@s.whatsapp.net").Count);
            Assert.AreEqual(1, TileCycle.Forget(queue, null).Count, "no chat named means no match");
            Assert.AreEqual(0, TileCycle.Forget(null, "anna@s.whatsapp.net").Count,
                "a queue that was never filled is an empty queue");
        }

        private static TileEntry Entry(string chatId, string sender, string body)
        {
            var entry = new TileEntry();
            entry.ChatId = chatId;
            entry.Sender = sender;
            entry.Body = body;

            return entry;
        }
    }
}
