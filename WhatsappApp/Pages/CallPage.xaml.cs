using System;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Protocol.WABinary;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// The call, on its own screen: who, which kind, which state, how long.
    ///
    /// Why a page and not only the banner: the banner is a strip on the home screen, and a
    /// call placed from a conversation had no screen of its own at all - the chat stayed in
    /// front and the call was one line somewhere behind it. This is the screen the official
    /// client opens, and the one a person expects after pressing "call".
    ///
    /// It draws from the one `CallSession` the app has (`DataService.Call`) and changes
    /// nothing itself: every button is a call to `DataService`, and the state that comes back
    /// through `CallChanged` is what the screen shows. A page that kept its own copy of the
    /// call would be a second opinion about which call is up.
    ///
    /// The timer does two things, and the second one is why it exists: it moves the duration
    /// on an answered call, and it ends a call that nobody picked up after
    /// `DataService.CallSetupSeconds`. Before that rule existed the phone kept a call it had
    /// offered forever: the read loop logged "a call is already up, so ... was not called" on
    /// the next dial, and no new call could be placed until the app was restarted. That is in
    /// the phone's own log, on 2 November 2026.
    ///
    /// The media is not here and does not exist yet: an answered call is a call whose
    /// signalling succeeded. See `docs/superpowers/specs/2026-11-02-voice-and-video-calls-design.md`.
    /// </summary>
    public sealed partial class CallPage : Page
    {
        private readonly DispatcherTimer _tick = new DispatcherTimer();

        /// <summary>
        /// Whether this page is gone: the tick can fire once after `OnNavigatedFrom`, and a
        /// navigation from a page that has already left is the second call screen.
        /// </summary>
        private bool _left;

        /// <summary>The side of the picture, in the 220 px circle.</summary>
        private const int PicturePixels = 220;

        /// <summary>Whose picture is on screen, so it is decoded once per call.</summary>
        private string _avatarJid;

        public CallPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // A page that is navigated to again is on screen again: `_left` is one page's
            // lifetime, not one call's, and leaving it true here made the second visit draw
            // nothing, tick nothing and never leave (the picture stayed as it was).
            _left = false;

            AcceptCallButton.Content = Loc.Get("Call_Accept", "Accept");
            DeclineCallButton.Content = Loc.Get("Call_Decline", "Decline");
            EndCallButton.Content = Loc.Get("Call_End", "End");

            // One second: the duration on screen moves once a second, and the deadline for an
            // unanswered call is measured in seconds.
            _tick.Interval = TimeSpan.FromSeconds(1);
            _tick.Tick += OnTick;
            _tick.Start();

            DataService.Instance.CallChanged -= OnCallChanged;
            DataService.Instance.CallChanged += OnCallChanged;

            Draw();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            _left = true;
            _tick.Stop();
            _tick.Tick -= OnTick;
            DataService.Instance.CallChanged -= OnCallChanged;
        }

        private void OnCallChanged(object sender, EventArgs e)
        {
            if (_left) return;

            Draw();
        }

        /// <summary>
        /// Once a second: a call nobody answered is ended here, and everything else is drawn
        /// again - the duration is a fact of the clock, not of a stanza.
        /// </summary>
        private void OnTick(object sender, object e)
        {
            if (_left) return;

            CallSession call = DataService.Instance.Call;
            if (call != null && call.Expired(DateTime.UtcNow, DataService.CallSetupSeconds))
            {
                Guarded.RunGuardedAsync("CallPage/no answer", DataService.Instance.EndUnansweredCallAsync());
                return;
            }

            Draw();
        }

        private void Draw()
        {
            CallSession call = DataService.Instance.Call;

            if (call == null || !call.Shows)
            {
                Leave();
                return;
            }

            string name = NameCache.Book.NameFor(call.PeerJid);
            if (string.IsNullOrEmpty(name)) name = JidUtils.Normalize(call.PeerJid);

            NameText.Text = string.IsNullOrEmpty(name) ? "" : name;
            InitialsText.Text = Initials(name);
            ShowAvatar(call.PeerJid);

            KindText.Text = call.IsVideo
                ? Loc.Get("Call_Video", "Video call")
                : Loc.Get("Call_Voice", "Voice call");

            string state;
            switch (call.State)
            {
                case CallState.Ringing:
                    state = Loc.Get("Call_Incoming", "Incoming call");
                    break;
                case CallState.Connecting:
                    // The same wait from two ends, said the way the caller hears it.
                    state = call.IsIncoming
                        ? Loc.Get("Call_Connecting", "Connecting...")
                        : Loc.Get("Call_Calling", "Calling...");
                    break;
                case CallState.Active:
                    state = Loc.Get("Call_Active", "In call");
                    break;
                default:
                    state = Loc.Get("Call_Ended", "Call ended");
                    break;
            }

            if (call.State == CallState.Ended && !string.IsNullOrEmpty(call.EndedReason))
            {
                state = state + " (" + call.EndedReason + ")";
            }

            StateText.Text = state;

            // A duration only exists once somebody answered: a call that never connected has
            // no `0:00` to show, it has nothing.
            DurationText.Text = call.WasAnswered ? FormatDuration(call.ConnectedSeconds) : "";
            DurationText.Visibility = call.WasAnswered ? Visibility.Visible : Visibility.Collapsed;

            NoteText.Text = Loc.Get("Call_NoAudio", "No audio yet: this build only signals the call.");
            NoteText.Visibility = call.State == CallState.Active ? Visibility.Visible : Visibility.Collapsed;

            // Accept and Decline are for a call that came in. A call placed from here is
            // waiting for the other phone, and Answer on it was a button that did nothing.
            bool ringing = call.State == CallState.Ringing && call.IsIncoming;
            bool live = call.State == CallState.Connecting || call.State == CallState.Active;
            bool over = call.State == CallState.Ended;

            CallIncomingButtons.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
            CallInProgressButtons.Visibility = (live || over) ? Visibility.Visible : Visibility.Collapsed;

            AcceptCallButton.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
            DeclineCallButton.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
            EndCallButton.Visibility = (live || over) ? Visibility.Visible : Visibility.Collapsed;
            EndCallButton.Content = over ? Loc.Get("Call_Close", "Close") : Loc.Get("Call_End", "End");

            // The two switches, and the same rule as the rest: they belong to a call that is
            // up or being set up. Each tile says which way its switch is, and is lit while it
            // is the unusual way - the microphone off, the sound on the loudspeaker.
            MuteCallButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            SpeakerCallButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;

            MuteCallButton.Content = call.Muted
                ? Loc.Get("Call_Muted", "Microphone off")
                : Loc.Get("Call_Mute", "Microphone on");
            SpeakerCallButton.Content = call.SpeakerOn
                ? Loc.Get("Call_SpeakerOn", "Speaker on")
                : Loc.Get("Call_Speaker", "Speaker off");

            PaintSwitch(MuteCallButton, call.Muted);
            PaintSwitch(SpeakerCallButton, call.SpeakerOn);
        }

        /// <summary>A switch tile: lit while the switch is engaged, dark while it is not.</summary>
        private static void PaintSwitch(Button tile, bool engaged)
        {
            tile.Background = new SolidColorBrush(engaged
                ? Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0)
                : Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
            tile.Foreground = new SolidColorBrush(engaged
                ? Color.FromArgb(0xFF, 0x10, 0x10, 0x10)
                : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        }

        /// <summary>
        /// Leaves the call screen, once. It is not a navigation to the home page: the call was
        /// opened from wherever the person was, and the back stack knows where that is.
        /// </summary>
        private void Leave()
        {
            if (_left) return;

            _left = true;
            if (Frame != null && Frame.CanGoBack) Frame.GoBack();
        }

        /// <summary>The picture of the person, from the row or from the copy on the phone.</summary>
        private void ShowAvatar(string jid)
        {
            if (jid == _avatarJid) return;

            _avatarJid = jid;
            AvatarCircle.Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
            InitialsText.Visibility = Visibility.Visible;

            string data = AvatarCache.Get(jid);
            if (string.IsNullOrEmpty(data))
            {
                Contact row = DataService.Instance.FindContact(jid);
                if (row != null) data = row.AvatarData;
            }

            if (!string.IsNullOrEmpty(data))
            {
                Guarded.RunGuardedAsync("CallPage/avatar", DecodeAvatarAsync(jid, data));
            }
        }

        private async Task DecodeAvatarAsync(string jid, string data)
        {
            BitmapImage bitmap = await ImageHelper.FromBase64Async(data, PicturePixels);
            if (bitmap == null || jid != _avatarJid) return;

            AvatarCircle.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
            InitialsText.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// The microphone switch. It changes the call's own state and nothing audible: there is
        /// no media to cut yet, and the note under the name says exactly that.
        /// </summary>
        private void MuteCall_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.ToggleMute();
        }

        /// <summary>The speaker switch: the earpiece or the loudspeaker.</summary>
        private void SpeakerCall_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.ToggleSpeaker();
        }

        private void AcceptCall_Click(object sender, RoutedEventArgs e)
        {
            Guarded.RunGuardedAsync("CallPage/accept", DataService.Instance.AcceptCallAsync());
        }

        private void DeclineCall_Click(object sender, RoutedEventArgs e)
        {
            Guarded.RunGuardedAsync("CallPage/decline", DataService.Instance.DeclineCallAsync());
        }

        /// <summary>
        /// The button that ends a call and the one that takes an ended call off the screen:
        /// the same gesture to a person, so it is the same button.
        /// </summary>
        private void EndCall_Click(object sender, RoutedEventArgs e)
        {
            CallSession call = DataService.Instance.Call;
            if (call != null && call.State == CallState.Ended)
            {
                DataService.Instance.DismissCall();
                return;
            }

            Guarded.RunGuardedAsync("CallPage/end", DataService.Instance.EndCallAsync());
        }

        /// <summary>The time as a call screen writes it: minutes and seconds, never hours.</summary>
        private static string FormatDuration(int seconds)
        {
            if (seconds < 0) seconds = 0;

            int minutes = seconds / 60;
            return minutes + ":" + (seconds % 60).ToString("00");
        }

        /// <summary>
        /// Two letters for the circle, the same rule the call log uses: a name that starts
        /// with a `+` keeps its two digits, an emoji keeps both halves of its surrogate pair.
        /// </summary>
        private static string Initials(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";

            string trimmed = name.Trim();
            if (trimmed.Length == 0) return "?";

            if (trimmed.StartsWith("+") && trimmed.Length > 1)
            {
                return trimmed.Substring(1, Math.Min(2, trimmed.Length - 1)).ToUpper();
            }

            int first = char.IsHighSurrogate(trimmed[0]) && trimmed.Length > 1 ? 2 : 1;
            return trimmed.Substring(0, first).ToUpper();
        }
    }
}
