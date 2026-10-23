using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.WaProto;

namespace ProtocolTests
{
    /// <summary>
    /// Who a chat is: the name to show and the phone number behind a `@lid`.
    ///
    /// The inputs are hand-built here and not the reference's bytes, because what is under
    /// test is the rule the bytes feed: a saved name beats a push name, a LID with a known
    /// number is one conversation and not two, and the placeholder WhatsApp uses for a
    /// nameless profile is not a name. The field numbers those inputs are built with come
    /// from the reference's own encoder, and the history fixture prints them field by field
    /// (`Vectors/HistoryFixture.cs`).
    /// </summary>
    public static class ChatNamesTests
    {
        private const string Lid = "150375613116620@lid";
        private const string Phone = "393892672185@s.whatsapp.net";

        [Test]
        public static void TheAddressBookNameWinsOverThePushName()
        {
            var names = new NameBook();

            names.RememberPushName(Phone, "Emanuela");
            Assert.AreEqual("Emanuela", names.NameFor(Phone), "the push name is better than a number");

            names.RememberSavedName(Phone, "Mamma");
            Assert.AreEqual("Mamma", names.NameFor(Phone),
                "the name saved on the phone is the one the person chose for the contact");
        }

        [Test]
        public static void ALidWithAKnownNumberIsOneAddress()
        {
            var names = new NameBook();

            Assert.AreEqual(Lid, names.Canonical(Lid), "an unknown LID is still itself");
            Assert.AreEqual(null, names.PhoneNumberFor(Lid),
                "a LID is not printed as a phone number before the pair is known");

            Assert.IsTrue(names.RememberMapping(Lid, Phone), "the pair is news");

            Assert.AreEqual(Phone, names.Canonical(Lid), "the chat is keyed on the real number now");
            Assert.AreEqual(Phone, names.PhoneNumberFor(Lid));
            Assert.AreEqual(Phone, names.Canonical(Phone), "the number is its own canonical address");
            Assert.AreEqual(Lid, names.LidFor(Phone));
            Assert.IsFalse(names.RememberMapping(Lid, Phone), "the same pair is not news twice");
        }

        /// <summary>
        /// A pair that arrives with another number replaces the one that was stored. Answering
        /// "news" without writing it left `Canonical` folding that chat into the old person's
        /// row for ever - the one case the method exists for.
        /// </summary>
        [Test]
        public static void ACorrectedPairReplacesTheNumberItHad()
        {
            const string other = "393401112222@s.whatsapp.net";
            var names = new NameBook();

            Assert.IsTrue(names.RememberMapping(Lid, Phone));
            Assert.IsTrue(names.RememberMapping(Lid, other), "a different number is news");

            Assert.AreEqual(other, names.Canonical(Lid), "the LID follows the number it was corrected to");
            Assert.AreEqual(other, names.PhoneNumberFor(Lid));
            Assert.AreEqual(Lid, names.LidFor(other));
            Assert.AreEqual(null, names.LidFor(Phone),
                "and the old number is nobody's LID any more");
        }

        [Test]
        public static void ANameLearnedBeforeThePairFollowsThePair()
        {
            var names = new NameBook();

            names.RememberPushName(Lid, "Emanuela");
            Assert.AreEqual("Emanuela", names.NameFor(Lid));

            names.RememberMapping(Lid, Phone);

            Assert.AreEqual("Emanuela", names.NameFor(Phone),
                "the name was stored under the LID and the row is keyed on the number now");
        }

        [Test]
        public static void TheInvisiblePlaceholderIsNotAName()
        {
            Assert.AreEqual(null, NameBook.Clean("\u3164"),
                "WhatsApp writes a Hangul filler for a profile with no name");
            Assert.AreEqual(null, NameBook.Clean("   "));
            Assert.AreEqual(null, NameBook.Clean(""));
            Assert.AreEqual("Maria grazia", NameBook.Clean("  Maria grazia  "));
        }

