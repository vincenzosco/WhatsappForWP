using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Protocol.Media;
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
    /// offered forever: the read loop logged "a call is already up, so. was not called" on
    /// the next dial, and no new call could be placed until the app was restarted. That is in
    /// the phone's own log,.
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

        /// <summary>The side of the picture, in the 260 px circle.</summary>
        private const int PicturePixels = 260;

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

            // The labels, not the content: each button is an icon with a caption under it now,
            // and setting `Content` here replaced the whole stack with a bare string.
            AcceptLabel.Text = Loc.Get("Call_Accept", "Accept");
            DeclineLabel.Text = Loc.Get("Call_Decline", "Decline");
            EndLabel.Text = Loc.Get("Call_End", "End");

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
            switch (call.Phase)
            {
                case CallPhase.Ringing:
                    state = Loc.Get("Call_Incoming", "Incoming call");
                    break;
                case CallPhase.Connecting:
                    // The same wait from two ends, said the way the caller hears it.
                    state = call.IsIncoming
                        ? Loc.Get("Call_Connecting", "Connecting...")
                        : Loc.Get("Call_Calling", "Calling...");
                    break;
                case CallPhase.Active:
                    state = Loc.Get("Call_Active", "In call");
                    break;
                default:
                    state = Loc.Get("Call_Ended", "Call ended");
                    break;
            }

            if (call.Phase == CallPhase.Ended && !string.IsNullOrEmpty(call.EndedReason))
            {
                state = state + " (" + call.EndedReason + ")";
            }

            StateText.Text = state;

            // A duration only exists once somebody answered: a call that never connected has
            // no `0:00` to show, it has nothing.
            DurationText.Text = call.WasAnswered ? FormatDuration(call.ConnectedSeconds) : "";
            DurationText.Visibility = call.WasAnswered ? Visibility.Visible : Visibility.Collapsed;

            NoteText.Text = Loc.Get("Call_NoAudio", "No audio yet: this build only signals the call.");
            NoteText.Visibility = call.Phase == CallPhase.Active ? Visibility.Visible : Visibility.Collapsed;

            DrawMedia(call.Phase == CallPhase.Connecting || call.Phase == CallPhase.Active);

            // Accept and Decline are for a call that came in. A call placed from here is
            // waiting for the other phone, and Answer on it was a button that did nothing.
            bool ringing = call.Phase == CallPhase.Ringing && call.IsIncoming;
            bool live = call.Phase == CallPhase.Connecting || call.Phase == CallPhase.Active;
            bool over = call.Phase == CallPhase.Ended;

            CallIncomingButtons.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
            CallInProgressButtons.Visibility = (live || over) ? Visibility.Visible : Visibility.Collapsed;

            AcceptCallButton.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
            DeclineCallButton.Visibility = ringing ? Visibility.Visible : Visibility.Collapsed;
            EndCallButton.Visibility = (live || over) ? Visibility.Visible : Visibility.Collapsed;
            EndLabel.Text = over ? Loc.Get("Call_Close", "Close") : Loc.Get("Call_End", "End");

            // A call that is over has one control, "Close": the two switches above are
            // collapsed, and a button pinned to the fifth column of a five-column row is one
            // button sitting at the right edge with three empty slots beside it. Across the
            // whole row is where a single button belongs, and the live call keeps the row of
            // three it was designed for.
            Grid.SetColumn(EndCallButton, over ? 0 : 4);
            Grid.SetColumnSpan(EndCallButton, over ? 5 : 1);

            // The two switches, and the same rule as the rest: they belong to a call that is
            // up or being set up. Each tile says which way its switch is, and is lit while it
            // is the unusual way - the microphone off, the sound on the loudspeaker.
            MuteCallButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            SpeakerCallButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;

            MuteLabel.Text = call.Muted
                ? Loc.Get("Call_Muted", "Microphone off")
                : Loc.Get("Call_Mute", "Microphone on");
            SpeakerLabel.Text = call.SpeakerOn
                ? Loc.Get("Call_SpeakerOn", "Speaker on")
                : Loc.Get("Call_Speaker", "Speaker off");

            PaintSwitch(MuteCallButton, call.Muted);
            PaintSwitch(SpeakerCallButton, call.SpeakerOn);
        }

        /// <summary>
        /// The media path, in the numbers the transport already keeps: the nominated pair, the
        /// state of the handshake, and how much has crossed the socket. Drawn from the same
        /// one-second tick as the duration, because none of these numbers raises an event on its
        /// own - a datagram arrives and a handshake step moves, and no stanza is sent about
        /// either.
        ///
        /// It says "candidates" and not "path ready": the count is the local addresses the
        /// offer or the answer carried, which is a fact this phone knows. A path that has not
        /// been nominated yet is the normal state for the first second of a call, and a screen
        /// that called that a failure would be wrong about every call.
        /// </summary>
        private void DrawMedia(bool live)
        {
            CallMediaStatus media = DataService.Instance.MediaStatus;

            if (!live || media == null || !media.Attached)
            {
                MediaText.Text = "";
                MediaText.Visibility = Visibility.Collapsed;
                return;
            }

            var lines = new List<string>();

            string state;
            switch (media.State)
            {
                case CallTransportState.Waiting:
                    state = Loc.Get("Call_MediaWaiting", "waiting for the other phone");
                    break;
                case CallTransportState.Handshaking:
                    state = Loc.Get("Call_MediaHandshaking", "handshake running");
                    break;
                case CallTransportState.Ready:
                    state = Loc.Get("Call_MediaReady", "connected");
                    break;
                default:
                    state = Loc.Get("Call_MediaFailed", "failed");
                    break;
            }

            lines.Add(string.Format(Loc.Get("Call_MediaLine", "Media: {0}"), state));

            lines.Add(string.Format(Loc.Get("Call_MediaCandidates", "Candidates: {0}"),
                media.Candidates));

            lines.Add(string.Format(Loc.Get("Call_MediaPeer", "Peer: {0}"),
                string.IsNullOrEmpty(media.Peer)
                    ? Loc.Get("Call_MediaNoPeer", "none yet")
                    : media.Peer));

            lines.Add(string.Format(Loc.Get("Call_MediaPackets", "Packets: {0} in, {1} out"),
                media.Received, media.Sent));

            int dropped = media.Dropped + media.NotFromPeer + media.MediaRefused;
            if (dropped > 0)
            {
                lines.Add(string.Format(Loc.Get("Call_MediaDropped", "Dropped: {0}"), dropped));
            }

            // A handshake can finish without proving the certificate the signalling named, and
            // audio is refused on one: a line that said "connected" alone would be a promise
            // the path does not keep.
            if (media.State == CallTransportState.Ready && !media.Trusted)
            {
                lines.Add(Loc.Get("Call_MediaUntrusted", "Handshake not verified"));
            }

            if (!string.IsNullOrEmpty(media.Failure))
            {
                lines.Add(media.Failure);
            }

            MediaText.Text = string.Join(Environment.NewLine, lines.ToArray());
            MediaText.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// A switch button: green while the switch is engaged, dark while it is not.
        ///
        /// Green and not white, because the glyph and the label are white: a white tile with
        /// a white icon was an engaged switch nobody could read. The tile is what changes,
        /// never the ink.
        /// </summary>
        private static void PaintSwitch(Button tile, bool engaged)
        {
            tile.Background = new SolidColorBrush(engaged
                ? Color.FromArgb(0xFF, 0x25, 0xD3, 0x66)
                : Color.FromArgb(0xFF, 0x2A, 0x2A, 0x2A));
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
        private void MuteCallButton_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.ToggleMute();
        }

        /// <summary>The speaker switch: the earpiece or the loudspeaker.</summary>
        private void SpeakerCallButton_Click(object sender, RoutedEventArgs e)
        {
            DataService.Instance.ToggleSpeaker();
        }

        private void AcceptCallButton_Click(object sender, RoutedEventArgs e)
        {
            Guarded.RunGuardedAsync("CallPage/accept", DataService.Instance.AcceptCallAsync());
        }

        private void DeclineCallButton_Click(object sender, RoutedEventArgs e)
        {
            Guarded.RunGuardedAsync("CallPage/decline", DataService.Instance.DeclineCallAsync());
        }

        /// <summary>
        /// The button that ends a call and the one that takes an ended call off the screen:
        /// the same gesture to a person, so it is the same button.
        /// </summary>
        private void EndCallButton_Click(object sender, RoutedEventArgs e)
        {
            CallSession call = DataService.Instance.Call;
            if (call != null && call.Phase == CallPhase.Ended)
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
