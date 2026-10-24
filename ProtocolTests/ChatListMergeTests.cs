using System;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// One chat, two addresses: which ids are the same conversation, and what one row made of
    /// two says.
    ///
    /// Why it is tested here: these are the rules that decide whether a message lands on the
    /// row that is already on screen or makes a second one - and on the phone a wrong answer
    /// looks like a chat list that shows yesterday's preview, a group with no name, and a
    /// conversation whose own messages are missing. The app-side code that calls them cannot
    /// run off the phone, so the rules live in `ChatListMerge` and this is where they are
    /// checked.
    /// </summary>
    public static class ChatListMergeTests
    {
        private const string Lid = "150375613116620@lid";
        private const string Phone = "393892672185@s.whatsapp.net";
        private const string Group = "120363412599616890@g.us";

        [Test]
        public static void ALidAndItsNumberAreOneChat()
        {
            var book = new NameBook();
            book.RememberMapping(Lid, Phone);

            Assert.AreEqual(Phone, ChatListMerge.CanonicalId(Lid, book),
                "the pair is known, so the chat is the number");
            Assert.IsTrue(ChatListMerge.SameChat(Lid, Phone, book),
                "the two addresses are one conversation");
            Assert.IsFalse(ChatListMerge.SameChat(Lid, "393000000000@s.whatsapp.net", book),
                "another number is another chat");
        }

        [Test]
        public static void ADiviceAndAnotherServerAreTheSameChat()
        {
            var book = new NameBook();

            Assert.AreEqual(Phone, ChatListMerge.CanonicalId("393892672185:5@s.whatsapp.net", book),
                "a message from a linked device is the same chat, without its device");
            Assert.AreEqual(Phone, ChatListMerge.CanonicalId("393892672185@c.us", book),
                "the old server name is the same account");

            Assert.IsTrue(ChatListMerge.SameChat("393892672185:5@s.whatsapp.net", Phone, book));
            Assert.IsFalse(ChatListMerge.SameChat(Group, Phone, book),
                "a group is not a person");
        }

        [Test]
        public static void ALidWhosePairIsUnknownIsStillItself()
        {
            var book = new NameBook();

            Assert.AreEqual(Lid, ChatListMerge.CanonicalId(Lid, book),
                "nothing is known, so nothing is folded: guessing a number would merge two people");
            Assert.IsFalse(ChatListMerge.SameChat(Lid, Phone, book));
        }

        [Test]
        public static void TheNewerPreviewAndTheCountsSurviveTheFold()
        {
            var older = State("+393892672185", "ieri", new DateTime(2026, 10, 23, 21, 2, 0), 2);
            older.Name = "+393892672185";
            older.Pinned = true;

            var newer = State(Phone, "oggi, il messaggio nuovo", new DateTime(2026, 10, 24, 9, 30, 0), 3);

            ChatRowState fused = ChatListMerge.Fuse(older, newer);

            Assert.AreEqual(older.Id, fused.Id, "the row keeps the id it was given; the caller decides which one");
            Assert.AreEqual("oggi, il messaggio nuovo", fused.Preview,
                "the newer row is what the chat last said");
            Assert.AreEqual(newer.At, fused.At);
            Assert.AreEqual(5, fused.Unread, "two counts of one chat add up");
            Assert.IsTrue(fused.Pinned, "a pin carried by either row survives");
        }

        [Test]
        public static void AFallbackNameNeverBeatsAName()
        {
            var numbered = State(Lid, "vecchio", DateTime.MinValue, 0);
            numbered.Name = "+393892672185";

            var named = State(Phone, "Mamma", DateTime.MinValue, 0);
            named.Name = "Mamma";

            Assert.AreEqual("Mamma", ChatListMerge.Fuse(numbered, named).Name,
                "the number written out is not a name");

            var group = State(Group, "Gruppo 120363412599616890", DateTime.MinValue, 0);
            group.Name = "Gruppo 120363412599616890";

            var subject = State(Group, "Family", DateTime.MinValue, 0);
            subject.Name = "Family";

            Assert.AreEqual("Family", ChatListMerge.Fuse(group, subject).Name,
                "the fallback the list invents must not beat the subject the server sent");
            Assert.AreEqual("Family", ChatListMerge.Fuse(subject, group).Name,
                "and it is true whichever row is kept");
        }

        [Test]
        public static void TheFallbackNamesAreRecognised()
        {
            Assert.IsTrue(ChatListMerge.LooksLikeFallback("+393892672185", Phone));
            Assert.IsTrue(ChatListMerge.LooksLikeFallback("393892672185", Phone));
            Assert.IsTrue(ChatListMerge.LooksLikeFallback("Gruppo 120363412599616890", Group));
            Assert.IsTrue(ChatListMerge.LooksLikeFallback("Group 120363412599616890", Group));
            Assert.IsTrue(ChatListMerge.LooksLikeFallback(null, Phone), "no name at all is no name");

            Assert.IsFalse(ChatListMerge.LooksLikeFallback("Mamma", Phone));
            Assert.IsFalse(ChatListMerge.LooksLikeFallback("Family", Group),
                "a group called Group is a name somebody chose");
        }

        [Test]
        public static void ThePreviewOfTheNewerRowWinsEvenWithNoWords()
        {
            var withWord = State(Phone, "ciao", new DateTime(2026, 10, 24, 10, 0, 0), 0);
            var newerMedia = State(Phone, null, new DateTime(2026, 10, 24, 11, 0, 0), 0);

            ChatRowState fused = ChatListMerge.Fuse(withWord, newerMedia);

            Assert.AreEqual("ciao", fused.Preview,
                "a media row with no words must not blank the caption the chat already showed");
            Assert.AreEqual(newerMedia.At, fused.At, "but its time is the truth");
        }

        private static ChatRowState State(string id, string preview, DateTime at, int unread)
        {
            var state = new ChatRowState();
            state.Id = id;
            // A name only where a test gives one: a row's name is not its address, and
            // the rule that prefers a real name needs the two to be different.
            state.Name = null;
            state.Preview = preview;
            state.At = at;
            state.Unread = unread;
            return state;
        }
    }
}
