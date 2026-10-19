using System;

namespace WhatsappApp.Protocol.WABinary
{
    /// <summary>
    /// Whether two phone numbers are the same person, when one of them came off the
    /// wire and the other out of the phone's address book.
    ///
    /// Why a rule and not a string comparison: WhatsApp always stores the number in
    /// international form (`393331234567`), while the address book on the phone keeps
    /// it the way a person typed it (`333 123 4567`, or with the trunk zero). The
    /// same contact would then have two spellings, and matching on equality would
    /// name nobody.
    ///
    /// The rule is the same three steps every client uses: the digits only, then the
    /// exact string, then the last nine and the last eight digits. Nine covers a
    /// country code of up to three digits plus a full national number; eight is the
    /// short case, and it is why a number shorter than <see cref="MinDigits"/> is
    /// refused instead of matched - a four-digit short code is not a contact.
    /// </summary>
    public static class PhoneNumbers
    {
        /// <summary>Short of this, a run of digits is not a phone number.</summary>
        public const int MinDigits = 7;

        /// <summary>The longest suffix compared when the full strings differ.</summary>
        private const int LongSuffix = 9;

        /// <summary>The shortest suffix compared, for a national number with no code.</summary>
        private const int ShortSuffix = 8;

        /// <summary>
        /// The digits of a phone number, or null when it holds too few to be one.
        /// Everything that is not a digit goes: spaces, dashes, parentheses and the
        /// plus sign.
        /// </summary>
        public static string Digits(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            var digits = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c >= '0' && c <= '9') digits.Append(c);
            }

            return digits.Length < MinDigits ? null : digits.ToString();
        }

        /// <summary>
        /// The number inside a JID, digits only, or null when the JID is not a person:
        /// a group is not a number, and a `@lid` is an identifier that must never be
        /// printed or matched as if it were one.
        /// </summary>
        public static string UserDigits(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return null;
            if (!jid.EndsWith("@" + JidUtils.WhatsappServer, StringComparison.OrdinalIgnoreCase)) return null;

            // The device suffix (`:5`) is not part of the number.
            jid = JidUtils.Normalize(jid);
            if (jid == null) return null;

            int at = jid.IndexOf('@');
            return Digits(at < 0 ? jid : jid.Substring(0, at));
        }

        /// <summary>Whether the two spellings name the same number.</summary>
        public static bool Same(string left, string right)
        {
            left = Digits(left);
            right = Digits(right);

            if (left == null || right == null) return false;
            if (left == right) return true;

            if (left.Length >= LongSuffix && right.Length >= LongSuffix
                && Tail(left, LongSuffix) == Tail(right, LongSuffix))
            {
                return true;
            }

            return left.Length >= ShortSuffix && right.Length >= ShortSuffix
                && Tail(left, ShortSuffix) == Tail(right, ShortSuffix);
        }

        /// <summary>The last `count` digits of a string that is known to be long enough.</summary>
        public static string Tail(string digits, int count)
        {
            if (digits == null || count <= 0 || digits.Length < count) return digits;
            return digits.Substring(digits.Length - count);
        }
    }
}
