using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Lumen.Audio;
using Lumen.Media;
using Lumen.Models;

namespace Lumen.UI
{
    /// <summary>
    /// 主窗口：自绘标题栏 + 三区布局（左歌词 / 右列表固定 50/50 + 底部播放进度区）。
    /// </summary>
    public partial class MainWindow : WindowChromeBase
    {
        private sealed class PlayRequest
        {
            public readonly int Generation;
            public readonly string Path;
            public readonly double StartAt;

            public PlayRequest(int generation, string path, double startAt)
            {
                Generation = generation;
                Path = path;
                StartAt = startAt;
            }
        }

        private readonly CommandLine _options;
        private readonly SingleInstance _single;
        private readonly Player _player = new Player();
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private readonly object _playQueueGate = new object();

        private Config _config;
        private MetadataFiller _filler;
        private SplashCard _splash;

        private LyricsPane _lyricsPane;
        private TrackListPane _listPane;
        private BottomBar _bottomBar;
        private Grid _splitBody;
        private DesktopLyricsWindow _desktopLyrics;
        private TrayIcon _tray;
        private MediaKeys _mediaKeys;
        private DispatcherTimer _playbackTimer;

        private Group _group;
        private List<Track> _view = new List<Track>();
        private int _currentIndex = -1;

        private volatile bool _isClosed;
        private int _playGeneration;
        private PlayRequest _pendingPlayRequest;
        private bool _playWorkerRunning;
        private int _consecutiveFailures;

        private Lyrics.LyricsDocument _currentLyrics;
        private string _currentLyricsPath;

        private double _lastKnownPosition;
        private bool _playbackEnded;
        private DateTime _lastProgressSave = DateTime.UtcNow;
        private string _lastSavedTrackPath;
        private double _lastSavedPositionSeconds = -1;

        private bool _lyricsPaneUserCollapsed;
        private bool _reallyExit;

        public MainWindow(CommandLine options, SingleInstance single)
        {
            _options = options;
            _single = single;

            Title = "Lumen 音乐";
            Width = 820;
            Height = 720;
            MinWidth = 600;
            MinHeight = 420;
            // 首次打开（没有保存过位置时）出现在屏幕正中央
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // 提前订阅关闭事件，防止初始化中途异常导致资源泄漏
            Closing += OnClosing;
            Closed += OnClosed;

            _config = Config.Load();
            _group = _config.EnsureGroup();
        }

        /// <summary>窗口显示后初始化（启动卡片此时可以收起）。</summary>
        public void InitializeAfterShow(SplashCard splash)
        {
            _splash = splash;
            try
            {
                RestoreWindowPlacement();
                TitleBar = BuildTitleBar();
                Body = BuildBody();

                _filler = new MetadataFiller(Dispatcher);
                _filler.BatchCompleted += OnMetadataBatchCompleted;
                _filler.AllCompleted += OnMetadataAllCompleted;

                HookPlayer();
                HookSingleInstance();
                HookHotkeys();
                HookMediaKeys();
                PreviewKeyDown += OnPreviewKeyDown;

                ApplyGroup(_group, false);
                QueueMetadata();

                // 恢复上次播放位置：每个分组各自记忆（不会自动播放）
                RestoreGroupProgress(true);

                // 处理命令行带来的文件
                if (_options.Files.Count > 0) OpenExternalFiles(_options.Files);

                UpdateModeButton();
                UpdateVolumeUi();

                // 桌面歌词：位置、字号、行数、锁定状态都会记住
                EnsureDesktopLyrics();
                SyncDesktopLyrics();

                // 托盘常驻
                _tray = new TrayIcon(this);
                _tray.MediaKeysAvailable = _mediaKeys != null && _mediaKeys.IsAvailable;
                _tray.MediaKeysDetail = _mediaKeys != null ? _mediaKeys.FailureReason : "未注册";
                _tray.UpdateState();

                StartPlaybackTimer();

                // 没有待补全的元数据时，直接收起启动卡片
                if (_filler.PendingCount == 0 && _splash != null)
                {
                    _splash.Dismiss();
                    _splash = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("初始化主窗口失败", ex);
                if (_splash != null)
                {
                    _splash.Dismiss();
                    _splash = null;
                }
                Dialogs.ShowError(this, "启动失败", ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // 界面搭建
        // ------------------------------------------------------------------

        private FrameworkElement BuildTitleBar()
        {
            var bar = new Grid { Height = TitleBarHeight, Background = Theme.Brush("Brush.Window") };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock
            {
                Text = "Lumen 音乐",
                FontSize = 12.5,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush("Brush.TextDim")
            };
            Grid.SetColumn(title, 0);
            bar.Children.Add(title);

            // 空白区域可拖动窗口
            var dragArea = new Border { Background = Brushes.Transparent };
            dragArea.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                DragMoveFromTitleBar(e);
            };
            Grid.SetColumn(dragArea, 1);
            bar.Children.Add(dragArea);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 2, 0) };

            var btnLyrics = CaptionButton(Icons.Lyric, "显示/隐藏歌词区");
            btnLyrics.Click += delegate { ToggleLyricsPane(); };
            buttons.Children.Add(btnLyrics);

            var btnMin = CaptionButton(Icons.Minimize, "最小化");
            btnMin.Click += delegate { WindowState = WindowState.Minimized; };
            buttons.Children.Add(btnMin);

            var btnMax = CaptionButton(Icons.Maximize, "最大化");
            btnMax.Click += delegate { ToggleMaximize(); };
            buttons.Children.Add(btnMax);

            var btnClose = CaptionButton(Icons.Close, "关闭（最小化到托盘）");
            btnClose.Style = (Style)FindResource("Btn.CaptionClose");
            btnClose.Click += delegate { Close(); };
            buttons.Children.Add(btnClose);

            Grid.SetColumn(buttons, 2);
            bar.Children.Add(buttons);
            return bar;
        }

        private Button CaptionButton(string icon, string tip)
        {
            var button = new Button
            {
                Style = (Style)FindResource("Btn.Caption"),
                Content = Icons.Create(icon, 12, null, Theme.Brush("Brush.TextDim"), 1.5),
                ToolTip = tip
            };
            return button;
        }

        private FrameworkElement BuildBody()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });

            // 主体：左歌词 / 右列表，固定 50/50
            _splitBody = new Grid();
            _splitBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _splitBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _lyricsPane = new LyricsPane();
            _lyricsPane.LineClicked += delegate (double seconds) { SeekTo(seconds); };
            Grid.SetColumn(_lyricsPane, 0);
            _splitBody.Children.Add(_lyricsPane);

            _listPane = new TrackListPane();
            _listPane.TrackActivated += delegate (Track t) { PlayTrack(t); };
            _listPane.PlayRequested += delegate (Track t) { PlayTrack(t); };
            _listPane.RemoveRequested += delegate (IList<Track> tracks) { RemoveTracks(tracks); };
            _listPane.RevealRequested += delegate (Track t) { RevealInExplorer(t); };
            _listPane.DeleteFileRequested += delegate (IList<Track> tracks) { DeleteTrackFiles(tracks); };
            _listPane.CleanInvalidRequested += delegate { CleanInvalidFiles(); };
            _listPane.SortChanged += delegate (string mode)
            {
                _config.SortMode = mode;
                // 打乱是一次性行为：一旦用户主动选择排序，就按排序重排，
                // 打乱结果不再强制覆盖排序，仅作为「记忆」保留在配置里。
                if (_group != null) _group.ShuffledOrder = null;
                RebuildView();
                SaveConfig();
            };
            _listPane.ShuffleRequested += delegate { ShuffleGroup(); };
            _listPane.ImportFilesRequested += delegate { ImportFiles(); };
            _listPane.ImportFolderRequested += delegate { ImportFolder(); };
            _listPane.NewGroupRequested += delegate { NewGroup(); };
            _listPane.RenameGroupRequested += delegate { RenameGroup(); };
            _listPane.DeleteGroupRequested += delegate { DeleteGroup(); };
            _listPane.GroupSelected += delegate (string id) { SwitchGroup(id); };
            _listPane.FilesDropped += delegate (IList<string> files) { ImportPaths(files, true); };
            Grid.SetColumn(_listPane, 1);
            _splitBody.Children.Add(_listPane);

