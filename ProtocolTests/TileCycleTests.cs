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

        /// <summary>
        /// The turns the queue holds: the message of each person who wrote, and - every so
        /// often - the picture alone of somebody still unanswered.
        ///
        /// Why: every turn used to be the same shape, a count over a name, so the tile read
        /// the same whichever way it landed. The request of 23 October 2026 was the message
        /// with the person, and a bare face between them.
        /// </summary>
        [Test]
        public static void AMessageTurnIsFollowedByThePictureOfTheSamePerson()
        {
            IList<TileEntry> queue = TileCycle.Remember(null, Entry("anna@s.whatsapp.net", "Anna", "ciao", "tile-anna.jpg"));

            IList<TileEntry> turns = TileCycle.Turns(queue, TileCycle.MaxEntries);

            Assert.AreEqual(2, turns.Count, "one message and one picture of the same person");
            Assert.AreEqual("ciao", turns[0].Body, "the message comes first");
            Assert.IsTrue(!turns[0].PictureOnly);
            Assert.AreEqual("anna@s.whatsapp.net", turns[1].ChatId, "the picture is the same conversation");
            Assert.IsTrue(turns[1].PictureOnly, "and carries no text");
        }

        [Test]
        public static void AConversationWithNoPictureGetsNoPictureTurn()
        {
            IList<TileEntry> queue = TileCycle.Remember(null, Entry("anna@s.whatsapp.net", "Anna", "ciao", null));

            IList<TileEntry> turns = TileCycle.Turns(queue, TileCycle.MaxEntries);

            Assert.AreEqual(1, turns.Count, "the app mark alone is not a face");
            Assert.IsTrue(!turns[0].PictureOnly);
        }

        [Test]
        public static void EveryMessageClaimsItsSlotBeforeAnyPicture()
        {
            IList<TileEntry> queue = new List<TileEntry>();
            for (int i = 1; i <= TileCycle.MaxEntries; i++)
            {
                queue = TileCycle.Remember(queue, Entry("c" + i + "@s.whatsapp.net", "C" + i, "m" + i, "tile-c" + i + ".jpg"));
            }

            IList<TileEntry> turns = TileCycle.Turns(queue, TileCycle.MaxEntries);

            Assert.AreEqual(TileCycle.MaxEntries, turns.Count, "five is the whole queue");
            for (int i = 0; i < turns.Count; i++)
            {
                Assert.IsTrue(!turns[i].PictureOnly, "with five people waiting, every turn names one");
            }
        }

        [Test]
        public static void AFreeSlotBecomesAPictureAndNobodyIsDropped()
        {
            IList<TileEntry> queue = new List<TileEntry>();
            for (int i = 1; i <= 4; i++)
            {
                queue = TileCycle.Remember(queue, Entry("c" + i + "@s.whatsapp.net", "C" + i, "m" + i, "tile-c" + i + ".jpg"));
            }

            IList<TileEntry> turns = TileCycle.Turns(queue, TileCycle.MaxEntries);

            Assert.AreEqual(TileCycle.MaxEntries, turns.Count, "the queue is full");
            Assert.IsTrue(turns[1].PictureOnly, "the newest conversation gets the spare slot, after its message");

            // Four people, four messages: the picture took the fifth slot and not one of theirs.
            var named = new List<string>();
            for (int i = 0; i < turns.Count; i++)
            {
                if (!turns[i].PictureOnly && !named.Contains(turns[i].ChatId)) named.Add(turns[i].ChatId);
            }
            Assert.AreEqual(4, named.Count, "every conversation is still on the tile");
        }

        [Test]
        public static void TheTurnsLeaveTheWaitingListAsItWas()
        {
            IList<TileEntry> queue = TileCycle.Remember(null, Entry("anna@s.whatsapp.net", "Anna", "ciao", "tile-anna.jpg"));

            TileCycle.Turns(queue, TileCycle.MaxEntries);

            Assert.IsTrue(!queue[0].PictureOnly, "the list holds conversations, not the turns drawn from them");
        }

        /// <summary>
        /// The updates nobody has watched get one turn, in front, and it costs the messages
        /// exactly one slot: four people waiting are still four turns beside it.
        /// </summary>
        [Test]
        public static void TheUpdatesTakeOneSlotAndComeFirst()
        {
            var waiting = new List<TileEntry>();
            for (int i = 0; i < TileCycle.MaxEntries; i++)
            {
                waiting.Add(Entry("c" + i + "@s.whatsapp.net", "Persona " + i, "ciao"));
            }

            var statuses = new TileEntry();
            statuses.Sender = "Stati";
            statuses.Body = "3 nuovi stati";

            // What NotificationService asks for when there is a status turn: one slot less
            // for the messages, then the turn in front.
            IList<TileEntry> turns = TileCycle.WithStatuses(
                TileCycle.Turns(waiting, TileCycle.MaxEntries - 1), statuses, TileCycle.MaxEntries);

            Assert.AreEqual(TileCycle.MaxEntries, turns.Count, "the queue is still five turns wide");
            Assert.AreEqual("Stati", turns[0].Sender, "the updates are the first thing the tile says");
            Assert.AreEqual("Persona 0", turns[1].Sender, "and the newest person is still there");
            Assert.AreEqual("Persona 3", turns[turns.Count - 1].Sender, "the last slot is the fourth person, not a gap");
        }

        /// <summary>
        /// Nothing unwatched: the turns are handed back as they came, so the ordinary draw
        /// does not pay for a copy it does not need.
        /// </summary>
        [Test]
        public static void NoUpdatesLeavesTheTurnsAlone()
        {
            var waiting = TileCycle.Remember(null, Entry("anna@s.whatsapp.net", "Anna", "ciao"));
            IList<TileEntry> turns = TileCycle.Turns(waiting, TileCycle.MaxEntries);

            IList<TileEntry> same = TileCycle.WithStatuses(turns, null, TileCycle.MaxEntries);

            Assert.IsTrue(ReferenceEquals(turns, same), "the same list is handed back, not a copy");
        }

        private static TileEntry Entry(string chatId, string sender, string body)
        {
            return Entry(chatId, sender, body, null);
        }

        private static TileEntry Entry(string chatId, string sender, string body, string image)
        {
            var entry = new TileEntry();
            entry.ChatId = chatId;
            entry.Sender = sender;
            entry.Body = body;
            entry.Image = image;

            return entry;
        }
    }
}
