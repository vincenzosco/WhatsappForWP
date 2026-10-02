using System;
using System.Collections.ObjectModel;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    /// <summary>
    /// The conversation's view state: which collection the list shows, where the
    /// newest bubble is, and the scroll viewer it is reached through.
    ///
    /// Why it exists: the E_UNEXPECTED that left a chat empty came from inserting
    /// into a collection the ListView was already watching, and it moved to the
    /// scroll when the insert was fixed. That is one seam - the list, the bind and
    /// the scroll all touch the same three things - and it was spread across the
    /// page's fields. Here it has one home and one interface.
    /// </summary>
    public sealed class ConversationView
    {
        /// <summary>
        /// How far from the bottom the conversation still counts as "at the
        /// bottom". A bubble is about sixty pixels tall: a smaller margin would
        /// answer "no" while the newest bubble is still fully on screen, and a
        /// larger one would follow a reader who has scrolled away.
        /// </summary>
        private const int BottomFollowMargin = 80;

        private readonly ListView _list;
        private readonly CoreDispatcher _dispatcher;

        private ObservableCollection<ChatMessage> _items;
        private ScrollViewer _viewer;
        private ChatMessage _pendingScroll;
        private bool _scrollQueued;
        private Action _onBottom;
        private bool _watched;

        public ConversationView(ListView list, CoreDispatcher dispatcher)
        {
            if (list == null) throw new ArgumentNullException("list");
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");

            _list = list;
            _dispatcher = dispatcher;
        }

        /// <summary>
        /// Binds the list to the collection. It is called only after the saved
        /// copy is in: binding before an insert into a collection the ListView is
        /// already watching, in the middle of a navigation, is what answered
        /// E_UNEXPECTED and left the conversation empty.
        /// </summary>
        public void Bind(ObservableCollection<ChatMessage> messages)
        {
            _items = messages;
            _list.ItemsSource = messages;
            Diag.Ok("conversation bound " + (messages == null ? 0 : messages.Count) + " message(s)");
        }

        /// <summary>
        /// Queues a move to the newest bubble, once per burst: a burst of incoming
        /// messages used to do an UpdateLayout + ScrollIntoView for each one, that
        /// is a full layout pass per message.
        /// </summary>
        public void ScrollTo(ChatMessage message)
        {
            QueueScroll(message);
        }

        /// <summary>
        /// Moves to the bottom, which is where the newest message is.
        /// </summary>
        public void ScrollToBottom()
        {
            QueueScroll(null);
        }

        /// <summary>
        /// Whether the conversation is showing its newest row.
        ///
        /// Only the scroll viewer knows: the list does not expose where it is. A
        /// list shorter than its viewport, and one without a viewer yet, are both
        /// "at the bottom" - there is nothing below to be torn away from.
        /// </summary>
        public bool AtBottom()
        {
            ScrollViewer viewer = Viewer();
            if (viewer == null || viewer.ScrollableHeight <= 0) return true;
            return viewer.ScrollableHeight - viewer.VerticalOffset <= BottomFollowMargin;
        }

        /// <summary>
        /// Watches the position so the caller is told when the reader reaches the
        /// bottom. The handler is put on once: ViewChanged fires on every frame of
        /// a scroll.
        /// </summary>
        public void WatchBottom(Action onBottom)
        {
            if (_watched) return;

            ScrollViewer viewer = Viewer();
            if (viewer == null) return;

            _onBottom = onBottom;
            viewer.ViewChanged += OnViewChanged;
            _watched = true;
        }

        /// <summary>Takes the position handler off, if it was put on.</summary>
        public void UnwatchBottom()
        {
            if (!_watched) return;

            ScrollViewer viewer = Viewer();
            if (viewer != null) viewer.ViewChanged -= OnViewChanged;
            _watched = false;
        }

        /// <summary>
        /// Forgets the viewer: it belongs to the visual tree of the page that is
        /// leaving, and a kept one would be scrolled through after the next
        /// navigation.
        /// </summary>
        public void Reset()
        {
            UnwatchBottom();
            _viewer = null;
            _pendingScroll = null;
        }

        private void QueueScroll(ChatMessage message)
        {
            _pendingScroll = message;
            if (_scrollQueued) return;

            _scrollQueued = true;
#pragma warning disable 4014
            _dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                // The dispatcher would swallow a fault raised here, so each step
                // logs its own: a scroll that throws must not only be visible, it
                // must say which call threw. UpdateLayout on a list that was just
                // bound during a navigation is what answered E_UNEXPECTED here, so
                // it is kept out of the common path - the viewer reaches the bottom
                // without a layout pass, and only the ScrollIntoView fallback needs
                // the container to exist.
                _scrollQueued = false;
                ChatMessage target = _pendingScroll;
                _pendingScroll = null;

                ScrollViewer viewer = Viewer();
                if (viewer != null && viewer.ScrollableHeight > 0)
                {
                    ChangeViewToBottom(viewer);
                    return;
                }

                try
                {
                    _list.UpdateLayout();
                    if (target != null) _list.ScrollIntoView(target);
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConversationView/scrollIntoView", ex);
                }
            });
#pragma warning restore 4014
        }

        private void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_onBottom == null || !AtBottom()) return;
            _onBottom();
        }

        /// <summary>
        /// The scroll viewer of the conversation, looked up once and kept. A walk
        /// of the visual tree during a navigation can answer E_UNEXPECTED too, so
        /// that lookup is named as well.
        /// </summary>
        private ScrollViewer Viewer()
        {
            if (_viewer == null)
            {
                try
                {
                    _viewer = FindScrollViewer(_list);
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConversationView/findViewer", ex);
                }
            }
            return _viewer;
        }

        /// <summary>
        /// Moves the conversation to its bottom, naming the call if it throws.
        /// </summary>
        private static void ChangeViewToBottom(ScrollViewer viewer)
        {
            try
            {
                viewer.ChangeView(null, viewer.ScrollableHeight, null);
            }
            catch (Exception ex)
            {
                Diag.Failed("ConversationView/changeView", ex);
            }
        }

        /// <summary>
        /// The scroll viewer a ListView keeps its items in. The list does not
        /// expose it, and it is the only handle that reaches the bottom exactly:
        /// its height is the end of the list, whatever the rows happen to measure.
        /// </summary>
        private static ScrollViewer FindScrollViewer(DependencyObject root)
        {
            if (root == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                ScrollViewer viewer = child as ScrollViewer;
                if (viewer != null) return viewer;

                viewer = FindScrollViewer(child);
                if (viewer != null) return viewer;
            }
            return null;
        }
    }
}
