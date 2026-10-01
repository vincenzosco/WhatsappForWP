# Media previews, document sending and inline voice notes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show received photos, videos, documents and PDFs as real preview cards in the chat, let the user pick and send documents, and play voice notes with an inline bar inside the bubble instead of a black full-screen player.

**Architecture:** Everything stays on the existing media pipeline. The adapter already recognises `document` on the receive side and already routes any non-image/non-video/non-audio attachment to `POST /send/file`, so no protocol change is needed: the app only has to declare the right `kind` and MIME type when it sends. On the app side `ChatMessage` gains presentation-only properties (file size, badge, playback state), `IncomingMediaStore` carries the original file name and the byte count through, and `ChatPage` hosts one hidden `MediaElement` that a per-bubble play/pause button drives. The video cover frame comes from `Windows.Media.Editing.MediaComposition.GetThumbnailAsync`, which Windows Phone 8.1 ships.

**Tech Stack:** C# 5 / WinRT XAML (Windows Phone 8.1, `WhatsappApp/`), Node.js adapter (`WhatsappBridge/`, no dependencies), the project's own guard scripts in `tools/`.

## Global Constraints

- **C# 5 only.** No `await` inside `catch` or `finally` (CS1985; `tools/check-csharp5.js` fails the build). Compute the failure, leave the block, then await.
- **Every new `.cs` file must be added to `WhatsappApp/WhatsappApp.csproj`** as a `<Compile Include="..."/>`, sorted next to its siblings, or the WP8.1 project will not compile it.
- **Every file is LF with no BOM.** Normalize after editing:
  `perl -i -0777 -pe 's/^\xEF\xBB\xBF//; s/\r\n/\n/g' <files>`
- **Target is Windows Phone 8.1.** Use only APIs that platform ships. Do not use `Segoe MDL2 Assets`, do not put a `Geometry` in a `ResourceDictionary`, do not use the `PathGeometry.Figures="M..."` string form (all three are `tools/check-icons.js` rules).
- **A `<Button x:Name="X">` must be wired to `Click="X_Click"`, and if it declares `Width` it must also declare `MinWidth="0" MinHeight="0"`** (`tools/check-actions.js`).
- **Every `x:Uid` and every `Loc.Get("Key", ...)` must exist in BOTH `WhatsappApp/Strings/en-US/Resources.resw` and `WhatsappApp/Strings/it-IT/Resources.resw`, with the identical key set** (`tools/check-resw.js --strict`).
- **Never build `Any CPU`.** The manifest declares `AppxBundlePlatforms=arm`; `Any CPU` fails with `MakeAppx 0x80080204`. Only `ARM` (device) or `x86` (emulator).
- **Commit messages are in English, imperative, and contain no apostrophe.** Docs contain no emoji except the warning sign U+26A0.
- **Fast gate (must print `GATE=OK`), run from the repo root:**
  `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js`
  then `node --test "tools/test/**/*.test.js"` and `cd WhatsappBridge && npm test`.
- **Adapter changes are mirrored.** After any change under `WhatsappBridge/`:
  `cd /Users/vincenzo/Documents/docker-whatsappforwp && node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP && node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP && cd server && npm test`, then commit and push `main`.
- The app has **no unit-test framework**. A C# task is verified by the fast gate plus the ARM build on the Parallels VM; a change that can be expressed as a textual rule is verified by a real unit test in `tools/test/`.

### Current state the plan builds on (do not re-derive)

- `WhatsappApp/Pages/ChatPage.xaml` holds the message `DataTemplate` twice: an incoming bubble and an outgoing bubble. Both contain, in this order, an image `Border`, a video `Border` (`IconPlay` + `ProgressRing`), an audio `Border` (`IconPlay` + `ProgressRing`), a document `Border` (sheet icon + `ProgressRing`), then the text and the time row. Every XAML change in this plan applies to **both** copies.
- The video `Border` sits at `ChatPage.xaml:181` (incoming) and `ChatPage.xaml:314` (outgoing); the audio `Border` at `ChatPage.xaml:205` and `ChatPage.xaml:338`; the document `Border` at `ChatPage.xaml:236` and `ChatPage.xaml:369`. These are the line numbers **before** this plan; after the first edit they shift, so locate them by their `Visibility="{Binding IsVideo|IsAudio|IsDocument, ...}"` attribute.
- `WhatsappBridge/server.js` `mediaKindOf` (around line 772) already returns `document` for anything that is not `image`/`video`/`audio`, and `sendMediaToGowa` already calls `session.gowa.sendFile(...)` for it. `POST /send/file` is implemented in `WhatsappBridge/gowa-client.js` `sendFile`.
- `WhatsappBridge/server.js` `playableMedia` already converts received Ogg/Opus voice notes to MP3 with ffmpeg, and `WhatsappBridge/Dockerfile` installs ffmpeg (around line 60). The server side of voice notes is done; this plan only fixes the phone side.

---

## File Structure

**Created:**
- `WhatsappApp/Services/VideoThumbnail.cs` - one responsibility: turn a local video file into a cover `BitmapImage` with `Windows.Media.Editing`, returning null on any failure.
- `docs/superpowers/plans/2026-10-01-media-preview-and-voice-player.md` - this document.

**Modified (app):**
- `WhatsappApp/Models/ChatMessage.cs` - presentation-only state: file size, document badge/title, playback state, video cover, audio failure.
- `WhatsappApp/Services/IncomingMediaStore.cs` - carry the original file name and the received byte count out of the piece stream.
- `WhatsappApp/Services/DataService.cs` - write that name and size onto the message when the bytes arrive.
- `WhatsappApp/Services/ImageHelper.cs` - `FromStreamAsync`, the one missing decode entry point (video cover).
- `WhatsappApp/Services/AttachmentInbox.cs` - MIME type, extension and `KindName` for documents.
- `WhatsappApp/Services/ImagePickerService.cs` - let the picker choose documents too.
- `WhatsappApp/Pages/ChatPage.xaml` - the document card, the video cover, the inline audio bar.
- `WhatsappApp/Pages/ChatPage.xaml.cs` - the inline player, the document send path, the cover load.
- `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw` - new keys.
- `WhatsappApp/WhatsappApp.csproj` - register `VideoThumbnail.cs`.

**Modified (tools / adapter / docs):**
- `tools/check-memory.js` - extend the decode guard to `FromStreamAsync`.
- `tools/test/check-memory.test.js` - the test for that rule.
- `WhatsappBridge/test/server.test.js` - a PDF goes to `sendFile`.
- `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md` - document sending and the voice player.
- `.agents/skills/maintain-the-app/SKILL.md` - the two invariants this plan adds.

---

## Task 1: Playback state on ChatMessage

**Files:**
- Modify: `WhatsappApp/Models/ChatMessage.cs` (field block near line 55; properties near line 200 and near line 517)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces, used by Tasks 2, 4, 6 and 7:
  - `bool IsPlaying { get; set; }` - this bubble's voice note is playing.
  - `bool IsAudioNotPlaying { get; }` - the inverse, for the play glyph.
  - `double PlaybackProgress { get; set; }` - 0..1.
  - `string PlaybackTimeText { get; set; }` - `"0:03 / 0:07"`, empty when not playing.
  - `bool AudioFailed { get; set; }` - the phone refused this voice note.
  - `string AudioErrorText { get; }` - the sentence shown on failure.
  - `long MediaSizeBytes { get; set; }` and `string MediaSizeText { get; }` - `"12 KB"`, empty when unknown.
  - `string DocumentTitle { get; }` and `string DocumentBadge { get; }` - `"contratto.pdf"` and `"PDF"`.
  - `BitmapImage VideoThumbnail { get; }` and `Task LoadVideoThumbnailAsync()`.

- [ ] **Step 1: Add the fields**

In `WhatsappApp/Models/ChatMessage.cs`, next to the existing `_mediaFilePath` field (around line 78), add:

```csharp
        // Presentation-only state of this bubble. None of it is wire data: the
        // server never sends a size, a badge or a playback position, and the
        // message cache serializes fields explicitly, so adding fields here does
        // not change what is stored.
        private long _mediaSizeBytes;
        private bool _isPlaying;          // a voice note of this bubble is playing
        private bool _audioFailed;        // the phone refused to decode it
        private double _playbackProgress; // 0..1, what the bar draws
        private string _playbackTimeText;
        private BitmapImage _videoThumbnail;
```

