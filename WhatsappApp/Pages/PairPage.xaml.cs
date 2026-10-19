using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Protocol.Client;
using WhatsappApp.Protocol.Qr;
using WhatsappApp.Protocol.Transport;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The first screen of a phone that has no account: the QR code that links it.
    ///
    /// It is the page the whole native client was built for. The code arrives as a string
    /// from the server (`PairingSession.StartAsync`), the app turns it into modules itself
    /// (`QrCode.Encode` - there is no PNG from a server any more) and draws them one pixel
    /// per module in the four colours a `WriteableBitmap` wants.
    ///
    /// Two things the page owns and nothing else does:
    ///
    /// - **The rotation.** WhatsApp issues several refs at once and each one is good for a
    ///   short time; a code left on screen past its ref is a code the phone will scan into
    ///   nothing at all. The page therefore advances to the next ref on a timer instead of
    ///   leaving one dead code up. The refs it already holds need no I/O, so the rotation
    ///   is a redraw.
    /// - **The end of the attempt.** When the refs run out, or when the server stops
    ///   answering, the session is over - the codes cannot be revived - so the page says so
    ///   and offers a new attempt, which is a new identity and a new set of codes.
    /// </summary>
    public sealed partial class PairPage : Page
    {
        /// <summary>
        /// Four pixels per module, which is the smallest module a phone camera reads
        /// reliably on this screen. The symbol is never stretched afterwards: `Stretch` on
        /// the Image would scale it by a fraction, and a module that lands on two pixels
        /// and a half is a module a scanner can see wrong.
        /// </summary>
        private const int PixelsPerModule = 4;

        /// <summary>How long one ref stays up when the server sent one.</summary>
        private const int SingleCodeSeconds = 20;

        /// <summary>And when it sent several: the batch is good for a minute, not twenty seconds.</summary>
        private const int ManyCodeSeconds = 60;

        /// <summary>The count at which the server is issuing a batch rather than a single code.</summary>
        private const int BatchSize = 2;

        private readonly DeviceRandomSource _random = new DeviceRandomSource();
        private readonly CredentialStore _store = new CredentialStore(new NativeCredentialStorage());

        /// <summary>
        /// The screen request, and why this page has one: a code goes stale the moment the
        /// app is suspended, and the screen locking is what suspends it. The phone sleeps,
        /// the socket dies, and the code stays on screen looking perfectly scannable - the
        /// run of 2026-10-10 did exactly that (QR up at 14:32:5x, `=== run ended ===` at
        /// 14:33:14 with the code still displayed, and the phone that scanned it answered
        /// "Impossibile collegare il dispositivo", because the ref belonged to a
        /// connection that no longer existed). The old connection page held one for the
        /// same reason; this page is where the QR lives now.
        ///
        /// Held while a code is on screen and released with it: an expired page has
        /// nothing to read, and holding a phone awake for it is a phone with no battery.
        /// </summary>
        private Windows.System.Display.DisplayRequest _displayRequest;
        private bool _displayRequestActive;

        private DispatcherTimer _rotation;
        private WebSocketTransport _transport;
        private PairingSession _session;
        private IList<string> _codes;
        private int _shown;

        /// <summary>One attempt at a time: the retry button and a rotation that ran out can otherwise overlap.</summary>
        private bool _pairing;

        public PairPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

#pragma warning disable 4014
            Guarded.RunGuardedAsync("PairPage/pairing", RunAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// The page is gone: nothing may keep talking to WhatsApp on its behalf. A pairing
        /// that is still in flight is abandoned, which is exactly what the server does with
        /// it when the connection drops.
        /// </summary>
        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            StopRotation();
            ReleaseScreenOn();
            if (_session != null) _session.Close();
        }

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            RetryButton.Visibility = Visibility.Collapsed;

#pragma warning disable 4014
            Guarded.RunGuardedAsync("PairPage/retry", RunAsync());
#pragma warning restore 4014
        }

        /// <summary>
        /// One whole attempt: a fresh identity, the handshake, the codes, and then the wait
        /// for the phone on the other side of the scan to say yes.
        ///
        /// The wait is bounded by the codes themselves - when every ref has been up for its
        /// time, the server has nothing left to accept a scan against, so it drops the
        /// connection and the attempt is over. `PairingSession.WaitForPairAsync` returns
        /// null on that timeout, which is not an error: it is the expired sentence and the
        /// retry button.
        /// </summary>
        private async Task RunAsync()
        {
            if (_pairing) return;
            _pairing = true;

            BusyBar.Visibility = Visibility.Visible;
            ExpiredText.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            QrImage.Visibility = Visibility.Visible;

            try
            {
                Credentials credentials = Credentials.Create(_random);

                _transport = new WebSocketTransport();
                _session = new PairingSession(_transport, credentials, new ProtocolLog(), _random);

                IList<string> codes = await _session.StartAsync();

                _codes = codes;
                _shown = 0;

                Draw(codes[0]);
                StartRotation(codes.Count);

                Credentials paired = await _session.WaitForPairAsync(
                    TimeSpan.FromSeconds(SecondsPerCode(codes.Count) * codes.Count));

                StopRotation();

                if (paired == null)
                {
                    ShowExpired();
                    return;
                }

                // The identity is new, so nothing the previous one wrote can be read: the
                // sessions on disk were built with the old identity key and a Signal MAC covers
                // both identities (`Cipher.Input`), which turns keeping them into every message
                // of every chat failing with "the message's MAC does not verify" - the run of
                // 2026-10-18, where not one stanza, from the account's own phone or from a
                // contact, could be opened. It goes before the save: a wipe that throws leaves
                // no account stored either, so the page shows the error and not a link that
                // cannot read.
                await FileSessionStorage.DeleteAllAsync();
                Diag.Ok("whatsapp: the protocol state of the previous identity is forgotten");

                // Written whole, before the app leaves this page: a session that is not
                // stored is a session the next start does not have, and the QR would come
                // back for a phone that is already linked.
                _store.Save(paired);

                // The account is new and the store has it: whatever was refused before is
                // no longer this phone's problem.
                SessionState.Expired = false;

                // And the login that makes the link real. The server drops the pairing
                // connection after `pair-device-sign` (the reference calls it
                // `expectDisconnect`), and a device that stops here is a device WhatsApp's
                // own side reports as failed to link - which is what the phone showed on
                // 2026-10-10 (see NativeConnection).
                NativeConnection.Instance.Start();

                Diag.Ok("whatsapp: linked as " + paired.Jid);

                Frame.Navigate(typeof(HomePage));
            }
            catch (Exception error)
            {
                Diag.Failed("PairPage/pairing", error);

                StopRotation();
                if (_session != null) _session.Close();

                ErrorText.Visibility = Visibility.Visible;
                RetryButton.Visibility = Visibility.Visible;
            }
            finally
            {
                _pairing = false;
                BusyBar.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// The next ref, on a timer. The codes are already in hand, so this is a redraw and
        /// not a round trip: the refs change at a rate the network cannot be trusted for.
        /// </summary>
        private void StartRotation(int count)
        {
            _rotation = new DispatcherTimer();
            _rotation.Interval = TimeSpan.FromSeconds(SecondsPerCode(count));
            _rotation.Tick += Rotation_Tick;
            _rotation.Start();
        }

        private void StopRotation()
        {
            if (_rotation == null) return;

            _rotation.Stop();
            _rotation.Tick -= Rotation_Tick;
            _rotation = null;
        }

        private void Rotation_Tick(object sender, object e)
        {
            _shown++;

            // Past the last ref the server issued: there is nothing left to show, and the
            // server is about to stop waiting too.
            if (_codes == null || _shown >= _codes.Count)
            {
                StopRotation();
                ShowExpired();
                return;
            }

            Draw(_codes[_shown]);
        }

        /// <summary>
        /// The QR as pixels: white paper, then the dark modules, each one `PixelsPerModule`
        /// square, with the quiet zone the standard asks for around them.
        ///
        /// The buffer is written whole in one go - a low-end WP8.1 phone has no room for a
        /// second copy of a 292x292 bitmap, and `WriteableBitmap` reads it as BGRA8.
        /// </summary>
        private void Draw(string text)
        {
            bool[,] modules = QrCode.Encode(text, QrErrorCorrection.Medium);

            int count = modules.GetLength(0);
            int side = (count + 2 * QrCode.QuietZone) * PixelsPerModule;
            int margin = QrCode.QuietZone * PixelsPerModule;

            var pixels = new byte[side * side * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = 0xFF;
                pixels[i + 1] = 0xFF;
                pixels[i + 2] = 0xFF;
                pixels[i + 3] = 0xFF;
            }

            for (int row = 0; row < count; row++)
            {
                for (int column = 0; column < count; column++)
                {
                    if (!modules[row, column]) continue;

                    for (int y = 0; y < PixelsPerModule; y++)
                    {
                        int line = (margin + row * PixelsPerModule + y) * side;

                        for (int x = 0; x < PixelsPerModule; x++)
                        {
                            int at = (line + margin + column * PixelsPerModule + x) * 4;
                            pixels[at] = 0;
                            pixels[at + 1] = 0;
                            pixels[at + 2] = 0;
                        }
                    }
                }
            }

            var bitmap = new WriteableBitmap(side, side);
            using (Stream buffer = bitmap.PixelBuffer.AsStream())
            {
                buffer.Write(pixels, 0, pixels.Length);
            }

            bitmap.Invalidate();

            QrImage.Source = bitmap;
            QrImage.Width = side;
            QrImage.Height = side;

            // There is a code on screen, so the screen has to stay on: see the field.
            // Idempotent, so the rotation calls it once per code for nothing.
            KeepScreenOn();

            Diag.Ok("whatsapp: the pairing code is on screen (" + side + " px, "
                + PixelsPerModule + " per module)");
        }

        /// <summary>
        /// The screen stays on: if it turns off or dims while the code is on screen, the
        /// code becomes unreadable and the scan fails with no error message at all.
        ///
        /// All inside the try, construction included: creating the request can fail on
        /// the platform, and a convenience must never stop the code from being shown.
        /// </summary>
        private void KeepScreenOn()
        {
            if (_displayRequestActive) return;
            try
            {
                if (_displayRequest == null) _displayRequest = new Windows.System.Display.DisplayRequest();
                _displayRequest.RequestActive();
                _displayRequestActive = true;
            }
            catch (Exception error)
            {
                // Active-request limit reached, or member not implemented on this
                // phone: we carry on without.
                Diag.Failed("PairPage/KeepScreenOn", error);
            }
        }

        private void ReleaseScreenOn()
        {
            if (!_displayRequestActive || _displayRequest == null) return;
            try { _displayRequest.RequestRelease(); }
            catch (Exception error) { Diag.Failed("PairPage/ReleaseScreenOn", error); }
            _displayRequestActive = false;
        }

        private static int SecondsPerCode(int count)
        {
            return count >= BatchSize ? ManyCodeSeconds : SingleCodeSeconds;
        }

        /// <summary>
        /// The codes are spent. The dead one is taken off the screen as well as explained:
        /// a page that leaves an unreadable code up invites a scan that does nothing, and
        /// the scanning phone has no way to say what went wrong.
        /// </summary>
        private void ShowExpired()
        {
            Diag.Ok("whatsapp: the pairing codes have run out");

            // The codes are gone, so the reason to be awake is gone with them.
            ReleaseScreenOn();

            QrImage.Visibility = Visibility.Collapsed;
            ExpiredText.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
        }
    }
}
