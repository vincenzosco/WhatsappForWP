using System;
using System.Text;

namespace WhatsappApp.Protocol.Crypto
{
    /// <summary>
    /// The app-state sync's LT-Hash: a 128-byte sum a set of records is hashed into, where
    /// adding and removing a record are two different arithmetic moves on that sum.
    ///
    /// Why it exists here: every snapshot and every patch carries a MAC over this sum, so a
    /// client that wants to verify what the server says about a chat's pin, a mute or a saved
    /// contact name has to compute the same number. Without it the MACs can only be skipped,
    /// and a skipped MAC is a client that believes whatever arrives.
    ///
    /// The rule is the reference's (`Utils/lt-hash.js`, `LT_HASH_ANTI_TAMPERING` with the
    /// salt `WhatsApp Patch Integrity`): each record's 32-byte value MAC is expanded to 128
    /// bytes with HKDF and added to the sum word by word, as 16-bit little-endian words that
    /// wrap; removing the record subtracts the same 128 bytes. A failed subtraction is not
    /// an error here - the words wrap - which is why the sum is a *sum* and not a set.
    /// </summary>
    public static class LtHash
    {
        /// <summary>How long the sum is. Fixed by the reference.</summary>
        public const int Length = 128;

        /// <summary>The HKDF info every value MAC is expanded with.</summary>
        private const string Info = "WhatsApp Patch Integrity";

        /// <summary>The sum of no records: 128 zero bytes.</summary>
        public static byte[] Empty()
        {
            return new byte[Length];
        }

        /// <summary>The sum with one record's value MAC added.</summary>
        public static byte[] Add(byte[] sum, byte[] valueMac)
        {
            return Mix(sum, valueMac, 1);
        }

        /// <summary>The sum with one record's value MAC taken back out.</summary>
        public static byte[] Subtract(byte[] sum, byte[] valueMac)
        {
            return Mix(sum, valueMac, -1);
        }

        private static byte[] Mix(byte[] sum, byte[] valueMac, int sign)
        {
            if (sum == null || sum.Length != Length) throw new ProtocolException("an LT-Hash sum is 128 bytes");
            if (valueMac == null || valueMac.Length == 0) throw new ProtocolException("an LT-Hash needs a value MAC");

            byte[] expanded = Hkdf.Derive(valueMac, null, Encoding.UTF8.GetBytes(Info), Length);
            var result = new byte[Length];

            for (int i = 0; i < Length; i += 2)
            {
                // 16 bits at a time, little endian, wrapping: `setUint16` in the reference
                // truncates rather than throwing, and the wrapping is what makes a removal
                // of something never added leave the sum alone instead of failing it.
                int word = sum[i] | (sum[i + 1] << 8);
                int add = expanded[i] | (expanded[i + 1] << 8);
                int mixed = (word + sign * add) & 0xffff;

                result[i] = (byte)(mixed & 0xff);
                result[i + 1] = (byte)((mixed >> 8) & 0xff);
            }

            return result;
        }
    }
}
