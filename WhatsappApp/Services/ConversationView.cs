using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Phone.Controls;
using WhatsappApp.Models;

namespace WhatsappApp.Services
{
    public sealed class ConversationView
    {
        private const double BottomFollowMargin = 80;
        private const double NearTopMargin = 160;

        private readonly LongListSelector _list;
        private readonly Dispatcher _dispatcher;
        private ObservableCollection<ChatMessage> _items;
        private ScrollBar _scrollBar;
        private ChatMessage _pendingScroll;
        private Action<bool> _onPosition;
        private Action _onNearTop;
        private bool? _lastAtBottom;
        private bool _scrollQueued;
        private bool _watchingScrollBar;
        private bool _waitingForLayout;
        private bool _nearTopSaid;

        public ConversationView(LongListSelector list, Dispatcher dispatcher)
        {
            if (list == null) throw new ArgumentNullException("list");
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");

            _list = list;
            _dispatcher = dispatcher;
        }

        public void Bind(ObservableCollection<ChatMessage> messages)
        {
            _items = messages;
            _list.ItemsSource = messages;
            AttachScrollBar();
            Diag.Ok("conversation bound " + (messages == null ? 0 : messages.Count) + " message(s)");
        }

        public void ScrollTo(ChatMessage message)
        {
            QueueScroll(message);
        }

        public void ScrollToBottom()
        {
            QueueScroll(null);
        }

        public bool AtBottom()
        {
            ScrollBar bar = ScrollBar();
            if (bar == null || bar.Maximum <= bar.Minimum) return true;
            return bar.Maximum - bar.Value <= BottomFollowMargin;
        }

        public void WatchPosition(Action<bool> atBottom)
        {
            _onPosition = atBottom;
            _lastAtBottom = null;
            AttachScrollBar();
            ReportPosition();
        }

        public void WatchNearTop(Action loadOlder)
        {
            _onNearTop = loadOlder;
            _nearTopSaid = false;
            AttachScrollBar();
        }

        public void Reset()
        {
            StopWaitingForLayout();
            if (_watchingScrollBar && _scrollBar != null)
                _scrollBar.ValueChanged -= OnScrollBarValueChanged;

            _scrollBar = null;
            _watchingScrollBar = false;
            _scrollQueued = false;
            _pendingScroll = null;
            _onPosition = null;
            _onNearTop = null;
            _lastAtBottom = null;
            _nearTopSaid = false;
        }

        private void QueueScroll(ChatMessage message)
        {
            _pendingScroll = message;
            if (_scrollQueued) return;

            _scrollQueued = true;
            _dispatcher.BeginInvoke(delegate
            {
                try { FlushScroll(); }
                catch (Exception ex) { Diag.Failed("ConversationView/flushScroll", ex); }
            });
        }

        private void FlushScroll()
        {
            if (VisualTreeHelper.GetChildrenCount(_list) == 0)
            {
                WaitForScrollLayout();
                return;
            }

            _scrollQueued = false;
            ChatMessage target = _pendingScroll;
            _pendingScroll = null;

            if (target == null && _items != null && _items.Count > 0)
                target = _items[_items.Count - 1];
            if (target != null && _items != null && !_items.Contains(target) && _items.Count > 0)
                target = _items[_items.Count - 1];

            if (target != null)
            {
                try { _list.ScrollTo(target); }
                catch (Exception ex) { Diag.Failed("ConversationView/scrollTo", ex); }
            }

            AttachScrollBar();
        }

        private void WaitForScrollLayout()
        {
            WaitForLayout();
        }

        private void WaitForLayout()
        {
            if (_waitingForLayout) return;
            _waitingForLayout = true;
            _list.LayoutUpdated += OnListLayoutUpdated;
        }

        private void StopWaitingForLayout()
        {
            if (!_waitingForLayout) return;
            _list.LayoutUpdated -= OnListLayoutUpdated;
            _waitingForLayout = false;
        }

        private void OnListLayoutUpdated(object sender, EventArgs e)
        {
            if (VisualTreeHelper.GetChildrenCount(_list) == 0) return;

            StopWaitingForLayout();
            AttachScrollBar();

            if (_scrollQueued)
            {
                _dispatcher.BeginInvoke(delegate
                {
                    try { FlushScroll(); }
                    catch (Exception ex) { Diag.Failed("ConversationView/flushScroll", ex); }
                });
            }
        }

        private void AttachScrollBar()
        {
            if (_onPosition == null && _onNearTop == null) return;

            ScrollBar bar = FindVerticalScrollBar(_list);
            if (bar == null)
            {
                WaitForLayout();
                return;
            }

            StopWaitingForLayout();
            if (_scrollBar != bar)
            {
                if (_watchingScrollBar && _scrollBar != null)
                    _scrollBar.ValueChanged -= OnScrollBarValueChanged;

                _scrollBar = bar;
                _scrollBar.ValueChanged += OnScrollBarValueChanged;
                _watchingScrollBar = true;
            }

            ReportNearTop();
            ReportPosition();
        }

        private ScrollBar ScrollBar()
        {
            if (_scrollBar == null)
            {
                try { _scrollBar = FindVerticalScrollBar(_list); }
                catch (Exception ex) { Diag.Failed("ConversationView/findScrollBar", ex); }
            }
            return _scrollBar;
        }

        private void OnScrollBarValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            ReportNearTop();
            ReportPosition();
        }

        private void ReportPosition()
        {
            if (_onPosition == null) return;

            bool atBottom = AtBottom();
            if (_lastAtBottom == atBottom) return;

            _lastAtBottom = atBottom;
            try { _onPosition(atBottom); }
            catch (Exception ex) { Diag.Failed("ConversationView/position", ex); }
        }

        private void ReportNearTop()
        {
            if (_onNearTop == null) return;

            ScrollBar bar = ScrollBar();
            if (bar == null) return;

            bool near = bar.Value <= NearTopMargin;
            if (!near)
            {
                _nearTopSaid = false;
                return;
            }

            if (_nearTopSaid) return;
            _nearTopSaid = true;

            try { _onNearTop(); }
            catch (Exception ex) { Diag.Failed("ConversationView/nearTop", ex); }
        }

        private static ScrollBar FindVerticalScrollBar(DependencyObject root)
        {
            if (root == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                ScrollBar bar = child as ScrollBar;
                if (bar != null && bar.Orientation == Orientation.Vertical) return bar;

                bar = FindVerticalScrollBar(child);
                if (bar != null) return bar;
            }

            return null;
        }
    }
}
