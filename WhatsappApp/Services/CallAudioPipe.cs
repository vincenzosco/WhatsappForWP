using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// One call's audio in both directions: the microphone through `CallAudioSender` into the
    /// media path, and the packets that arrive back out through `CallPlayback`.
    ///
    /// The pieces are separate because each one answers a different question - the sender
    /// turns a stream into frames, the microphone owns the platform's capture, the playback
    /// owns the speaker - and a call is the three of them alive at once, which is the only
    /// thing this class decides. It starts when the handshake has proved the peer's
    /// fingerprint, because `SendAudio` refuses a frame before that, and it stops when the call
    /// ends: a microphone left open is a call that looks live and is not.
    ///
    /// The switch on the call screen is `Muted`, and it mutes the sender and not the capture: a
    /// call that stops sending frames is a call the far end decides has gone away.
    /// </summary>
    public static class CallAudioPipe
    {
        private static readonly object _gate = new object();

        private static CallAudioSender _sender;
        private static CallOpus.CallEncoder _encoder;
        private static CallOpus.CallDecoder _decoder;
        private static CallPlayback _playback;

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
        /// Opens both directions of one call. `send` is the media path's `SendAudio`, `media`
        /// is the audio of the handshake that just finished, and `element` is the page's
        /// hidden player - a `MediaElement` belongs to the UI thread and this method is called
        /// from it.
        /// </summary>
        public static async Task<bool> StartAsync(MediaElement element, CallMediaSession media,
            CallAudioSender.Send send, bool muted)
        {
            if (element == null || media == null || send == null) return false;
            if (_running || _starting) return false;

            _starting = true;

            CallOpus.CallEncoder encoder = null;
            CallOpus.CallDecoder decoder = null;
            CallPlayback playback = null;

            try
            {
                encoder = CallOpus.CreateEncoder();
                decoder = CallOpus.CreateDecoder();

                if (encoder == null || decoder == null)
                {
                    Diag.Ok("call audio: this package has no Opus codec, so the call carries no audio");
                    return false;
                }

                var sender = new CallAudioSender(encoder.Encode, send);
                sender.Muted = muted;

                var playout = new CallAudioPlayout(decoder.Decode, media.Take, media.SkipGap);

                playback = CallPlayback.Start(element, playout);
                if (playback == null) return false;

                lock (_gate)
                {
                    _encoder = encoder;
                    _decoder = decoder;
                    _sender = sender;
                    _playback = playback;
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

            lock (_gate)
            {
                if (!_running && _sender == null && _playback == null) return;

                _running = false;

                sender = _sender;
                encoder = _encoder;
                decoder = _decoder;
                playback = _playback;

                _sender = null;
                _encoder = null;
                _decoder = null;
                _playback = null;
            }

            await CallMicrophone.StopAsync();

            if (playback != null) playback.Dispose();
            if (encoder != null) encoder.Dispose();
            if (decoder != null) decoder.Dispose();

            if (sender == null) return;

            Diag.Ok("call audio: " + sender.Frames + " frame(s) out, " + sender.Sent
                + " packet(s) sent, " + sender.Refused + " refused, " + sender.Dropped
                + " dropped by the encoder"
                + (playback == null ? "" : "; " + playback.Playout.Played + " frame(s) played, "
                    + playback.Playout.Waited + " waited, " + playback.Playout.Skipped
                    + " gap(s) lost, " + playback.Playout.Undecodable + " undecodable"));
        }
    }
}
