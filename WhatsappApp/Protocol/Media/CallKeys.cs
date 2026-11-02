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

        /// <summary>The SSRC derived from the call key (`SECURE_SSRC_LENGTH`).</summary>
        public const int SecureSsrcLength = 4;

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
}
