using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Lumen.Lyrics;
using Lumen.Models;

namespace Lumen.Media
{
    /// <summary>
    /// 后台元数据补全。
    ///
    /// 性能要点（修复「导入 600 首卡好几秒」）：
    ///   1. 用线程池并行探测，严格限制并发数不超过 MaxParallel。
    ///   2. 后台仅计算结果，切回 UI 线程批量写回 Track 并触发 BatchCompleted。
    ///   3. 界面刷新做节流（最多每 400ms 一次），队列清空后保证补发最终刷新。
    /// </summary>
    public sealed class MetadataFiller
    {
        /// <summary>界面刷新的最小间隔（毫秒）。</summary>
        private const int FlushIntervalMs = 400;

        /// <summary>并行探测的线程数。Media Foundation 不宜开太多。</summary>
        private static readonly int MaxParallel = Math.Max(2, Math.Min(6, Environment.ProcessorCount));

        private readonly struct FilledResult
        {
            public readonly Track Track;
            public readonly double Duration;
            public readonly int HasLyrics;

            public FilledResult(Track track, double duration, int hasLyrics)
            {
                Track = track;
                Duration = duration;
                HasLyrics = hasLyrics;
            }
        }

        private readonly List<Track> _queue = new List<Track>();
        private readonly HashSet<Track> _queuedSet = new HashSet<Track>();
        private readonly List<FilledResult> _completed = new List<FilledResult>();
        private readonly object _gate = new object();
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        private CancellationTokenSource _cts = new CancellationTokenSource();
        private int _active;
        private int _flushQueued;
        private int _pendingForceFlush;
        private long _lastFlushTick = Environment.TickCount64;

        /// <summary>一批元数据补完（在 UI 线程回调，传入本批实际更新的曲目列表）。</summary>
        public event Action<IList<Track>> BatchCompleted;

        /// <summary>全部补完（在 UI 线程回调）。</summary>
        public event Action AllCompleted;

        public MetadataFiller(System.Windows.Threading.Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public int PendingCount
        {
            get { lock (_gate) return _queue.Count + _active; }
        }

        public bool IsRunning
        {
            get { lock (_gate) return _active > 0; }
        }

        /// <summary>加入待补全的曲目（会跳过已补全及已在队列中的曲目）。</summary>
        public void Enqueue(IEnumerable<Track> tracks)
        {
            if (tracks == null) return;

            int toStart = 0;
            CancellationToken token;

            lock (_gate)
            {
                foreach (var t in tracks)
                {
                    if (t == null || t.MetadataLoaded) continue;
                    if (_queuedSet.Add(t))
                    {
                        _queue.Add(t);
                    }
                }

                if (_queue.Count == 0) return;

                // 严格按缺口启动工作线程，不击穿 MaxParallel
                int slots = MaxParallel - _active;
                if (slots > 0)
                {
                    toStart = Math.Min(slots, _queue.Count);
                    _active += toStart;
                }
                token = _cts.Token;
            }

            for (int i = 0; i < toStart; i++)
            {
                ThreadPool.QueueUserWorkItem(delegate { WorkLoop(token); });
            }
        }

        /// <summary>停止当前补全并重置取消令牌（可安全再次 Enqueue）。</summary>
        public void Cancel()
        {
            CancellationTokenSource oldCts;
            lock (_gate)
            {
                oldCts = _cts;
                _cts = new CancellationTokenSource();
                _queue.Clear();
                _queuedSet.Clear();
                _completed.Clear();
            }

            Interlocked.Exchange(ref _pendingForceFlush, 0);

            try { oldCts.Cancel(); }
            catch { }
            try { oldCts.Dispose(); }
            catch { }
        }

        private void WorkLoop(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Track track;
                    lock (_gate)
                    {
                        if (_queue.Count == 0) break;
                        // 从尾部取，减少 list 搬移开销
                        int last = _queue.Count - 1;
                        track = _queue[last];
                        _queue.RemoveAt(last);
                        _queuedSet.Remove(track);
                    }

                    if (track == null || track.MetadataLoaded) continue;

                    var result = ProbeMetadata(track);
                    if (token.IsCancellationRequested) break;

                    bool empty;
                    lock (_gate)
                    {
                        _completed.Add(result);
                        empty = _queue.Count == 0;
                    }

                    Flush(empty, token);
                }
            }
            catch (Exception ex)
            {
                Log.Error("元数据补全线程异常", ex);
            }
            finally
            {
                bool allDone = false;
                lock (_gate)
                {
                    _active--;
                    if (_active < 0) _active = 0;
                    allDone = _active == 0 && _queue.Count == 0;
                }

                if (allDone && !token.IsCancellationRequested)
                {
                    Flush(true, token);
                    OnAllDone(token);
                }
            }
        }