- [ ] **Step 2: Extend the MediaType and MediaFileName setters**

The two setters must announce the computed properties. Replace the `MediaType` setter body:

```csharp
        [DataMember]
        public string MediaType
        {
            get { return _mediaType; }
            set
            {
                _mediaType = value;
                OnPropertyChanged();
                OnPropertyChanged("IsVideo");
                OnPropertyChanged("IsAudio");
                OnPropertyChanged("IsDocument");
                OnPropertyChanged("ShowsText");
                OnPropertyChanged("DocumentTitle");
                OnPropertyChanged("DocumentBadge");
                OnPropertyChanged("AudioErrorText");
            }
        }
```

and the `MediaFileName` property:

```csharp
        [DataMember]
        public string MediaFileName
        {
            get { return _mediaFileName; }
            set
            {
                _mediaFileName = value;
                OnPropertyChanged();
                OnPropertyChanged("DocumentTitle");
                OnPropertyChanged("DocumentBadge");
            }
        }
```

- [ ] **Step 3: Add the new properties**

Place this block immediately after the `IsDocument` property (around line 537). `Loc` and `BitmapImage` are already imported by the file.

```csharp
        /// <summary>
        /// Size of the received file, in bytes. Zero when it is not known: a
        /// history row that has not been downloaded yet, or a message built here
        /// before the file was read. A zero is not shown.
        /// </summary>
        public long MediaSizeBytes
        {
            get { return _mediaSizeBytes; }
            set
            {
                _mediaSizeBytes = value;
                OnPropertyChanged();
                OnPropertyChanged("MediaSizeText");
            }
        }

        /// <summary>The size as a word for the bubble. Empty when it is unknown.</summary>
        public string MediaSizeText
        {
            get
            {
                if (_mediaSizeBytes <= 0) return "";
                if (_mediaSizeBytes < 1024) return _mediaSizeBytes.ToString(CultureInfo.InvariantCulture) + " B";
                if (_mediaSizeBytes < 1024 * 1024)
                    return (_mediaSizeBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KB";
                double mb = _mediaSizeBytes / (1024.0 * 1024.0);
                return mb.ToString("0.#", CultureInfo.InvariantCulture) + " MB";
            }
        }

        /// <summary>
        /// The name drawn on a document card. The real file name when there is
        /// one, otherwise the text the adapter wrote for the row (the file name
        /// it carried, or the word [Document]).
        /// </summary>
        public string DocumentTitle
        {
            get
            {
                if (!IsDocument) return "";
                if (!string.IsNullOrEmpty(_mediaFileName)) return _mediaFileName;
                return string.IsNullOrEmpty(Text) ? Loc.Get("ChatMessage_File", "File") : Text;
            }
        }

        /// <summary>
        /// The short type badge of a document card: PDF, DOCX, XLSX, ZIP. It
        /// comes from the extension of the name, which is the only thing we have:
        /// the adapter sends no separate type word.
        /// </summary>
        public string DocumentBadge
        {
            get
            {
                string name = !string.IsNullOrEmpty(_mediaFileName) ? _mediaFileName : Text;
                string fallback = Loc.Get("ChatMessage_File", "File").ToUpperInvariant();
                if (string.IsNullOrEmpty(name)) return fallback;

                int dot = name.LastIndexOf('.');
                if (dot < 0 || dot == name.Length - 1) return fallback;

                string extension = name.Substring(dot + 1).ToUpperInvariant();
                return extension.Length > 4 ? extension.Substring(0, 4) : extension;
            }
        }

        /// <summary>
        /// This bubble's voice note is playing. A tap on play raises it, a tap on
        /// pause lowers it, and the end of the file lowers it: the two glyphs of
        /// the bar read this one flag.
        /// </summary>
        public bool IsPlaying
        {
            get { return _isPlaying; }
            set
            {
                _isPlaying = value;
                OnPropertyChanged();
                OnPropertyChanged("IsAudioNotPlaying");
            }
        }

        /// <summary>The play glyph shows exactly when nothing is playing.</summary>
        public bool IsAudioNotPlaying
        {
            get { return !_isPlaying; }
        }

        /// <summary>How far the voice note got, from 0 to 1.</summary>
        public double PlaybackProgress
        {
            get { return _playbackProgress; }
            set
            {
                _playbackProgress = value;
                OnPropertyChanged();
            }
        }

        /// <summary>"0:03 / 0:07", or empty when nothing is playing.</summary>
        public string PlaybackTimeText
        {
            get { return _playbackTimeText; }
            set
            {
                _playbackTimeText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// The phone refused to decode this voice note. It is not the network: it
        /// is the platform, and the sentence takes the place of the play glyph
        /// instead of leaving a tap that does nothing.
        /// </summary>
        public bool AudioFailed
        {
            get { return _audioFailed; }
            set
            {
                _audioFailed = value;
                OnPropertyChanged();
            }
        }

        /// <summary>The sentence of a voice note the phone cannot play.</summary>
        public string AudioErrorText
        {
            get { return Loc.Get("ChatPage_AudioError", "This voice note cannot be played."); }
        }

        /// <summary>
        /// Frame of the video, drawn under the play triangle. It is not wire data
        /// and it is not cached on disk: it is rebuilt from the local file, once
        /// per message, and it is null when the phone could not make one.
        /// </summary>
        public BitmapImage VideoThumbnail
        {
            get { return _videoThumbnail; }
        }

        /// <summary>
        /// Builds the cover frame, once, from the video already on disk. A failure
        /// is not fatal: it is recorded and the bubble keeps its plain box.
        /// </summary>
        public async Task LoadVideoThumbnailAsync()
        {
            if (_videoThumbnail != null || !IsVideo || string.IsNullOrEmpty(_mediaFilePath)) return;
            _videoThumbnail = await VideoThumbnail.FromFileAsync(_mediaFilePath, 480);
            OnPropertyChanged("VideoThumbnail");
        }
```

- [ ] **Step 4: Run the guards**

Run: `node tools/check-csharp5.js && node tools/check-resw.js --strict && node tools/check-docs.js`
Expected: `OK: 42 C# file(s) are C# 5 compatible.` and `OK: 149 key(s) ...` and the docs line. `ChatMessage_File`, `ChatPage_AudioError` already exist, so no new key is needed here.

- [ ] **Step 5: Commit**

```bash
git add WhatsappApp/Models/ChatMessage.cs
git commit -m "Give a bubble the state its media preview and voice bar need"
```

---

## Task 2: The inline voice-note player

