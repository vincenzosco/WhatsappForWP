using System;
using System.Text;
using WhatsappApp.Protocol.Crypto;

namespace WhatsappApp.Protocol.Media
{
    /// <summary>
    /// The call key, expanded into the key material a call's media engine is handed.
    ///
    /// Why this file exists: `enc_key` in the signalling is a **32-byte** secret, and what a
    /// media stack needs is not 32 bytes but a fixed run of key material - the reference
    /// calls the expanded form 46 bytes and derives a per-call SSRC from the same secret.
    /// Until this existed the app could not take the first step after the handshake.
    ///
    /// Where the numbers come from, and this matters more than the code:
    /// `com.whatsapp.calling.crypto.DefaultCryptoCallback` decompiled out of
    /// `C:\Users\Vincenzo\Downloads\WhatsApp.apk` with jadx 1.3.5 on 2 November 2026 (the
    /// DEX header was re-stamped `035` because that jadx predates the format this build
    /// ships; the decompiled copy is `.tools/apk/full/sources/...`). Its two constants -
    /// `E2E_CALL_KEY_LENGTH = 32`, `E2E_EXTENDED_V2_KEY_LENGTH = 46`,
    /// `SECURE_SSRC_LENGTH = 4` - and its two derivations:
    ///
    ///     expandCallKey(callKey, callId)  ->  1oO.A00(callKey, callId.getBytes(), 46)
    ///     generateSecureSsrc(key, info, s) -> 1oO.A02(key, s, info, 4)
    ///     hkdfSha256(key, salt, info, out) -> 1oO.A02(key, salt, info, out.length)
    ///
    /// so the two-argument form is HKDF **with no salt** and the four-argument form is HKDF
    /// with one; `getSecureSsrc` refuses a salt that is not exactly four bytes. What the
    /// 46 bytes are split into (SRTP key, SRTP salt, ...) is decided by the native engine
    /// and is **not** in the APK - see the calls design document - so this file produces the
    /// run and stops there rather than inventing an order.
    ///
    /// The vectors in `ProtocolTests/CallKeysTests.cs` were produced by Node's
    /// `crypto.hkdfSync`, independently of this code, which is what makes them a check and
    /// not a restatement.
    /// </summary>
    public static class CallKeys
    {
        /// <summary>The `enc_key` the signalling carries.</summary>
        public const int CallKeyLength = 32;

        /// <summary>What the reference expands it to (`E2E_EXTENDED_V2_KEY_LENGTH`).</summary>
        public const int ExpandedKeyLength = 46;

        /// <summary>
        /// The cipher key inside the expanded run, for the SRTP session: 32 bytes, which is
        /// AES-256-CM (RFC 6188).
        ///
        /// Why 32 and 14: 32 + 14 is 46, exactly the length the reference expands to, and 14
        /// is the only salt length SRTP has for AES-CM. That arithmetic is the whole evidence
        /// for this split - the native engine is what divides the run, and the APK does not
        /// contain that step - so `SplitMedia` says so, and nothing here pretends the order was
        /// observed. What it does buy is a call whose 46 bytes can actually seal a packet.
        /// </summary>
        public const int MediaKeyLength = 32;

        /// <summary>And the salt beside it: 46 - 32.</summary>
        public const int MediaSaltLength = 14;

        /// <summary>The SSRC derived from the call key (`SECURE_SSRC_LENGTH`).</summary>
        public const int SecureSsrcLength = 4;

