using System;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Cifra e autentica i frame scambiati con l'adapter (WhatsappBridge).
    ///
    /// AES-256-CBC + HMAC-SHA256, non AES-GCM: su Windows Phone 8.1 il membro
    /// esiste nella proiezione WinRT ma a runtime lancia
    /// NotImplementedException 0x80004001 (visto sul dispositivo in
    /// DIAG SelfCheck.crypto), e CBC e' l'alternativa autenticata che il
    /// telefono esegue davvero.
    ///
    /// Formato del payload, dopo il prefisso di 4 byte con la lunghezza:
    ///   [1 byte tag cifrario][16 byte IV][cifrato][32 byte HMAC-SHA256]
    /// L'HMAC copre IV e cifrato (encrypt-then-MAC) e si verifica PRIMA di
    /// decifrare: un payload manomesso non arriva mai a CBC.
    ///
    /// Le chiavi devono combaciare con WhatsappBridge/crypto-helper.js:
    ///   master = SHA-256(passphrase)
    ///   encKey = HMAC-SHA256(master, "wp8-adapter enc")
    ///   macKey = HMAC-SHA256(master, "wp8-adapter mac")
    /// </summary>
    public static class CryptoHelper
    {
        // Deve restare identica a DEFAULT_PASSPHRASE in crypto-helper.js
        // (o al valore di BRIDGE_KEY sul server).
        private const string Passphrase = "WhatsAppCommunityWP8-2026";

        /// <summary>Tag del cifrario CBC+HMAC: primo byte del payload.</summary>
        public const byte CipherCbcHmac = 2;

        /// <summary>Tag di AES-GCM: riconosciuto e rifiutato, vedi il commento in testa.</summary>
        public const byte CipherGcm = 1;

        /// <summary>Nome del cifrario, per la riga di SelfCheck.</summary>
        public const string ModeDescription = "AES-256-CBC + HMAC-SHA256";

        private const int IvLength = 16;
        private const int MacLength = 32;

        // tag + IV + almeno un blocco + HMAC
        private const int MinPayloadLength = 1 + IvLength + 16 + MacLength;

        private static readonly byte[] EncKey = DeriveKey("wp8-adapter enc");
        private static readonly byte[] MacKey = DeriveKey("wp8-adapter mac");

        /// <summary>
        /// Deriva una delle due chiavi dal master come fa il server:
        /// HMAC-SHA256(SHA-256(passphrase), etichetta).
        /// </summary>
        private static byte[] DeriveKey(string label)
        {
            var hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var master = hash.HashData(
                CryptographicBuffer.ConvertStringToBinary(Passphrase, BinaryStringEncoding.Utf8));

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
        /// Cifra in [tag][IV][cifrato][HMAC]. Il tag dice all'adapter con quale
        /// cifrario e' stato scritto il frame.
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

            // L'HMAC copre IV e cifrato, in quest'ordine: e' quello che calcola
            // crypto-helper.js con update(iv).update(body).
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
        /// Verifica la firma e poi decifra. Lancia ArgumentException quando il
        /// payload e' troppo corto, quando il tag non e' quello che sappiamo
        /// eseguire o quando la firma non torna.
        /// </summary>
        public static byte[] Decrypt(byte[] data)
        {
            if (data == null || data.Length < MinPayloadLength)
            {
                throw new ArgumentException("Invalid encrypted payload (too short)");
            }

            if (data[0] != CipherCbcHmac)
            {
                // Il tag 1 e' AES-GCM: l'adapter non lo usa verso questa app, ma
                // se succedesse e' meglio rifiutarlo che interpretarlo male.
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
        /// Confronto a tempo costante: un confronto che esce al primo byte
        /// diverso lascia misurare la firma.
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
