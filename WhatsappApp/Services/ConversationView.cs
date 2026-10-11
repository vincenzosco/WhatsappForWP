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
        private Action<bool> _onPosition;
        private bool? _lastAtBottom;
        private bool _watched;
        // The position was asked for before the list had a ScrollViewer, and the
        // layout pass that gives it one has not run yet: LayoutUpdated is where
        // the wait ends.
        private bool _waitingForViewer;
        // A scroll was asked for before the list was in the tree: it is kept and
        // flushed by the layout pass instead of dropped.
        private bool _waitingForScrollLayout;

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

            // A position watch asked for during a navigation found no viewer; this
            // is the next chance. The list still has no ScrollViewer on this line
            // - it is created by the layout pass that follows - so a watch that
            // finds nothing here waits for LayoutUpdated instead of giving up,
            // which is what left the arrow unable to ever appear.
            AttachViewChanged();
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
        /// Watches the position so the caller is told, in both directions, whether
        /// the newest row is on screen. It replaces what used to be a one-way
        /// notification (the reader reached the bottom): the arrow that comes back
        /// to the newest message has to be shown when it is left, which is the half
        /// nobody was told about.
        ///
        /// The callback is called once right here, so the arrow starts in the right
        /// state instead of waiting for the first scroll.
        /// </summary>
        public void WatchPosition(Action<bool> atBottom)
        {
            _onPosition = atBottom;
            _lastAtBottom = null;
            AttachViewChanged();
        }

        /// <summary>
        /// Puts the position handler on the viewer, once. It is called again from
        /// Bind, and that second call is the point: the page asks for a position
        /// during OnNavigatedTo, before the list has a viewer, and a handler that
        /// gave up then would never hear anything. A conversation with no viewer is
        /// at its bottom - there is nothing below to be torn away from - and that
        /// is what it reports while it waits.
        /// </summary>
        private void AttachViewChanged()
        {
            if (_onPosition == null) return;

            ReportPosition();

            if (_watched) return;

            ScrollViewer viewer = Viewer();
            if (viewer != null)
            {
                StopWaitingForViewer();
                viewer.ViewChanged += OnViewChanged;
                _watched = true;
                return;
            }

            // No viewer yet: the ListView creates it in the layout pass that follows
            // the bind, not on the bind itself, so a watch put on now has nothing to
            // subscribe to. LayoutUpdated is that pass; it is taken off the moment a
            // viewer is found, and DetachViewChanged takes it off if the page leaves
            // first.
            if (_waitingForViewer) return;

            _waitingForViewer = true;
            _list.LayoutUpdated += OnListLayoutUpdated;
        }

        /// <summary>
        /// The layout pass ran: the list may have its viewer now, and if it does the
        /// watch moves from the layout to the scroll and stops watching the layout.
        /// </summary>
        private void OnListLayoutUpdated(object sender, object e)
        {
            ScrollViewer viewer = Viewer();
            Diag.Ok("probe position layout: viewer=" + (viewer != null));
            if (viewer == null) return;

            StopWaitingForViewer();
            viewer.ViewChanged += OnViewChanged;
            _watched = true;
            ReportPosition();
        }

        /// <summary>Stops waiting for the viewer, if the wait was on.</summary>
        private void StopWaitingForViewer()
        {
            if (!_waitingForViewer) return;

            _list.LayoutUpdated -= OnListLayoutUpdated;
            _waitingForViewer = false;
        }

        /// <summary>Takes the position handler off, if it was put on.</summary>
        private void DetachViewChanged()
        {
            StopWaitingForViewer();

            if (!_watched) return;

            ScrollViewer viewer = Viewer();
            if (viewer != null) viewer.ViewChanged -= OnViewChanged;
            _watched = false;
        }

        /// <summary>The answer changed? Then it is worth saying.</summary>
        private void ReportPosition()
        {
            bool atBottom = AtBottom();
            if (_lastAtBottom == atBottom) return;

            _lastAtBottom = atBottom;
            _onPosition(atBottom);
        }

        /// <summary>
        /// Forgets the viewer: it belongs to the visual tree of the page that is
        /// leaving, and a kept one would be scrolled through after the next
        /// navigation.
        /// </summary>
        public void Reset()
        {
            DetachViewChanged();
            StopWaitingForScrollLayout();
            _scrollQueued = false;
            _viewer = null;
            _pendingScroll = null;
            // The page that asked is the one leaving, and its callback closes over
            // its own buttons: a kept one would be called for a page in the tree no
            // more.
            _onPosition = null;
            _lastAtBottom = null;
        }

        private void QueueScroll(ChatMessage message)
        {
            _pendingScroll = message;
            if (_scrollQueued) return;

            _scrollQueued = true;
#pragma warning disable 4014
            _dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                try { FlushScroll(); }
                catch (Exception ex) { Diag.Failed("ConversationView/flushScroll", ex); }
            });
