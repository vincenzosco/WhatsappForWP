using System;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The speaker of a call: a `MediaStreamSource` asks for one sample at a time and gets a
    /// `CallAudioPlayout` frame, and the `MediaElement` behind it plays the result.
    ///
    /// `MediaElement` alone plays files, and a call has no file: its audio exists only as
    /// packets arriving at their own pace. `MediaStreamSource` is the other door - the player
    /// calls `SampleRequested` on its own clock, and what answers is one frame of twenty
    /// milliseconds - so the clock is the player's and this class never keeps one.
    ///
    /// The pause in a call is real: a player that is handed a shorter stream ends it, so the
    /// playout's silence is played like any other frame and a lost packet leaves a gap of the
    /// right length. What is played is 16 kHz mono PCM, which is what the decoder makes.
    /// </summary>
    public sealed class CallPlayback : IDisposable
    {
        /// <summary>The MIME type of a stream of raw PCM, which is all this source carries.</summary>
        private const string MimeType = "audio/wav";

        private readonly MediaStreamSource _source;
        private readonly MediaElement _element;
        private readonly CallAudioPlayout _playout;

        private bool _closed;

        // Where the next frame goes on the player's timeline. A sample's timestamp is the
        // position it is played at, so it has to move with every frame; a stream whose frames
        // all say the same instant is a stream the player has no order for.
        private TimeSpan _next;

        private CallPlayback(MediaStreamSource source, MediaElement element, CallAudioPlayout playout)
        {
            _source = source;
            _element = element;
            _playout = playout;
        }

        /// <summary>The clock of what is heard, for the counters a call's last line carries.</summary>
        public CallAudioPlayout Playout
        {
            get { return _playout; }
        }

        /// <summary>
        /// Plays one call's audio through `element`, which has to be on the UI thread: a
        /// `MediaElement` is a XAML control and one built on a network thread throws. Null
        /// when there is nothing to play with.
        /// </summary>
        public static CallPlayback Start(MediaElement element, CallAudioPlayout playout)
        {
            if (element == null || playout == null) return null;

            try
            {
                var properties = AudioEncodingProperties.CreatePcm(
                    (uint)CallAudioSender.SampleRate, 1, 16);

                var source = new MediaStreamSource(new AudioStreamDescriptor(properties));
                var playback = new CallPlayback(source, element, playout);

                source.SampleRequested += playback.OnSampleRequested;

                // `SetMediaStreamSource` and not `SetSource`: this platform's `MediaElement`
                // offers only the stream overload of the latter, and a `MediaStreamSource` is
                // the one thing it takes as a live source.
                element.AutoPlay = false;
                element.SetMediaStreamSource(source);
                element.Play();

                Diag.Ok("call audio: the speaker plays " + CallAudioSender.SampleRate
                    + " Hz mono PCM in " + CallAudioPlayout.FrameMilliseconds + " ms frames");

                return playback;
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/opening the speaker", ex);
                return null;
            }
        }

        /// <summary>
        /// One frame for the player: always twenty milliseconds of audio, silence included, so
        /// the stream the player reads has one shape for the whole call.
        /// </summary>
        private void OnSampleRequested(MediaStreamSource sender,
            MediaStreamSourceSampleRequestedEventArgs args)
        {
            try
            {
                if (_closed)
                {
                    args.Request.Sample = null;
                    return;
                }

                byte[] frame = _playout.Tick();

                var buffer = new DataWriter();
                buffer.WriteBytes(frame);
                IBuffer bytes = buffer.DetachBuffer();
                buffer.Dispose();

                TimeSpan duration = TimeSpan.FromMilliseconds(CallAudioPlayout.FrameMilliseconds);

                MediaStreamSample sample = MediaStreamSample.CreateFromBuffer(bytes, _next);
                sample.Duration = duration;

                _next += duration;

                args.Request.Sample = sample;
            }
            catch (Exception ex)
            {
                // A player that is handed nothing ends the stream, which on a call is a call
                // that goes quiet for good: the silence is the safer answer.
                Diag.Failed("call audio/a sample for the speaker", ex);

                try
                {
                    args.Request.Sample = null;
                }
                catch (Exception inner)
                {
                    Diag.Failed("call audio/a sample that was refused", inner);
                }
            }
        }

        /// <summary>
        /// Stops the player and lets the source go. The element stays where it is: the next call
        /// sets a source on it again, and a page that is gone takes it with it.
        /// </summary>
        public void Dispose()
        {
            _closed = true;

            try
            {
                _source.SampleRequested -= OnSampleRequested;
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/letting the source go", ex);
            }

            try
            {
                _element.Stop();
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/stopping the speaker", ex);
            }
        }
    }
}
