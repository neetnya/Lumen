using System;
using System.Windows;
using System.Windows.Forms;

namespace Lumen.UI
{
    /// <summary>
    /// 托盘常驻（对应 README 的「托盘」）：
    /// 点关闭最小化到托盘继续播放；托盘右键可显示窗口 / 播放暂停 / 上下首 /
    /// 播放模式 / 显示桌面歌词 / 解锁与锁定桌面歌词 / 退出；单击恢复窗口。
    /// 媒体键的启用状态写在托盘提示文字里（菜单里只保留操作项）。
    /// </summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly MainWindow _window;
        private readonly TaskbarCreatedWindow _taskbarWatcher;

        private readonly ToolStripMenuItem _itemPlayPause;
        private readonly ToolStripMenuItem _itemPrev;
        private readonly ToolStripMenuItem _itemNext;
        private readonly ToolStripMenuItem _itemMode;
        private readonly ToolStripMenuItem _itemDesktopLyrics;
        private readonly ToolStripMenuItem _itemLock;
        private readonly ToolStripMenuItem _itemShow;

        private System.Drawing.Icon _iconNormal;
        private System.Drawing.Icon _iconPaused;

        private bool _disposed;
        private bool? _lastIconPlaying;
        private string _lastTooltipText;

        /// <summary>媒体键状态（写进托盘提示文字）。</summary>
        public bool MediaKeysAvailable { get; set; }
        public string MediaKeysDetail { get; set; }

        public TrayIcon(MainWindow window)
        {
            _window = window;

            int iconSize = Math.Max(16, SystemInformation.SmallIconSize.Width);
            _iconNormal = TrayIconFactory.CreateWithState(iconSize, true);
            _iconPaused = TrayIconFactory.CreateWithState(iconSize, false);

            _menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                ShowCheckMargin = true,
                Renderer = new DarkMenuRenderer()
            };

            _itemShow = new ToolStripMenuItem("显示窗口");
            _itemShow.Click += delegate { ShowWindow(); };
            _menu.Items.Add(_itemShow);

            _menu.Items.Add(new ToolStripSeparator());

            _itemPlayPause = new ToolStripMenuItem("播放 / 暂停");
            _itemPlayPause.Click += delegate { _window.PlayPauseFromTray(); UpdateState(); };
            _menu.Items.Add(_itemPlayPause);

            _itemPrev = new ToolStripMenuItem("上一首");
            _itemPrev.Click += delegate { _window.PrevFromTray(); UpdateState(); };
            _menu.Items.Add(_itemPrev);

            _itemNext = new ToolStripMenuItem("下一首");
            _itemNext.Click += delegate { _window.NextFromTray(); UpdateState(); };
            _menu.Items.Add(_itemNext);

            _itemMode = new ToolStripMenuItem("播放模式");
            _itemMode.Click += delegate { _window.CycleModeFromTray(); UpdateState(); };
            _menu.Items.Add(_itemMode);

            _menu.Items.Add(new ToolStripSeparator());

            _itemDesktopLyrics = new ToolStripMenuItem("显示桌面歌词");
            _itemDesktopLyrics.Click += delegate
            {
                _window.ToggleDesktopLyrics();
                UpdateState();
            };
            _menu.Items.Add(_itemDesktopLyrics);

            // 桌面歌词锁定后鼠标完全穿透，词条收不到事件，
            // 所以解锁入口必须放在托盘里（README）
            _itemLock = new ToolStripMenuItem("解锁桌面歌词");
            _itemLock.Click += delegate
            {
                _window.EnsureDesktopLyricsForTray();
                var lyrics = _window.DesktopLyrics;
                if (lyrics != null)
                {
                    lyrics.SetLocked(!lyrics.IsLocked);
                    _window.SaveConfigNow();
                }
                UpdateState();
            };
            _menu.Items.Add(_itemLock);

            _menu.Items.Add(new ToolStripSeparator());

            var itemExit = new ToolStripMenuItem("退出");
            itemExit.Click += delegate { _window.ExitApplication(); };
            _menu.Items.Add(itemExit);