            Grid.SetRow(_splitBody, 0);
            root.Children.Add(_splitBody);

            _bottomBar = new BottomBar();
            _bottomBar.PlayPauseClicked += delegate { TogglePlayPause(); };
            _bottomBar.PrevClicked += delegate { PlayPrevious(); };
            _bottomBar.NextClicked += delegate { PlayNext(true); };
            _bottomBar.ModeClicked += delegate { CycleMode(); };
            _bottomBar.SeekRequested += delegate (double seconds) { SeekTo(seconds); };
            _bottomBar.VolumeChanged += delegate (float v)
            {
                _config.VolumePercent = (int)Math.Round(v * 100);
                _player.Volume = v;
                _config.Muted = false;
                _player.Muted = false;
                UpdateVolumeUi();
            };
            _bottomBar.VolumeDelta += delegate (float delta) { NudgeVolume(delta); };
            _bottomBar.MuteToggled += delegate
            {
                _config.Muted = !_config.Muted;
                _player.Muted = _config.Muted;
                UpdateVolumeUi();
            };
            Grid.SetRow(_bottomBar, 1);
            root.Children.Add(_bottomBar);

            ApplyPaneVisibility();
            return root;
        }

        // ------------------------------------------------------------------
        // 元数据回调
        // ------------------------------------------------------------------

        private void OnMetadataBatchCompleted(IList<Track> updated)
        {
            if (_isClosed || Dispatcher.HasShutdownStarted) return;
            if (_listPane != null)
            {
                _listPane.RefreshRows(updated);
                _listPane.SetPlaying(_currentIndexPath());
            }
        }

        private void OnMetadataAllCompleted()
        {
            if (_isClosed || Dispatcher.HasShutdownStarted) return;
            bool sortByDuration = _config != null &&
                (_config.SortMode == TrackSorter.Duration || _config.SortMode == TrackSorter.DurationDesc);
            RefreshList(sortByDuration);
            if (_splash != null)
            {
                _splash.Dismiss();
                _splash = null;
            }
        }

        // ------------------------------------------------------------------
        // 分组 / 列表
        // ------------------------------------------------------------------

        private void ApplyGroup(Group group, bool restorePosition)
        {
            _group = group ?? _config.EnsureGroup();
            _config.CurrentGroupId = _group.Id;
            _lastKnownPosition = 0;
            _playbackEnded = false;
            _consecutiveFailures = 0;

            _listPane.ClearSearch();
            _listPane.SetGroups(_config.Groups, _group.Id);
            RebuildView();

            if (restorePosition)
            {
                RestoreGroupProgress(false);
            }
        }

        private void SwitchGroup(string id)
        {
            if (id == null || (_group != null && id == _group.Id)) return;
            SaveGroupProgress();

            var group = _config.FindGroup(id);
            if (group == null) return;

            _config.CurrentGroupId = id;
            ApplyGroup(group, true);
            SaveConfig();
        }

        /// <summary>把分组内容按当前排序/打乱重建为可见列表。</summary>
        private void RebuildView()
        {
            _view = TrackSorter.Sort(_group, _config.SortMode);
            _listPane.SetTracks(_view, _config.SortMode);
            _currentIndex = _currentIndexPath();
            // 重建列表后按新位置刷新播放标记，否则排序/打乱后标记会停在旧 index。
            _listPane.SetPlaying(_currentIndex);
        }

        private int _currentIndexPath()
        {
            var path = _player.CurrentPath;
            if (string.IsNullOrEmpty(path)) return -1;
            return IndexOfPath(path);
        }

        private int IndexOfPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            for (int i = 0; i < _view.Count; i++)
            {
                if (string.Equals(_view[i].Path, path, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        private static bool GroupContainsPath(Group group, string path)
        {
            if (group == null || group.Tracks == null || string.IsNullOrEmpty(path)) return false;
            for (int i = 0; i < group.Tracks.Count; i++)
            {
                var t = group.Tracks[i];
                if (t != null && string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void RefreshList(bool rebuildView)
        {
            if (rebuildView) RebuildView();
            _listPane.RefreshRows();
            _listPane.SetPlaying(_currentIndexPath());
        }

        /// <summary>停止播放并同步清空歌词区、桌面歌词、底栏及（可选）当前分组记忆进度。</summary>
        private void StopPlaybackAndClearUi(bool clearGroupProgress = true)
        {
            Interlocked.Increment(ref _playGeneration);
            lock (_playQueueGate)
            {
                _pendingPlayRequest = null;
            }

            _player.Stop();
            _currentIndex = -1;
            _lastKnownPosition = 0;
            _playbackEnded = false;
            _currentLyrics = null;
            _currentLyricsPath = null;

            if (clearGroupProgress && _group != null)
            {
                _group.LastTrackPath = null;
                _group.LastPositionSeconds = 0;
            }

            if (_listPane != null) _listPane.SetPlaying(-1);
            if (_lyricsPane != null) _lyricsPane.SetLyrics(null);
            if (_desktopLyrics != null)
            {
                _desktopLyrics.SetLyrics(null);
                _desktopLyrics.SetPlaying(false);
            }

            UpdateBottomBar();
            SyncPlaybackTimerState();
        }

        /// <summary>批量移除（仅从列表移除，不删文件）。</summary>
        private void RemoveTracks(IList<Track> tracks)
        {
            if (tracks == null || tracks.Count == 0 || _group == null) return;

            bool stoppedCurrent = false;
            foreach (var t in tracks)
            {
                if (t == null) continue;
                _group.Tracks.Remove(t);
                if (_group.ShuffledOrder != null) _group.ShuffledOrder.Remove(t.Path);
                if (string.Equals(_group.LastTrackPath, t.Path, StringComparison.OrdinalIgnoreCase))
                {
                    _group.LastTrackPath = null;
                    _group.LastPositionSeconds = 0;
                }
                if (string.Equals(_player.CurrentPath, t.Path, StringComparison.OrdinalIgnoreCase))
                {
                    stoppedCurrent = true;
                }
            }

            if (stoppedCurrent)
            {
                StopPlaybackAndClearUi(true);
            }

            RebuildView();
            SaveConfig();
            _listPane.FlashStatus("已从列表移除 " + tracks.Count + " 首");
        }

        /// <summary>检测并删除当前分组里已经失效（文件不存在）的曲目。</summary>
        private void CleanInvalidFiles()
        {
            if (_group == null) return;

            var invalid = new List<Track>();
            foreach (var t in _group.Tracks)
            {
                if (string.IsNullOrEmpty(t.Path) || !System.IO.File.Exists(t.Path))
                    invalid.Add(t);
            }

            if (invalid.Count == 0)
            {
                _listPane.FlashStatus("没有失效的文件");
                return;
            }

            if (!Dialogs.Confirm(this, "清理失效文件",
                    "当前分组有 " + invalid.Count + " 首文件已不存在，是否从列表移除？\n（不会删除任何磁盘文件）",
                    "清理"))
                return;

            RemoveTracks(invalid);
            _listPane.FlashStatus("已清理 " + invalid.Count + " 首失效文件");
        }

        /// <summary>右键菜单「删除此文件」：单首送回收站不确认；多选会先确认。</summary>
        private void DeleteTrackFiles(IList<Track> tracks)
        {
            if (tracks == null || tracks.Count == 0 || _group == null) return;

            if (tracks.Count > 1)
            {
                if (!Dialogs.Confirm(this, "删除文件",
                        "将这 " + tracks.Count + " 首送进回收站？\n（可从回收站还原）",
                        "删除"))
                    return;
            }

            int ok = 0;
            int removedMissing = 0;
            foreach (var track in tracks)
            {
                if (track == null || string.IsNullOrEmpty(track.Path)) continue;

                bool isPlayingThis = string.Equals(_player.CurrentPath, track.Path, StringComparison.OrdinalIgnoreCase);

                if (!System.IO.File.Exists(track.Path))
                {
                    if (isPlayingThis) StopPlaybackAndClearUi(true);
                    if (string.Equals(_group.LastTrackPath, track.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        _group.LastTrackPath = null;
                        _group.LastPositionSeconds = 0;
                    }
                    _group.Tracks.Remove(track);
                    if (_group.ShuffledOrder != null) _group.ShuffledOrder.Remove(track.Path);
                    removedMissing++;
                    continue;
                }

                var name = System.IO.Path.GetFileName(track.Path);

                if (isPlayingThis)
                {
                    StopPlaybackAndClearUi(true);
                }

                int result = Native.Win32.DeleteToRecycleBin(track.Path);
                if (result != 0)
                {
                    Log.Warn("送回收站失败: " + name + " (code " + result + ")");
                    Dialogs.ShowError(this, "删除失败", "无法将文件送进回收站：" + name);
                    continue;
                }

                if (string.Equals(_group.LastTrackPath, track.Path, StringComparison.OrdinalIgnoreCase))
                {
                    _group.LastTrackPath = null;
                    _group.LastPositionSeconds = 0;
                }
                _group.Tracks.Remove(track);
                if (_group.ShuffledOrder != null) _group.ShuffledOrder.Remove(track.Path);
                ok++;
            }

            RebuildView();
            SaveConfig();
            if (ok > 0 && removedMissing > 0)
                _listPane.FlashStatus("已送进回收站 " + ok + " 首，移除失效 " + removedMissing + " 首");
            else if (ok > 0)
                _listPane.FlashStatus("已送进回收站 " + ok + " 首");
            else if (removedMissing > 0)
                _listPane.FlashStatus("已移除 " + removedMissing + " 首失效项");
        }

        private static void RevealInExplorer(Track track)
        {
            if (track == null || string.IsNullOrEmpty(track.Path)) return;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + track.Path + "\"");
            }
            catch (Exception ex)
            {
                Log.Warn("打开资源管理器失败: " + ex.Message);
            }
        }

        /// <summary>「打乱顺序」是一次性操作，结果写进配置，下次打开还是这个顺序。</summary>
        private void ShuffleGroup()
        {
            if (_group == null || _group.Tracks.Count == 0) return;

            var paths = new List<string>();
            foreach (var t in _group.Tracks) paths.Add(t.Path);

            var random = new Random();
            for (int i = paths.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                var tmp = paths[i]; paths[i] = paths[j]; paths[j] = tmp;
            }

            _group.ShuffledOrder = paths;
            RebuildView();
            SaveConfig();
            _listPane.FlashStatus("已打乱顺序（顺序会被记住）");
        }

        // ------------------------------------------------------------------
        // 导入
        // ------------------------------------------------------------------

        private void ImportFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Filter = "音频文件|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.wma;*.ape;*.aiff;*.aif;*.mp4|所有文件|*.*",
                Title = "导入文件"
            };
            if (dialog.ShowDialog(this) == true) ImportPaths(dialog.FileNames, false);
        }

        private void ImportFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "导入文件夹" };
            if (dialog.ShowDialog(this) != true) return;

            var folder = dialog.FolderName;
            var name = System.IO.Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = folder.TrimEnd('\\', '/');

            // 只要当前有分组，就问「并入还是新建」。
            // （之前用 Tracks.Count > 0 判断，导致在导入出来的空分组里点导入时
            //   根本不出选择框，看起来像「没有任何反应」。）
            bool createNew = true;
            if (_group != null)
            {
                var choice = Dialogs.Choose(this,
                    "导入文件夹",
                    "把「" + name + "」作为新分组，还是并入当前分组「" + _group.Name + "」？",
                    "新建分组",
                    "并入当前分组");
                if (choice == null) return;
                createNew = choice == true;
            }

            var targetGroup = _group;
            var folderPath = folder;
            var groupName = name;
            var makeNew = createNew;
            var token = _lifetimeCts.Token;

            // 扫描放后台，避免大文件夹把界面冻住
            _listPane.FlashStatus("正在扫描…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (token.IsCancellationRequested || _isClosed) return;

                List<Track> tracks;
                try
                {
                    int counter = 0;
                    tracks = MediaScanner.FromFiles(new[] { folderPath }, ref counter);
                }
                catch (Exception ex)
                {
                    Log.Error("扫描文件夹失败", ex);
                    tracks = new List<Track>();
                }

                if (token.IsCancellationRequested || _isClosed) return;

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_isClosed || Dispatcher.HasShutdownStarted) return;

                    if (tracks.Count == 0)
                    {
                        Dialogs.ShowInfo(this, "没有找到音频", "这个文件夹里没有找到支持的音频文件。");
                        _listPane.FlashStatus("");
                        return;
                    }

                    // 回到 UI 线程实时分配递增的 AddedOrder，避免并发导入顺序冲突
                    int nextOrder = NextAddedOrder();
                    for (int i = 0; i < tracks.Count; i++) tracks[i].AddedOrder = nextOrder++;

                    if (makeNew)
                    {
                        var group = new Group
                        {
                            Name = string.IsNullOrEmpty(groupName) ? "新分组" : groupName,
                            Tracks = tracks
                        };
                        _config.Groups.Add(group);
                        _config.CurrentGroupId = group.Id;
                        ApplyGroup(group, false);
                    }
                    else
                    {
                        if (targetGroup == null || !_config.Groups.Contains(targetGroup))
                        {
                            _listPane.FlashStatus("目标分组已不存在，导入已取消");
                            return;
                        }
                        AddTracksToGroup(targetGroup, tracks);
                    }

                    _filler.Enqueue(tracks);
                    SaveConfig();
                    _listPane.FlashStatus("已导入 " + tracks.Count + " 首");
                }), DispatcherPriority.Background);
            });
        }

        private void ImportPaths(IList<string> paths, bool fromDrop)
        {
            if (paths == null || paths.Count == 0) return;

            // 扫描放到后台线程：600 首要走几千次目录枚举 + 文件属性读取，
            // 放在 UI 线程会直接把界面冻住好几秒。
            var snapshot = new List<string>(paths);
            var targetGroup = _group;
            var token = _lifetimeCts.Token;

            _listPane.FlashStatus("正在扫描…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (token.IsCancellationRequested || _isClosed) return;

                List<Track> tracks;
                try
                {
                    int order = 0;
                    tracks = MediaScanner.FromFiles(snapshot, ref order);
                }
                catch (Exception ex)
                {
                    Log.Error("扫描失败", ex);
                    tracks = new List<Track>();
                }

                if (token.IsCancellationRequested || _isClosed) return;

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_isClosed || Dispatcher.HasShutdownStarted) return;

                    if (tracks.Count == 0)
                    {
                        string tip = fromDrop
                            ? "拖进来的内容里没有支持的音频文件。"
                            : "所选内容里没有支持的音频文件。";
                        Dialogs.ShowInfo(this, "没有找到音频", tip);
                        _listPane.FlashStatus("");
                        return;
                    }

                    if (targetGroup == null || !_config.Groups.Contains(targetGroup))
                    {
                        _listPane.FlashStatus("目标分组已不存在，导入已取消");
                        return;
                    }

                    int nextOrder = NextAddedOrder();
                    for (int i = 0; i < tracks.Count; i++) tracks[i].AddedOrder = nextOrder++;

                    AddTracksToGroup(targetGroup, tracks);
                    _filler.Enqueue(tracks);
                    SaveConfig();
                    _listPane.FlashStatus("已导入 " + tracks.Count + " 首");
                }), DispatcherPriority.Background);
            });
        }

        private void AddTracks(List<Track> tracks)
        {
            AddTracksToGroup(_group, tracks);
        }

        private void AddTracksToGroup(Group targetGroup, List<Track> tracks)
        {
            if (targetGroup == null || tracks == null || tracks.Count == 0) return;
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in targetGroup.Tracks)
            {
                if (t != null && !string.IsNullOrEmpty(t.Path)) existing.Add(t.Path);
            }

            bool added = false;
            foreach (var t in tracks)
            {
                if (t == null || string.IsNullOrEmpty(t.Path)) continue;
                if (existing.Contains(t.Path)) continue;
                targetGroup.Tracks.Add(t);
                existing.Add(t.Path);
                added = true;
            }

            if (added)
            {
                // 重新导入有新增曲目时才恢复有序（README）
                targetGroup.ShuffledOrder = null;
                if (ReferenceEquals(targetGroup, _group))
                {
                    RebuildView();
                }
                else if (_listPane != null && _group != null)
                {
                    _listPane.SetGroups(_config.Groups, _group.Id);
                }
            }
        }

        private int NextAddedOrder()
        {
            int max = 0;
            foreach (var g in _config.Groups)
                foreach (var t in g.Tracks)
                    if (t.AddedOrder > max) max = t.AddedOrder;
            return max + 1;
        }

        // ------------------------------------------------------------------
        // 分组管理
        // ------------------------------------------------------------------

        private void NewGroup()
        {
            var name = Dialogs.Prompt(this, "新建分组", "分组名称：", "新分组");
            if (string.IsNullOrWhiteSpace(name)) return;

            var group = new Group { Name = name.Trim() };
            _config.Groups.Add(group);
            _config.CurrentGroupId = group.Id;
            ApplyGroup(group, false);
            SaveConfig();
        }

        private void RenameGroup()
        {
            if (_group == null) return;
            var name = Dialogs.Prompt(this, "重命名分组", "新的分组名称：", _group.Name);
            if (string.IsNullOrWhiteSpace(name)) return;

            _group.Name = name.Trim();
            _listPane.SetGroups(_config.Groups, _group.Id);
            SaveConfig();
        }

        private void DeleteGroup()
        {
            if (_group == null || _config.Groups.Count <= 1)
            {
                Dialogs.ShowInfo(this, "无法删除", "至少要保留一个分组。");
                return;
            }

            if (!Dialogs.Confirm(this, "删除分组",
                    "确定删除分组「" + _group.Name + "」吗？\n（只从列表里移除，不会删除你的音乐文件）",
                    "删除"))
                return;

            var removing = _group;
            _config.Groups.Remove(removing);
            StopPlaybackAndClearUi(true);
            _config.CurrentGroupId = _config.Groups[0].Id;
            ApplyGroup(_config.Groups[0], false);
            SaveConfig();
        }

        // ------------------------------------------------------------------
        // 播放
        // ------------------------------------------------------------------

        private void HookPlayer()
        {
            _player.PlaybackEnded += OnPlayerPlaybackEnded;
            _player.PlaybackFailed += OnPlayerPlaybackFailed;
        }

        private void OnPlayerPlaybackEnded()
        {
            if (_isClosed) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_isClosed || Dispatcher.HasShutdownStarted) return;
                OnTrackEnded();
            }));
        }

        private void OnPlayerPlaybackFailed(string message)
        {
            if (_isClosed) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_isClosed || Dispatcher.HasShutdownStarted) return;

                _consecutiveFailures++;
                int circuitLimit = Math.Min(_view.Count, 5);
                if (_view.Count <= 1 || circuitLimit <= 0 || _consecutiveFailures >= circuitLimit)
                {
                    _listPane.FlashStatus("连续播放失败，已停止自动切歌：" + message);
                    UpdateBottomBar();
                    SyncPlaybackTimerState();
                    return;
                }

                _listPane.FlashStatus(message);
                // 播放失败自动跳到下一首（README）
                PlayNext(false);
            }));
        }

        private void PlayTrack(Track track)
        {
            PlayTrack(track, false);
        }

        private void PlayTrack(Track track, bool isAutoAdvance)
        {
            if (_isClosed || track == null) return;

            if (!isAutoAdvance)
            {
                _consecutiveFailures = 0;
            }

            // 文件已被删除：不再尝试播放，直接提示，避免解码/打开句柄卡死
            if (!string.IsNullOrEmpty(track.Path) && !System.IO.File.Exists(track.Path))
            {
                _listPane.FlashStatus("文件不存在: " + track.Path);
                return;
            }

            // 启动时即推进尝试游标，防止失败后游标停滞导致反复重试同一首
            int index = _view.IndexOf(track);
            if (index < 0) index = IndexOfPath(track.Path);
            if (index >= 0) _currentIndex = index;

            double startAt = 0.0;
            int gen = Interlocked.Increment(ref _playGeneration);
            string path = track.Path;

            // 串行单循环播放队列：快速切歌时仅保留最新请求，避免并发 Play 导致音画撕裂
            bool startWorker = false;
            lock (_playQueueGate)
            {
                _pendingPlayRequest = new PlayRequest(gen, path, startAt);
                if (!_playWorkerRunning)
                {
                    _playWorkerRunning = true;
                    startWorker = true;
                }
            }

            if (startWorker)
            {
                ThreadPool.QueueUserWorkItem(delegate { ProcessPlayQueue(); });
            }
        }

        private void ProcessPlayQueue()
        {
            while (true)
            {
                PlayRequest req;
                lock (_playQueueGate)
                {
                    if (_isClosed || _pendingPlayRequest == null)
                    {
                        _playWorkerRunning = false;
                        return;
                    }
                    req = _pendingPlayRequest;
                    _pendingPlayRequest = null;
                }

                // 进入 _player.Play() 前校验代数与窗口存活状态
                if (_isClosed || req.Generation != Volatile.Read(ref _playGeneration))
                    continue;

                bool ok = false;
                try
                {
                    ok = _player.Play(req.Path, req.StartAt);
                }
                catch (Exception ex)
                {
                    Log.Warn("后台播放异常: " + ex.Message);
                }

                // 返回后再次校验代数与窗口存活状态
                if (_isClosed || req.Generation != Volatile.Read(ref _playGeneration))
                    continue;

                if (!ok) continue;

                var captured = req;
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_isClosed || Dispatcher.HasShutdownStarted) return;
                    if (captured.Generation != Volatile.Read(ref _playGeneration)) return;

                    int resolvedIndex = IndexOfPath(captured.Path);
                    _consecutiveFailures = 0;
                    _currentIndex = resolvedIndex;
                    _lastKnownPosition = captured.StartAt;
                    _playbackEnded = false;
                    _listPane.SetPlaying(resolvedIndex);
                    LoadLyricsFor(captured.Path);
                    UpdateBottomBar();
                    SyncPlaybackTimerState();
                }));
            }
        }

        private void TogglePlayPause()
        {
            if (!_player.HasTrack)
            {
                // 还没选歌：从当前播放位置或第一首开始
                if (_currentIndex >= 0 && _currentIndex < _view.Count) PlayTrack(_view[_currentIndex]);
                else if (_view.Count > 0) PlayTrack(_view[0]);
                return;
            }

            _player.TogglePlayPause();
            UpdateBottomBar();
            if (_desktopLyrics != null && _config.DesktopLyricsVisible)
                _desktopLyrics.SetPlaying(_player.IsPlaying);
            SyncPlaybackTimerState();
        }

        private Track ResolveRepeatOneTrack()
        {
            if (_view.Count == 0) return null;

            int byPath = _currentIndexPath();
            if (byPath >= 0 && byPath < _view.Count)
                return _view[byPath];

            int clamped = Math.Max(0, Math.Min(_currentIndex, _view.Count - 1));
            return _view[clamped];
        }

        private void PlayNext(bool fromUser)
        {
            if (_view.Count == 0) return;

            if (fromUser) _consecutiveFailures = 0;

            if (_player.Mode == Player.PlayMode.RepeatOne && !fromUser)
            {
                var repeatTrack = ResolveRepeatOneTrack();
                if (repeatTrack != null) PlayTrack(repeatTrack, true);
                return;
            }

            int next = _currentIndex + 1;
            if (next < 0 || next >= _view.Count)
            {
                if (_player.Mode == Player.PlayMode.Sequential && next >= _view.Count)
                {
                    StopPlaybackAndClearUi(false);
                    return;
                }
                next = 0;
            }

            // 跳过已失效（文件不存在）的曲目，最多找一圈，避免自动切歌时反复失败或卡死
            int attempts = _view.Count;
            while (attempts-- > 0)
            {
                var t = _view[next];
                if (t != null && !string.IsNullOrEmpty(t.Path) && System.IO.File.Exists(t.Path))
                {
                    PlayTrack(t, !fromUser);
                    return;
                }

                next++;
                if (next >= _view.Count)
                {
                    if (_player.Mode == Player.PlayMode.Sequential)
                    {
                        StopPlaybackAndClearUi(false);
                        return;
                    }
                    next = 0;
                }
            }

            StopPlaybackAndClearUi(false);
        }

        private void PlayPrevious()
        {
            if (_view.Count == 0) return;

            _consecutiveFailures = 0;

            int prev = _currentIndex - 1;
            if (prev < 0 || prev >= _view.Count)
            {
                if (_player.Mode == Player.PlayMode.Sequential)
                    prev = 0;
                else
                    prev = _view.Count - 1;
            }

            // 与 PlayNext 对称：跳过已失效（文件不存在）的曲目，最多找一圈
            int attempts = _view.Count;
            while (attempts-- > 0)
            {
                var t = _view[prev];
                if (t != null && !string.IsNullOrEmpty(t.Path) && System.IO.File.Exists(t.Path))
                {
                    PlayTrack(t, false);
                    return;
                }

                prev--;
                if (prev < 0)
                {
                    if (_player.Mode == Player.PlayMode.Sequential)
                    {
                        StopPlaybackAndClearUi(false);
                        return;
                    }
                    prev = _view.Count - 1;
                }
            }

            StopPlaybackAndClearUi(false);
        }

        private void OnTrackEnded()
        {
            // 当前曲目已放到结尾：进度按 README 归零（下次从头开始）
            _playbackEnded = true;
            SaveGroupProgress();
            _config.SaveAsync();

            if (_view.Count == 0)
            {
                SyncPlaybackTimerState();
                return;
            }

            if (_player.Mode == Player.PlayMode.RepeatOne)
            {
                var repeatTrack = ResolveRepeatOneTrack();
                if (repeatTrack != null) PlayTrack(repeatTrack, true);
                return;
            }
            PlayNext(false);
        }

        private void CycleMode()
        {
            var mode = (int)_player.Mode;
            mode = (mode + 1) % 3;
            _player.Mode = (Player.PlayMode)mode;
            _config.PlayMode = mode;
            UpdateModeButton();
            SaveConfig();
        }

        private void SeekTo(double seconds)
        {
            if (!_player.HasTrack) return;
            _player.Seek(seconds);
            _lastKnownPosition = Math.Max(0, seconds);
            _playbackEnded = IsAtEnd(_lastKnownPosition, _player.DurationSeconds);
            UpdateBottomBar();
            _lyricsPane.UpdatePosition(_player.PositionSeconds, true);
            if (_desktopLyrics != null && _config.DesktopLyricsVisible && _player.IsPlaying)
                _desktopLyrics.UpdatePosition(_player.PositionSeconds);
        }

        // ------------------------------------------------------------------
        // 歌词
        // ------------------------------------------------------------------

        private void LoadLyricsFor(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                _currentLyrics = null;
                _currentLyricsPath = null;
                _lyricsPane.SetLyrics(null);
                if (_desktopLyrics != null) _desktopLyrics.SetLyrics(null);
                return;
            }

            if (!string.Equals(_currentLyricsPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _currentLyrics = null;
                _currentLyricsPath = path;
            }

            var duration = _player.DurationSeconds;
            var token = _lifetimeCts.Token;

            // 读歌词要走磁盘；缓存未命中时放后台，避免切歌时卡一下。
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (token.IsCancellationRequested || _isClosed) return;

                Lyrics.LyricsDocument doc = null;
                try
                {
                    doc = Lyrics.LyricsFinder.Find(path, duration);
                }
                catch (Exception ex)
                {
                    Log.Warn("加载歌词失败: " + ex.Message);
                }

                if (token.IsCancellationRequested || _isClosed) return;

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_isClosed || Dispatcher.HasShutdownStarted) return;
                    // 期间又切歌了：丢弃这次结果
                    if (!string.Equals(_player.CurrentPath, path, StringComparison.OrdinalIgnoreCase)) return;

                    _currentLyrics = doc;
                    _currentLyricsPath = path;
                    _lyricsPane.SetLyrics(doc);
                    if (_desktopLyrics != null)
                    {
                        _desktopLyrics.SetLyrics(doc);
                        if (_config.DesktopLyricsVisible)
                        {
                            _desktopLyrics.SetPlaying(_player.IsPlaying);
                            if (_player.IsPlaying) _desktopLyrics.UpdatePosition(_player.PositionSeconds);
                        }
                    }
                    AutoToggleLyricsPane(doc);
                }), DispatcherPriority.Background);
            });
        }

        private void AutoToggleLyricsPane(Lyrics.LyricsDocument doc)
        {
            if (_lyricsPaneUserCollapsed) return;

            bool hasLyrics = doc != null && !doc.IsEmpty;
            bool changed = _config.ShowLyricsPane != hasLyrics;
            _config.ShowLyricsPane = hasLyrics;
            if (changed) ApplyPaneVisibility();
        }

        private void ToggleLyricsPane()
        {
            _config.ShowLyricsPane = !_config.ShowLyricsPane;
            _lyricsPaneUserCollapsed = !_config.ShowLyricsPane;
            ApplyPaneVisibility();
            SaveConfig();
        }

        private void ApplyPaneVisibility()
        {
            bool showLyrics = _config.ShowLyricsPane;
            bool showList = _config.ShowListPane;

            // 两侧都关掉时至少保留列表，避免空白窗口
            if (!showLyrics && !showList)
            {
                showList = true;
                _config.ShowListPane = true;
            }

            _lyricsPane.Visibility = showLyrics ? Visibility.Visible : Visibility.Collapsed;
            _listPane.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;

            if (_splitBody != null && _splitBody.ColumnDefinitions.Count == 2)
            {
                // 隐藏一侧后另一侧自动占满（固定 50/50，不可拖动）
                _splitBody.ColumnDefinitions[0].Width = showLyrics
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);
                _splitBody.ColumnDefinitions[1].Width = showList
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);
            }
        }

        // ------------------------------------------------------------------
        // 桌面歌词
        // ------------------------------------------------------------------

        private void HookDesktopLyrics(DesktopLyricsWindow window)
        {
            window.PositionChanged += OnDesktopLyricsPositionChanged;
            window.LockChanged += OnDesktopLyricsLockChanged;
            window.FontScaleChanged += OnDesktopLyricsFontScaleChanged;
            window.HiddenRequested += OnDesktopLyricsHiddenRequested;
        }

        private void UnhookDesktopLyrics(DesktopLyricsWindow window)
        {
            if (window == null) return;
            window.PositionChanged -= OnDesktopLyricsPositionChanged;
            window.LockChanged -= OnDesktopLyricsLockChanged;
            window.FontScaleChanged -= OnDesktopLyricsFontScaleChanged;
            window.HiddenRequested -= OnDesktopLyricsHiddenRequested;
        }

        private void OnDesktopLyricsPositionChanged(double left, double top, double height)
        {
            if (_isClosed) return;
            _config.DesktopLyricsLeft = left;
            _config.DesktopLyricsTop = top;
            _config.DesktopLyricsHeight = height;
            _config.SaveAsync();
        }

        private void OnDesktopLyricsLockChanged(bool locked)
        {
            if (_isClosed) return;
            _config.DesktopLyricsLocked = locked;
            SaveConfig();
            if (_tray != null) _tray.UpdateState();
        }

        private void OnDesktopLyricsFontScaleChanged(double scale)
        {
            if (_isClosed) return;
            _config.DesktopLyricsFontScale = scale;
            SaveConfig();
        }

        private void OnDesktopLyricsHiddenRequested()
        {
            if (_isClosed) return;
            _config.DesktopLyricsVisible = false;
            SyncDesktopLyrics();
            SaveConfig();
        }

        private void SyncDesktopLyrics()
        {
            if (_desktopLyrics == null) return;

            // 位置、字号、行数、锁定状态都会记住
            _desktopLyrics.ApplyConfig(_config);

            if (_config.DesktopLyricsVisible)
            {
                if (!_desktopLyrics.IsVisible) _desktopLyrics.Show();
                _desktopLyrics.SetTickerEnabled(true);

                if (_player.HasTrack)
                {
                    // 直接复用缓存歌词，不在 UI 线程同步读盘
                    if (_currentLyrics != null &&
                        string.Equals(_currentLyricsPath, _player.CurrentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _desktopLyrics.SetLyrics(_currentLyrics);
                        _desktopLyrics.SetPlaying(_player.IsPlaying);
                        if (_player.IsPlaying)
                            _desktopLyrics.UpdatePosition(_player.PositionSeconds);
                    }
                    else
                    {
                        LoadLyricsFor(_player.CurrentPath);
                    }
                }
                else
                {
                    _desktopLyrics.SetLyrics(null);
                    _desktopLyrics.SetPlaying(false);
                }
            }
            else
            {
                _desktopLyrics.Hide();
                _desktopLyrics.SetTickerEnabled(false);
            }

            if (_tray != null) _tray.UpdateState();
        }

        private void EnsureDesktopLyrics()
        {
            if (_desktopLyrics != null) return;
            _desktopLyrics = new DesktopLyricsWindow();
            HookDesktopLyrics(_desktopLyrics);
            _desktopLyrics.SetSavedPosition(_config.DesktopLyricsLeft, _config.DesktopLyricsTop);
        }

        // ------------------------------------------------------------------
        // 窗口与状态
        // ------------------------------------------------------------------

        private void RestoreWindowPlacement()
        {
            if (!double.IsNaN(_config.WindowLeft) && !double.IsNaN(_config.WindowTop))
            {
                // 结合 VirtualScreenLeft/Top 支持多屏负坐标校验
                double left = _config.WindowLeft;
                double top = _config.WindowTop;
                double vLeft = SystemParameters.VirtualScreenLeft;
                double vTop = SystemParameters.VirtualScreenTop;
                double vRight = vLeft + SystemParameters.VirtualScreenWidth;
                double vBottom = vTop + SystemParameters.VirtualScreenHeight;

                if (left >= vLeft - 200 && left < vRight - 60 &&
                    top >= vTop - 20 && top < vBottom - 60)
                {
                    Left = left;
                    Top = top;
                }
            }
            if (_config.WindowWidth > 400) Width = _config.WindowWidth;
            if (_config.WindowHeight > 300) Height = _config.WindowHeight;
            if (_config.WindowMaximized) WindowState = WindowState.Maximized;

            _player.Volume = _config.VolumePercent / 100f;
            _player.Muted = _config.Muted;
            _player.Mode = (Player.PlayMode)Math.Max(0, Math.Min(2, _config.PlayMode));
        }

        private void SaveWindowPlacement()
        {
            _config.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _config.WindowLeft = Left;
                _config.WindowTop = Top;
                _config.WindowWidth = Width;
                _config.WindowHeight = Height;
            }
        }

        /// <summary>
        /// 保存当前分组的播放进度。仅当当前播放曲目属于当前分组时才写回，防止跨分组污染。
        /// 返回本次是否有实际进度变化。
        /// </summary>
        private bool SaveGroupProgress()
        {
            if (_group == null) return false;

            var path = _player.CurrentPath;
            if (string.IsNullOrEmpty(path)) return false;
            if (!GroupContainsPath(_group, path)) return false;

            double duration = _player.DurationSeconds;
            bool ended = _playbackEnded || IsAtEnd(_player.PositionSeconds, duration);

            double position;
            if (ended)
            {
                // 上次已经放到结尾的曲子，下次从头开始
                position = 0;
            }
            else
            {
                // 播放中：用实时位置；已停止但未播完：用记住的最后位置
                double live = _player.PositionSeconds;
                position = live > 0.05 ? live : _lastKnownPosition;
            }

            position = Math.Max(0, position);

            bool changed = !string.Equals(_lastSavedTrackPath, path, StringComparison.OrdinalIgnoreCase) ||
                           Math.Abs(_lastSavedPositionSeconds - position) >= 0.5;

            _group.LastTrackPath = path;
            _group.LastPositionSeconds = position;

            if (changed)
            {
                _lastSavedTrackPath = path;
                _lastSavedPositionSeconds = position;
            }

            return changed;
        }

        private static bool IsAtEnd(double position, double duration)
        {
            return duration > 0 && position >= duration - 0.6;
        }

        private void SaveConfig()
        {
            SaveGroupProgress();
            _config.Save();
        }

        private void UpdateModeButton()
        {
            if (_bottomBar == null) return;
            _bottomBar.SetMode((int)_player.Mode);
        }

        private void UpdateVolumeUi()
        {
            if (_bottomBar == null) return;
            _bottomBar.SetVolume(_config.VolumePercent / 100f, _config.Muted);
        }

        /// <summary>按增量调整音量（鼠标滚轮 / 上下方向键共用）。</summary>
        private void NudgeVolume(float delta)
        {
            int current = _config.VolumePercent;
            int next = Math.Max(0, Math.Min(100, current + (int)Math.Round(delta * 100)));
            if (next == current && !_config.Muted) return;

            _config.VolumePercent = next;
            _player.Volume = next / 100f;
            _config.Muted = false;
            _player.Muted = false;
            UpdateVolumeUi();
            SaveConfig();
        }

        private void UpdateBottomBar()
        {
            if (_bottomBar == null) return;

            Track track = (_currentIndex >= 0 && _currentIndex < _view.Count) ? _view[_currentIndex] : null;
            _bottomBar.SetTrack(track);
            _bottomBar.SetPlaying(_player.IsPlaying);
            _bottomBar.SetTimeline(_player.PositionSeconds, _player.DurationSeconds, _player.HasTrack);
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 点关闭 = 最小化到托盘继续播放
            if (!_reallyExit)
            {
                e.Cancel = true;
                Hide();
                SaveConfig();
                return;
            }

            SaveWindowPlacement();
            SaveConfig();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _isClosed = true;
            Interlocked.Increment(ref _playGeneration);
            lock (_playQueueGate)
            {
                _pendingPlayRequest = null;
            }

            try { _lifetimeCts.Cancel(); } catch { }
            try { _lifetimeCts.Dispose(); } catch { }

            if (_playbackTimer != null)
            {
                try
                {
                    _playbackTimer.Stop();
                    _playbackTimer.Tick -= OnPlaybackTimerTick;
                }
                catch { }
            }

            try
            {
                _player.PlaybackEnded -= OnPlayerPlaybackEnded;
                _player.PlaybackFailed -= OnPlayerPlaybackFailed;
            }
            catch { }

            if (_single != null)
            {
                try
                {
                    _single.FilesReceived -= OnSingleInstanceFilesReceived;
                    _single.ActivationRequested -= OnSingleInstanceActivationRequested;
                }
                catch { }
            }

            if (_desktopLyrics != null)
            {
                try { UnhookDesktopLyrics(_desktopLyrics); } catch { }
            }

            if (_filler != null)
            {
                try
                {
                    _filler.BatchCompleted -= OnMetadataBatchCompleted;
                    _filler.AllCompleted -= OnMetadataAllCompleted;
                    _filler.Cancel();
                }
                catch { }
            }

            if (_mediaKeys != null)
            {
                try
                {
                    _mediaKeys.PlayPausePressed -= OnMediaKeyPlayPause;
                    _mediaKeys.NextPressed -= OnMediaKeyNext;
                    _mediaKeys.PrevPressed -= OnMediaKeyPrev;
                    _mediaKeys.StopPressed -= OnMediaKeyStop;
                    _mediaKeys.Dispose();
                }
                catch { }
            }

            try { _player.Dispose(); } catch { }
            try { _desktopLyrics?.Close(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _single?.Dispose(); } catch { }
        }

        /// <summary>真正退出（托盘菜单调用）。</summary>
        public void ExitApplication()
        {
            _reallyExit = true;
            try { Close(); } catch { }
            if (Application.Current != null) Application.Current.Shutdown();
        }

        public Config Configuration { get { return _config; } }
        public Player AudioPlayer { get { return _player; } }
        public DesktopLyricsWindow DesktopLyrics { get { return _desktopLyrics; } }
        public void EnsureDesktopLyricsForTray() { EnsureDesktopLyrics(); }
        public void RefreshDesktopLyrics() { SyncDesktopLyrics(); }
        public void ToggleDesktopLyrics()
        {
            _config.DesktopLyricsVisible = !_config.DesktopLyricsVisible;
            SyncDesktopLyrics();
            SaveConfig();
        }
        public void SaveConfigNow() { SaveConfig(); }
        public void PlayPauseFromTray() { TogglePlayPause(); }
        public void NextFromTray() { PlayNext(true); }
        public void PrevFromTray() { PlayPrevious(); }
        public void CycleModeFromTray() { CycleMode(); }
        public void ReloadConfig() { _config.Save(); }

        private void HookSingleInstance()
        {
            if (_single == null) return;
            _single.FilesReceived += OnSingleInstanceFilesReceived;
            _single.ActivationRequested += OnSingleInstanceActivationRequested;
        }

        private void OnSingleInstanceFilesReceived(IList<string> files)
        {
            if (_isClosed) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_isClosed || Dispatcher.HasShutdownStarted) return;
                OpenExternalFiles(files);
                SingleInstance.BringToFront(this);
            }));
        }

        private void OnSingleInstanceActivationRequested()
        {
            if (_isClosed) return;
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (_isClosed || Dispatcher.HasShutdownStarted) return;
                SingleInstance.BringToFront(this);
            }));
        }

        // ------------------------------------------------------------------
        // 快捷键（只保留编辑类，播放控制交给媒体键）
        // ------------------------------------------------------------------

        private void HookHotkeys()
        {
            InputBindings.Add(new KeyBinding(
                new RelayCommand(delegate { ImportFiles(); }),
                Key.O, ModifierKeys.Control));

            InputBindings.Add(new KeyBinding(
                new RelayCommand(delegate { ImportFolder(); }),
                Key.O, ModifierKeys.Control | ModifierKeys.Shift));

            InputBindings.Add(new KeyBinding(
                new RelayCommand(delegate { _listPane.FocusSearch(); }),
                Key.F, ModifierKeys.Control));

            InputBindings.Add(new KeyBinding(
                new RelayCommand(delegate { ToggleLyricsPane(); }),
                Key.L, ModifierKeys.Control | ModifierKeys.Alt));

            InputBindings.Add(new KeyBinding(
                new RelayCommand(delegate { ToggleDesktopLyrics(); }),
                Key.D, ModifierKeys.Control | ModifierKeys.Alt));

            InputBindings.Add(new KeyBinding(
                new RelayCommand(delegate
                {
                    _config.Muted = !_config.Muted;
                    _player.Muted = _config.Muted;
                    UpdateVolumeUi();
                    SaveConfig();
                }),
                Key.M, ModifierKeys.Control | ModifierKeys.Alt));

            // Esc 退出最大化
            InputBindings.Add(new KeyBinding(
                new RelayCommand(delegate
                {
                    if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                }),
                Key.Escape, ModifierKeys.None));
        }

        /// <summary>
        /// 全局按键：空格=播放/暂停，←/→=快退/快进，↑/↓=调大/调小音量。
        /// 仅在无修饰键且焦点不在文本输入框（如搜索框）时生效，避免误触发。
        /// </summary>
        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;

            // 焦点在文本框 / 密码框等可输入控件里时，方向键与空格属于正常输入/光标操作，交给控件处理。
            var focused = Keyboard.FocusedElement as DependencyObject;
            bool listFocused = false;
            while (focused != null)
            {
                if (focused is TextBox || focused is PasswordBox) return;
                if (focused is ListBox || focused is ListBoxItem) listFocused = true;

                if (focused is Visual || focused is Visual3D)
                {
                    var visualParent = VisualTreeHelper.GetParent(focused);
                    focused = visualParent ?? LogicalTreeHelper.GetParent(focused);
                }
                else
                {
                    focused = LogicalTreeHelper.GetParent(focused);
                }
            }

            switch (e.Key)
            {
                case Key.Space:
                    TogglePlayPause();
                    e.Handled = true;
                    break;

                case Key.Left:
                    SeekBy(-5);
                    e.Handled = true;
                    break;

                case Key.Right:
                    SeekBy(5);
                    e.Handled = true;
                    break;

                case Key.Up:
                    if (listFocused) return;
                    NudgeVolume(0.05f);
                    e.Handled = true;
                    break;

                case Key.Down:
                    if (listFocused) return;
                    NudgeVolume(-0.05f);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>按增量调整播放位置（左右方向键）。</summary>
        private void SeekBy(double deltaSeconds)
        {
            if (!_player.HasTrack) return;
            SeekTo(_player.PositionSeconds + deltaSeconds);
        }

        // ------------------------------------------------------------------
        // 系统媒体键
        // ------------------------------------------------------------------

        private void HookMediaKeys()
        {
            _mediaKeys = new MediaKeys();
            _mediaKeys.PlayPausePressed += OnMediaKeyPlayPause;
            _mediaKeys.NextPressed += OnMediaKeyNext;
            _mediaKeys.PrevPressed += OnMediaKeyPrev;
            _mediaKeys.StopPressed += OnMediaKeyStop;

            bool ok = _mediaKeys.Register();
            Log.Info(ok
                ? "媒体键已启用" + (_mediaKeys.FailureReason != null ? "（" + _mediaKeys.FailureReason + "）" : "")
                : "媒体键不可用：" + _mediaKeys.FailureReason);
        }

        private void OnMediaKeyPlayPause()
        {
            if (!_isClosed) TogglePlayPause();
        }

        private void OnMediaKeyNext()
        {
            if (!_isClosed) PlayNext(true);
        }

        private void OnMediaKeyPrev()
        {
            if (!_isClosed) PlayPrevious();
        }

        private void OnMediaKeyStop()
        {
            if (!_isClosed) StopPlaybackAndClearUi(false);
        }

        // ------------------------------------------------------------------
        // 播放进度定时器（驱动歌词高亮 / 桌面歌词 / 进度条）
        // ------------------------------------------------------------------

        private void StartPlaybackTimer()
        {
            _playbackTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _playbackTimer.Tick += OnPlaybackTimerTick;
            SyncPlaybackTimerState();
        }

        private void SyncPlaybackTimerState()
        {
            if (_playbackTimer == null || _isClosed) return;

            if (_player.IsPlaying)
            {
                if (_playbackTimer.Interval != TimeSpan.FromMilliseconds(120))
                    _playbackTimer.Interval = TimeSpan.FromMilliseconds(120);
                if (!_playbackTimer.IsEnabled)
                    _playbackTimer.Start();
            }
            else
            {
                if (_playbackTimer.IsEnabled)
                    _playbackTimer.Stop();
            }
        }

        private void OnPlaybackTimerTick(object sender, EventArgs e)
        {
            if (_isClosed) return;
            TickPlayback();
        }

        private void TickPlayback()
        {
            try
            {
                if (!_player.HasTrack || !_player.IsPlaying)
                {
                    UpdateBottomBar();
                    if (_desktopLyrics != null && _config.DesktopLyricsVisible)
                        _desktopLyrics.SetPlaying(false);
                    SyncPlaybackTimerState();
                    return;
                }

                double position = _player.PositionSeconds;
                double duration = _player.DurationSeconds;

                // 记录播放过程中见过的最后一个有效位置：
                // 播放结束后 position 会回落，靠它才能保住进度
                if (position > 0.05)
                {
                    _lastKnownPosition = position;
                    _playbackEnded = IsAtEnd(position, duration);
                }

                _bottomBar.SetTimeline(position, duration, true);
                _bottomBar.SetPlaying(true);
                _lyricsPane.UpdatePosition(position, true);

                if (_desktopLyrics != null && _config.DesktopLyricsVisible)
                {
                    _desktopLyrics.SetPlaying(true);
                    _desktopLyrics.UpdatePosition(position);
                }

                // 分组进度定期落盘（每 5 秒），且仅在进度有实际变化时才写盘
                if ((DateTime.UtcNow - _lastProgressSave).TotalSeconds >= 5)
                {
                    _lastProgressSave = DateTime.UtcNow;
                    if (SaveGroupProgress())
                    {
                        _config.SaveAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("播放进度刷新失败: " + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // 启动时/切换分组时恢复播放进度
        // ------------------------------------------------------------------

        private void RestoreGroupProgress(bool scrollToPlaying)
        {
            if (_group == null || string.IsNullOrEmpty(_group.LastTrackPath)) return;
            if (!System.IO.File.Exists(_group.LastTrackPath))
            {
                _group.LastTrackPath = null;
                _group.LastPositionSeconds = 0;
                return;
            }

            try
            {
                // 恢复时不会自动播放（等用户按播放键）
                if (_player.Load(_group.LastTrackPath, _group.LastPositionSeconds))
                {
                    _currentIndex = IndexOfPath(_group.LastTrackPath);
                    _lastKnownPosition = _group.LastPositionSeconds;
                    _playbackEnded = false;
                    _lastSavedTrackPath = _group.LastTrackPath;
                    _lastSavedPositionSeconds = _group.LastPositionSeconds;
                    _listPane.SetPlaying(_currentIndex);
                    LoadLyricsFor(_group.LastTrackPath);
                    UpdateBottomBar();
                    _bottomBar.SetPlaying(false);
                    SyncPlaybackTimerState();

                    if (scrollToPlaying)
                    {
                        // 打开时定位到当前播放位置（等布局完成后再滚动）
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (_isClosed || Dispatcher.HasShutdownStarted) return;
                            _listPane.ScrollToPlaying();
                        }), DispatcherPriority.Loaded);
                    }

                    Log.Info(string.Format("已恢复分组「{0}」的进度: {1} @ {2:0.0}s",
                        _group.Name,
                        System.IO.Path.GetFileName(_group.LastTrackPath),
                        _group.LastPositionSeconds));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("恢复播放进度失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 从文件关联打开：在现有分组里就切过去并播放；
        /// 不在库里就统一收进「打开的文件」分组（README）。
        /// </summary>
        public void OpenExternalFiles(IList<string> files)
        {
            if (_isClosed || files == null || files.Count == 0) return;

            Track firstExisting = null;
            Group firstExistingOwner = null;
            bool firstTargetIsExisting = false;
            bool firstTargetResolved = false;

            var toScan = new List<string>();
            foreach (var path in files)
            {
                if (string.IsNullOrWhiteSpace(path)) continue;

                var found = FindTrackInLibrary(path);
                if (found != null)
                {
                    if (!firstTargetResolved)
                    {
                        firstTargetResolved = true;
                        firstTargetIsExisting = true;
                    }
                    if (firstExisting == null)
                    {
                        firstExisting = found;
                        firstExistingOwner = FindGroupOfTrack(found);
                    }
                    continue;
                }

                if (!firstTargetResolved)
                {
                    firstTargetResolved = true;
                    firstTargetIsExisting = false;
                }
                toScan.Add(path);
            }

            if (toScan.Count == 0)
            {
                if (firstExisting != null)
                {
                    if (firstExistingOwner != null && firstExistingOwner.Id != _config.CurrentGroupId)
                    {
                        SaveGroupProgress();
                        _config.CurrentGroupId = firstExistingOwner.Id;
                        ApplyGroup(firstExistingOwner, false);
                    }
                    PlayTrack(firstExisting);
                }
                return;
            }

            var token = _lifetimeCts.Token;
            _listPane.FlashStatus("正在打开外部文件…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (token.IsCancellationRequested || _isClosed) return;

                List<Track> pending;
                try
                {
                    int scanOrder = 0;
                    pending = MediaScanner.FromFiles(toScan, ref scanOrder);
                }
                catch (Exception ex)
                {
                    Log.Error("扫描外部文件失败", ex);
                    pending = new List<Track>();
                }

                if (token.IsCancellationRequested || _isClosed) return;

                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (_isClosed || Dispatcher.HasShutdownStarted) return;

                    if (pending.Count == 0)
                    {
                        _listPane.FlashStatus("");
                        if (firstExisting != null)
                        {
                            if (firstExistingOwner != null &&
                                _config.Groups.Contains(firstExistingOwner) &&
                                firstExistingOwner.Id != _config.CurrentGroupId)
                            {
                                SaveGroupProgress();
                                _config.CurrentGroupId = firstExistingOwner.Id;
                                ApplyGroup(firstExistingOwner, false);
                            }
                            PlayTrack(firstExisting);
                        }
                        return;
                    }

                    // 循环外统一递增 AddedOrder
                    int nextOrder = NextAddedOrder();
                    for (int i = 0; i < pending.Count; i++) pending[i].AddedOrder = nextOrder++;

                    var openGroup = _config.FindGroupByName("打开的文件") ?? CreateOpenFilesGroup();
                    AddTracksToGroup(openGroup, pending);
                    _filler.Enqueue(pending);

                    if (firstTargetIsExisting && firstExisting != null &&
                        firstExistingOwner != null && _config.Groups.Contains(firstExistingOwner))
                    {
                        if (firstExistingOwner.Id != _config.CurrentGroupId)
                        {
                            SaveGroupProgress();
                            _config.CurrentGroupId = firstExistingOwner.Id;
                            ApplyGroup(firstExistingOwner, false);
                        }
                        PlayTrack(firstExisting);
                    }
                    else
                    {
                        if (openGroup.Id != _config.CurrentGroupId)
                        {
                            SaveGroupProgress();
                            _config.CurrentGroupId = openGroup.Id;
                            ApplyGroup(openGroup, false);
                        }
                        else
                        {
                            RebuildView();
                        }

                        // 仅对首个目标曲目调用一次 PlayTrack（播放 pending[0] 而非 _view[0]）
                        Track targetTrack = pending[0];
                        int idx = IndexOfPath(targetTrack.Path);
                        if (idx >= 0 && idx < _view.Count) targetTrack = _view[idx];
                        PlayTrack(targetTrack);
                    }

                    SaveConfig();
                    _listPane.FlashStatus("");
                }), DispatcherPriority.Background);
            });
        }

        private Group CreateOpenFilesGroup()
        {
            var existing = _config.FindGroupByName("打开的文件");
            if (existing != null) return existing;

            var group = new Group { Name = "打开的文件" };
            _config.Groups.Add(group);
            return group;
        }

        private Track FindTrackInLibrary(string path)
        {
            foreach (var g in _config.Groups)
            {
                foreach (var t in g.Tracks)
                {
                    if (string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)) return t;
                }
            }
            return null;
        }

        private Group FindGroupOfTrack(Track track)
        {
            foreach (var g in _config.Groups)
            {
                if (g.Tracks.Contains(track)) return g;
            }
            return null;
        }

        private void QueueMetadata()
        {
            if (_filler == null) return;
            var all = new List<Track>();
            foreach (var g in _config.Groups)
            {
                foreach (var t in g.Tracks)
                {
                    if (!t.MetadataLoaded) all.Add(t);
                }
            }
            if (all.Count > 0) _filler.Enqueue(all);
        }

        /// <summary>供自检使用：统计当前状态。</summary>
        public IDictionary<string, object> DescribeState()
        {
            var state = new Dictionary<string, object>();
            state["groups"] = _config.Groups.Count;
            state["tracks"] = _group != null ? _group.Tracks.Count : 0;
            state["visible"] = _view.Count;
            state["lyricsPane"] = _config.ShowLyricsPane;
            state["listPane"] = _config.ShowListPane;
            state["hasDevice"] = Player.HasOutputDevice();
            state["playing"] = _player.IsPlaying;
            return state;
        }
    }
}
