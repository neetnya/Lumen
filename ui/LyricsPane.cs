using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Lumen.Lyrics;

namespace Lumen.UI
{
    /// <summary>
    /// 歌词区：
    /// 当前行高亮放大、自动居中滚动、相邻两句微微提亮、点击任意一句跳转播放。
    /// 纯文本歌词也能滚动查看。
    /// </summary>
    public sealed class LyricsPane : Grid
    {
        private static readonly DependencyProperty AnimatedVerticalOffsetProperty =
            DependencyProperty.RegisterAttached(
                "AnimatedVerticalOffset",
                typeof(double),
                typeof(LyricsPane),
                new PropertyMetadata(0.0, OnAnimatedVerticalOffsetChanged));

        private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var sv = d as ScrollViewer;
            if (sv == null) return;
            var pane = sv.Tag as LyricsPane;
            if (pane != null) pane._isProgrammaticScroll = true;
            try
            {
                sv.ScrollToVerticalOffset((double)e.NewValue);
            }
            finally
            {
                if (pane != null) pane._isProgrammaticScroll = false;
            }
        }

        private readonly ScrollViewer _scroll;
        private readonly StackPanel _stack;
        private readonly TextBlock _empty;
        private readonly List<TextBlock> _lines = new List<TextBlock>();
        private readonly DispatcherTimer _resumeAutoScrollTimer;

        private LyricsDocument _doc;
        private int _current = -2;
        private int _hoveredIndex = -1;
        private bool _userScrolling;
        private bool _isProgrammaticScroll;

        /// <summary>点击某句歌词（参数为秒）。</summary>
        public event Action<double> LineClicked;

        public LyricsPane()
        {
            Background = Theme.Brush("Brush.Panel");
            ClipToBounds = true;

            _stack = new StackPanel
            {
                Margin = new Thickness(28, 24, 28, 60),
                VerticalAlignment = VerticalAlignment.Top
            };

            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _stack,
                CanContentScroll = false,
                Tag = this
            };
            _scroll.ScrollChanged += OnScrollChanged;
            _scroll.PreviewMouseWheel += OnMouseWheel;
            _scroll.SizeChanged += delegate { UpdateVerticalPadding(); };
            Children.Add(_scroll);

            _empty = new TextBlock
            {
                Text = "暂无歌词",
                Foreground = Theme.Brush("Brush.TextFaint"),
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Children.Add(_empty);

            _resumeAutoScrollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _resumeAutoScrollTimer.Tick += delegate
            {
                _resumeAutoScrollTimer.Stop();
                _userScrolling = false;
                if (_doc != null && _doc.IsSynced && _current >= 0)
                {
                    CenterLine(_current);
                }
            };
        }

        private void UpdateVerticalPadding()
        {
            double vh = _scroll.ViewportHeight;
            if (vh <= 0) vh = _scroll.ActualHeight;

            if (_doc != null && !_doc.IsSynced)
            {
                _stack.Margin = new Thickness(28, 24, 28, 40);
                return;
            }

            double pad = vh > 40 ? Math.Max(24, vh / 2.0 - 20.0) : 24.0;
            _stack.Margin = new Thickness(28, pad, 28, pad);
        }

        // ------------------------------------------------------------------

        public void SetLyrics(LyricsDocument doc)
        {
            _doc = doc;
            _lines.Clear();
            _stack.Children.Clear();
            _current = -2;
            _hoveredIndex = -1;
            _userScrolling = false;
            _resumeAutoScrollTimer.Stop();

            _scroll.BeginAnimation(AnimatedVerticalOffsetProperty, null);
            _isProgrammaticScroll = true;
            try
            {
                _scroll.ScrollToVerticalOffset(0);
            }
            finally
            {
                _isProgrammaticScroll = false;
            }

            if (doc == null || doc.IsEmpty)
            {
                _empty.Visibility = Visibility.Visible;
                _scroll.Visibility = Visibility.Collapsed;
                return;
            }

            _empty.Visibility = Visibility.Collapsed;
            _scroll.Visibility = Visibility.Visible;

            bool synced = doc.IsSynced;
            UpdateVerticalPadding();

            for (int i = 0; i < doc.Lines.Count; i++)
            {
                var line = doc.Lines[i];
                var block = new TextBlock
                {
                    Text = line.Text,
                    TextWrapping = TextWrapping.Wrap,
                    // 文字水平居中
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    FontSize = 15,
                    LineHeight = 24,
                    Margin = new Thickness(0, 7, 0, 7),
                    Foreground = Theme.Brush("Brush.TextDim"),
                    Cursor = synced ? Cursors.Hand : Cursors.Arrow,
                    Tag = i
                };

                if (synced)
                {
                    block.MouseLeftButtonUp += OnLineClick;
                    block.MouseEnter += OnLineEnter;
                    block.MouseLeave += OnLineLeave;
                }

                _lines.Add(block);
                _stack.Children.Add(block);
            }
        }

