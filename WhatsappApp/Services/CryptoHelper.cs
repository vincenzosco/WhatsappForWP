using System;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Encrypts/decrypts the messages exchanged with the server using
    /// AES-256-GCM with a pre-shared key, for privacy on the network.
    ///
    /// Payload format (sent after the 4-byte length prefix):
    ///   [12 bytes random IV][AES-256-GCM ciphertext || 16 bytes auth tag]
    ///
    /// The passphrase MUST match the server one:
    ///   - Node server: BRIDGE_KEY env var, or the default below
    ///     (see WhatsappBridge/crypto-helper.js).
    /// </summary>
    public static class CryptoHelper
    {
        // Keep in sync with the server passphrase (default in crypto-helper.js)
        private const string Passphrase = "WhatsAppCommunityWP8-2026";

        private const uint IvLength = 12;

        private static readonly byte[] Key = DeriveKey(Passphrase);

        private static byte[] DeriveKey(string passphrase)
        {
            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var hashed = provider.HashData(
                CryptographicBuffer.ConvertStringToBinary(passphrase, BinaryStringEncoding.Utf8));
            byte[] key;
            CryptographicBuffer.CopyToByteArray(hashed, out key);
            return key;
        }

        /// <summary>
        /// Encrypts plaintext into [iv][ciphertext || tag].
        /// </summary>
        public static byte[] Encrypt(byte[] plaintext)
        {
            var iv = CryptographicBuffer.GenerateRandom(IvLength);
            var algorithm = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesGcm);
            var key = algorithm.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(Key));

            // For GCM the returned buffer already includes the auth tag appended
            var encrypted = CryptographicEngine.Encrypt(
                key,
                CryptographicBuffer.CreateFromByteArray(plaintext),
                iv);

            byte[] ivBytes;
            byte[] cipherBytes;
            CryptographicBuffer.CopyToByteArray(iv, out ivBytes);
            CryptographicBuffer.CopyToByteArray(encrypted, out cipherBytes);

            var result = new byte[ivBytes.Length + cipherBytes.Length];
            Buffer.BlockCopy(ivBytes, 0, result, 0, ivBytes.Length);
            Buffer.BlockCopy(cipherBytes, 0, result, ivBytes.Length, cipherBytes.Length);
            return result;
        }

        /// <summary>
        /// Decrypts [iv][ciphertext || tag] back to plaintext.
        /// Throws if the payload was tampered with or the key is wrong.
        /// </summary>
        public static byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length < IvLength + 16)
            {
                throw new ArgumentException("Payload cifrato non valido");
            }

            var iv = CryptographicBuffer.CreateFromByteArray(data, 0, IvLength);
            var cipher = CryptographicBuffer.CreateFromByteArray(data, IvLength, (uint)(data.Length - IvLength));

            var algorithm = SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesGcm);
            var key = algorithm.CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(Key));

            var decrypted = CryptographicEngine.Decrypt(key, cipher, iv);

            byte[] plain;
            CryptographicBuffer.CopyToByteArray(decrypted, out plain);
            return plain;
        }
    }
}
