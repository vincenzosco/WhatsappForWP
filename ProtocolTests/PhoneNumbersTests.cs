using WhatsappApp.Protocol.WABinary;

namespace ProtocolTests
{
    /// <summary>
    /// The rule that makes a number from the wire and the same number typed in the
    /// phone's address book one person.
    ///
    /// The cases are the ones that actually happen: the address book keeps spaces
    /// and dashes, it may keep the number national (no country code, with the trunk
    /// zero) while WhatsApp always sends it international, and it holds short codes
    /// that must never match a contact.
    /// </summary>
    public static class PhoneNumbersTests
    {
        [Test]
        public static void TheSameNumberInTwoSpellingsIsOnePerson()
        {
            Assert.IsTrue(
                PhoneNumbers.Same("+39 333 123 4567", "393331234567"),
                "spaces and the plus are not part of the number");

            Assert.IsTrue(
                PhoneNumbers.Same("333-123-4567", "393331234567"),
                "a national number with no country code is the same person");

            Assert.IsTrue(
                PhoneNumbers.Same("0333 1234567", "393331234567"),
                "a trunk zero is not part of the line");
        }

        [Test]
        public static void TwoDifferentNumbersAreNotTheSamePerson()
        {
            Assert.IsTrue(
                !PhoneNumbers.Same("393331234567", "393331234568"),
                "one digit is enough to be somebody else");

            Assert.IsTrue(
                !PhoneNumbers.Same("15551230000", "15551230001"),
                "and it is the digit that differs, not the spelling");
        }

        [Test]
        public static void AShortCodeIsNotAContact()
        {
            Assert.IsTrue(PhoneNumbers.Digits("40404") == null, "five digits are a short code");
            Assert.IsTrue(!PhoneNumbers.Same("40404", "40404"), "and two of them do not match");
            Assert.IsTrue(PhoneNumbers.Digits("1234567") != null, "seven is the first a person can have");
        }

        [Test]
        public static void AGroupAndALidAreNotPhoneNumbers()
        {
            Assert.IsTrue(PhoneNumbers.UserDigits("120363412599616890@g.us") == null,
                "a group address is not a number");
            Assert.IsTrue(PhoneNumbers.UserDigits("124906356396256@lid") == null,
                "a LID is an identifier, not a number");
            Assert.AreEqual("393331234567",
                PhoneNumbers.UserDigits("393331234567:5@s.whatsapp.net"),
                "the device suffix comes off with the JID");
        }
    }
}