        private void OnLineClick(object sender, MouseButtonEventArgs e)
        {
            var block = sender as TextBlock;
            if (block == null || _doc == null || !_doc.IsSynced) return;

            int index = (int)block.Tag;
            if (index < 0 || index >= _doc.Lines.Count) return;

            var handler = LineClicked;
            if (handler != null) handler(_doc.Lines[index].Time);
        }

        private void OnLineEnter(object sender, MouseEventArgs e)
        {
            if (_doc == null || !_doc.IsSynced) return;
            var block = sender as TextBlock;
            if (block == null) return;
            int index = (int)block.Tag;
            _hoveredIndex = index;
            if (index == _current) return;
            block.Foreground = Theme.Brush("Brush.Text");
        }

        private void OnLineLeave(object sender, MouseEventArgs e)
        {
            var block = sender as TextBlock;
            if (block == null) return;
            int index = (int)block.Tag;
            if (_hoveredIndex == index) _hoveredIndex = -1;
            if (index == _current) return;
            block.Foreground = Theme.Brush("Brush.TextDim");
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            // 用户手动滚动后，停止当前平滑滚动动画并暂停自动居中 3 秒
            _scroll.BeginAnimation(AnimatedVerticalOffsetProperty, null);
            _userScrolling = true;
            _resumeAutoScrollTimer.Stop();
            _resumeAutoScrollTimer.Start();
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (_isProgrammaticScroll) return;
            if (e.ExtentHeightChange == 0 && Math.Abs(e.VerticalChange) > 0.01)
            {
                _userScrolling = true;
                _resumeAutoScrollTimer.Stop();
                _resumeAutoScrollTimer.Start();
            }
        }

        // ------------------------------------------------------------------

        /// <summary>随播放进度更新高亮行（由主窗口定时调用）。</summary>
        public void UpdatePosition(double seconds, bool isSynced)
        {
            if (_doc == null || _doc.IsEmpty) return;

            if (!_doc.IsSynced)
            {
                // 纯文本：不做同步高亮
                return;
            }

            int index = _doc.IndexAt(seconds);
            if (index == _current) return;

            int oldIndex = _current;
            _current = index;

            ApplyHighlight(oldIndex, index);
            if (!_userScrolling && index >= 0)
            {
                int targetIndex = index;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
                {
                    if (!_userScrolling && _current == targetIndex)
                    {
                        CenterLine(targetIndex);
                    }
                }));
            }
        }

        private void ApplyHighlight(int oldIndex, int newIndex)
        {
            if (oldIndex >= 0 && oldIndex < _lines.Count && oldIndex != newIndex)
            {
                var prev = _lines[oldIndex];
                prev.FontSize = 15;
                prev.FontWeight = FontWeights.Normal;
                prev.Foreground = oldIndex == _hoveredIndex
                    ? Theme.Brush("Brush.Text")
                    : Theme.Brush("Brush.TextDim");
                prev.Opacity = 1.0;
            }

            if (newIndex >= 0 && newIndex < _lines.Count)
            {
                var curr = _lines[newIndex];
                // 当前行高亮放大
                curr.FontSize = 19;
                curr.FontWeight = FontWeights.SemiBold;
                curr.Foreground = Theme.Brush("Brush.Accent");
                curr.Opacity = 1.0;
            }
        }

        private void CenterLine(int index)
        {
            if (index < 0 || index >= _lines.Count) return;

            var block = _lines[index];
            try
            {
                // 让当前行居中：目标偏移 = 行中心 - 视口一半
                var transform = block.TransformToAncestor(_stack);
                var pos = transform.Transform(new Point(0, 0));
                double lineCenter = pos.Y + block.ActualHeight / 2.0;
                double target = lineCenter - _scroll.ViewportHeight / 2.0 + _stack.Margin.Top;

                target = Math.Max(0, Math.Min(target, _scroll.ScrollableHeight));

                var animation = new DoubleAnimation
                {
                    From = _scroll.VerticalOffset,
                    To = target,
                    Duration = TimeSpan.FromMilliseconds(260),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                _scroll.BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
            }
            catch (Exception ex)
            {
                Log.Warn("歌词自动居中滚动失败: " + ex.Message);
            }
        }

        /// <summary>清空高亮（停止播放时）。</summary>
        public void ClearHighlight()
        {
            int oldIndex = _current;
            _current = -2;
            ApplyHighlight(oldIndex, -1);
        }
    }
}
