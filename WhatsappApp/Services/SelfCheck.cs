using System;
using System.Text;
using System.Threading.Tasks;

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
    ///     DIAG ok: crypto AES-256-GCM
    ///     DIAG ok: schermo sempre acceso (DisplayRequest)
    ///     DIAG ok: beacon UDP in ascolto sulla porta 8587
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
        /// Il cifrario del canale: se GCM non e' implementato qui, il socket non
        /// puo' funzionare e la cosa va saputa subito, non alla prima schermata
        /// vuota.
        /// </summary>
        private static void CheckCrypto()
        {
            try
            {
                byte[] probe = Encoding.UTF8.GetBytes("whatsapp-wp8");
                byte[] frame = CryptoHelper.Encrypt(probe);
                byte[] back = CryptoHelper.Decrypt(frame);

                bool equal = back != null && back.Length == probe.Length;
                for (int i = 0; equal && i < probe.Length; i++) equal = back[i] == probe[i];

                if (equal) Diag.Ok("crypto AES-256-GCM");
                else Diag.Failed("SelfCheck.crypto",
                    new InvalidOperationException("il giro di andata e ritorno non torna"));
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.crypto", ex);
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
                Diag.Ok("schermo sempre acceso (DisplayRequest)");
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
                if (DiscoveryService.Instance.IsListening) Diag.Ok("beacon UDP in ascolto sulla porta 8587");
                else Diag.Failed("SelfCheck.discovery",
                    new InvalidOperationException("la porta UDP non si e' aperta"));
            }
            catch (Exception ex)
            {
                Diag.Failed("SelfCheck.discovery", ex);
            }
        }
    }
}