        /// <summary>仅在后台探测数据，不直接修改 UI 绑定的 Track 字段。</summary>
        private static FilledResult ProbeMetadata(Track track)
        {
            double duration = 0;
            try
            {
                if (!string.IsNullOrEmpty(track.Path) && File.Exists(track.Path))
                    duration = MediaScanner.ProbeDuration(track.Path);
            }
            catch (Exception ex)
            {
                Log.Warn("探测时长失败 " + track.Path + ": " + ex.Message);
            }

            int hasLyrics = 0;
            try
            {
                if (!string.IsNullOrEmpty(track.Path))
                    hasLyrics = LyricsFinder.FindLyricsFile(track.Path) != null ? 1 : 0;
            }
            catch { }

            return new FilledResult(track, duration, hasLyrics);
        }

        /// <summary>节流后请求一次 UI 线程批量写回与刷新。</summary>
        private void Flush(bool force, CancellationToken token)
        {
            if (_dispatcher == null || token.IsCancellationRequested) return;

            if (!force)
            {
                long elapsed = Environment.TickCount64 - Volatile.Read(ref _lastFlushTick);
                if (elapsed < FlushIntervalMs) return;
            }

            // 同一时刻只排一个刷新；若已有刷新在途且本次是强制刷新，记录待补发标志
            if (Interlocked.CompareExchange(ref _flushQueued, 1, 0) != 0)
            {
                if (force) Interlocked.Exchange(ref _pendingForceFlush, 1);
                return;
            }

            Volatile.Write(ref _lastFlushTick, Environment.TickCount64);

            _dispatcher.BeginInvoke(new Action(delegate
            {
                List<FilledResult> batch = null;
                try
                {
                    if (!token.IsCancellationRequested)
                    {
                        lock (_gate)
                        {
                            if (_completed.Count > 0)
                            {
                                batch = new List<FilledResult>(_completed);
                                _completed.Clear();
                            }
                        }

                        if (batch != null && batch.Count > 0)
                        {
                            var updated = new List<Track>(batch.Count);
                            for (int i = 0; i < batch.Count; i++)
                            {
                                var item = batch[i];
                                item.Track.Duration = item.Duration;
                                item.Track.HasLyrics = item.HasLyrics;
                                item.Track.MetadataLoaded = true;
                                updated.Add(item.Track);
                            }

                            var handler = BatchCompleted;
                            if (handler != null)
                            {
                                try { handler(updated); }
                                catch (Exception ex) { Log.Warn("刷新元数据失败: " + ex.Message); }
                            }
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _flushQueued, 0);
                }

                // 若在途期间收到强制刷新请求，立即补发一次避免漏刷尾批
                if (Interlocked.Exchange(ref _pendingForceFlush, 0) != 0 && !token.IsCancellationRequested)
                {
                    Flush(true, token);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnAllDone(CancellationToken token)
        {
            if (token.IsCancellationRequested || _dispatcher == null) return;

            _dispatcher.BeginInvoke(new Action(delegate
            {
                if (token.IsCancellationRequested) return;
                var handler = AllCompleted;
                if (handler == null) return;
                try { handler(); }
                catch (Exception ex) { Log.Warn("元数据完成回调失败: " + ex.Message); }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