**Files:**
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (the two audio `Border`s, and a new hidden `MediaElement` beside `VideoViewer`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (player, timer, `Media_Tapped` audio branch, `PlayMedia`, `OnNavigatedFrom`)

**Interfaces:**
- Consumes: from Task 1, `IsPlaying`, `IsAudioNotPlaying`, `PlaybackProgress`, `PlaybackTimeText`, `AudioFailed`, `AudioErrorText`.
- Produces: nothing other tasks call.

- [ ] **Step 1: Replace the audio bar in the message template**

In `WhatsappApp/Pages/ChatPage.xaml`, find the incoming `Border` whose `Visibility` is `{Binding IsAudio, Converter={StaticResource BoolToVisibility}}` (it has `Width="220" Height="56" Background="#FF263238"` and `Tapped="Media_Tapped"`). Replace the whole `Border` (open tag through `</Border>`) with:

```xml
                                <!-- A voice note or an audio file: play/pause, a
                                     bar and the time. The transport is ours and
                                     not the system one, so nothing opens full
                                     screen and the conversation stays in view.
                                     The tap is the button: the border has no
                                     Tapped of its own, which is what kept the
                                     button and the border from both firing. -->
                                <Border CornerRadius="4"
                                        Margin="0,0,0,4"
                                        Width="220" Height="56"
                                        Background="#FF263238"
                                        Visibility="{Binding IsAudio, Converter={StaticResource BoolToVisibility}}">
                                    <Grid Margin="6,0,6,0">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="Auto"/>
                                            <ColumnDefinition Width="*"/>
                                            <ColumnDefinition Width="Auto"/>
                                        </Grid.ColumnDefinitions>
                                        <Button x:Name="PlayAudioButton" Grid.Column="0"
                                                Background="Transparent"
                                                MinWidth="0" MinHeight="0"
                                                Width="36" Height="36"
                                                BorderThickness="0" Padding="0"
                                                VerticalAlignment="Center"
                                                Click="PlayAudioButton_Click">
                                            <Grid>
                                                <Path Fill="White" Width="20" Height="20"
                                                      HorizontalAlignment="Center" VerticalAlignment="Center"
                                                      Visibility="{Binding IsAudioNotPlaying, Converter={StaticResource BoolToVisibility}}">
                                                    <!-- IconPlay -->
                                                    <Path.Data>
                                                        <PathGeometry>
                                                            <PathGeometry.Figures>
                                                                <PathFigure StartPoint="6,3" IsClosed="True">
                                                                    <PathFigure.Segments>
                                                                        <PolyLineSegment Points="22,12 6,21"/>
                                                                    </PathFigure.Segments>
                                                                </PathFigure>
                                                            </PathGeometry.Figures>
                                                        </PathGeometry>
                                                    </Path.Data>
                                                </Path>
                                                <Path Fill="White" Width="18" Height="18"
                                                      HorizontalAlignment="Center" VerticalAlignment="Center"
                                                      Visibility="{Binding IsPlaying, Converter={StaticResource BoolToVisibility}}">
                                                    <!-- IconPause -->
                                                    <Path.Data>
                                                        <PathGeometry>
                                                            <PathGeometry.Figures>
                                                                <PathFigure StartPoint="5,3" IsClosed="True">
                                                                    <PathFigure.Segments>
                                                                        <PolyLineSegment Points="9,3 9,21 5,21"/>
                                                                    </PathFigure.Segments>
                                                                </PathFigure>
                                                                <PathFigure StartPoint="15,3" IsClosed="True">
                                                                    <PathFigure.Segments>
                                                                        <PolyLineSegment Points="19,3 19,21 15,21"/>
                                                                    </PathFigure.Segments>
                                                                </PathFigure>
                                                            </PathGeometry.Figures>
                                                        </PathGeometry>
                                                    </Path.Data>
                                                </Path>
                                            </Grid>
                                        </Button>
                                        <ProgressBar Grid.Column="1"
                                                     Margin="8,0,8,0"
                                                     VerticalAlignment="Center"
                                                     Height="4"
                                                     Minimum="0" Maximum="1"
                                                     Value="{Binding PlaybackProgress}"
                                                     Background="#55FFFFFF"
                                                     Foreground="White"/>
                                        <TextBlock Grid.Column="2"
                                                   Text="{Binding PlaybackTimeText}"
                                                   Foreground="#B0FFFFFF" FontSize="11"
                                                   VerticalAlignment="Center"/>
                                        <TextBlock Grid.ColumnSpan="3"
                                                   Text="{Binding AudioErrorText}"
                                                   Visibility="{Binding AudioFailed, Converter={StaticResource BoolToVisibility}}"
                                                   Foreground="White" FontSize="12"
                                                   HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                        <ProgressRing IsActive="True" Width="24" Height="24"
                                                      Foreground="White"
                                                      HorizontalAlignment="Center" VerticalAlignment="Center"
                                                      Visibility="{Binding IsMediaLoading, Converter={StaticResource BoolToVisibility}}"/>
                                    </Grid>
                                </Border>
```

Then apply the **identical replacement** to the outgoing copy, the one in the second `<DataTemplate>` half with the same `Visibility="{Binding IsAudio, ...}"` attribute. Both copies must end up byte-identical.

- [ ] **Step 2: Add the hidden player**

Still in `ChatPage.xaml`, immediately **before** the `<!-- Full-screen image. ... -->` comment that introduces `<Grid x:Name="ImageViewer" ...>`, insert:

```xml
        <!-- The voice-note player. One for the whole page, not one per bubble:
             a MediaElement per row of a virtualizing list is a decoder per row.
             It is 1x1 and transparent instead of Collapsed, because a collapsed
             MediaElement is not laid out and on this platform it does not play.
             Its transport is ours, in the bubble. -->
        <MediaElement x:Name="VoicePlayer"
                      Width="1" Height="1"
                      Opacity="0"
                      IsHitTestVisible="False"
                      HorizontalAlignment="Left" VerticalAlignment="Top"
                      AutoPlay="False"
                      AreTransportControlsEnabled="False"
                      MediaEnded="VoicePlayer_MediaEnded"
                      MediaFailed="VoicePlayer_MediaFailed"/>
```

- [ ] **Step 3: Run the XAML guards and watch them fail on the missing handler**

Run: `node tools/check-icons.js && node tools/check-actions.js`
Expected: icons pass with `15 distinct icon(s)` (was 14) and actions pass with `22 button(s)` (was 20). The handlers are wired but do not exist yet; the XAML compiler catches that in Step 5, not these guards.

- [ ] **Step 4: Implement the player in the code-behind**

In `WhatsappApp/Pages/ChatPage.xaml.cs`, add `using System.Globalization;` to the usings block at the top of the file.

Then, immediately after the `_playingAudio` field and the `PlayMedia` method are removed, add the player. Concretely:

4a. Delete the field:
```csharp
        // True when what is playing is a voice note: the view is the same, but the
        // error sentence is not.
        private bool _playingAudio;
```

4b. Delete the `IsAudio` branch inside `Media_Tapped` (the block that starts `if (message.IsAudio)` and ends with its `return;`). A voice note is now reached through the button, never through the border.

4c. Change the video call site: `PlayMedia(message, false);` becomes `PlayVideo(message);`, and replace the whole `PlayMedia` method with:

```csharp
        /// <summary>
        /// Opens the received video in the full-screen player. The source is the
        /// local file (ms-appdata): the player opens it on its own and there is no
        /// stream to keep open for the life of the page. A voice note does not
        /// come here any more: it plays in its own bubble.
        /// </summary>
        private void PlayVideo(ChatMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.MediaFilePath)) return;

            try
            {
                StopVideo();
                VideoPlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
                VideoViewer.Visibility = Visibility.Visible;
                VideoPlayer.Play();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.PlayVideo", ex);
                ShowVideoError();
            }
        }
```

4d. Simplify `ShowVideoError` (no `_playingAudio` any more):

```csharp
        private void ShowVideoError()
        {
            try
            {
                VideoPlayer.Stop();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ShowVideoError", ex);
            }
            VideoErrorText.Text = Loc.Get("ChatPage_VideoError", "This video cannot be played.");
            VideoErrorText.Visibility = Visibility.Visible;
        }
```

4e. In `StopVideo`, delete the line `_playingAudio = false;`.

4f. Add the whole voice player next to `StopVideo`:

```csharp
        // The voice note whose file is loaded in VoicePlayer, whether it is
        // playing or paused, and the name of that file. One player for the page:
        // starting another one rewinds the first.
        private ChatMessage _voiceMessage;
        private string _voiceLoadedFile;
        private DispatcherTimer _playbackTimer;

        /// <summary>
        /// The play/pause glyph of one bubble. A second tap on the same voice note
        /// pauses it and keeps the position; tapping another one rewinds the first.
        /// With no bytes yet it is the same tap that asks for them, which is what
        /// the old bar did before it could be played.
        /// </summary>
        private void PlayAudioButton_Click(object sender, RoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var message = element == null ? null : element.DataContext as ChatMessage;
            if (message == null) return;

            if (string.IsNullOrEmpty(message.MediaFilePath))
            {
                if (Downloadable(message)) RequestMedia(message);
                return;
            }

            ToggleVoice(message);
        }

        private void ToggleVoice(ChatMessage message)
        {
            if (message == null) return;

            // The same one, playing: this tap is a pause. The position stays, and
            // the next tap resumes from there.
            if (message.IsPlaying)
            {
                PauseVoice(message);
                return;
            }

            // A different one was playing or paused: it goes back to its start, so
            // that two bars cannot both look active.
            if (_voiceMessage != null && _voiceMessage != message) ResetVoice(_voiceMessage);

            try
            {
                if (_voiceLoadedFile != message.MediaFilePath)
                {
                    VoicePlayer.Source = new Uri("ms-appdata:///local/" + message.MediaFilePath);
                    _voiceLoadedFile = message.MediaFilePath;
                }
                message.AudioFailed = false;
                _voiceMessage = message;
                VoicePlayer.Play();
                message.IsPlaying = true;
                StartPlaybackTimer();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.ToggleVoice", ex);
                ResetVoice(message);
                message.AudioFailed = true;
            }
        }

        private void PauseVoice(ChatMessage message)
        {
            try
            {
                VoicePlayer.Pause();
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.PauseVoice", ex);
            }

            message.IsPlaying = false;
            StopPlaybackTimer();
        }

        /// <summary>Back to the start, with nothing to show.</summary>
        private void ResetVoice(ChatMessage message)
        {
            if (message == null) return;
            message.IsPlaying = false;
            message.PlaybackProgress = 0;
            message.PlaybackTimeText = "";
        }

        /// <summary>Closes the player: it is called when the page is left.</summary>
        private void StopVoice()
        {
            try
            {
                VoicePlayer.Stop();
                VoicePlayer.Source = null;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage.StopVoice", ex);
            }

            ResetVoice(_voiceMessage);
            _voiceMessage = null;
            _voiceLoadedFile = null;
            StopPlaybackTimer();
        }

        private void VoicePlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            var message = _voiceMessage;
            if (message != null)
            {
                message.PlaybackProgress = 1;
                message.IsPlaying = false;
                message.PlaybackTimeText = "";
            }
            StopPlaybackTimer();
        }

        /// <summary>
        /// The phone refused the file. On WP8.1 the event carries only the message,
        /// not the exception, so it is logged as one: an unplayable voice note and
        /// a silent one used to look the same.
        /// </summary>
        private void VoicePlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            string reason = (e != null && !string.IsNullOrEmpty(e.ErrorMessage))
                ? e.ErrorMessage
                : "media failed";
            Diag.Failed("ChatPage/VoicePlayer", new InvalidOperationException(reason));

            var message = _voiceMessage;
            if (message != null)
            {
                message.IsPlaying = false;
                message.AudioFailed = true;
            }
            StopPlaybackTimer();
        }

        private void StartPlaybackTimer()
        {
            if (_playbackTimer == null)
            {
                _playbackTimer = new DispatcherTimer();
                _playbackTimer.Interval = TimeSpan.FromMilliseconds(250);
                _playbackTimer.Tick += PlaybackTimer_Tick;
            }
            _playbackTimer.Start();
        }

        private void StopPlaybackTimer()
        {
            if (_playbackTimer != null) _playbackTimer.Stop();
        }

        /// <summary>
        /// Four times a second, the position of the one voice note that can be
        /// playing. It is read here and not bound to the MediaElement because
        /// MediaElement.Position is not a dependency property: there is no binding
        /// to hang it on.
        /// </summary>
        private void PlaybackTimer_Tick(object sender, object e)
        {
            var message = _voiceMessage;
            if (message == null)
            {
                StopPlaybackTimer();
                return;
            }

            double total = 0;
            try
            {
                if (VoicePlayer.NaturalDuration.HasTimeSpan)
                    total = VoicePlayer.NaturalDuration.TimeSpan.TotalSeconds;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/NaturalDuration", ex);
            }

            double position = 0;
            try
            {
                position = VoicePlayer.Position.TotalSeconds;
            }
            catch (Exception ex)
            {
                Diag.Failed("ChatPage/Position", ex);
            }

            message.PlaybackProgress = total > 0 ? Math.Min(1.0, position / total) : 0;
            message.PlaybackTimeText = total > 0 ? FormatClock(position) + " / " + FormatClock(total) : "";
        }

        private static string FormatClock(double seconds)
        {
            if (seconds < 0) seconds = 0;
            int total = (int)Math.Round(seconds);
            return (total / 60) + ":" + (total % 60).ToString("00", CultureInfo.InvariantCulture);
        }
```

4g. In `OnNavigatedFrom`, next to the existing `StopVideo();`, add `StopVoice();`.

- [ ] **Step 5: Build for ARM and fix anything the compiler says**

Run (on the Mac):
```bash
prlctl exec "Windows 11" cmd /c "if exist C:\\Temp\\wp81 rd /s /q C:\\Temp\\wp81 & robocopy C:\\Mac\\Home\\Documents\\WhatsappForWP C:\\Temp\\wp81 /E /XD obj bin AppPackages BundleArtifacts node_modules .tools .git /NFL /NDL /NJH /NJS /NP > C:\\Temp\\copy.log 2>&1 & echo COPYDONE"
prlctl exec "Windows 11" cmd /c "cd /d C:\\Temp\\wp81 && C:\\PROGRA~2\\MSBuild\\12.0\\Bin\\MSBuild.exe WhatsappApp.sln /t:Rebuild /p:Configuration=Debug /p:Platform=ARM /nologo /v:m > C:\\Temp\\build.log 2>&1 & echo DONE"
```
then read the tail of `C:\Temp\build.log` (the `prlctl` output is unreliable in line: write the tail to the shared folder and read it on the Mac):
```bash
prlctl exec "Windows 11" cmd /c "powershell -NoProfile -Command \"(Get-Content C:\\Temp\\build.log -Tail 25) | Set-Content C:\\Mac\\Home\\Documents\\WhatsappForWP\\build-vm.log\"; echo DONE"
```
Expected: `0 Error(s)` and `Your package has been successfully created.`, then `rm -f build-vm.log`.

- [ ] **Step 6: Run the fast gate**

Run: `node tools/check-csharp5.js && node tools/check-icons.js && node tools/check-resw.js --strict && node tools/check-docs.js && node tools/check-framing.js && node tools/check-tile.js && node tools/check-memory.js && node tools/check-actions.js && echo GATE=OK`
Expected: `GATE=OK`.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Pages/ChatPage.xaml.cs
git commit -m "Play a voice note in its own bubble instead of filling the screen"
```

---

## Task 3: A real document card on a received file

**Files:**
- Modify: `WhatsappApp/Services/IncomingMediaStore.cs` (carry name and size)
- Modify: `WhatsappApp/Services/DataService.cs` (`ApplyMedia`, around line 534)
- Modify: `WhatsappApp/Models/ChatMessage.cs` (`ShowsText`, `IsDocument` block)
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (the two document `Border`s)

**Interfaces:**
- Consumes: from Task 1, `MediaSizeBytes`, `MediaSizeText`, `DocumentTitle`, `DocumentBadge`.
- Produces: `IncomingMediaResult.FileName` and `IncomingMediaResult.SizeBytes`.

- [ ] **Step 1: Carry the name and the size out of the piece stream**

In `WhatsappApp/Services/IncomingMediaStore.cs`, add to `IncomingMediaResult` (after `LocalFileName`):

```csharp
        /// <summary>The name the file had on the other end, when the frame carried one.</summary>
        public string FileName;

        /// <summary>How many bytes were written, 0 when the media stayed in memory.</summary>
        public long SizeBytes;
```

Add to the `Pending` class, after `public StringBuilder Base64;`:

```csharp
            public string FileName;
            public long Bytes;
```

In `StartAsync`, inside the `new Pending { ... }` initializer, add `FileName = frame.MediaFileName,` (next to `MimeType = frame.MediaMimeType,`).

In `AddChunkCoreAsync`, replace the write branch:

```csharp
                if (pending.ToDisk)
                {
                    byte[] part = Convert.FromBase64String(frame.MediaData);
                    pending.Writer.WriteBytes(part);
                    await pending.Writer.StoreAsync();
                    pending.Bytes += part.Length;
                }
                else
                {
                    pending.Base64.Append(frame.MediaData);
                }
```

and in the result block that follows, add `FileName` and `SizeBytes`:

```csharp
            var result = new IncomingMediaResult
            {
                MediaType = pending.MediaType,
                MimeType = pending.MimeType,
                FileName = pending.FileName,
                SizeBytes = pending.Bytes
            };
```

- [ ] **Step 2: Write them onto the message**

In `WhatsappApp/Services/DataService.cs`, in `ApplyMedia`, inside the `if (!string.IsNullOrEmpty(result.LocalFileName))` branch, immediately after `target.MediaFilePath = result.LocalFileName;`, add:

```csharp
                    // What the card draws: the name the file had, and how big it
                    // is. The name is written here and not earlier because a
                    // history row arrives without one.
                    if (!string.IsNullOrEmpty(result.FileName)) target.MediaFileName = result.FileName;
                    target.MediaSizeBytes = result.SizeBytes;
```

- [ ] **Step 3: Stop repeating the name under the card**

In `WhatsappApp/Models/ChatMessage.cs`, the `ShowsText` property must not draw the file name twice. Replace its body:

```csharp
        public bool ShowsText
        {
            get
            {
                if (string.IsNullOrEmpty(Text)) return false;
                if (IsVideo && IsMediaPlaceholder) return false;
                // A document card already carries its name: repeating it under the
                // card is the same word twice. When the text is a caption and not
                // the name, it stays.
                if (IsDocument && string.Equals(DocumentTitle, Text)) return false;
                return true;
            }
        }
```

- [ ] **Step 4: Draw the card**

In `WhatsappApp/Pages/ChatPage.xaml`, find the incoming `Border` whose `Visibility` is `{Binding IsDocument, Converter={StaticResource BoolToVisibility}}` (it has `Width="220" Height="56"`, `Background="#FF263238"`, `Tapped="Media_Tapped"`, and a sheet `IconDocument`). Replace the whole `Border` with:

```xml
                                <!-- A document: a type badge, the name and the
                                     size, tapped to open it with the app the
                                     phone uses for that type. The name was
                                     already on the wire and the size is known
                                     once the bytes are all here; before that the
                                     card shows the name alone. -->
                                <Border CornerRadius="4"
                                        Margin="0,0,0,4"
                                        Width="220"
                                        Background="#FF263238"
                                        Visibility="{Binding IsDocument, Converter={StaticResource BoolToVisibility}}"
                                        Tapped="Media_Tapped">
                                    <Grid Margin="10,8,10,8">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="Auto"/>
                                            <ColumnDefinition Width="*"/>
                                        </Grid.ColumnDefinitions>
                                        <Border Grid.Column="0"
                                                Width="40" Height="40" CornerRadius="4"
                                                Background="#FF37474F"
                                                VerticalAlignment="Center">
                                            <TextBlock Text="{Binding DocumentBadge}"
                                                       Foreground="White" FontSize="11" FontWeight="SemiBold"
                                                       HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                        </Border>
                                        <StackPanel Grid.Column="1" Margin="10,0,0,0"
                                                    VerticalAlignment="Center">
                                            <TextBlock Text="{Binding DocumentTitle}"
                                                       Foreground="White" FontSize="14"
                                                       TextWrapping="Wrap" MaxLines="2"
                                                       TextTrimming="CharacterEllipsis"/>
                                            <TextBlock Text="{Binding MediaSizeText}"
                                                       Foreground="#B0FFFFFF" FontSize="11"
                                                       Margin="0,2,0,0"/>
                                        </StackPanel>
                                        <ProgressRing IsActive="True" Width="24" Height="24"
                                                      Grid.ColumnSpan="2"
                                                      Foreground="White"
                                                      HorizontalAlignment="Center" VerticalAlignment="Center"
                                                      Visibility="{Binding IsMediaLoading, Converter={StaticResource BoolToVisibility}}"/>
                                    </Grid>
                                </Border>
```

Then apply the **identical replacement** to the outgoing copy with the same `IsDocument` binding.

- [ ] **Step 5: Run the XAML guards**

Run: `node tools/check-icons.js && node tools/check-resw.js --strict`
Expected: `OK: 24 inline icon Path(s), 15 distinct icon(s).` and `OK: 149 key(s) ...`. The sheet `IconDocument` is removed from both bubbles and no longer exists anywhere: the badge is a text block now. If the guard reports the icon as *unused* that is not an error, but if it reports zero occurrences of nothing, that is fine - the icon count drops to 23 Paths.

- [ ] **Step 6: Run the fast gate**

Run the full fast gate from Global Constraints.
Expected: `GATE=OK`.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Services/IncomingMediaStore.cs WhatsappApp/Services/DataService.cs WhatsappApp/Models/ChatMessage.cs WhatsappApp/Pages/ChatPage.xaml
git commit -m "Draw a received document as a name, a type and a size"
```

---

## Task 4: Pick and send documents

**Files:**
- Modify: `WhatsappApp/Services/ImagePickerService.cs`
- Modify: `WhatsappApp/Services/AttachmentInbox.cs` (`MimeFor`, `ExtensionFor`, `KindName`)
- Modify: `WhatsappApp/Pages/ChatPage.xaml.cs` (`ShowPendingAttachment`, `SendAttachmentAsync`, the picker call)
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (the preview bar)
- Modify: `WhatsappApp/Strings/en-US/Resources.resw`, `WhatsappApp/Strings/it-IT/Resources.resw`
- Modify: `WhatsappBridge/test/server.test.js`

**Interfaces:**
- Consumes: from Task 1, `MediaSizeBytes`.
- Produces: `AttachmentInbox.KindName` now returns one of `"image"`, `"video"`, `"document"`; `AttachmentInbox.MimeType` is never `image/jpeg` for a file that is not an image.

- [ ] **Step 1: Write the failing adapter test**

In `WhatsappBridge/test/server.test.js`, immediately after the test `un allegato immagine va a sendImage e uno sconosciuto a sendFile`, add:

```js
test('un pdf inviato va a sendFile, non a sendImage', async () => {
  const delivered = [];
  const gowa = {
    sendFile: async (phone, caption, buffer, mimeType, fileName) => {
      delivered.push({ door: 'file', mimeType, fileName });
      return 'F1';
    },
    sendImage: async () => { throw new Error('un pdf non e un immagine'); },
    sendVideo: async () => { throw new Error('un pdf non e un video'); }
  };
  const bridge = createBridge({ config: {}, gowa, log: noop, debug: noop });
  bridge.setConnectedForTest();
  bridge.addClientForTest({ write: () => {} });

  await bridge.handleControl({ Type: 3, Command: 'media.begin', Text: 'a@s.whatsapp.net',
    MediaTransferId: 'T1', MediaFileName: 'contratto.pdf',
    MediaMimeType: 'application/pdf', MediaChunkTotal: 1 });
  await bridge.handleControl({ Type: 3, Command: 'media.chunk',
    MediaTransferId: 'T1', MediaChunkIndex: 0,
    MediaData: Buffer.from('pdf-bytes').toString('base64') });
  await bridge.handleControl({ Type: 3, Command: 'media.end',
    MediaTransferId: 'T1', Text: '' });

  assert.strictEqual(delivered.length, 1);
  assert.strictEqual(delivered[0].door, 'file');
  assert.strictEqual(delivered[0].mimeType, 'application/pdf');
  assert.strictEqual(delivered[0].fileName, 'contratto.pdf');
});
```

- [ ] **Step 2: Run it to verify the adapter already behaves**

Run: `cd WhatsappBridge && node --test test/server.test.js`
Expected: PASS. If it fails, `mediaKindOf` or `sendMediaToGowa` changed and the send path must be fixed before the app is touched.

- [ ] **Step 3: Let the picker choose documents**

Replace the whole of `WhatsappApp/Services/ImagePickerService.cs` with:

```csharp
using Windows.Storage.Pickers;

namespace WhatsappApp.Services
{
    /// <summary>
    /// Opens the WP8.1 file picker.
    ///
    /// It uses PickSingleFileAndContinue and NOT PickSingleFileAsync: the
    /// Microsoft documentation says PickSingleFileAsync is not supported on
    /// Windows Phone (neither for Windows Runtime nor for Silverlight) and points
    /// at PickSingleFileAndContinue. On the phone the first one failed, and the
    /// attach button looked dead.
    ///
    /// The difference that matters: PickSingleFileAndContinue returns nothing. It
    /// deactivates the app, and the chosen file comes back to App.OnActivated as a
    /// PickFileContinuation. The result therefore does not pass through here: App
    /// deposits it in AttachmentInbox.
    ///
    /// Documents are in the list because the picker is the only way in: with only
    /// images and videos there was no route at all to send a PDF, and the adapter
    /// has always had one (POST /send/file).
    /// </summary>
    public static class ImagePickerService
    {
        public static void RequestFile()
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".bmp");

            // A video is not an image, but it comes from the same button: the
            // file gives the type, and the send path chooses the right way.
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".mov");
            picker.FileTypeFilter.Add(".3gp");
            picker.FileTypeFilter.Add(".avi");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".webm");

            // Documents. The file name is kept, so the type badge of the card
            // comes from the real extension.
            picker.FileTypeFilter.Add(".pdf");
            picker.FileTypeFilter.Add(".doc");
            picker.FileTypeFilter.Add(".docx");
            picker.FileTypeFilter.Add(".xls");
            picker.FileTypeFilter.Add(".xlsx");
            picker.FileTypeFilter.Add(".ppt");
            picker.FileTypeFilter.Add(".pptx");
            picker.FileTypeFilter.Add(".txt");
            picker.FileTypeFilter.Add(".csv");
            picker.FileTypeFilter.Add(".rtf");
            picker.FileTypeFilter.Add(".zip");
            picker.FileTypeFilter.Add(".7z");
            picker.FileTypeFilter.Add(".rar");

            // CS0618: deprecated since Windows 10, but it is the only one Windows
            // Phone 8.1 implements. It is not a warning to fix, it is the platform.
#pragma warning disable 618
            picker.PickSingleFileAndContinue();
#pragma warning restore 618
        }
    }
}
```

Update the call site in `ChatPage.xaml.cs` `AttachButton_Click`: `ImagePickerService.RequestImage();` becomes `ImagePickerService.RequestFile();`. The `Diag.Failed` label `"ChatPage/image"` becomes `"ChatPage/pick"`.

- [ ] **Step 4: Give every file its real type**

In `WhatsappApp/Services/AttachmentInbox.cs`, replace `MimeFor`:

```csharp
        /// <summary>
        /// The MIME type of an extension, as WhatsApp sends it. The last line is
        /// application/octet-stream and not image/jpeg: a file the list does not
        /// know is a file, and calling it an image is how a PDF used to be sent as
        /// a picture.
        /// </summary>
        private static string MimeFor(string extension)
        {
            string value = (extension ?? "").ToLower();
            if (value == ".png") return "image/png";
            if (value == ".gif") return "image/gif";
            if (value == ".bmp") return "image/bmp";
            if (value == ".mp4") return "video/mp4";
            if (value == ".mov") return "video/quicktime";
            if (value == ".3gp") return "video/3gpp";
            if (value == ".avi") return "video/x-msvideo";
            if (value == ".mkv") return "video/x-matroska";
            if (value == ".webm") return "video/webm";
            if (value == ".pdf") return "application/pdf";
            if (value == ".doc") return "application/msword";
            if (value == ".docx") return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
            if (value == ".xls") return "application/vnd.ms-excel";
            if (value == ".xlsx") return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            if (value == ".ppt") return "application/vnd.ms-powerpoint";
            if (value == ".pptx") return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
            if (value == ".txt") return "text/plain";
            if (value == ".csv") return "text/csv";
            if (value == ".rtf") return "application/rtf";
            if (value == ".zip") return "application/zip";
            if (value == ".7z") return "application/x-7z-compressed";
            if (value == ".rar") return "application/vnd.rar";
            return "application/octet-stream";
        }
```

and replace `ExtensionFor`:

```csharp
        /// <summary>The extension of the copied file, from the name or the MIME type.</summary>
        private static string ExtensionFor(string mimeType, string fileName)
        {
            string name = fileName ?? "";
            int dot = name.LastIndexOf('.');
            if (dot >= 0 && dot < name.Length - 1) return name.Substring(dot).ToLower();

            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return ".mp4";
            if (mime == "image/png") return ".png";
            if (mime == "image/gif") return ".gif";
            if (mime == "image/bmp") return ".bmp";
            if (mime.StartsWith("image/")) return ".jpg";
            // Anything else is a file, and a file with no name has no extension
            // worth inventing.
            return ".bin";
        }
```

and replace `KindName`:

```csharp
        /// <summary>
        /// "image", "video" or "document": the word the adapter and the app use to
        /// decide how to send and how to draw. The MIME type may be missing (a
        /// shared bitmap), so the word is also derived from the extension.
        /// </summary>
        public static string KindName(string mimeType, string fileName)
        {
            string mime = (mimeType ?? "").ToLower();
            if (mime.StartsWith("video/")) return "video";
            if (mime.StartsWith("image/")) return "image";

            string name = (fileName ?? "").ToLower();
            if (name.EndsWith(".mp4") || name.EndsWith(".mov") || name.EndsWith(".3gp")
                || name.EndsWith(".avi") || name.EndsWith(".mkv") || name.EndsWith(".webm"))
            {
                return "video";
            }
            if (name.EndsWith(".jpg") || name.EndsWith(".jpeg") || name.EndsWith(".png")
                || name.EndsWith(".gif") || name.EndsWith(".bmp"))
            {
                return "image";
            }
            return "document";
        }
```

- [ ] **Step 5: Send a document as a document**

In `ChatPage.xaml.cs` `SendAttachmentAsync`, replace the message construction and the branches that depend on the kind:

```csharp
            string kind = AttachmentInbox.KindName(mimeType, fileName);

            // A document is not an image: MessageType.Text keeps the empty image
            // box of the bubble from being drawn on top of the card, and
            // MediaType "document" is what makes IsDocument true.
            MessageType type = kind == "video" ? MessageType.Video
                : (kind == "image" ? MessageType.Image : MessageType.Text);

            var message = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                Text = caption ?? "",
                SenderId = CommunicationService.Instance.MyUserId ?? "me",
                SenderName = CommunicationService.Instance.MyUsername ?? Loc.Get("ChatPage_Me", "Me"),
                ChatId = _contact.Id,
                Timestamp = DateTime.Now,
                Type = type,
                IsIncoming = false,
                Status = MessageStatus.Sending,
                // The bytes are on disk: here there is only where to find them.
                MediaFilePath = localFileName,
                MediaMimeType = mimeType,
                MediaFileName = fileName,
                MediaType = kind
            };

            // Local decoding: the sender sees their own image.
            if (message.Type == MessageType.Image) await message.LoadMediaImageAsync();
            // The sender sees their own video cover, when the phone can make one.
            if (message.Type == MessageType.Video) await message.LoadVideoThumbnailAsync();
```

Then, inside the `try` block, right after `ulong length = (await file.GetBasicPropertiesAsync()).Size;`, add:

```csharp
                message.MediaSizeBytes = (long)length;
```

- [ ] **Step 6: Show the document in the preview bar**

In `ChatPage.xaml.cs`, replace the body of `ShowPendingAttachment` from `bool video = ...` to the end of the method:

```csharp
            string kind = AttachmentInbox.KindName(_selectedMediaMimeType, _selectedMediaFileName);
            bool video = kind == "video";
            bool document = kind == "document";

            PreviewLabel.Text = video
                ? Loc.Get("ChatPage_VideoSelected", "Video selected")
                : (document
                    ? Loc.Get("ChatPage_DocumentSelected", "Document selected")
                    : Loc.Get("ChatPage_ImageSelected.Text", "Image selected"));

            SelectedVideoPreview.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            SelectedImagePreview.Visibility = (!video && !document) ? Visibility.Visible : Visibility.Collapsed;
            SelectedDocumentPreview.Visibility = document ? Visibility.Visible : Visibility.Collapsed;
            SelectedDocumentPreviewText.Text = document ? _selectedMediaFileName : "";

            ImagePreviewBar.Visibility = Visibility.Visible;
            if (!video && !document)
            {
#pragma warning disable 4014
                ShowLocalPreviewAsync(_selectedLocalFileName);
#pragma warning restore 4014
            }
            else
            {
                // A video is not decoded here, and a document has nothing to
                // draw: the bar says what is being sent.
                SelectedImagePreview.Source = null;
            }
```

Also clear the new elements in `ClearSelectedImage`:

```csharp
        private void ClearSelectedImage()
        {
            _selectedLocalFileName = null;
            _selectedMediaFileName = null;
            _selectedMediaMimeType = null;
            SelectedImagePreview.Source = null;
            SelectedDocumentPreview.Visibility = Visibility.Collapsed;
            SelectedDocumentPreviewText.Text = "";
            ImagePreviewBar.Visibility = Visibility.Collapsed;
        }
```

- [ ] **Step 7: Add the document preview to the bar**

In `ChatPage.xaml`, inside `<Grid x:Name="ImagePreviewBar" ...>`, after the `SelectedVideoPreview` `Path` and before the `PreviewLabel` `TextBlock`, insert:

```xml
                <!-- A document: the badge and the name, drawn where an image
                     shows its thumbnail. -->
                <Border x:Name="SelectedDocumentPreview" Grid.Column="0"
                        Width="40" Height="40" Margin="12,12,0,12" CornerRadius="4"
                        Background="#FF37474F"
                        Visibility="Collapsed">
                    <Grid>
                        <TextBlock x:Name="SelectedDocumentPreviewText"
                                   Text=""
                                   Foreground="White" FontSize="9" FontWeight="SemiBold"
                                   HorizontalAlignment="Center" VerticalAlignment="Center"
                                   TextTrimming="CharacterEllipsis"/>
                    </Grid>
                </Border>
```

- [ ] **Step 8: Add the two resource keys**

In `WhatsappApp/Strings/en-US/Resources.resw`, after the `ChatPage_VideoSelected` entry, add:

```xml
  <data name="ChatPage_DocumentSelected" xml:space="preserve">
    <value>Document selected</value>
  </data>
```

In `WhatsappApp/Strings/it-IT/Resources.resw`, at the same place, add:

```xml
  <data name="ChatPage_DocumentSelected" xml:space="preserve">
    <value>Documento selezionato</value>
  </data>
```

- [ ] **Step 9: Run the guards**

Run: `node tools/check-resw.js --strict && node tools/check-actions.js && node tools/check-resw.js`
Expected: `OK: 150 key(s) in en-US and it-IT, every x:Uid and Loc.Get lookup resolved.` and `OK: 22 button(s) ...`. `ChatPage_DocumentSelected` is used by `Loc.Get`, so it is not reported as unused.

- [ ] **Step 10: Run the fast gate and the adapter tests**

Run the full fast gate, then `cd WhatsappBridge && npm test`.
Expected: `GATE=OK` and `198` passing adapter tests (197 before the new one plus the one added here, so at least 199).

- [ ] **Step 11: Mirror the adapter**

Run:
```bash
cd /Users/vincenzo/Documents/docker-whatsappforwp && node tools/sync.js --from /Users/vincenzo/Documents/WhatsappForWP && node tools/sync.js --check --from /Users/vincenzo/Documents/WhatsappForWP && cd server && npm test
```
Expected: `OK: server/ matches the adapter (28 file(s)).` and the tests pass.

- [ ] **Step 12: Commit**

```bash
git add WhatsappApp/Services/ImagePickerService.cs WhatsappApp/Services/AttachmentInbox.cs WhatsappApp/Pages/ChatPage.xaml.cs WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Strings/en-US/Resources.resw WhatsappApp/Strings/it-IT/Resources.resw WhatsappBridge/test/server.test.js
git commit -m "Let the picker choose a document and send it as one"
```

---

## Task 5: Extend the decode guard, then add FromStreamAsync

The guard is written first, so the new entry point exists to satisfy a rule that is already enforced.

**Files:**
- Modify: `tools/check-memory.js` (the `CALL` constant)
- Modify: `tools/test/check-memory.test.js`
- Modify: `WhatsappApp/Services/ImageHelper.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Task<BitmapImage> ImageHelper.FromStreamAsync(IRandomAccessStream stream, int decodePixelWidth)`, used by Task 6.

- [ ] **Step 1: Write the failing guard test**

In `tools/test/check-memory.test.js`, after the test `ImageHelper.FromFileAsync chiede anche lui la misura`, add:

```js
test('ImageHelper.FromStreamAsync chiede anche lui la misura', () => {
  const problems = memory.decodeProblems(
    'Cover = await ImageHelper.FromStreamAsync(stream);', FILE);
  assert.strictEqual(problems.length, 1);
  assert.match(problems[0], /two arguments/);
});

test('FromStreamAsync con la sua misura passa', () => {
  const problems = memory.decodeProblems(
    'Cover = await ImageHelper.FromStreamAsync(thumb, 480);', FILE);
  assert.deepStrictEqual(problems, []);
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `node --test tools/test/check-memory.test.js`
Expected: FAIL on `ImageHelper.FromStreamAsync chiede anche lui la misura` with `assert.strictEqual(problems.length, 1)` receiving 0, because `FromStreamAsync` is not in the guard yet.

- [ ] **Step 3: Add the rule**

In `tools/check-memory.js`, replace the `CALL` constant:

```js
const CALL = /ImageHelper\.From(Base64|Bytes|File|Stream)Async\(/;
```

and, in `decodeProblems`, update the problem sentence to name the new argument the same way for all four entry points:

```js
    if (parts.length !== 2) {
      problems.push(`${file}: ImageHelper.From${match[1]}Async takes two arguments ` +
        '(the encoded image or stream and the width it is shown at): without the ' +
        'second one the bitmap is decoded at the size of the file');
      continue;
    }
```

- [ ] **Step 4: Run the guard tests**

Run: `node --test tools/test/check-memory.test.js`
Expected: PASS, all cases.

- [ ] **Step 5: Add the entry point**

In `WhatsappApp/Services/ImageHelper.cs`, add after `FromFileAsync`:

```csharp
        /// <summary>
        /// bitmap from a stream that is already open: it serves the cover frame of
        /// a video, which Windows.Media.Editing hands back as a stream and not as
        /// bytes or a file. The stream is not disposed here; the caller owns it.
        /// </summary>
        public static async Task<BitmapImage> FromStreamAsync(IRandomAccessStream stream, int decodePixelWidth)
        {
            if (stream == null) return null;

            var bitmap = new BitmapImage();
            if (decodePixelWidth > 0) bitmap.DecodePixelWidth = decodePixelWidth;
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
```

- [ ] **Step 6: Run the guard and the fast gate**

Run: `node tools/check-memory.js && node tools/check-csharp5.js`
Expected: `OK: every decoded bitmap asks for the width it is shown at, ...` and `OK: 42 C# file(s) are C# 5 compatible.`

- [ ] **Step 7: Commit**

```bash
git add tools/check-memory.js tools/test/check-memory.test.js WhatsappApp/Services/ImageHelper.cs
git commit -m "Guard FromStreamAsync with the same decode width rule as the others"
```

---

## Task 6: A cover frame for a received video

**Files:**
- Create: `WhatsappApp/Services/VideoThumbnail.cs`
- Modify: `WhatsappApp/WhatsappApp.csproj`
- Modify: `WhatsappApp/Pages/ChatPage.xaml` (the two video `Border`s)
- Modify: `WhatsappApp/Services/DataService.cs` (`ApplyMedia`)

**Interfaces:**
- Consumes: from Task 5, `ImageHelper.FromStreamAsync(stream, width)`; from Task 1, `ChatMessage.LoadVideoThumbnailAsync()` and `ChatMessage.VideoThumbnail`.
- Produces: `VideoThumbnail.FromFileAsync(string localFileName, int decodePixelWidth)`.

- [ ] **Step 1: Write the service**

Create `WhatsappApp/Services/VideoThumbnail.cs`:

```csharp
using System;
using System.Threading.Tasks;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The cover frame of a video, for the bubble.
    ///
    /// Why it exists: a received video showed a dark box with a triangle and
    /// nothing else, so two videos looked the same and the box said nothing about
    /// which one it was. WhatsApp draws a frame; this is how this platform can do
    /// the same. Windows Phone 8.1 ships Windows.Media.Editing, so the frame comes
    /// from the phone and no thumbnail travels over the socket.
    ///
    /// It is best effort by design: a codec the phone cannot open, a file that is
    /// gone, a device that refuses the composition - each returns null, the caller
    /// records it, and the bubble keeps the plain box. A missing frame is not an
    /// error the user has to see.
    /// </summary>
    public static class VideoThumbnail
    {
        /// <summary>
        /// A frame of the video already in the app folder, at the width it is
        /// shown at, or null.
        /// </summary>
        public static async Task<BitmapImage> FromFileAsync(string localFileName, int decodePixelWidth)
        {
            if (string.IsNullOrEmpty(localFileName)) return null;

            try
            {
                StorageFile file = await ApplicationData.Current.LocalFolder.GetFileAsync(localFileName);
                MediaClip clip = await MediaClip.CreateFromFileAsync(file);

                var composition = new MediaComposition();
                composition.Clips.Add(clip);

                // The first frame, at the width the bubble draws (480 covers the
                // 220 px box at 1.5x). A height of 0 keeps the aspect ratio.
                IRandomAccessStreamWithContentType frame = await composition.GetThumbnailAsync(
                    TimeSpan.Zero, decodePixelWidth, 0, VideoFramePrecision.NearestKeyFrame);
                if (frame == null) return null;

                using (frame)
                {
                    return await ImageHelper.FromStreamAsync(frame, decodePixelWidth);
                }
            }
            catch (Exception ex)
            {
                Diag.Failed("VideoThumbnail.FromFileAsync", ex);
                return null;
            }
        }
    }
}
```

- [ ] **Step 2: Register it in the project**

In `WhatsappApp/WhatsappApp.csproj`, add next to the other `Services` `<Compile>` items (the list is alphabetical; place it after the `Services\VideoCompressor.cs` item if it exists there, otherwise next to `Services\SerialQueue.cs`):

```xml
    <Compile Include="Services\VideoThumbnail.cs" />
```

- [ ] **Step 3: Run the guard and build**

Run: `node tools/check-csharp5.js && node tools/check-memory.js`
Expected: `OK: 43 C# file(s) are C# 5 compatible.` and the memory line.
Then run the ARM build from Task 2 Step 5.
Expected: `0 Error(s)`. **If the build fails with a missing type in `Windows.Media.Editing`**, this SDK does not project the namespace: stop here, delete `VideoThumbnail.cs` from the csproj and the repo, and skip the rest of this task - the video keeps its play box exactly as before, and that is the fallback this plan accepts.

- [ ] **Step 4: Draw the cover under the triangle**

In `ChatPage.xaml`, in the incoming video `Border` (the one with `Visibility="{Binding IsVideo, ...}"`, `Width="220" Height="140"`), inside its `<Grid>` and **before** the `Path` that draws `IconPlay`, insert:

```xml
                                        <Image Source="{Binding VideoThumbnail}"
                                               Stretch="UniformToFill"
                                               HorizontalAlignment="Stretch" VerticalAlignment="Stretch"/>
```

Then apply the identical insertion to the outgoing video `Border`. The box background stays visible while the frame is still null.

- [ ] **Step 5: Build the cover when the bytes arrive**

In `WhatsappApp/Services/DataService.cs` `ApplyMedia`, inside the `if (!string.IsNullOrEmpty(result.LocalFileName))` branch, after the size lines added in Task 3, add:

```csharp
                    if (string.Equals(result.MediaType, "video", StringComparison.OrdinalIgnoreCase))
                        await target.LoadVideoThumbnailAsync();
```

`ApplyMedia` is already `async Task` and is called on the UI thread by `ApplyMediaFrame`, which is what a `BitmapImage` needs.

- [ ] **Step 6: Run the fast gate and the ARM build**

Run the full fast gate, then the ARM build.
Expected: `GATE=OK` and `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add WhatsappApp/Services/VideoThumbnail.cs WhatsappApp/WhatsappApp.csproj WhatsappApp/Pages/ChatPage.xaml WhatsappApp/Services/DataService.cs
git commit -m "Show a frame of a received video under its play triangle"
```

---

## Task 7: Document what the screens now show

**Files:**
- Modify: `WhatsappBridge/README.md`, `WhatsappBridge/README.it.md`
- Modify: `.agents/skills/maintain-the-app/SKILL.md`

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

- [ ] **Step 1: Update the adapter README pair**

In `WhatsappBridge/README.md`, in the bullet that ends `.../send/file are three different routes, and before this a video went out as an image.`, append:

```
  A document is the fourth case and it has its own route already: the picker on
  the phone now offers PDFs and office files, the app declares the file name and
  the real MIME type, and the adapter sends it with `/send/file`. Voice notes are
  played inside their bubble: the phone asks the adapter for the bytes, the
  adapter hands back an MP3 (see below), and the app draws play/pause and a bar
  without leaving the conversation.
```

Make the matching addition to `WhatsappBridge/README.it.md`, in the bullet that ends `.../send/file sono tre rotte diverse, e prima di questo un video partiva come immagine.`:

```
  Un documento e' il quarto caso e ha gia' la sua rotta: il picker sul telefono
  adesso offre PDF e file di ufficio, l'app dichiara il nome del file e il tipo
  MIME vero, e l'adapter lo manda con `/send/file`. I vocali si ascoltano dentro
  il loro fumetto: il telefono chiede i byte all'adapter, l'adapter risponde con
  un MP3 (vedi sotto), e l'app disegna play/pausa e una barra senza uscire dalla
  conversazione.
```

Both files must keep the same heading structure (`node tools/check-docs.js` compares heading levels, not bullet text).

- [ ] **Step 2: Add the invariants to the skill**

In `.agents/skills/maintain-the-app/SKILL.md`, in the "Known gotchas" list, after the bullet that begins `**A suspended app keeps looking online.**`, add:

```
- **A media bubble shows only what the server sent.** The size of a document and
  the name of the file come out of `IncomingMediaStore` (`FileName`, `SizeBytes`)
  and are written onto the message in `DataService.ApplyMedia`; the type badge is
  the extension of the name, computed in `ChatMessage.DocumentBadge`. Nothing is
  guessed for a row whose bytes have not arrived: an unknown size is an empty
  string, not a zero.
- **One voice player for the whole page.** `ChatPage.VoicePlayer` is a single
  hidden `MediaElement` driven by the play button of the active bubble; the
  position is pushed onto the message by a 250 ms `DispatcherTimer`, because
  `MediaElement.Position` is not a dependency property and cannot be bound. Do
  not give each bubble a `MediaElement`: the list virtualizes, and that is one
  decoder per visible row.
```

- [ ] **Step 3: Run the doc guards**

Run: `node tools/check-docs.js && node tools/check-resw.js --strict`
Expected: `OK: 2 doc pair(s) in step (1 closed by "Disclosure"), no emoji in 44 .md file(s).` (43 before this plan's document plus this plan) and the resw line.

- [ ] **Step 4: Commit**

```bash
git add WhatsappBridge/README.md WhatsappBridge/README.it.md .agents/skills/maintain-the-app/SKILL.md
git commit -m "Write down what a media bubble may show and who owns the voice player"
```

---

## Device checklist (not automatable here)

The build proves nothing about the phone. After Task 7, install the ARM debug bundle and walk this list once:

1. A received photo: thumbnail in the bubble, full screen on tap.
2. A received video: cover frame under the triangle, full screen playback on tap, frame survives a scroll away and back.
3. A received voice note: play/pause in the bubble, bar advances, the time counts, it pauses on a second tap and resumes on a third, it stops when another voice note starts, and it stops when leaving the chat.
4. A voice note the phone cannot decode (rename a text file to `.mp3` and send it): the card says it cannot be played, with no black screen.
5. Sending a PDF: the preview bar shows the badge and the name, the sent bubble is a document card with the name and size, and the contact receives a real PDF.
6. A received PDF: card with badge, name and size, tap opens it with the phone app, and no app on the phone shows the "no app can open this file" sentence.
7. Memory: open a chat with twenty photos and a video, scroll it, and confirm the app is not closed (the decode widths are the ceiling this project already defends).

---

## Self-Review

**Spec coverage:**
- "Anteprima nella chat dei ricevuti" - Task 3 (documents, with name, badge and size) and Task 6 (video cover). Photos already had a thumbnail and a full-screen viewer, so no task changes them; the plan states that instead of inventing work. PDF preview is the document card plus the system viewer; a renderer inside the app is not possible on WP8.1 and the plan does not pretend otherwise.
- "Si, aggiungi i documenti" - Task 4, end to end: picker, MIME, extension, kind, send path, preview bar, adapter test.
- "Player dentro il fumetto" - Tasks 1 and 2.
- "Capire se i vocali hanno bug" - the diagnosis is recorded in the plan's context: the server already transcodes with ffmpeg and the Docker image ships it, the phone played through the shared full-screen `MediaElement` with no in-bubble state, and `AudioFailed` had no card to show. Tasks 1 and 2 replace that mechanism rather than patching it.

**Placeholder scan:** no `TBD`, no "add error handling", no "similar to Task N". Every step that changes code shows the code. The one conditional ("if the SDK does not project Windows.Media.Editing, stop") has an exact action and an exact end state.

**Type consistency:** `MediaSizeBytes`/`MediaSizeText`, `DocumentTitle`/`DocumentBadge`, `IsPlaying`/`IsAudioNotPlaying`/`PlaybackProgress`/`PlaybackTimeText`, `AudioFailed`/`AudioErrorText`, `VideoThumbnail`/`LoadVideoThumbnailAsync` are defined once in Task 1 and referenced with those exact names in Tasks 2, 3, 4 and 6. `IncomingMediaResult.FileName`/`SizeBytes` are defined in Task 3 and consumed in the same task. `ImageHelper.FromStreamAsync` is defined in Task 5 and called in Task 6. `VideoThumbnail.FromFileAsync` is defined and called in Task 6. `PlayVideo` replaces the two-argument `PlayMedia` in Task 2, and no later task calls the old name.
