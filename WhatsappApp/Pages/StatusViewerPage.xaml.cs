using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// One person's updates, full screen: what the contact posted.
    ///
    /// Why it exists: the status tab could only *list* an update. Tapping a row sent the
    /// `read` receipt - the author's ring filled - and then nothing appeared, so the one
    /// thing a status is for (seeing the photo or the sentence somebody put up) was the
    /// one thing this app could not do. The pane was a table of contents with no pages.
    ///
    /// It plays the author's whole day, not the one row that was tapped. The pane draws
    /// one line per person ("3 updates"), so the tap has to mean what that line says:
    /// the page reads the day off `DataService.Statuses`, starts at the first update
    /// nobody has watched - which is where WhatsApp starts - and moves on by itself
    /// when a photo has had its moment or a video has ended. The segments at the top
    /// say how many there are and how far the reader has got.
    ///
    /// The page draws the row it is on and follows it: a status that arrived with
    /// its photo shows it at once, and one that came from the history with only its keys
    /// asks for the bytes here and fills in when they arrive. The same three cases the
    /// row already distinguishes - a photo, a video, words alone - are the three the
    /// screen shows.
    /// </summary>
    public sealed partial class StatusViewerPage : Page
    {
        /// <summary>
        /// A status is drawn at the screen's size, and 720 is the width this phone
        /// reports: the same ceiling the conversation's viewer decodes at
        /// (tools/check-memory.js refuses anything larger).
        /// </summary>
        private const int ViewerDecodePixels = 720;

        /// <summary>
        /// How long a photo or a sentence stays on screen before the next update takes
        /// its place. Five seconds is the official app's own beat, and it is also the
        /// shortest that lets a caption be read.
        /// </summary>
        private const int PhotoSeconds = 5;

        /// <summary>The author's updates, oldest first - the order they were posted in.</summary>
        private readonly List<ChatMessage> _queue = new List<ChatMessage>();

        /// <summary>Where in `_queue` the screen is.</summary>
        private int _index;

        private ChatMessage _status;

        /// <summary>The clock that moves a photo on. Built on the first photo shown.</summary>
        private DispatcherTimer _timer;

        /// <summary>The bytes were already asked for: asking on every refresh would
        /// download the same photo once per property change.</summary>
        private bool _asked;

        private bool _videoStarted;

        /// <summary>A decode is already running: the row changes twice in a row (the bytes,
        /// then the watched flag) and the second refresh must not start a second one.</summary>
        private bool _decoding;

        public StatusViewerPage()
        {
            this.InitializeComponent();

            string back = Loc.Get("ChatPage_BackTooltip", "Back");
            ToolTipService.SetToolTip(CloseButton, back);
            AutomationProperties.SetName(CloseButton, back);

            // Icon-only buttons take no x:Uid (it would overwrite the Path): the tooltip
            // and the spoken name are the label.
            string previous = Loc.Get("StatusViewer_Previous", "Previous update");
            string next = Loc.Get("StatusViewer_Next", "Next update");

            ToolTipService.SetToolTip(PreviousButton, previous);
            AutomationProperties.SetName(PreviousButton, previous);
            ToolTipService.SetToolTip(NextButton, next);
            AutomationProperties.SetName(NextButton, next);
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _status = e.Parameter as ChatMessage;
            if (_status == null)
            {
                // A page with no row is a navigation that went wrong, not a status that
                // is empty: it says so instead of showing a black screen with no clue.
                Diag.Ok("status: opened with no row, so there is nothing to show");
                MissingPanel.Visibility = Visibility.Visible;
                RetryButton.Visibility = Visibility.Collapsed;
                return;
            }

            BuildQueue();

            Diag.Ok("status: opened " + _status.Id + " by " + _status.ChatId
                + ", " + _queue.Count + " update(s) of the day");

            // The tap that got here *is* the view, and the first update shown is the one
            // the reader came for: `Show` sends the receipt for each one as it appears.
            Show(FirstUnwatched());
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            StopTimer();
            StopVideo();

            if (_status != null) _status.PropertyChanged -= OnStatusChanged;
            _status = null;

            _queue.Clear();
        }

        /// <summary>
        /// The day of the author tapped, in the order it was posted.
        ///
        /// The feed is newest first (that is how the list is read), and a person's updates
        /// are watched the way they were put up, so the queue is the reverse. A row that is
        /// no longer in the feed - the reader tapped just as it expired - is still shown:
        /// the page was handed it, and an empty screen with no explanation is worse.
        /// </summary>
        private void BuildQueue()
        {
            _queue.Clear();

            ObservableCollection<ChatMessage> feed = DataService.Instance.Statuses;

            for (int i = 0; i < feed.Count; i++)
            {
                ChatMessage row = feed[i];
                if (row == null || string.IsNullOrEmpty(row.ChatId)) continue;
                if (!string.Equals(row.ChatId, _status.ChatId, StringComparison.Ordinal)) continue;

                _queue.Add(row);
            }

            if (_queue.Count == 0)
            {
                _queue.Add(_status);
                return;
            }

            _queue.Reverse();
        }

        /// <summary>Where the reader starts: the first update of the day nobody has watched.</summary>
        private int FirstUnwatched()
        {
            for (int i = 0; i < _queue.Count; i++)
            {
                if (!_queue[i].IsStatusViewed) return i;
            }

            return 0;
        }

        /// <summary>
        /// Puts the update at `index` on screen and marks it watched.
        ///
        /// Everything the previous one left is put away first: the photo source, the video
        /// player, the "could not be loaded" panel and the spinner. A status page that
        /// advanced while the old picture was still up would show the two as one.
        /// </summary>
        private void Show(int index)
        {
            if (index < 0) return;

            if (index >= _queue.Count)
            {
                // The day is over: the way out is the way a status ends.
                Close();
                return;
            }

            StopTimer();
            StopVideo();

            if (_status != null) _status.PropertyChanged -= OnStatusChanged;

            _index = index;
            _status = _queue[index];
            _asked = false;
            _videoStarted = false;

            AuthorText.Text = string.IsNullOrEmpty(_status.SenderName) ? _status.ChatId : _status.SenderName;
            TimeText.Text = _status.FormattedTime;

            StatusImage.Source = null;
            StatusImage.Visibility = Visibility.Collapsed;
            StatusVideo.Visibility = Visibility.Collapsed;
            MissingPanel.Visibility = Visibility.Collapsed;
            Loading.IsActive = false;
            Loading.Visibility = Visibility.Collapsed;
            RetryButton.Visibility = Visibility.Visible;

            RenderSegments();
            UpdatePager();

            // From here on the row's changes are this page's business: the bytes arriving,
            // the request failing, or the watched flag of the one just shown.
            _status.PropertyChanged += OnStatusChanged;

            DataService.Instance.MarkStatusViewed(_status);

            Diag.Ok("status: showing " + (_index + 1) + " of " + _queue.Count
                + " (" + _status.Id + ")");

            Refresh();
        }

        /// <summary>
        /// One bar per update of the day: the ones up to and including this one are filled.
        ///
        /// Built here and not in the markup because how many there are is the author's
        /// business, and a `DataTemplate` per bar would be a list bound to a list for three
        /// rectangles. A lone update gets no bar: one filled segment says nothing.
        /// </summary>
        private void RenderSegments()
        {
            Segments.Children.Clear();
            Segments.ColumnDefinitions.Clear();

            if (_queue.Count < 2)
            {
                Segments.Visibility = Visibility.Collapsed;
                return;
            }

            Segments.Visibility = Visibility.Visible;

            for (int i = 0; i < _queue.Count; i++)
            {
                Segments.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });

                var bar = new Border();
                bar.Height = 3;
                bar.CornerRadius = new CornerRadius(1.5);
                bar.Margin = new Thickness(2, 0, 2, 0);

                // White for what has been seen, the same white at a third for the rest,
                // over the black ground the whole page is drawn on.
                bar.Background = new SolidColorBrush(i <= _index
                    ? Colors.White
                    : Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));

                Grid.SetColumn(bar, i);
                Segments.Children.Add(bar);
            }
        }

        /// <summary>"2 of 3" and the two arrows, or nothing when the day is one update.</summary>
        private void UpdatePager()
        {
            bool several = _queue.Count > 1;
            Pager.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
            if (!several) return;

            CounterText.Text = string.Format(Loc.Get("StatusViewer_Count", "{0} of {1}"),
                _index + 1, _queue.Count);

            // There is nothing before the first one: a live arrow that does nothing reads
            // as a page that did not answer.
            PreviousButton.IsEnabled = _index > 0;
        }

        /// <summary>
        /// The row changed under the page: the bytes arrived (the picture or the video
        /// file), the request failed, or it was marked watched. Every one of those is a
        /// reason to look again at what should be on screen.
        /// </summary>
        private async void OnStatusChanged(object sender, PropertyChangedEventArgs e)
        {
            // The bytes of a status are stored from the socket's own loop: whether that
            // resumes on this thread or not is not something this page can assume, and
            // touching the controls from another one throws. A dispatcher that refuses
            // the call is named instead of ending the process.
            if (!Dispatcher.HasThreadAccess)
            {
                try
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate { Refresh(); });
                }
                catch (Exception ex)
                {
                    Diag.Failed("StatusViewerPage/OnStatusChanged", ex);
                }
                return;
            }

            Refresh();
        }

        private void Refresh()
        {
            if (_status == null) return;

            bool video = _status.Type == MessageType.Video;
            bool picture = _status.Type == MessageType.Image;
            bool hasBytes = !string.IsNullOrEmpty(_status.MediaData)
                || !string.IsNullOrEmpty(_status.MediaFilePath);

            // A status that declares a photo or a video and has no bytes yet is one
            // whose picture is still on its way; the words of a status with no media
            // are the update itself.
            bool waiting = (video || picture) && !hasBytes;
            bool words = !video && !picture;

            StatusText.Text = _status.Text ?? "";
            StatusText.Visibility = words ? Visibility.Visible : Visibility.Collapsed;

            // A photo's caption is the same sentence under the picture, and it is not
            // repeated in the middle of the screen.
            bool caption = !words && !string.IsNullOrEmpty(_status.Text);
            CaptionText.Text = caption ? _status.Text : "";
            CaptionBar.Visibility = caption ? Visibility.Visible : Visibility.Collapsed;

            Loading.IsActive = waiting && _status.IsMediaLoading;
            Loading.Visibility = Loading.IsActive ? Visibility.Visible : Visibility.Collapsed;

            // "It could not be loaded" is only true once something was asked for and did
            // not come: on the first look at a status whose photo is still on its way, the
            // panel would be a lie for the moment the request takes to leave.
            MissingPanel.Visibility = waiting && _asked && !_status.IsMediaLoading
                ? Visibility.Visible
                : Visibility.Collapsed;

            // One request at a time: a retry pressed while the previous download is still
            // running would ask the server for the same file twice.
            RetryButton.IsEnabled = !_status.IsMediaLoading;

            if (waiting)
            {
                StatusImage.Visibility = Visibility.Collapsed;
                StatusVideo.Visibility = Visibility.Collapsed;
                AskForBytes();
                return;
            }

            if (video)
            {
                ShowVideo();
                return;
            }

            if (picture)
            {
                ShowPicture();
                return;
            }

            // Words alone are the update: they get the same moment a photo gets, so a feed
            // of sentences is read at a pace and not one tap at a time.
            StartTimer();
        }

        /// <summary>
        /// Asks WhatsApp for the bytes of an update that came without them - a status
        /// out of the history sync is exactly that: its keys and its address, no file.
        /// </summary>
        private void AskForBytes()
        {
            if (_asked) return;
            _asked = true;

            DataService.Instance.RequestStatusMedia(_status);
        }

        private async void ShowPicture()
        {
            if (StatusImage.Source != null && StatusImage.Visibility == Visibility.Visible)
            {
                StartTimer();
                return;
            }

            if (_decoding) return;

            // The row this decode belongs to: the reader may move on while it runs, and the
            // picture of an update that is no longer on screen is not drawn over the next.
            ChatMessage row = _status;
            _decoding = true;

            try
            {
                BitmapImage bitmap = await row.LoadBitmapAsync(ViewerDecodePixels);

                if (row != _status) return;

                if (bitmap == null)
                {
                    // Bytes that no decoder here accepts: the panel says so instead of
                    // leaving a black screen that looks like a page still loading.
                    MissingPanel.Visibility = Visibility.Visible;
                    return;
                }

                StatusImage.Source = bitmap;
                StatusImage.Visibility = Visibility.Visible;
                StatusVideo.Visibility = Visibility.Collapsed;
                MissingPanel.Visibility = Visibility.Collapsed;

                StartTimer();
            }
            catch (Exception ex)
            {
                // A photo the phone cannot draw is one update, not the app: the words
                // that say so stay on a black screen instead of a closed page.
                Diag.Failed("StatusViewerPage/ShowPicture", ex);

                if (row == _status) MissingPanel.Visibility = Visibility.Visible;
            }
            finally
            {
                _decoding = false;

                // The reader moved on while the decode was running: that update's own
                // `Refresh` was turned away by the flag above, and the one now on screen
                // has to be drawn by the call that comes after it.
                if (row != _status) Refresh();
            }
        }

        /// <summary>
        /// A status video plays where the photo would be, with its own transport: the
        /// file is the one the media path already stored, so there is no stream to keep
        /// open and no decoder per row (this page has one).
        /// </summary>
        private void ShowVideo()
        {
            string file = _status.MediaFilePath;
            if (string.IsNullOrEmpty(file)) return;

            try
            {
                StatusImage.Visibility = Visibility.Collapsed;
                StatusVideo.Visibility = Visibility.Visible;
                MissingPanel.Visibility = Visibility.Collapsed;

                if (_videoStarted) return;
                _videoStarted = true;

                StatusVideo.Source = new Uri("ms-appdata:///local/" + file);
                StatusVideo.Play();
            }
            catch (Exception ex)
            {
                Diag.Failed("StatusViewerPage/ShowVideo", ex);
                MissingPanel.Visibility = Visibility.Visible;
            }
        }

        private void StatusVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // On WP8.1 the event carries only the message, not the exception. The row
            // stays and says the update could not be loaded, so a failure and a status
            // that was never opened do not look the same.
            string reason = (e != null && !string.IsNullOrEmpty(e.ErrorMessage))
                ? e.ErrorMessage
                : "media failed";
            Diag.Failed("StatusViewerPage/video", new InvalidOperationException(reason));

            // The player gave up, so the *next* attempt has to be allowed to start: with this
            // flag left set, Retry hid the panel, asked for the video again and `ShowVideo`
            // returned at its own guard - a button that did nothing.
            _videoStarted = false;

            StatusVideo.Visibility = Visibility.Collapsed;
            MissingPanel.Visibility = Visibility.Visible;

            // And the day goes on: a clip this phone cannot decode is one update, and the
            // ones after it are still worth the reader's time.
            StartTimer();
        }

        /// <summary>
        /// The video ended by itself: the next update takes its place, which is the same
        /// beat a photo gets from the clock.
        /// </summary>
        private void StatusVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            Diag.Ok("status: the video ended");
            Next();
        }

        private void StopVideo()
        {
            if (StatusVideo == null) return;

            try
            {
                StatusVideo.Stop();
                StatusVideo.Source = null;
            }
            catch (Exception ex)
            {
                Diag.Failed("StatusViewerPage/StopVideo", ex);
            }
        }

        // ------------------------------------------------------------ the clock

        /// <summary>
        /// Starts the beat that moves a photo, a caption or a failed clip on. A lone update
        /// is not put on a clock: nothing follows it, and taking the page away mid-sentence
        /// is not what a reader asked for.
        /// </summary>
        private void StartTimer()
        {
            if (_queue.Count < 2) return;

            if (_timer == null)
            {
                _timer = new DispatcherTimer();
                _timer.Interval = TimeSpan.FromSeconds(PhotoSeconds);
                _timer.Tick += Timer_Tick;
            }

            if (_timer.IsEnabled) return;
            _timer.Start();
        }

        private void StopTimer()
        {
            if (_timer != null && _timer.IsEnabled) _timer.Stop();
        }

        private void Timer_Tick(object sender, object e)
        {
            StopTimer();
            Next();
        }

        // --------------------------------------------------------- moving on

        private void Next()
        {
            // Past the last one the page leaves, the way a status ends.
            if (_index + 1 >= _queue.Count)
            {
                Close();
                return;
            }

            Show(_index + 1);
        }

        private void Previous()
        {
            if (_index == 0) return;
            Show(_index - 1);
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            Next();
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            Previous();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// Asks for the bytes again.
        ///
        /// A status whose photo did not arrive - the server took it down, the connection
        /// died in the middle, the phone had no data - used to be a dead end: the words
        /// said it could not be loaded and the only way back to the picture was to leave
        /// the page and open it again. The tap that opened the status is the first
        /// attempt; this is the second, and it goes through the same request.
        /// </summary>
        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_status == null || _status.IsMediaLoading) return;

            MissingPanel.Visibility = Visibility.Collapsed;

            // The flag is what lets AskForBytes send the request again, and raising the
            // loading bit is what spins the indicator: the refresh it triggers calls
            // AskForBytes with the flag cleared.
            _asked = false;
            _status.IsMediaLoading = true;

            Diag.Ok("status: asked for " + _status.Id + " again");
        }

        /// <summary>
        /// Back to the status list: the tinted frame pops if it can, and a page that
        /// arrived without one (a deep link, a tile) goes home rather than nowhere.
        /// </summary>
        private void Close()
        {
            StopTimer();
            StopVideo();

            Frame frame = Frame;
            if (frame == null) return;

            if (frame.CanGoBack)
            {
                frame.GoBack();
                return;
            }

            frame.Navigate(typeof(HomePage));
        }
    }
}
