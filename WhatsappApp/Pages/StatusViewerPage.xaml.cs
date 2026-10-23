using System;
using System.ComponentModel;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using WhatsappApp.Models;
using WhatsappApp.Services;

namespace WhatsappApp.Pages
{
    /// <summary>
    /// One status, full screen: what the contact posted.
    ///
    /// Why it exists: the status tab could only *list* an update. Tapping a row sent the
    /// `read` receipt - the author's ring filled - and then nothing appeared, so the one
    /// thing a status is for (seeing the photo or the sentence somebody put up) was the
    /// one thing this app could not do. The pane was a table of contents with no pages.
    ///
    /// The page draws the row it was handed and follows it: a status that arrived with
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

        private ChatMessage _status;

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
            ToolTipService.SetToolTip(CloseButton, Loc.Get("ChatPage_BackTooltip", "Back"));
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

            AuthorText.Text = string.IsNullOrEmpty(_status.SenderName) ? _status.ChatId : _status.SenderName;
            TimeText.Text = _status.FormattedTime;

            _status.PropertyChanged -= OnStatusChanged;
            _status.PropertyChanged += OnStatusChanged;

            Diag.Ok("status: opened " + _status.Id + " by " + _status.ChatId);

            // The tap that got here *is* the view: the receipt goes first, so the
            // author's ring fills even when the picture never loads.
            DataService.Instance.MarkStatusViewed(_status);

            Refresh();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            if (_status != null) _status.PropertyChanged -= OnStatusChanged;
            _status = null;

            StopVideo();
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
            if (StatusImage.Source != null && StatusImage.Visibility == Visibility.Visible) return;
            if (_decoding) return;

            _decoding = true;

            try
            {
                BitmapImage bitmap = await _status.LoadBitmapAsync(ViewerDecodePixels);

                // The row may be gone: the page was left while the decode was running.
                if (_status == null || bitmap == null) return;

                StatusImage.Source = bitmap;
                StatusImage.Visibility = Visibility.Visible;
                StatusVideo.Visibility = Visibility.Collapsed;
                MissingPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                // A photo the phone cannot draw is one update, not the app: the words
                // that say so stay on a black screen instead of a closed page.
                Diag.Failed("StatusViewerPage/ShowPicture", ex);
                MissingPanel.Visibility = Visibility.Visible;
            }
            finally
            {
                _decoding = false;
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

            StatusVideo.Visibility = Visibility.Collapsed;
            MissingPanel.Visibility = Visibility.Visible;
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
