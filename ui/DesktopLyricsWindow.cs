using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Lumen.Lyrics;
using Lumen.Native;

namespace Lumen.UI
{
    /// <summary>
    /// 桌面歌词。
    /// - 独立置顶歌词条，白字黑描边，无卡拉 OK 逐字变色
    /// - 宽度固定为屏幕可用宽度的 60%，水平居中，不可拖宽
    /// - 字号固定，只跟字号倍数有关，与句子长短无关
    /// - 切句瞬间生效；只有超长句才横向滚动（跑马灯），滚完回到句首
    /// - 没有歌词时就是一条空条，不显示任何占位文字
    /// - 解锁：拖动移动、拖上下边缘改高度、双击锁定；锁定：鼠标完全穿透
    /// </summary>
    public sealed class DesktopLyricsWindow : Window
    {
        private const double WidthRatio = 0.60;
        private const double MinBarHeight = 46;
        private const double MaxBarHeight = 320;
        private const double BaseFontSize = 30;
        private const double MarqueeSpeedDipPerSec = 56.0;

        /// <summary>默认位置：屏幕上方，距工作区顶部留一点边距。</summary>
        private const double DefaultTopGap = 60;

        private static readonly SolidColorBrush HitTestBrush = CreateFrozenBrush(Color.FromArgb(1, 0, 0, 0));
        private static readonly SolidColorBrush HoverBackdropBrush = CreateFrozenBrush(Color.FromArgb(88, 20, 22, 26));

        private readonly Canvas _canvas;
        private readonly OutlinedTextBlock _text;
        private readonly TranslateTransform _marqueeTransform = new TranslateTransform();
        private readonly Stopwatch _marqueeClock = new Stopwatch();

        private LyricsDocument _doc;
        private int _currentIndex = -1;
        private bool _locked;
        private bool _isPlaying;
        private double _fontScale = 1.0;

        private readonly DispatcherTimer _marquee;

        private double _marqueeOffset;
        private double _marqueeTarget;
        private bool _needsMarquee;
        private DateTime _marqueePauseUntil = DateTime.MinValue;

        private double _dragStartY;
        private double _dragStartHeight;
        private double _dragStartBottom;
        private bool _resizingTop;
        private bool _resizingBottom;
        private bool _draggingWindow;

        private Point _dragStartScreen;
        private double _dragStartLeft;
        private double _dragStartTop;

        private double _desktopLeft = double.NaN;
        private double _desktopTop = double.NaN;

        public event Action<double, double, double> PositionChanged;
        public event Action<bool> LockChanged;
        public event Action<double> FontScaleChanged;
        public event Action HiddenRequested;

        public DesktopLyricsWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Height = 92;
            FontFamily = new FontFamily("Microsoft YaHei");
            SnapsToDevicePixels = true;
            ShowActivated = false;

            _text = new OutlinedTextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei"),
                FontSize = BaseFontSize,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                // 白字黑描边（均匀居中描边）
                Outline = Brushes.Black,
                OutlineThickness = 2,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                RenderTransform = _marqueeTransform
            };

            _canvas = new Canvas
            {
                ClipToBounds = true,
                Background = HitTestBrush
            };
            _canvas.Children.Add(_text);

            // 背景框彻底去掉：Canvas 直接作为窗口内容，
            // 解锁态使用非零透明背景保证可命中点击，锁定态置 null 并启用穿透。
            Content = _canvas;

            _marquee = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _marquee.Tick += OnMarqueeTick;

            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
            LostMouseCapture += OnLostMouseCapture;
            MouseRightButtonUp += OnRightClick;
            MouseEnter += delegate { UpdateCanvasBackdrop(); };
            MouseLeave += delegate
            {
                UpdateCanvasBackdrop();
                if (!_resizingTop && !_resizingBottom && !_draggingWindow)
                    Cursor = Cursors.Arrow;
            };

