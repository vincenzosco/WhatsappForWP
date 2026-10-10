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

                // Written whole, before the app leaves this page: a session that is not
                // stored is a session the next start does not have, and the QR would come
                // back for a phone that is already linked.
                _store.Save(paired);

                // The account is new and the store has it: whatever was refused before is
                // no longer this phone's problem.
                SessionState.Expired = false;

                Diag.Ok("whatsapp: linked as " + paired.Jid);

                Frame.Navigate(typeof(ChatsPage));
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

            Diag.Ok("whatsapp: the pairing code is on screen (" + side + " px, "
                + PixelsPerModule + " per module)");
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

            QrImage.Visibility = Visibility.Collapsed;
            ExpiredText.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
        }
    }
}