        /// <summary>
        /// The 46 expanded bytes read as one SRTP session's master key and salt: the first 32
        /// are the AES-256-CM key, the last 14 the salt.
        ///
        /// An inference, and named as one: 32 + 14 = 46 is what makes it more than a guess, and
        /// what makes it less than a fact is that the division happens in the native engine. A
        /// call that turns out to need the halves in the other order fails its first packet's
        /// tag rather than misbehaving silently, which is the failure mode to want.
        /// </summary>
        public static CallMediaKeys SplitMedia(byte[] expanded)
        {
            if (expanded == null || expanded.Length != ExpandedKeyLength)
            {
                throw new ProtocolException("the media split takes the " + ExpandedKeyLength
                    + "-byte expansion, not " + (expanded == null ? "none" : expanded.Length.ToString()));
            }

            var key = new byte[MediaKeyLength];
            var salt = new byte[MediaSaltLength];

            Buffer.BlockCopy(expanded, 0, key, 0, MediaKeyLength);
            Buffer.BlockCopy(expanded, MediaKeyLength, salt, 0, MediaSaltLength);

            return new CallMediaKeys(key, salt);
        }

        /// <summary>
        /// The call key expanded for one call: HKDF-SHA256 with no salt and the call's id as
        /// the info, which is what ties the key material to the call it belongs to.
        ///
        /// The id is encoded as UTF-8: the reference calls `getBytes()` with no charset, and
        /// Android's default charset is UTF-8.
        /// </summary>
        public static byte[] Expand(byte[] callKey, string callId)
        {
            if (callKey == null || callKey.Length != CallKeyLength)
            {
                throw new ProtocolException("a call key is " + CallKeyLength + " bytes");
            }

            byte[] info = Encoding.UTF8.GetBytes(callId == null ? "" : callId);

            // No salt: RFC 5869 reads an absent salt as a run of zeros as long as the hash,
            // which is what `Hkdf.Extract` already does with null.
            return Hkdf.Derive(callKey, null, info, ExpandedKeyLength);
        }

        /// <summary>
        /// The four-byte SSRC one side will send with, derived from the call key so that both
        /// ends agree on it without putting it on the wire.
        ///
        /// The argument names follow the reference's own parameter order, read from its call
        /// sites: `generateSecureSsrc(key, info, salt)` becomes
        /// `HKDF(key, salt, info, 4)`. The salt is the four-byte value the caller supplies,
        /// and the reference refuses anything else - so does this.
        /// </summary>
        public static byte[] SecureSsrc(byte[] callKey, byte[] info, byte[] salt)
        {
            if (callKey == null || callKey.Length == 0)
            {
                throw new ProtocolException("a secure SSRC needs a call key");
            }

            if (info == null || info.Length == 0)
            {
                throw new ProtocolException("a secure SSRC needs an info");
            }

            if (salt == null || salt.Length != SecureSsrcLength)
            {
                throw new ProtocolException("the SSRC salt is " + SecureSsrcLength + " bytes");
            }

            return Hkdf.Derive(callKey, salt, info, SecureSsrcLength);
        }

        /// <summary>
        /// HKDF-SHA256 as the media callback exposes it, for the other derivations the native
        /// engine asks for. It is here rather than in `Hkdf` because the length is the
        /// caller's and the parameters are the reference's: key first, then salt, then info.
        /// </summary>
        public static byte[] Derive(byte[] key, byte[] salt, byte[] info, int length)
        {
            if (key == null || key.Length == 0)
            {
                throw new ProtocolException("a derivation needs a key");
            }

            if (length <= 0 || length > 255 * Sha256.DigestLength)
            {
                throw new ProtocolException("HKDF cannot expand to " + length + " bytes");
            }

            return Hkdf.Derive(key, salt, info, length);
        }
    }

    /// <summary>
    /// One media direction's master key and salt, ready for `SrtpSession`: 32 bytes of
    /// AES-256-CM key and 14 of salt.
    /// </summary>
    public sealed class CallMediaKeys
    {
        public readonly byte[] Key;
        public readonly byte[] Salt;

        public CallMediaKeys(byte[] key, byte[] salt)
        {
            if (key == null || key.Length != CallKeys.MediaKeyLength)
            {
                throw new ProtocolException("a media key is " + CallKeys.MediaKeyLength + " bytes");
            }

            if (salt == null || salt.Length != CallKeys.MediaSaltLength)
            {
                throw new ProtocolException("a media salt is " + CallKeys.MediaSaltLength + " bytes");
            }

            Key = key;
            Salt = salt;
        }
    }
}
