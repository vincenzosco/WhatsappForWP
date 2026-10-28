using WhatsappApp.Services;

namespace ProtocolTests
{
    /// <summary>
    /// The name a status row shows.
    ///
    /// The bug these pin: a status arrives from the `participant` of a `status@broadcast`
    /// message, which is often a `@lid` - fourteen digits this phone has never paired with a
    /// number. The row asked `DisplayNameForJid`, which answers the address when it knows
    /// nothing better, and the list showed `+150375613116620` where WhatsApp sends the
    /// poster's own name in the same stanza (`notify`). The report of 28 October 2026.
    /// </summary>
    public static class StatusNameTests
    {
        /// <summary>The case of the report: a LID resolved to digits, and a name in `notify`.</summary>
        [Test]
        public static void AnAddressIsReplacedByTheNameThePostCarries()
        {
            Assert.AreEqual("Rosalba \uD83C\uDF39",
                StatusName.Choose("+150375613116620", "Rosalba \uD83C\uDF39"),
                "fourteen digits are not a name when the poster wrote one");
        }

        /// <summary>A number without its `+` is the same address: a LID arrives like this.</summary>
        [Test]
        public static void ABareRunOfDigitsIsStillAnAddress()
        {
            Assert.AreEqual("Rosalba", StatusName.Choose("150375613116620", "Rosalba"));
        }

        /// <summary>The phone's own name for a contact wins: it is what the address book says.</summary>
        [Test]
        public static void ASavedNameIsNotReplacedByThePushName()
        {
            Assert.AreEqual("Mamma", StatusName.Choose("Mamma", "Rosalba"),
                "the name this phone saved is the user's word for that person");
        }

        /// <summary>A status with no `notify` keeps whatever the phone resolved.</summary>
        [Test]
        public static void NoNotifiedNameKeepsTheResolvedOne()
        {
            Assert.AreEqual("+393292480745", StatusName.Choose("+393292480745", null));
            Assert.AreEqual("Mamma", StatusName.Choose("Mamma", "  "));
        }

        /// <summary>Nothing resolved and nothing notified is an empty row, not a crash.</summary>
        [Test]
        public static void NothingAtAllIsAnEmptyName()
        {
            Assert.AreEqual("", StatusName.Choose(null, null));
            Assert.AreEqual("Rosalba", StatusName.Choose("", "Rosalba"));
        }

        /// <summary>The shapes that are addresses, and the ones that are words.</summary>
        [Test]
        public static void OnlyAnAddressLooksLikeAnAddress()
        {
            Assert.IsTrue(StatusName.LooksLikeAddress("+393292480745"), "a number with its plus");
            Assert.IsTrue(StatusName.LooksLikeAddress("150375613116620"), "a LID read out loud");
            Assert.IsTrue(StatusName.LooksLikeAddress("+39 329 248 0745"), "a number written with spaces");

            Assert.IsFalse(StatusName.LooksLikeAddress("Rosalba"), "a name is a name");
            Assert.IsFalse(StatusName.LooksLikeAddress("Rosalba 2"), "a name with a number in it is a name");
            Assert.IsFalse(StatusName.LooksLikeAddress("007"), "three digits are a word");
            Assert.IsFalse(StatusName.LooksLikeAddress(""), "an empty name is not an address");
        }
    }
}
