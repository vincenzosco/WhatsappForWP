using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Checks at startup, in DEBUG only, that the three platform things the app
    /// cannot do without really exist on this phone.
    ///
    /// It is needed because compiling does not prove it: the WP8.1 WinRT
    /// projection lists members that at run time can answer "not implemented", and
    /// from a Mac there is no way to notice. The result goes into the log in the
    /// Diag shape, so one debug round says everything in three lines:
    ///
    ///     DIAG ok: crypto AES-256-CBC + HMAC-SHA256
    ///     DIAG ok: screen kept awake (DisplayRequest)
    ///     DIAG ok: UDP discovery beacon listening on port 8587
    ///
    /// and in place of an "ok" line a line with the failure and its HRESULT.
    /// </summary>
    public static class SelfCheck
    {
        /// <summary>Returns nothing and throws nothing: it is a message in the log.</summary>
        public static async void RunAsync()
        {
            CheckCrypto();
            CheckScreenRequest();
            await CheckDiscoveryAsync();
        }

        /// <summary>
        /// The channel cipher. First the round trip, then the test vector: that is
        /// computed by the server with its key derivation, so if it matches the two
        /// sides really speak to each other. When it fails, the site says at which
        /// step.
        /// </summary>
        private static void CheckCrypto()
        {
            // The same vector as WhatsappBridge/test/crypto-helper.test.js:
            // IV = 000102...0e0f, text {"Type":0,"Text":"ciao"}.
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
                // If it fails here the channel cannot work: that was the case of
                // the old CryptoHelper, which used AES-GCM (0x80004001).
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

        /// <summary>The screen kept on while the code is on screen.</summary>
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

        /// <summary>The beacon listening: it is what makes the server found on its own.</summary>
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
