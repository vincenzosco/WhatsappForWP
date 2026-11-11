using System;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Streams;
using WhatsappApp.Protocol.Media;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The microphone of a call: `MediaCapture` records 16 kHz mono PCM into a stream, and the
    /// bytes that land in it are handed to `CallAudioSender` as they arrive.
    ///
    /// A voice note is recorded into a file and finished when the recording stops
    /// (`AudioRecorder`), which a call cannot use: a call needs the samples while they are
    /// being spoken. The door for that on this platform is `StartRecordToStreamAsync` - an
    /// `InMemoryRandomAccessStream` grows as the recorder writes it, and a reader at a
    /// position of its own reads only what it has not read yet.
    ///
    /// The stream starts with a WAV header, and that header is the other thing this file
    /// knows: the first bytes are walked until the `data` chunk, whose body is the PCM. The
    /// format is checked there rather than assumed - a capture in M4A, at another rate or in
    /// two channels is a call this phone cannot speak, and the line it writes is the whole
    /// answer to why the far end hears nothing.
    ///
    /// Media Foundation has no device enumerator on this phone (`CallAudioProbe`), so
    /// `MediaCapture` is the only way to the microphone, and the recorder owns it: this class
    /// and `AudioRecorder` are never open at the same time.
    /// </summary>
    public static class CallMicrophone
    {
        /// <summary>
        /// How often the stream is read: half a frame, so the samples of one frame are on their
        /// way while the rest of it is still being recorded instead of waiting for the next full
        /// window. One pass reads whatever has landed, so this is a poll and not a buffer.
        /// </summary>
        private const int PumpMilliseconds = 10;

        /// <summary>How much of the stream is read per pass, which is forty frames.</summary>
        private const int MaxRead = 8192;

        /// <summary>How much of the header is kept: `fmt ` and `data` in the first few hundred bytes.</summary>
        private const int HeaderBytes = 1024;

        /// <summary>The file the call's voice is recorded into, in `LocalFolder`, deleted at the end.</summary>
        private const string RecorderFile = "call-audio.wav";

        private static readonly object _gate = new object();
        private static readonly byte[] _head = new byte[HeaderBytes];

        private static MediaCapture _capture;
        private static IRandomAccessStream _stream;
        private static StorageFile _file;
        private static CallAudioSender _sender;

        private static bool _running;
        private static bool _starting;

        // Which call the pump that is running belongs to. A pump outlives one call's reset:
        // it can be inside a read when the call ends and the next one starts, and a read that
        // belongs to the call before must not move the reader of the call after.
        private static int _run;

        // The microphone that is being closed. A call answered while the previous capture is
        // still stopping would open a second `MediaCapture` on a device that is still in use.
        private static Task _closing;

        private static long _read;
        private static int _headLength;
        private static bool _headerDone;
        private static int _dataAt = -1;
        private static string _refusal;

        private static int _bytes;
        private static long _startedMs;

        /// <summary>Whether the microphone of a call is open.</summary>
        public static bool IsRunning
        {
            get { return _running; }
        }

        /// <summary>How many bytes of PCM this call's microphone has produced.</summary>
        public static int Bytes
        {
            get { return _bytes; }
        }

        /// <summary>
        /// Opens the microphone for one call's sender, or answers false with the reason in the
        /// log: a denied capability, a phone with no WAV profile, an engine that does not
        /// answer. The sender is not created here - it is the caller's, because it is what
        /// knows the encoder and the socket.
        /// </summary>
        public static async Task<bool> StartAsync(CallAudioSender sender)
        {
            if (sender == null) return false;
            if (_running) return false;
            if (_starting) return false;

            _starting = true;

            MediaCapture capture = null;
            IRandomAccessStream stream = null;
            StorageFile file = null;
            bool inMemory = false;
            int run;

            // The voice of a call goes to a file and not to memory. A WAV at this rate grows by
            // thirty-two kilobytes a second, so twenty minutes of talking is thirty-eight
            // megabytes held for audio nothing ever reads twice - a fifth of this phone's whole
            // 185 MB budget, while the memory watcher is busy dropping pictures to stay under
            // it. The file is deleted when the call ends.
            try
            {
                StorageFile created = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    RecorderFile, CreationCollisionOption.ReplaceExisting);

                stream = await created.OpenAsync(FileAccessMode.ReadWrite);
                file = created;
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/the microphone's file", ex);

                file = null;
                stream = null;
            }

            if (stream == null)
            {
                stream = new InMemoryRandomAccessStream();
                inMemory = true;

                Diag.Ok("call audio: the call's voice is held in memory, because the file did not open");
            }

            // The previous call is still letting its microphone go: opening on top of it fails
            // with a device in use, and the line that would follow says a call has no voice.
            Task closing = _closing;
            if (closing != null)
            {
                try
                {
                    await closing;
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/waiting for the microphone to close", ex);
                }
            }

            try
            {
                // Audio only: the default asks for video as well, which is a different
                // capability and a different consent.
                var settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.Audio;

                capture = new MediaCapture();
                await capture.InitializeAsync(settings);

                MediaEncodingProfile profile = null;

                // WAV and nothing else: the encoder takes samples, and an M4A would have to be
                // AAC-decoded by something this app cannot reach.
                try
                {
                    profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/the wav profile", ex);
                }

                if (profile == null)
                {
                    Diag.Ok("call audio: this phone has no WAV profile, so a call has no microphone");
                    Release(capture, stream);
                    Forget(file);
                    return false;
                }

                // A recorder that will not write to a file is asked again in memory. The
                // second start cannot await inside the catch that noticed it, so it runs
                // after the notice instead.
                bool refusedFile = false;

                try
                {
                    await capture.StartRecordToStreamAsync(profile, stream);
                }
                catch (Exception ex)
                {
                    if (inMemory) throw;

                    Diag.Failed("call audio/the microphone refused the file", ex);
                    refusedFile = true;
                }

                if (refusedFile)
                {
                    // The call keeps its voice in memory instead of losing the microphone.
                    Release(null, stream);
                    Forget(file);

                    file = null;
                    stream = new InMemoryRandomAccessStream();
                    inMemory = true;

                    await capture.StartRecordToStreamAsync(profile, stream);
                }

                lock (_gate)
                {
                    _capture = capture;
                    _stream = stream;
                    _file = file;
                    _sender = sender;

                    _read = 0;
                    _headLength = 0;
                    _headerDone = false;
                    _dataAt = -1;
                    _refusal = null;

                    _bytes = 0;
                    _startedMs = NowMilliseconds();
                    _running = true;
                    run = ++_run;
                }

                Guarded.RunGuarded("call audio/the pump", delegate { return PumpAsync(run); });

                Diag.Ok("call audio: the microphone records into " + (inMemory ? "memory" : "a file")
                    + " as a WAV at " + CallAudioSender.SampleRate + " Hz mono");

                return true;
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/opening the microphone", ex);
                Release(capture, stream);
                Forget(file);
                return false;
            }
            finally
            {
                _starting = false;
            }
        }

        /// <summary>
        /// Closes the microphone only if it is still the one recording for `sender`. A call that
        /// ended while its capture was opening is still holding the device when the next call
        /// starts, and closing the newer capture because the older call asked would leave the
        /// call being answered with no voice at all.
        /// </summary>
        public static Task StopAsync(CallAudioSender sender)
        {
            lock (_gate)
            {
                if (sender != null && !ReferenceEquals(_sender, sender)) return Task.FromResult(true);
            }

            return StopAsync();
        }

        /// <summary>
        /// Closes the microphone and says what the call's own voice cost: how many bytes came
        /// out of it, over how long, and - when the header turned out to be something else -
        /// why it never became audio.
        /// </summary>
        public static Task StopAsync()
        {
            MediaCapture capture;
            IRandomAccessStream stream;
            StorageFile file;

            lock (_gate)
            {
                // The number moves, so a pump that is between two reads finds itself out of
                // date at the next one and stops instead of reading into the next call.
                _run++;
                _running = false;
                _sender = null;

                capture = _capture;
                stream = _stream;
                file = _file;
                _capture = null;
                _stream = null;
                _file = null;
            }

            Task closing = CloseAsync(capture, stream, file);
            _closing = closing;

            return closing;
        }

        /// <summary>
        /// Stops the recorder and lets the device and the stream go, then says what the call's
        /// own voice cost. Nothing is read here that another thread writes without the lock: a
        /// 64-bit field on this platform is two words, and half of each is not a number.
        /// </summary>
        private static async Task CloseAsync(MediaCapture capture, IRandomAccessStream stream,
            StorageFile file)
        {
            bool stopped = capture == null;

            if (capture != null)
            {
                try
                {
                    await capture.StopRecordAsync();
                    stopped = true;
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/stopping the microphone", ex);
                }
            }

            if (capture != null)
            {
                try
                {
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/disposing the microphone", ex);
                }
            }

            if (stream != null)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/disposing the stream", ex);
                }
            }

            Forget(file);

            if (capture != null)
            {
                int bytes;
                long startedMs;

                lock (_gate)
                {
                    bytes = _bytes;
                    startedMs = _startedMs;
                }

                Diag.Ok("call audio: the microphone is closed after " + (NowMilliseconds() - startedMs)
                    + " ms and " + bytes + " byte(s) of PCM, and "
                    + (file == null ? "no file was left behind" : "its file is gone")
                    + (stopped ? "" : "; the recorder did not stop") + (Description()));
            }
        }

        /// <summary>What went wrong, as one clause for the last line, or an empty string.</summary>
        private static string Description()
        {
            string refusal;
            lock (_gate) refusal = _refusal;

            return string.IsNullOrEmpty(refusal) ? "" : "; " + refusal;
        }

        /// <summary>
        /// Reads what the recorder has written since the last pass and gives it to the sender,
        /// twenty milliseconds at a time. The loop is the clock and the reader at once: a
        /// reader on a timer of its own would be a second clock the sender has to trust.
        /// </summary>
        private static async Task PumpAsync(int run)
        {
            while (true)
            {
                CallAudioSender sender;
                IRandomAccessStream stream;

                lock (_gate)
                {
                    if (!_running || _run != run) return;

                    sender = _sender;
                    stream = _stream;
                }

                if (sender == null || stream == null) return;

                try
                {
                    await Task.Delay(PumpMilliseconds);

                    ulong size = stream.Size;
                    long read = _read;

                    if ((long)size <= read) continue;

                    int wanted = (int)Math.Min((ulong)MaxRead, size - (ulong)read);

                    // The reader is closed on every path of this turn, including a
                    // LoadAsync that throws: a pump that fails once per attempt used to
                    // leave one reader behind per attempt, for the length of the call.
                    var reader = new DataReader(stream.GetInputStreamAt((ulong)read));

                    int got;
                    byte[] bytes;

                    try
                    {
                        await reader.LoadAsync((uint)wanted);

                        got = (int)reader.UnconsumedBufferLength;
                        if (got <= 0) continue;

                        bytes = new byte[got];
                        reader.ReadBytes(bytes);
                    }
                    finally
                    {
                        reader.Dispose();
                    }

                    lock (_gate)
                    {
                        // The call ended and another began while this read was in flight: its
                        // reader is not this one any more, and moving it would cut the first
                        // frames of the new call. The reader is already disposed above; closing
                        // it again here was a second close of one object, thrown from inside a
                        // lock, which is the one place an exception stops a call's microphone.
                        if (_run != run) return;

                        _read += got;
                    }

                    if (!Feed(sender, bytes))
                    {
                        Close();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    // A stream the recorder closed under the reader is the end of the call,
                    // and one pass that throws is one pass: the loop says so and stops.
                    Diag.Failed("call audio/reading the microphone", ex);
                    Close();

                    return;
                }
            }
        }

        /// <summary>
        /// One read of the stream, behind the header. Until the `data` chunk is found nothing
        /// is PCM, and the walk is what finds it; after it, every byte is audio.
        /// </summary>
        private static bool Feed(CallAudioSender sender, byte[] bytes)
        {
            if (_headerDone)
            {
                _bytes += bytes.Length;
                sender.Push(bytes, 0, bytes.Length);
                return true;
            }

            int room = HeaderBytes - _headLength;
            int take = Math.Min(room, bytes.Length);

            Array.Copy(bytes, 0, _head, _headLength, take);
            _headLength += take;

            if (!FindData())
            {
                if (_refusal != null) return false;

                // A header that does not fit in the window is not a header this phone writes:
                // refusing is better than reading the middle of one as samples.
                if (_headLength == HeaderBytes)
                {
                    _refusal = "the capture wrote " + HeaderBytes
                        + " byte(s) with no data chunk, so it is not a WAV";
                    Diag.Ok("call audio: " + _refusal);
                    return false;
                }

                return true;
            }

            _headerDone = true;

            int fromHead = _dataAt;
            int inHead = _headLength - fromHead;

            if (inHead > 0)
            {
                _bytes += inHead;
                sender.Push(_head, fromHead, inHead);
            }

            int surplus = bytes.Length - take;
            if (surplus > 0)
            {
                _bytes += surplus;
                sender.Push(bytes, take, surplus);
            }

            Diag.Ok("call audio: the first " + _bytes + " byte(s) of PCM are behind a "
                + fromHead + "-byte header, which is what a call's microphone gives");

            return true;
        }

        /// <summary>
        /// Gives up this call's microphone: a stream that is not the format a call encodes, or
        /// one the recorder took away under the reader, leaves nothing to pump. A pump left
        /// running would read a stream nobody consumes for the rest of the call, at eight
        /// kilobytes a pass, and the recording would grow behind it.
        /// </summary>
        private static void Close()
        {
            lock (_gate)
            {
                if (!_running) return;

                _run++;
                _running = false;
                _sender = null;

                if (_refusal == null) _refusal = "the stream could not be read";
            }

            Guarded.RunGuarded("call audio/closing the microphone", StopAsync);
        }

        /// <summary>
        /// Walks the bytes kept so far until the `data` chunk, checking the format on the way.
        /// True means `_dataAt` is the first sample; false means the header is not complete yet
        /// or this is not a capture a call can encode.
        /// </summary>
        private static bool FindData()
        {
            if (_headLength < 12) return false;

            if (Text(0, 4) != "RIFF" || Text(8, 4) != "WAVE")
            {
                _refusal = "the capture is not a WAV";
                Diag.Ok("call audio: " + _refusal + ", so a call has no microphone");
                return false;
            }

            int at = 12;

            while (at + 8 <= _headLength)
            {
                string id = Text(at, 4);
                int size = (int)Little(at + 4, 4);
                int body = at + 8;

                if (id == "data")
                {
                    // A WAV that has just started recording has its data chunk with a
                    // placeholder length: the samples are what follows, whatever it says.
                    _dataAt = body;
                    return true;
                }

                if (id == "fmt " && size >= 16 && body + 16 <= _headLength)
                {
                    int format = (int)Little(body, 2);
                    int channels = (int)Little(body + 2, 2);
                    int rate = (int)Little(body + 4, 4);
                    int bits = (int)Little(body + 14, 2);

                    if (format != 1 || channels != 1 || rate != CallAudioSender.SampleRate || bits != 16)
                    {
                        _refusal = "the microphone writes " + rate + " Hz, " + channels
                            + " channel(s), " + bits + " bit(s), format " + format
                            + ", and a call needs " + CallAudioSender.SampleRate
                            + " Hz mono 16-bit PCM";

                        Diag.Ok("call audio: " + _refusal);
                        return false;
                    }
                }

                if (size <= 0) return false;

                at = body + size + (size % 2);
            }

            return false;
        }

        private static string Text(int at, int length)
        {
            if (at < 0 || at + length > _headLength) return string.Empty;

            return System.Text.Encoding.UTF8.GetString(_head, at, length);
        }

        private static long Little(int at, int length)
        {
            long value = 0;

            for (int i = length - 1; i >= 0; i--)
            {
                if (at + i < 0 || at + i >= _headLength) return 0;

                value = (value << 8) | _head[at + i];
            }

            return value;
        }

        /// <summary>
        /// Deletes the call's file, off the caller's thread: it holds the voice of a call that
        /// is over, and thirty-eight megabytes of it is the difference between a phone that
        /// answers and a phone that is closed for memory.
        /// </summary>
        private static void Forget(StorageFile file)
        {
            if (file == null) return;

            Guarded.RunGuarded("call audio/letting the call's file go", delegate { return DeleteAsync(file); });
        }

        private static async Task DeleteAsync(StorageFile file)
        {
            try
            {
                await file.DeleteAsync();
            }
            catch (Exception ex)
            {
                Diag.Failed("call audio/letting the call's file go", ex);
            }
        }

        private static void Release(MediaCapture capture, IRandomAccessStream stream)
        {
            if (capture != null)
            {
                try
                {
                    capture.Dispose();
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/disposing the microphone", ex);
                }
            }

            if (stream != null)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception ex)
                {
                    Diag.Failed("call audio/disposing the stream", ex);
                }
            }
        }

        private static long NowMilliseconds()
        {
            return DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }
    }
}