            _icon = new NotifyIcon
            {
                Icon = _iconNormal,
                ContextMenuStrip = _menu,
                Visible = true,
                Text = "Lumen 音乐"
            };
            // 统一左键单击唤起窗口，避免同时绑定 DoubleClick 导致重复触发
            _icon.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowWindow();
            };

            _menu.Opening += delegate { UpdateState(); };

            try
            {
                _taskbarWatcher = new TaskbarCreatedWindow(RestoreTrayIcon);
            }
            catch (Exception ex)
            {
                Log.Warn("创建任务栏重启监听窗口失败: " + ex.Message);
            }

            UpdateState();
        }

        private void RestoreTrayIcon()
        {
            if (_disposed) return;
            try
            {
                _icon.Visible = false;
                _icon.Visible = true;
                _lastIconPlaying = null;
                _lastTooltipText = null;
                UpdateState();
            }
            catch (Exception ex)
            {
                Log.Warn("恢复托盘图标失败: " + ex.Message);
            }
        }

        private void ShowWindow()
        {
            if (_disposed) return;
            try
            {
                SingleInstance.BringToFront(_window);
            }
            catch (Exception ex)
            {
                Log.Warn("显示窗口失败: " + ex.Message);
            }
        }

        /// <summary>同步菜单勾选状态与托盘提示文字。</summary>
        public void UpdateState()
        {
            if (_disposed) return;

            try
            {
                var config = _window.Configuration;
                var player = _window.AudioPlayer;

                bool playing = player != null && player.IsPlaying;
                _itemPlayPause.Text = playing ? "暂停" : "播放";
                if (_lastIconPlaying != playing)
                {
                    _lastIconPlaying = playing;
                    _icon.Icon = playing ? _iconNormal : _iconPaused;
                }

                string modeName = "顺序播放";
                if (player != null)
                {
                    if (player.Mode == Audio.Player.PlayMode.RepeatList) modeName = "列表循环";
                    else if (player.Mode == Audio.Player.PlayMode.RepeatOne) modeName = "单曲循环";
                }
                _itemMode.Text = "播放模式：" + modeName;

                _itemDesktopLyrics.Checked = config.DesktopLyricsVisible;

                var lyrics = _window.DesktopLyrics;
                bool locked = lyrics != null && lyrics.IsLocked;

                _itemLock.Text = locked
                    ? "解锁桌面歌词"
                    : "锁定桌面歌词";
                _itemLock.Checked = locked;
                _itemLock.Enabled = config.DesktopLyricsVisible;

                // 媒体键状态写在托盘提示文字里（菜单里只保留操作项）
                string keys = MediaKeysAvailable
                    ? "媒体键：已启用"
                    : "媒体键：不可用" + (string.IsNullOrEmpty(MediaKeysDetail) ? "" : "（" + MediaKeysDetail + "）");

                string title = "Lumen 音乐";
                if (player != null && player.HasTrack)
                {
                    var name = System.IO.Path.GetFileNameWithoutExtension(player.CurrentPath);
                    title = name + (playing ? " - 播放中" : " - 已暂停");
                }

                // .NET 9 NotifyIcon.Text 上限 127 字符：优先保留第二行媒体键状态，必要时截断第一行曲名
                const int maxTooltipLength = 127;
                string secondLine = keys.Length > maxTooltipLength
                    ? keys.Substring(0, maxTooltipLength)
                    : keys;

                string text;
                int maxTitleLength = maxTooltipLength - 1 - secondLine.Length;
                if (maxTitleLength <= 0)
                {
                    text = secondLine;
                }
                else
                {
                    string firstLine = title;
                    if (firstLine.Length > maxTitleLength)
                    {
                        firstLine = maxTitleLength > 1
                            ? firstLine.Substring(0, maxTitleLength - 1) + "…"
                            : firstLine.Substring(0, maxTitleLength);
                    }
                    text = firstLine + "\n" + secondLine;
                }

                if (!string.Equals(_lastTooltipText, text, StringComparison.Ordinal))
                {
                    _lastTooltipText = text;
                    _icon.Text = text;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("刷新托盘状态失败: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_taskbarWatcher != null)
            {
                try { _taskbarWatcher.Dispose(); } catch { }
            }

            try
            {
                _icon.Visible = false;
                _icon.ContextMenuStrip = null;
                _icon.Dispose();
            }
            catch { }

            try { _menu.Dispose(); } catch { }

            if (_iconNormal != null) { try { _iconNormal.Dispose(); } catch { } _iconNormal = null; }
            if (_iconPaused != null) { try { _iconPaused.Dispose(); } catch { } _iconPaused = null; }
        }

        /// <summary>轻量原生窗口：监听 TaskbarCreated，在 Explorer 重启后自动恢复托盘图标。</summary>
        private sealed class TaskbarCreatedWindow : NativeWindow, IDisposable
        {
            private readonly uint _taskbarCreatedMsg;
            private readonly Action _onTaskbarCreated;

            public TaskbarCreatedWindow(Action onTaskbarCreated)
            {
                _onTaskbarCreated = onTaskbarCreated;
                _taskbarCreatedMsg = Lumen.Native.Win32.RegisterWindowMessage("TaskbarCreated");
                CreateHandle(new CreateParams
                {
                    Caption = "LumenTrayWatcher",
                    Style = 0
                });
            }

            protected override void WndProc(ref Message m)
            {
                if (_taskbarCreatedMsg != 0 && (uint)m.Msg == _taskbarCreatedMsg)
                {
                    var handler = _onTaskbarCreated;
                    if (handler != null) handler();
                }
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    try { DestroyHandle(); } catch { }
                }
            }
        }
    }

    /// <summary>深色托盘菜单渲染（与软件整体风格一致）。</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled
                ? System.Drawing.Color.FromArgb(230, 232, 234)
                : System.Drawing.Color.FromArgb(110, 113, 118);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(38, 40, 44)))
            {
                e.Graphics.FillRectangle(brush, e.AffectedBounds);
            }
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            var prevMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            try
            {
                var r = e.ImageRectangle;
                float cx = r.X + r.Width / 2f;
                float cy = r.Y + r.Height / 2f;
                var color = e.Item.Enabled
                    ? System.Drawing.Color.FromArgb(79, 163, 255)
                    : System.Drawing.Color.FromArgb(110, 113, 118);

                using (var pen = new System.Drawing.Pen(color, 1.8f))
                {
                    pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    g.DrawLines(pen, new[]
                    {
                        new System.Drawing.PointF(cx - 4.0f, cy),
                        new System.Drawing.PointF(cx - 1.2f, cy + 3.0f),
                        new System.Drawing.PointF(cx + 4.2f, cy - 3.0f)
                    });
                }
            }
            finally
            {
                g.SmoothingMode = prevMode;
            }
        }
    }

    internal sealed class DarkColorTable : ProfessionalColorTable
    {
        public override System.Drawing.Color MenuItemSelected
        {
            get { return System.Drawing.Color.FromArgb(58, 62, 67); }
        }

        public override System.Drawing.Color MenuItemBorder
        {
            get { return System.Drawing.Color.FromArgb(58, 62, 67); }
        }

        public override System.Drawing.Color MenuBorder
        {
            get { return System.Drawing.Color.FromArgb(52, 55, 60); }
        }

        public override System.Drawing.Color ToolStripDropDownBackground
        {
            get { return System.Drawing.Color.FromArgb(38, 40, 44); }
        }

        public override System.Drawing.Color ImageMarginGradientBegin
        {
            get { return System.Drawing.Color.FromArgb(38, 40, 44); }
        }

        public override System.Drawing.Color ImageMarginGradientMiddle
        {
            get { return System.Drawing.Color.FromArgb(38, 40, 44); }
        }

        public override System.Drawing.Color ImageMarginGradientEnd
        {
            get { return System.Drawing.Color.FromArgb(38, 40, 44); }
        }

        public override System.Drawing.Color CheckBackground
        {
            get { return System.Drawing.Color.FromArgb(38, 40, 44); }
        }

        public override System.Drawing.Color CheckSelectedBackground
        {
            get { return System.Drawing.Color.FromArgb(58, 62, 67); }
        }

        public override System.Drawing.Color SeparatorDark
        {
            get { return System.Drawing.Color.FromArgb(52, 55, 60); }
        }

        public override System.Drawing.Color SeparatorLight
        {
            get { return System.Drawing.Color.FromArgb(52, 55, 60); }
        }
    }
}
