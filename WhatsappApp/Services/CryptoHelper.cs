using System;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Encrypts and authenticates the frames exchanged with the adapter
    /// (WhatsappBridge).
    ///
    /// AES-256-CBC + HMAC-SHA256, not AES-GCM: on Windows Phone 8.1 the member
    /// exists in the WinRT projection but at run time throws
    /// NotImplementedException 0x80004001 (seen on the device in
    /// DIAG SelfCheck.crypto), and CBC is the authenticated alternative the phone
    /// really executes.
    ///
    /// Payload format, after the 4-byte length prefix:
    ///   [1-byte cipher tag][16-byte IV][ciphertext][32-byte HMAC-SHA256]
    /// The HMAC covers the IV and the ciphertext (encrypt-then-MAC) and is
    /// verified BEFORE decrypting: a tampered payload never reaches CBC.
    ///
    /// The keys must match WhatsappBridge/crypto-helper.js:
    ///   master = SHA-256(passphrase)
    ///   encKey = HMAC-SHA256(master, "wp8-adapter enc")
    ///   macKey = HMAC-SHA256(master, "wp8-adapter mac")
    /// </summary>
    public static class CryptoHelper
    {
        // It must stay identical to DEFAULT_PASSPHRASE in crypto-helper.js
        // (or to the BRIDGE_KEY value on the server).
        private const string Passphrase = "WhatsAppCommunityWP8-2026";

        /// <summary>Cipher tag of CBC+HMAC: the first byte of the payload.</summary>
        public const byte CipherCbcHmac = 2;

        /// <summary>AES-GCM tag: recognized and refused, see the comment at the top.</summary>
        public const byte CipherGcm = 1;

        /// <summary>Cipher name, for the SelfCheck line.</summary>
        public const string ModeDescription = "AES-256-CBC + HMAC-SHA256";

        private const int IvLength = 16;
        private const int MacLength = 32;

        // tag + IV + at least one block + HMAC
        private const int MinPayloadLength = 1 + IvLength + 16 + MacLength;

        private static byte[] EncKey = DeriveKey(Passphrase, "wp8-adapter enc");
        private static byte[] MacKey = DeriveKey(Passphrase, "wp8-adapter mac");

        /// <summary>
        /// Sets the shared key from the settings, so a server that does not use the
        /// compiled default can still be reached. An empty value keeps the default,
        /// which is what a private server with no BRIDGE_KEY uses. It is called
        /// before a connection is opened, never while one is running.
        /// </summary>
        public static void SetPassphrase(string passphrase)
        {
            string value = string.IsNullOrEmpty(passphrase) ? Passphrase : passphrase;
            EncKey = DeriveKey(value, "wp8-adapter enc");
            MacKey = DeriveKey(value, "wp8-adapter mac");
        }

        /// <summary>
        /// Derives one of the two keys from the master the way the server does:
        /// HMAC-SHA256(SHA-256(passphrase), label).
        /// </summary>
        private static byte[] DeriveKey(string passphrase, string label)
        {
            var hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var master = hash.HashData(
                CryptographicBuffer.ConvertStringToBinary(passphrase, BinaryStringEncoding.Utf8));

            var provider = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256);
            var key = provider.CreateKey(master);
            var signed = CryptographicEngine.Sign(
                key,
                CryptographicBuffer.ConvertStringToBinary(label, BinaryStringEncoding.Utf8));

            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(signed, out bytes);
            return bytes;
        }

        /// <summary>
        /// Encrypts into [tag][IV][ciphertext][HMAC]. The tag tells the adapter
        /// which cipher the frame was written with.
        /// </summary>
        public static byte[] Encrypt(byte[] plaintext)
        {
            var iv = CryptographicBuffer.GenerateRandom((uint)IvLength);

            var algorithm = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbcPkcs7);
            var key = algorithm.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(EncKey));

            var encrypted = CryptographicEngine.Encrypt(
                key,
                CryptographicBuffer.CreateFromByteArray(plaintext),
                iv);

            byte[] ivBytes;
            byte[] cipherBytes;
            CryptographicBuffer.CopyToByteArray(iv, out ivBytes);
            CryptographicBuffer.CopyToByteArray(encrypted, out cipherBytes);

            // The HMAC covers the IV and the ciphertext, in that order: that is
            // what crypto-helper.js computes with update(iv).update(body).
            var signed = new byte[ivBytes.Length + cipherBytes.Length];
            Buffer.BlockCopy(ivBytes, 0, signed, 0, ivBytes.Length);
            Buffer.BlockCopy(cipherBytes, 0, signed, ivBytes.Length, cipherBytes.Length);

            byte[] mac = Hmac(signed);

            var result = new byte[1 + signed.Length + mac.Length];
            result[0] = CipherCbcHmac;
            Buffer.BlockCopy(signed, 0, result, 1, signed.Length);
            Buffer.BlockCopy(mac, 0, result, 1 + signed.Length, mac.Length);
            return result;
        }

        /// <summary>
        /// Verifies the signature and then decrypts. It throws ArgumentException
        /// when the payload is too short, when the tag is not one we can execute
        /// or when the signature does not match.
        /// </summary>
        public static byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length < MinPayloadLength)
            {
                throw new ArgumentException("Invalid encrypted payload (too short)");
            }

            if (data[0] != CipherCbcHmac)
            {
                // Tag 1 is AES-GCM: the adapter does not use it toward this app,
                // but if it happened, refusing it is better than misreading it.
                throw new ArgumentException("Unsupported cipher tag: " + data[0]);
            }

            int signedLength = data.Length - 1 - MacLength;
            var signed = new byte[signedLength];
            Buffer.BlockCopy(data, 1, signed, 0, signedLength);

            var mac = new byte[MacLength];
            Buffer.BlockCopy(data, 1 + signedLength, mac, 0, MacLength);

            if (!FixedTimeEquals(Hmac(signed), mac))
            {
                throw new ArgumentException("Invalid HMAC signature");
            }

            byte[] ivBytes = new byte[IvLength];
            Buffer.BlockCopy(data, 1, ivBytes, 0, IvLength);

            int cipherLength = signedLength - IvLength;
            byte[] cipherBytes = new byte[cipherLength];
            Buffer.BlockCopy(data, 1 + IvLength, cipherBytes, 0, cipherLength);

            var algorithm = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesCbcPkcs7);
            var key = algorithm.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(EncKey));

            var decrypted = CryptographicEngine.Decrypt(
                key,
                CryptographicBuffer.CreateFromByteArray(cipherBytes),
                CryptographicBuffer.CreateFromByteArray(ivBytes));

            byte[] plain;
            CryptographicBuffer.CopyToByteArray(decrypted, out plain);
            return plain;
        }

        private static byte[] Hmac(byte[] data)
        {
            var provider = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256);
            var key = provider.CreateKey(CryptographicBuffer.CreateFromByteArray(MacKey));
            var signed = CryptographicEngine.Sign(key, CryptographicBuffer.CreateFromByteArray(data));

            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(signed, out bytes);
            return bytes;
        }

        /// <summary>
        /// Constant-time comparison: a comparison that exits at the first
        /// differing byte lets the signature be measured.
        /// </summary>
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;

            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }
    }
}
