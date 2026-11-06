using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// One call's audio in both directions: the microphone through `CallAudioSender` into the
    /// media path, and the packets that arrive back out through `CallPlayback`.
    ///
    /// The audio belongs to the call and not to a screen. A person who opens a conversation
    /// while talking keeps hearing and keeps being heard, so what this class follows is the
    /// call's own state - `CallPhase.Active` and a media path whose handshake has proved the
    /// peer's fingerprint - sampled once a second by a timer the app owns.
    ///
    /// The speaker is a `MediaElement` this class creates for the call and puts in a `Popup`,
    /// not the one a page could draw. A `MediaElement` plays only while it is in a live tree,
    /// and `Window.Current.Content` is the frame every navigation in this app casts to, so the
    /// element goes one level aside: the popup overlays nothing (its child is zero-sized) and
    /// survives the page that opened the call.
    ///
    /// The switch on the call screen is `Muted`, and it mutes the sender and not the capture: a
    /// call that stops sending frames is a call the far end decides has gone away.
    /// </summary>
    public static class CallAudioPipe
    {
        /// <summary>How often the call is asked whether its audio should be up: one second, as the call screen draws.</summary>
        private const int TickMilliseconds = 1000;

        private static readonly object _gate = new object();

        private static DispatcherTimer _tick;
        private static Popup _host;
        private static MediaElement _element;

        private static CallAudioSender _sender;
        private static CallOpus.CallEncoder _encoder;
        private static CallOpus.CallDecoder _decoder;
        private static CallPlayback _playback;
        private static CallMediaSession _media;

        private static bool _running;
        private static bool _starting;

        /// <summary>Whether this call's audio is open.</summary>
        public static bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>The microphone switch: frames keep going out, made of silence.</summary>
        public static bool Muted
        {
            set
            {
                CallAudioSender sender = _sender;
                if (sender != null) sender.Muted = value;
            }
        }

        /// <summary>
        /// Starts following the calls of this process. Called once at start-up, on the UI
        /// thread, because the timer and the element are XAML's.
        /// </summary>
        public static void Start()
        {
            if (_tick != null) return;

            _tick = new DispatcherTimer();
            _tick.Interval = TimeSpan.FromMilliseconds(TickMilliseconds);
            _tick.Tick += OnTick;
            _tick.Start();

            Diag.Ok("call audio: the call's audio follows the call and not the screen");
        }

        /// <summary>
        /// Once a second: opens the two directions when a call is up on a trusted path, closes
        /// them when it is not. Nothing here raises an event - a handshake step is not one - so
        /// the question is asked rather than waited for.
        /// </summary>
        private static void OnTick(object sender, object e)
        {
            try
            {
                CallSession call = DataService.Instance.Call;
                CallMediaSession media = DataService.Instance.CallAudio;
                CallMediaStatus status = DataService.Instance.MediaStatus;

                bool live = call != null && call.Phase == CallPhase.Active
                    && media != null && status != null && status.Trusted;

                if (!live)
                {
                    if (_running)
                    {
                        Guarded.RunGuardedAsync("call audio/closed", StopAsync());
                    }

                    return;
                }

                Muted = call.Muted;

                if (_running || _starting) return;

                _starting = true;
                Guarded.RunGuardedAsync("call audio/opened",
                    StartAsync(media, DataService.Instance.SendCallAudio, call.Muted));
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/the call's tick", ex);
            }
        }

        /// <summary>
        /// Opens both directions of one call. `send` is the media path's `SendAudio` and
        /// `media` is the audio of the handshake that just finished; the speaker is this
        /// class's own, made on the calling thread, which is the UI thread. `muted` is where the
        /// call screen's switch already was: a call that was muted before the far end answered
        /// starts muted, and not a second of the room goes out first.
        /// </summary>
        public static async Task<bool> StartAsync(CallMediaSession media, CallAudioSender.Send send,
            bool muted)
        {
            CallOpus.CallEncoder encoder = null;
            CallOpus.CallDecoder decoder = null;
            CallPlayback playback = null;

            try
            {
                if (media == null || send == null) return false;
                if (_running) return false;

                encoder = CallOpus.CreateEncoder();
                decoder = CallOpus.CreateDecoder();

                if (encoder == null || decoder == null)
                {
                    Diag.Ok("call audio: this package has no Opus codec, so the call carries no audio");
                    return false;
                }

                var sender = new CallAudioSender(encoder.Encode, send);
                sender.Muted = muted;

                // Told what is queued behind a missing packet, the playout stops waiting for one
                // that was overtaken: the wait would be silence over audio already in hand.
                var playout = new CallAudioPlayout(decoder.Decode, media.Take, media.SkipGap,
                    delegate { return media.Pending > 0; });

                playback = CallPlayback.Start(Player(), playout);
                if (playback == null) return false;

                lock (_gate)
                {
                    _encoder = encoder;
                    _decoder = decoder;
                    _sender = sender;
                    _playback = playback;
                    _media = media;
                    _running = true;
                }

                // The microphone last: a speaker that is up and a capture that is not is a
                // call where one direction works, and both are named in the log.
                if (!await CallMicrophone.StartAsync(sender))
                {
                    Diag.Ok("call audio: the call can hear but not speak, because the microphone did not open");
                }

                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/starting the call's audio", ex);

                if (playback != null) playback.Dispose();
                if (encoder != null) encoder.Dispose();
                if (decoder != null) decoder.Dispose();

                return false;
            }
            finally
            {
                _starting = false;
            }
        }

        /// <summary>
        /// Closes both directions and writes the one line a call leaves behind: what went out,
        /// what was heard, and what had to be waited for or given up on.
        /// </summary>
        public static async Task StopAsync()
        {
            CallAudioSender sender;
            CallOpus.CallEncoder encoder;
            CallOpus.CallDecoder decoder;
            CallPlayback playback;
            CallMediaSession media;

            lock (_gate)
            {
                if (!_running && _sender == null && _playback == null) return;

                _running = false;

                sender = _sender;
                encoder = _encoder;
                decoder = _decoder;
                playback = _playback;
                media = _media;

                _sender = null;
                _encoder = null;
                _decoder = null;
                _playback = null;
                _media = null;
            }

            await CallMicrophone.StopAsync();

            if (playback != null) playback.Dispose();
            if (encoder != null) encoder.Dispose();
            if (decoder != null) decoder.Dispose();

            ClosePlayer();

            if (sender == null) return;

            Diag.Ok("call audio: " + sender.Frames + " frame(s) out, " + sender.Sent
                + " packet(s) sent, " + sender.Refused + " refused, " + sender.Dropped
                + " dropped by the encoder"
                + (playback == null ? "" : "; " + playback.Playout.Played + " frame(s) played, "
                    + playback.Playout.Waited + " waited, " + playback.Playout.Skipped
                    + " gap(s) lost, " + playback.Playout.Undecodable + " undecodable")
                + (media == null ? "" : "; a packet waited " + media.AverageDelayMilliseconds
                    + " ms on average and " + media.MaxDelayMilliseconds + " ms at worst to play"));
        }

        /// <summary>
        /// The speaker of this call, in a popup that is opened for it. A player built on this
        /// thread and left out of every tree is a player that raises nothing and plays
        /// nothing, which is why the popup is there.
        /// </summary>
        private static MediaElement Player()
        {
            if (_element != null) return _element;

            var element = new MediaElement();
            element.Width = 0;
            element.Height = 0;
            element.AutoPlay = false;
            element.IsLooping = false;
            element.IsHitTestVisible = false;

            var host = new Popup();
            host.Child = element;
            host.IsLightDismissEnabled = false;
            host.IsOpen = true;

            _element = element;
            _host = host;

            return element;
        }

        /// <summary>Lets the speaker go with the call: the next one builds its own.</summary>
        private static void ClosePlayer()
        {
            Popup host = _host;
            _host = null;
            _element = null;

            if (host == null) return;

            try
            {
                host.IsOpen = false;
                host.Child = null;
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/letting the speaker go", ex);
            }
        }
    }
}
