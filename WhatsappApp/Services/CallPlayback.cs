using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Streams the call's decoded PCM frames into the Silverlight phone media pipeline.
    /// </summary>
    public sealed class CallPlayback : IDisposable
    {
        private const string PcmWaveFormat = "01000100803E0000007D0000020010000000";

        private readonly CallAudioSource _source;
        private readonly MediaElement _element;
        private readonly CallAudioPlayout _playout;

        private CallPlayback(CallAudioSource source, MediaElement element, CallAudioPlayout playout)
        {
            _source = source;
            _element = element;
            _playout = playout;
        }

        public CallAudioPlayout Playout
        {
            get { return _playout; }
        }

        public static CallPlayback Start(MediaElement element, CallAudioPlayout playout)
        {
            if (element == null || playout == null) return null;

            try
            {
                var source = new CallAudioSource(playout);
                var playback = new CallPlayback(source, element, playout);

                element.AutoPlay = false;
                element.SetSource(source);
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

        public void Dispose()
        {
            _source.Stop();

            try
            {
                _element.Stop();
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/stopping the speaker", ex);
            }
        }

        private sealed class CallAudioSource : MediaStreamSource
        {
            private readonly CallAudioPlayout _playout;
            private MediaStreamDescription _audio;
            private long _nextTimestamp;
            private volatile bool _closed;

            public CallAudioSource(CallAudioPlayout playout)
            {
                _playout = playout;
                AudioBufferLength = CallAudioPlayout.FrameMilliseconds * 3;
            }

            protected override void OpenMediaAsync()
            {
                var streamAttributes = new Dictionary<MediaStreamAttributeKeys, string>();
                streamAttributes[MediaStreamAttributeKeys.CodecPrivateData] = PcmWaveFormat;
                _audio = new MediaStreamDescription(MediaStreamType.Audio, streamAttributes);

                var sourceAttributes = new Dictionary<MediaSourceAttributesKeys, string>();
                sourceAttributes[MediaSourceAttributesKeys.CanSeek] = "false";
                ReportOpenMediaCompleted(sourceAttributes, new[] { _audio });
            }

            protected override void GetSampleAsync(MediaStreamType mediaStreamType)
            {
                if (_closed || mediaStreamType != MediaStreamType.Audio)
                {
                    ReportGetSampleCompleted(null);
                    return;
                }

                try
                {
                    byte[] frame = _playout.Tick();
                    var stream = new MemoryStream(frame, false);
                    var sample = new MediaStreamSample(
                        _audio, stream, 0, frame.Length, _nextTimestamp, null);

                    _nextTimestamp += TimeSpan.FromMilliseconds(CallAudioPlayout.FrameMilliseconds).Ticks;
                    ReportGetSampleCompleted(sample);
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/a sample for the speaker", ex);
                    _closed = true;

                    try
                    {
                        ReportGetSampleCompleted(null);
                    }
                    catch (Exception inner)
                    {
                        Diag.Failed("call audio/a sample that was refused", inner);
                    }
                }
            }

            protected override void SeekAsync(long seekToTime)
            {
                ReportSeekCompleted(_nextTimestamp);
            }

            protected override void GetDiagnosticAsync(MediaStreamSourceDiagnosticKind diagnosticKind)
            {
                ReportGetDiagnosticCompleted(diagnosticKind, 0);
            }

            protected override void SwitchMediaStreamAsync(MediaStreamDescription mediaStreamDescription)
            {
                ReportSwitchMediaStreamCompleted(_audio);
            }

            protected override void CloseMedia()
            {
                _closed = true;
            }

            public void Stop()
            {
                _closed = true;
            }
        }
    }
}
