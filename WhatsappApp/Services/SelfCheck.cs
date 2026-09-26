using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Verifica all'avvio, solo in DEBUG, che le tre cose di piattaforma da cui
    /// l'app non puo' prescindere esistano davvero su questo telefono.
    ///
    /// Serve perche' compilare non lo dimostra: la proiezione WinRT di WP8.1
    /// elenca dei membri che a runtime possono rispondere "non implementato", e
    /// da un Mac non c'e' modo di accorgersene. Il risultato finisce nel log con
    /// la forma di Diag, cosi' un giro di debug dice tutto in tre righe:
    ///
    ///     DIAG ok: crypto AES-256-CBC + HMAC-SHA256
    ///     DIAG ok: screen kept awake (DisplayRequest)
    ///     DIAG ok: UDP discovery beacon listening on port 8587
    ///
    /// e al posto di una riga "ok" una riga con il guasto e il suo HRESULT.
    /// </summary>
    public static class SelfCheck
    {
        /// <summary>Non restituisce niente e non lancia: e' un messaggio nel log.</summary>
        public static async void RunAsync()
        {
            CheckCrypto();
            CheckScreenRequest();
            await CheckDiscoveryAsync();
        }

        /// <summary>
        /// Il cifrario del canale. Prima il giro di andata e ritorno, poi il
        /// vettore di prova: quello e' calcolato dal server con la sua
        /// derivazione delle chiavi, quindi se combacia le due parti si parlano
        /// davvero. Quando fallisce, il sito dice a quale passo.
        /// </summary>
        private static void CheckCrypto()
        {
            // Stesso vettore di WhatsappBridge/test/crypto-helper.test.js:
            // IV = 000102...0e0f, testo {"Type":0,"Text":"ciao"}.
            const string VectorHex =
                "02000102030405060708090a0b0c0d0e0f" +
                "2fc17f6d19a9bea8e286ceebf69ca87c72cf5e563e0d09ee3d755fb40f87c336" +
                "e65a51262cff115a41eb866b8a82275d7d61dfb35bb0f5060ab9f1b316d45e2d";
            const string VectorPlain = "{\"Type\":0,\"Text\":\"ciao\"}";

            byte[] probe = Encoding.UTF8.GetBytes("whatsapp-wp8");

            byte[] frame;
            try
            {
                frame = CryptoHelper.Encrypt(probe);
            }
            catch (Exception ex)
            {
                // Se fallisce qui il canale non puo' funzionare: era il caso del
                // vecchio CryptoHelper, che usava AES-GCM (0x80004001).
                Diag.Failed("SelfCheck.crypto/encrypt", ex);
                return;
            }

            byte[] back;
            try
            {
                back = CryptoHelper.Decrypt(frame);
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.crypto/decrypt", ex);
                return;
            }

            bool equal = back != null && back.Length == probe.Length;
            for (int i = 0; equal && i < probe.Length; i++) equal = back[i] == probe[i];
            if (!equal)
            {
                Diag.Failed("SelfCheck.crypto/roundtrip",
                    new InvalidOperationException("the encrypt/decrypt round trip does not return the same bytes"));
                return;
            }

            try
            {
                IBuffer vector = CryptographicBuffer.DecodeFromHexString(VectorHex);
                byte[] vectorBytes;
                CryptographicBuffer.CopyToByteArray(vector, out vectorBytes);

                byte[] plain = CryptoHelper.Decrypt(vectorBytes);
                string text = Encoding.UTF8.GetString(plain, 0, plain.Length);

                if (text == VectorPlain) Diag.Ok("crypto " + CryptoHelper.ModeDescription);
                else Diag.Failed("SelfCheck.crypto/vector",
                    new InvalidOperationException("the known-answer vector does not match: " + text));
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.crypto/vector", ex);
            }
        }

        /// <summary>Lo schermo acceso mentre si inquadra il codice.</summary>
        private static void CheckScreenRequest()
        {
            try
            {
                var request = new Windows.System.Display.DisplayRequest();
                request.RequestActive();
                request.RequestRelease();
                Diag.Ok("screen kept awake (DisplayRequest)");
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.screen", ex);
            }
        }

        /// <summary>L'ascolto dei beacon: e' quello che fa trovare il server da soli.</summary>
        private static async Task CheckDiscoveryAsync()
        {
            try
            {
                await DiscoveryService.Instance.StartAsync();
                if (DiscoveryService.Instance.IsListening) Diag.Ok("UDP discovery beacon listening on port 8587");
                else Diag.Failed("SelfCheck.discovery",
                    new InvalidOperationException("the UDP port did not open"));
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.discovery", ex);
            }
        }
    }
}