            IsVisibleChanged += delegate
            {
                if (!IsVisible)
                {
                    _marquee.Stop();
                    _marqueeClock.Reset();
                }
                else if (_needsMarquee && _isPlaying && !_marquee.IsEnabled)
                {
                    _marqueeClock.Restart();
                    _marquee.Start();
                }
            };

            Loaded += delegate
            {
                ApplyPosition();
                ApplyFontSize();
                UpdateCanvasBackdrop();
            };

            SizeChanged += delegate { LayoutText(); };
        }

        private static SolidColorBrush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private void UpdateCanvasBackdrop()
        {
            if (_locked)
            {
                _canvas.Background = null;
            }
            else
            {
                _canvas.Background = (IsMouseOver || _draggingWindow || _resizingTop || _resizingBottom)
                    ? HoverBackdropBrush
                    : HitTestBrush;
            }
        }

        // ------------------------------------------------------------------
        // 配置
        // ------------------------------------------------------------------

        public void ApplyConfig(Config config)
        {
            if (config == null) return;

            _fontScale = config.DesktopLyricsFontScale <= 0 ? 1.0 : config.DesktopLyricsFontScale;
            if (config.DesktopLyricsHeight >= MinBarHeight) Height = config.DesktopLyricsHeight;

            ApplyPosition();
            ApplyFontSize();

            if (config.DesktopLyricsLocked != _locked) SetLocked(config.DesktopLyricsLocked);
            else UpdateCanvasBackdrop();
        }

        private void ApplyPosition()
        {
            // 宽度固定为屏幕可用宽度的 60%，水平居中，不可拖宽；
            // 将物理像素工作区按当前屏 DPI 缩放比换算为 DIP。
            var area = GetWorkAreaSafe();
            double scale = DpiScale();
            if (scale <= 0) scale = 1.0;

            double workLeft = area.left / scale;
            double workTop = area.top / scale;
            double screenWidth = (area.right - area.left) / scale;
            double screenHeight = (area.bottom - area.top) / scale;
            if (screenWidth < 100) screenWidth = 1920;
            if (screenHeight < 100) screenHeight = 1080;

            Width = Math.Max(320, screenWidth * WidthRatio);

            // 默认出现在屏幕上方（工作区顶部往下留一点边距）
            double defaultLeft = workLeft + (screenWidth - Width) / 2.0;
            double defaultTop = workTop + DefaultTopGap;

            bool hasSaved = !double.IsNaN(_desktopLeft) && !double.IsNaN(_desktopTop)
                            && !double.IsInfinity(_desktopLeft) && !double.IsInfinity(_desktopTop);
            if (hasSaved)
            {
                double vLeft = SystemParameters.VirtualScreenLeft;
                double vTop = SystemParameters.VirtualScreenTop;
                double vRight = vLeft + SystemParameters.VirtualScreenWidth;
                double vBottom = vTop + SystemParameters.VirtualScreenHeight;

                bool inVisibleArea = _desktopLeft + Width > vLeft + 40
                                     && _desktopLeft < vRight - 40
                                     && _desktopTop + Height > vTop + 20
                                     && _desktopTop < vBottom - 20;
                if (inVisibleArea)
                {
                    Left = _desktopLeft;
                    Top = _desktopTop;
                }
                else
                {
                    Left = defaultLeft;
                    Top = defaultTop;
                }
            }
            else
            {
                Left = defaultLeft;
                Top = defaultTop;
            }

            Log.Info(string.Format(
                "桌面歌词: 位置 {0:0},{1:0} 尺寸 {2:0}x{3:0} DPI={4:0.00} 工作区 {5},{6}-{7},{8}",
                Left, Top, Width, Height, scale, area.left, area.top, area.right, area.bottom));
        }

        /// <summary>取工作区；窗口句柄还没建好时用主显示器的工作区。</summary>
        private Native.Win32.RECT GetWorkAreaSafe()
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            return Native.Win32.GetWorkArea(handle);
        }

        public double SavedLeft { get { return Left; } }
        public double SavedTop { get { return Top; } }

        public void SetSavedPosition(double left, double top)
        {
            if (!double.IsNaN(left)) _desktopLeft = left;
            if (!double.IsNaN(top)) _desktopTop = top;
        }

        private void ApplyFontSize()
        {
            // 字号固定：只跟窗口大小 / 字号倍数有关，和句子长短无关
            double screenFactor = Math.Max(0.6, Height / 92.0);
            _text.FontSize = BaseFontSize * _fontScale * screenFactor;
            LayoutText();
        }

        // ------------------------------------------------------------------
        // 歌词
        // ------------------------------------------------------------------

        public void SetLyrics(LyricsDocument doc)
        {
            _doc = doc;
            _currentIndex = -1;
            _marqueeOffset = 0;
            _marqueeTransform.X = 0;
            _needsMarquee = false;

            if (doc == null || doc.IsEmpty)
            {
                // 没有歌词时就是一条空条，不显示任何占位文字
                _text.Text = string.Empty;
                LayoutText();
                return;
            }

            if (_isPlaying) UpdatePosition(0);
        }

        /// <summary>更新当前行（切句瞬间生效，不做垂直滚动动画）。</summary>
        public void UpdatePosition(double seconds)
        {
            if (_doc == null || _doc.IsEmpty) return;
            if (!_doc.IsSynced)
            {
                if (_currentIndex != 0)
                {
                    _currentIndex = 0;
                    ShowLine(_doc.Lines[0].Text);
                }
                return;
            }

            int index = _doc.IndexAt(seconds);
            if (index == _currentIndex) return;

            _currentIndex = index;
            if (index < 0)
            {
                // 前奏阶段清空文本，不强制显示第 0 句
                ShowLine(string.Empty);
                return;
            }

            ShowLine(_doc.Lines[index].Text);
        }

        /// <summary>设置播放状态：暂停时不显示歌词（清空文字），继续时恢复。</summary>
        public void SetPlaying(bool playing)
        {
            if (_isPlaying == playing) return;
            _isPlaying = playing;

            if (!playing)
            {
                // 暂停：清空显示（跑马灯也停）
                _text.Text = string.Empty;
                _marquee.Stop();
                _marqueeClock.Reset();
                LayoutText();
                return;
            }

            // 恢复播放：重新显示当前句
            if (_doc == null || _doc.IsEmpty) return;
            if (_currentIndex >= 0 && _currentIndex < _doc.Lines.Count)
            {
                ShowLine(_doc.Lines[_currentIndex].Text);
            }
            else
            {
                UpdatePosition(0);
            }
        }

        private void ShowLine(string text)
        {
            _text.Text = text ?? string.Empty;

            // 切句瞬间生效，重置跑马灯
            _marqueeOffset = 0;
            _marqueeTransform.X = 0;
            _marqueePauseUntil = DateTime.UtcNow.AddSeconds(1.2);
            LayoutText();
        }

        private void LayoutText()
        {
            double available = Math.Max(0, _canvas.ActualWidth - 24);
            _text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double textWidth = _text.DesiredSize.Width;
            double textHeight = _text.DesiredSize.Height;

            // 只有超长句才横向滚动
            _needsMarquee = textWidth > available && available > 0 && !string.IsNullOrEmpty(_text.Text);
            _marqueeTarget = _needsMarquee ? textWidth - available + 24 : 0;

            if (_needsMarquee)
            {
                Canvas.SetLeft(_text, 12);
                _marqueeTransform.X = -_marqueeOffset;
                if (IsVisible && _isPlaying && !_marquee.IsEnabled)
                {
                    _marqueeClock.Restart();
                    _marquee.Start();
                }
            }
            else
            {
                _marquee.Stop();
                _marqueeClock.Reset();
                _marqueeOffset = 0;
                _marqueeTransform.X = 0;
                // 不超长时水平居中
                Canvas.SetLeft(_text, Math.Max(12, (_canvas.ActualWidth - textWidth) / 2.0));
            }

            // 垂直居中
            Canvas.SetTop(_text, Math.Max(0, (_canvas.ActualHeight - textHeight) / 2.0));
        }

        private void OnMarqueeTick(object sender, EventArgs e)
        {
            if (!_needsMarquee || !IsVisible || !_isPlaying)
            {
                _marquee.Stop();
                _marqueeClock.Reset();
                return;
            }

            double dt = _marqueeClock.Elapsed.TotalSeconds;
            _marqueeClock.Restart();
            if (DateTime.UtcNow < _marqueePauseUntil) return;

            if (dt > 0.1) dt = 0.1;
            _marqueeOffset += MarqueeSpeedDipPerSec * dt;
            if (_marqueeOffset >= _marqueeTarget)
            {
                // 滚完回到句首
                _marqueeOffset = 0;
                _marqueePauseUntil = DateTime.UtcNow.AddSeconds(1.6);
            }
            _marqueeTransform.X = -_marqueeOffset;
        }

        // ------------------------------------------------------------------
        // 锁定 / 穿透
        // ------------------------------------------------------------------

        public bool IsLocked { get { return _locked; } }

        public void SetLocked(bool locked)
        {
            _locked = locked;
            if (locked)
            {
                _resizingTop = false;
                _resizingBottom = false;
                _draggingWindow = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                Cursor = Cursors.Arrow;
            }

            UpdateCanvasBackdrop();

            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                Win32.SetClickThrough(handle, locked);
                Win32.SetTopMost(handle);
                Win32.SetToolWindow(handle);
            }

            var handler = LockChanged;
            if (handler != null) handler(locked);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                Win32.SetTopMost(handle);
                Win32.SetToolWindow(handle);
                Win32.SetClickThrough(handle, _locked);
            }
            UpdateCanvasBackdrop();
            ApplyPosition();
            ApplyFontSize();
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // 保持置顶
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero && !_locked) Win32.SetTopMost(handle);
        }

        // ------------------------------------------------------------------
        // 鼠标：拖动移动 / 拖上下边缘改高度 / 双击锁定
        // ------------------------------------------------------------------

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_locked) return;

            if (e.ClickCount == 2)
            {
                SetLocked(true);
                e.Handled = true;
                return;
            }

            var pos = e.GetPosition(this);
            // 上下边缘 6px 内视为改高度
            _resizingTop = pos.Y < 6;
            _resizingBottom = !_resizingTop && pos.Y > ActualHeight - 6;

            if (_resizingTop || _resizingBottom)
            {
                _dragStartY = PointToScreen(pos).Y;
                _dragStartHeight = ActualHeight;
                _dragStartTop = Top;
                _dragStartBottom = Top + ActualHeight;
            }
            else
            {
                // 自己跟踪拖动，不用 DragMove()：
                // 词条带 WS_EX_NOACTIVATE，DragMove 依赖窗口激活，会静默失效，
                // 表现就是「拖不动」。
                _draggingWindow = true;
                _dragStartScreen = PointToScreen(pos);
                _dragStartLeft = Left;
                _dragStartTop = Top;
            }

            UpdateCanvasBackdrop();
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_locked) return;

            var pos = e.GetPosition(this);

            if ((_resizingTop || _resizingBottom) && e.LeftButton == MouseButtonState.Pressed)
            {
                double current = PointToScreen(pos).Y;
                double scale = DpiScale();
                if (scale <= 0) scale = 1.0;
                double deltaDip = (current - _dragStartY) / scale;

                if (_resizingTop)
                {
                    double newHeight = Math.Max(MinBarHeight, Math.Min(MaxBarHeight, _dragStartHeight - deltaDip));
                    Top = _dragStartBottom - newHeight;
                    Height = newHeight;
                }
                else
                {
                    double newHeight = Math.Max(MinBarHeight, Math.Min(MaxBarHeight, _dragStartHeight + deltaDip));
                    Height = newHeight;
                }

                ApplyFontSize();
                return;
            }

            if (_draggingWindow && e.LeftButton == MouseButtonState.Pressed)
            {
                // 手动跟随鼠标移动（换算成 DPI 无关单位）
                // 不做任何位置吸附：拖到哪就是哪
                var now = PointToScreen(pos);
                double scale = DpiScale();
                if (scale <= 0) scale = 1.0;
                Left = _dragStartLeft + (now.X - _dragStartScreen.X) / scale;
                Top = _dragStartTop + (now.Y - _dragStartScreen.Y) / scale;
                return;
            }

            Cursor = (pos.Y < 6 || pos.Y > ActualHeight - 6) ? Cursors.SizeNS : Cursors.SizeAll;
        }

        private double DpiScale()
        {
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                return source.CompositionTarget.TransformToDevice.M11;
            var dpi = VisualTreeHelper.GetDpi(this);
            if (dpi.DpiScaleX > 0) return dpi.DpiScaleX;
            return 1.0;
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            bool wasInteracting = _resizingTop || _resizingBottom || _draggingWindow;
            _resizingTop = false;
            _resizingBottom = false;
            _draggingWindow = false;

            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            UpdateCanvasBackdrop();

            if (wasInteracting)
            {
                _desktopLeft = Left;
                _desktopTop = Top;
                var handler = PositionChanged;
                if (handler != null) handler(Left, Top, Height);
            }
        }

        private void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            _resizingTop = false;
            _resizingBottom = false;
            _draggingWindow = false;
            UpdateCanvasBackdrop();
        }

        private void OnRightClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowMenu();
        }

        /// <summary>右键菜单任何时候都能用（会自动激活词条）。</summary>
        public void ShowMenu()
        {
            var menu = new ContextMenu();

            var fontMenu = new MenuItem { Header = "字体大小" };
            fontMenu.Items.Add(FontItem("稍大一点", 1.1));
            fontMenu.Items.Add(FontItem("稍小一点", 1 / 1.1));
            fontMenu.Items.Add(new Separator());
            fontMenu.Items.Add(FontItem("大很多", 1.35));
            fontMenu.Items.Add(FontItem("小很多", 1 / 1.35));
            fontMenu.Items.Add(new Separator());

            var custom = new MenuItem { Header = "输入百分比…" };
            custom.Click += delegate
            {
                // 传 null 使弹窗 CenterScreen，避免贴在屏幕顶部被截断
                var value = Dialogs.Prompt(null, "字体大小", "输入百分比（50 ~ 300）：",
                    Math.Round(_fontScale * 100).ToString("0"));
                if (string.IsNullOrWhiteSpace(value)) return;

                double percent;
                if (!double.TryParse(value.Trim().TrimEnd('%'), out percent)) return;
                percent = Math.Max(50, Math.Min(300, percent));
                SetFontScale(percent / 100.0);
            };
            fontMenu.Items.Add(custom);
            menu.Items.Add(fontMenu);

            menu.Items.Add(new Separator());

            var lockItem = new MenuItem { Header = _locked ? "解锁" : "锁定" };
            lockItem.Click += delegate { SetLocked(!_locked); };
            menu.Items.Add(lockItem);

            menu.Items.Add(new Separator());

            var hide = new MenuItem { Header = "隐藏桌面歌词" };
            hide.Click += delegate
            {
                var handler = HiddenRequested;
                if (handler != null) handler();
                else Hide();
            };
            menu.Items.Add(hide);

            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        private MenuItem FontItem(string header, double factor)
        {
            var item = new MenuItem { Header = header };
            item.Click += delegate { SetFontScale(_fontScale * factor); };
            return item;
        }

        private void SetFontScale(double scale)
        {
            _fontScale = Math.Max(0.5, Math.Min(3.0, scale));
            ApplyFontSize();

            var handler = FontScaleChanged;
            if (handler != null) handler(_fontScale);
        }

        /// <summary>主窗口定时调用：位置已由 TickPlayback 推入，这里不再自己轮询。</summary>
        public void SetTickerEnabled(bool enabled)
        {
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _marquee.Stop();
            _marqueeClock.Reset();
        }
    }
}