#pragma warning restore 4014
        }

        /// <summary>
        /// Runs the queued scroll, or keeps it for the layout pass that follows.
        ///
        /// The dispatch runs before the ListView has created its children when the
        /// scroll is asked for at bind time, and a list with no viewer has no way to
        /// reach the bottom. Dropping the scroll there is the chat that opened on
        /// its oldest row: the bind asks for the newest row, and the layout that
        /// would have carried it out had not happened yet. It is kept instead, and
        /// LayoutUpdated flushes it.
        ///
        /// The dispatcher would swallow a fault raised here, so each step logs its
        /// own: a scroll that throws must not only be visible, it must say which
        /// call threw. UpdateLayout on a list that was just bound during a
        /// navigation is what answered E_UNEXPECTED here, so it is kept out of the
        /// common path - the viewer reaches the bottom without a layout pass, and
        /// only the ScrollIntoView fallback needs the container to exist.
        /// </summary>
        private void FlushScroll()
        {
            // WP8.1 has no FrameworkElement.IsLoaded (that one is Windows 10), so
            // the test is the list having a visual child at all - its ScrollViewer.
            int children = VisualTreeHelper.GetChildrenCount(_list);
            if (children == 0)
            {
                WaitForScrollLayout();
                return;
            }

            _scrollQueued = false;

            // The row to show: the one that was asked for, or the newest of the
            // collection when the bottom is what the caller wanted.
            ChatMessage target = _pendingScroll;
            _pendingScroll = null;
            if (target == null && _items != null && _items.Count > 0)
                target = _items[_items.Count - 1];

            // ScrollIntoView, not a move to ScrollableHeight. A ListView that has
            // not measured its rows yet answers a ScrollableHeight of two pixels for
            // fifty messages - the rows are realized as they come on screen - and
            // moving to that "bottom" leaves the conversation on its oldest row,
            // which is exactly the chat that opened on "those of an earlier time".
            // ScrollIntoView asks the list for the row instead: it realizes what it
            // must and puts that row on screen against the bottom edge.
            if (target != null)
            {
                try
                {
                    _list.ScrollIntoView(target);
                    Diag.Ok("probe flush: scrolled to a row, offset=" + Offset()
                        + " scrollable=" + Scrollable());
                    return;
                }
                catch (Exception ex)
                {
                    Diag.Failed("ConversationView/scrollIntoView", ex);
                }
            }

            // Nothing to name - an empty collection, a bottom asked for before the
            // bind - so the viewer is the only handle left.
            ScrollViewer viewer = Viewer();
            Diag.Ok("probe flush: no row, viewer=" + (viewer != null)
                + " scrollable=" + Scrollable());
            if (viewer != null && viewer.ScrollableHeight > 0)
            {
                ChangeViewToBottom(viewer);
            }
        }

        /// <summary>The viewer's current offset, or -1 while there is no viewer.</summary>
        private double Offset()
        {
            ScrollViewer viewer = Viewer();
            return viewer == null ? -1 : viewer.VerticalOffset;
        }

        /// <summary>The viewer's scrollable height, or -1 while there is no viewer.</summary>
        private double Scrollable()
        {
            ScrollViewer viewer = Viewer();
            return viewer == null ? -1 : viewer.ScrollableHeight;
        }

        /// <summary>
        /// The list is not in the tree yet, so the scroll waits for the layout that
        /// puts it there. _scrollQueued stays on: a second request only replaces
        /// what will be flushed, instead of scheduling a second dispatch.
        /// </summary>
        private void WaitForScrollLayout()
        {
            if (_waitingForScrollLayout) return;

            _waitingForScrollLayout = true;
            _list.LayoutUpdated += OnScrollLayoutUpdated;
        }

        /// <summary>
        /// The layout ran and the list is in the tree: the flush is asked for on the
        /// dispatcher rather than done here, because touching the scroll in the
        /// middle of a layout pass is the re-entrancy this class avoids.
        /// </summary>
        private void OnScrollLayoutUpdated(object sender, object e)
        {
            Diag.Ok("probe scroll layout: children=" + VisualTreeHelper.GetChildrenCount(_list));
            if (VisualTreeHelper.GetChildrenCount(_list) == 0) return;

            StopWaitingForScrollLayout();
#pragma warning disable 4014
            _dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                try { FlushScroll(); }
                catch (Exception ex) { Diag.Failed("ConversationView/flushScroll", ex); }
            });
#pragma warning restore 4014
        }

        private void StopWaitingForScrollLayout()
        {
            if (!_waitingForScrollLayout) return;

            _list.LayoutUpdated -= OnScrollLayoutUpdated;
            _waitingForScrollLayout = false;
        }

        private void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_onPosition == null) return;
            ReportPosition();
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