        [Test]
        public static void AHistoryPairKeysTheConversationOnTheNumber()
        {
            var chats = new NativeChats();
            HistorySync sync = OneSyncWith(ConversationFor(Lid));
            sync.LidMappings.Add(new LidMapping { PhoneNumber = Phone, Lid = Lid });

            chats.ApplyHistory(sync);

            Assert.AreEqual(1, chats.Rows.Count);
            Assert.AreEqual(Phone, chats.Rows[0].Jid,
                "the sync named the pair, so the row is the phone number");
        }

        [Test]
        public static void TheAddressBookTheHistoryCarriesNamesTheRow()
        {
            var chats = new NativeChats();

            HistorySync sync = OneSyncWith(ConversationFor(Lid));
            sync.InlineContacts.Add(new InlineContact
            {
                LidJid = Lid,
                PnJid = Phone,
                FullName = "Mamma"
            });
            sync.PushNames.Add(new HistoryPushName { Jid = Phone, Name = "Emanuela" });

            chats.ApplyHistory(sync);

            Assert.AreEqual(1, chats.Rows.Count, "the pair made one conversation and not two");
            Assert.AreEqual("Mamma", chats.Rows[0].Name,
                "the name saved in the address book is the one the list shows");
        }

        [Test]
        public static void AMappingLearnedLaterFoldsTheRowItAlreadyHad()
        {
            var chats = new NativeChats();
            chats.ApplyHistory(OneSyncWith(ConversationFor(Lid)));

            Assert.AreEqual(Lid, chats.Rows[0].Jid, "without the pair the row is the LID");

            Assert.IsTrue(chats.LearnMapping(Lid, Phone), "the fold is news");
            Assert.AreEqual(1, chats.Rows.Count, "one person, one row");
            Assert.AreEqual(Phone, chats.Rows[0].Jid);
        }

        [Test]
        public static void TheHistoryPayloadReadsThePairsTheAddressBookAndTheFlag()
        {
            byte[] mapping = new ProtoWriter()
                .Text(1, Phone)
                .Text(2, Lid)
                .ToArray();

            byte[] contact = new ProtoWriter()
                .Text(1, Phone)
                .Text(2, Lid)
                .Text(3, "Mamma")
                .Text(4, "Mamma")
                .Text(5, "mamma.42")
                .ToArray();

            byte[] conversation = new ProtoWriter()
                .Text(1, Lid)
                .Text(39, Phone)
                .Text(42, Lid)
                .ToArray();

            byte[] payload = new ProtoWriter()
                .Varint(1, 0)
                .Bytes(2, conversation)
                .Bytes(15, mapping)
                .Bytes(20, contact)
                .Varint(21, 1)
                .ToArray();

            HistorySync sync = HistorySync.Decode(payload);

            Assert.AreEqual(1, sync.LidMappings.Count, "field 15 is the LID table");
            Assert.AreEqual(Phone, sync.LidMappings[0].PhoneNumber);
            Assert.AreEqual(Lid, sync.LidMappings[0].Lid);

            Assert.IsTrue(sync.InlineContactsProvided, "field 21 says the address book came with it");
            Assert.AreEqual(1, sync.InlineContacts.Count, "field 20 is the address book");
            Assert.AreEqual("Mamma", sync.InlineContacts[0].FullName);
            Assert.AreEqual("mamma.42", sync.InlineContacts[0].Username);

            Assert.AreEqual(Phone, sync.Conversations[0].PnJid, "field 39 is the number behind the LID");
            Assert.AreEqual(Lid, sync.Conversations[0].LidJid, "field 42 is the LID of the number");
        }

        private static HistorySync OneSyncWith(HistoryConversation conversation)
        {
            var sync = new HistorySync();
            sync.SyncType = HistorySync.Full;
            sync.Conversations.Add(conversation);

            return sync;
        }

        private static HistoryConversation ConversationFor(string jid)
        {
            var conversation = new HistoryConversation();
            conversation.Jid = jid;
            conversation.ConversationTimestamp = 1791600000L;

            return conversation;
        }
    }
}
