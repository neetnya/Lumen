using System;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Lumen.Audio
{
    /// <summary>
    /// 基于 NAudio 的播放核心。
    /// 解码：MediaFoundationReader（MP3 / AAC / M4A / WMA / MP4 容器）
    ///       + AudioFileReader（WAV / AIFF / 以及 MF 不可用时的回退）
    /// 输出：WASAPI 共享模式（WasapiOut），构造或初始化失败时回退 WaveOutEvent。
    /// 播放模式：顺序播放（到底停止）/ 列表循环 / 单曲循环。
    /// </summary>
    public sealed class Player : IDisposable
    {
        public enum PlayMode
        {
            Sequential = 0,   // 顺序播放（到底停止）
            RepeatList = 1,   // 列表循环
            RepeatOne = 2     // 单曲循环
        }

        private sealed class EofTrackingSampleProvider : ISampleProvider
        {
            private readonly ISampleProvider _source;
            private volatile bool _reachedEof;

            public EofTrackingSampleProvider(ISampleProvider source)
            {
                _source = source;
            }

            public WaveFormat WaveFormat { get { return _source.WaveFormat; } }

            public bool ReachedEof { get { return _reachedEof; } }

            public void Reset()
            {
                _reachedEof = false;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int read = _source.Read(buffer, offset, count);
                if (read == 0)
                {
                    _reachedEof = true;
                }
                return read;
            }
        }

        private readonly object _gate = new object();

        private IWavePlayer _output;
        private WaveStream _reader;
        private VolumeSampleProvider _volumeProvider;
        private EofTrackingSampleProvider _eofTracker;
        private EventHandler<StoppedEventArgs> _stoppedHandler;

        private string _currentPath;
        private double _lastPositionSeconds;
        private double _lastDurationSeconds;
        private bool _outputFaulted;

        private long _sessionId;
        private volatile bool _stoppingIntentionally;
        private volatile bool _disposed;
        private float _volume = 0.8f;
        private bool _muted;
        private PlayMode _mode = PlayMode.Sequential;

        /// <summary>当前曲目自然播放结束（用于自动切下一首）。</summary>
        public event Action PlaybackEnded;

        /// <summary>播放失败（解码不了 / 设备异常）。</summary>
        public event Action<string> PlaybackFailed;

        public event Action PlaybackStarted;

        // ------------------------------------------------------------------
        // 状态
        // ------------------------------------------------------------------

        public string CurrentPath
        {
            get
            {
                lock (_gate)
                {
                    return _currentPath;
                }
            }
        }

        public bool HasTrack
        {
            get
            {
                lock (_gate)
                {
                    return !string.IsNullOrEmpty(_currentPath) && (_reader != null || _outputFaulted);
                }
            }
        }

        public bool IsPlaying
        {
            get
            {
                lock (_gate)
                {
                    return _output != null && _output.PlaybackState == PlaybackState.Playing;
                }
            }
        }

        public bool IsPaused
        {
            get
            {
                lock (_gate)
                {
                    return _output != null && _output.PlaybackState == PlaybackState.Paused;
                }
            }
        }

        /// <summary>当前播放位置（秒）。</summary>
        public double PositionSeconds
        {
            get
            {
                lock (_gate)
                {
                    if (_reader != null)
                    {
                        try
                        {
                            double pos = _reader.CurrentTime.TotalSeconds;
                            if (double.IsFinite(pos) && pos >= 0)
                            {
                                _lastPositionSeconds = pos;
                                return pos;
                            }
                        }
                        catch { }
                    }
                    return _outputFaulted ? _lastPositionSeconds : 0;
                }
            }
        }

        /// <summary>总时长（秒）；未知返回 0。</summary>
        public double DurationSeconds
        {
            get
            {
                lock (_gate)
                {
                    if (_reader != null)
                    {
                        try
                        {
                            double total = _reader.TotalTime.TotalSeconds;
                            if (total > 0 && double.IsFinite(total))
                            {
                                _lastDurationSeconds = total;
                                return total;
                            }
                        }
                        catch { }
                    }
                    return _outputFaulted ? _lastDurationSeconds : 0;
                }
            }
        }

        public PlayMode Mode
        {
            get
            {
                lock (_gate)
                {
                    return _mode;
                }
            }
            set
            {
                lock (_gate)
                {
                    _mode = value;
                }
            }
        }

        public float Volume
        {
            get
            {
                lock (_gate)
                {
                    return _volume;
                }
            }
            set
            {
                lock (_gate)
                {
                    float clamped = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : _volume;
                    _volume = clamped;
                    if (_volumeProvider != null)
                    {
                        _volumeProvider.Volume = _muted ? 0f : _volume;
                    }
                }
            }
        }

        public bool Muted
        {
            get
            {
                lock (_gate)
                {
                    return _muted;
                }
            }
            set
            {
                lock (_gate)
                {
                    _muted = value;
                    if (_volumeProvider != null)
                    {
                        _volumeProvider.Volume = _muted ? 0f : _volume;
                    }
                }
            }
        }

        /// <summary>音频输出设备是否可用。</summary>
        public static bool HasOutputDevice()
        {
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                using (var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    if (device != null && device.State == DeviceState.Active)
                    {
                        return true;
                    }
                }
            }
            catch { }

            try
            {
                return WaveOut.DeviceCount > 0;
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 打开 / 播放 / 暂停 / 停止
        // ------------------------------------------------------------------

        /// <summary>
        /// 打开文件并开始播放。startSeconds &gt; 0 时从该位置开始。
        /// 失败会触发 PlaybackFailed 并返回 false。
        /// </summary>
        public bool Play(string path, double startSeconds)
        {
            string failedMessage = null;
            bool startedOk = false;

            lock (_gate)
            {
                if (_disposed) return false;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    failedMessage = "文件不存在: " + path;
                }
                else
                {
                    try
                    {
                        CloseInternal();

                        long session = ++_sessionId;
                        _stoppingIntentionally = false;
                        _outputFaulted = false;
                        _lastPositionSeconds = 0;
                        _lastDurationSeconds = 0;

                        _reader = OpenReader(path);
                        if (_reader == null)
                        {
                            failedMessage = "无法解码: " + Path.GetFileName(path);
                        }
                        else
                        {
                            ApplyStartPosition(_reader, startSeconds);
                            IWaveProvider pipeline = BuildPipeline(_reader);
                            var output = CreateAndInitOutput(pipeline);

                            EventHandler<StoppedEventArgs> stoppedHandler = (s, e) => OnPlaybackStopped(session, s, e);
                            output.PlaybackStopped += stoppedHandler;

                            _output = output;
                            _stoppedHandler = stoppedHandler;
                            _currentPath = path;
                            UpdateCachedTimesUnsafe();

                            _output.Play();
                            startedOk = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("播放失败: " + path, ex);
                        CloseInternal();
                        _currentPath = null;
                        _outputFaulted = false;
                        failedMessage = Describe(ex, path);
                    }
                }
            }

            if (failedMessage != null)
            {
                RaiseFailed(failedMessage);
                return false;
            }

            if (startedOk)
            {
                var started = PlaybackStarted;
                if (started != null) started();
                return true;
            }

            return false;
        }

        /// <summary>只加载不播放（用于恢复上次进度）。</summary>
        public bool Load(string path, double startSeconds)
        {
            lock (_gate)
            {
                if (_disposed) return false;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

                try
                {
                    CloseInternal();

                    long session = ++_sessionId;
                    _stoppingIntentionally = false;
                    _outputFaulted = false;
                    _lastPositionSeconds = 0;
                    _lastDurationSeconds = 0;

                    _reader = OpenReader(path);
                    if (_reader == null) return false;

                    ApplyStartPosition(_reader, startSeconds);
                    IWaveProvider pipeline = BuildPipeline(_reader);
                    var output = CreateAndInitOutput(pipeline);

                    EventHandler<StoppedEventArgs> stoppedHandler = (s, e) => OnPlaybackStopped(session, s, e);
                    output.PlaybackStopped += stoppedHandler;

                    _output = output;
                    _stoppedHandler = stoppedHandler;
                    _currentPath = path;
                    UpdateCachedTimesUnsafe();
                    return true;
                }
                catch (Exception ex)
                {
                    Log.Error("加载失败: " + path, ex);
                    CloseInternal();
                    _currentPath = null;
                    _outputFaulted = false;
                    return false;
                }
            }
        }

        public void Pause()
        {
            lock (_gate)
            {
                if (_disposed || _output == null) return;
                try
                {
                    if (_output.PlaybackState == PlaybackState.Playing)
                    {
                        _output.Pause();
                        UpdateCachedTimesUnsafe();
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("暂停失败: " + ex.Message);
                }
            }
        }

        public void Resume()
        {
            string rebuildPath = null;
            double rebuildPosition = 0;

            lock (_gate)
            {
                if (_disposed) return;

                if (_output != null)
                {
                    try
                    {
                        if (_output.PlaybackState != PlaybackState.Playing)
                        {
                            _output.Play();
                        }
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("继续播放失败，尝试重建输出: " + ex.Message);
                        UpdateCachedTimesUnsafe();
                        DisposeOutputOnlyUnsafe();
                        _outputFaulted = true;
                    }
                }

                if (!string.IsNullOrEmpty(_currentPath))
                {
                    rebuildPath = _currentPath;
                    rebuildPosition = _lastPositionSeconds;
                }
            }

            if (!string.IsNullOrEmpty(rebuildPath))
            {
                Play(rebuildPath, rebuildPosition);
            }
        }

        public void TogglePlayPause()
        {
            bool shouldPause;
            lock (_gate)
            {
                if (_disposed) return;
                if (string.IsNullOrEmpty(_currentPath) || (_reader == null && !_outputFaulted)) return;
                shouldPause = _output != null && _output.PlaybackState == PlaybackState.Playing;
            }

            if (shouldPause) Pause();
            else Resume();
        }

        public void Stop()
        {
            lock (_gate)
            {
                CloseInternal();
                _currentPath = null;
                _outputFaulted = false;
                _lastPositionSeconds = 0;
                _lastDurationSeconds = 0;
            }
        }

        /// <summary>跳转到指定秒数。</summary>
        public void Seek(double seconds)
        {
            lock (_gate)
            {
                if (_disposed) return;
                double clampedSeconds = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;

                if (_reader == null)
                {
                    if (_outputFaulted)
                    {
                        _lastPositionSeconds = _lastDurationSeconds > 0
                            ? Math.Min(clampedSeconds, _lastDurationSeconds)
                            : clampedSeconds;
                    }
                    return;
                }

                try
                {
                    var total = _reader.TotalTime;
                    var target = TimeSpan.FromSeconds(clampedSeconds);
                    if (total > TimeSpan.Zero && target > total) target = total;
                    _reader.CurrentTime = target;
                    _lastPositionSeconds = target.TotalSeconds;
                    if (_eofTracker != null) _eofTracker.Reset();
                }
                catch (Exception ex)
                {
                    Log.Warn("跳转失败: " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // 内部
        // ------------------------------------------------------------------

        private static void ApplyStartPosition(WaveStream reader, double startSeconds)
        {
            if (reader == null || !double.IsFinite(startSeconds) || startSeconds <= 0.05) return;
            try
            {
                var target = TimeSpan.FromSeconds(startSeconds);
                if (reader.TotalTime > TimeSpan.Zero && target < reader.TotalTime)
                {
                    reader.CurrentTime = target;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("设置起始播放位置失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 构建 ISampleProvider 管线：
        /// WaveStream -> ISampleProvider -> (多声道下混至立体声) -> VolumeSampleProvider -> EofTrackingSampleProvider -> SampleToWaveProvider。
        /// </summary>
        private IWaveProvider BuildPipeline(WaveStream reader)
        {
            ISampleProvider sampleProvider = reader.ToSampleProvider();

            if (sampleProvider.WaveFormat.Channels > 2)
            {
                sampleProvider = new MultiplexingSampleProvider(new ISampleProvider[] { sampleProvider }, 2);
            }

            _volumeProvider = new VolumeSampleProvider(sampleProvider)
            {
                Volume = _muted ? 0f : _volume
            };

            _eofTracker = new EofTrackingSampleProvider(_volumeProvider);
            return new SampleToWaveProvider(_eofTracker);
        }

        /// <summary>
        /// 按扩展名选择解码器。MP3/AAC/M4A/WMA/MP4 走 Media Foundation，
        /// WAV/AIFF 走 NAudio 自带解析；两者都失败时互相回退。
        /// </summary>
        private static WaveStream OpenReader(string path)
        {
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            bool preferManaged = ext == ".wav" || ext == ".aiff" || ext == ".aif" || ext == ".aifc";

            if (preferManaged)
            {
                try { return new AudioFileReader(path); }
                catch (Exception ex) { Log.Warn("AudioFileReader 失败，回退 MediaFoundation: " + ex.Message); }
                try { return new MediaFoundationReader(path); }
                catch (Exception ex) { Log.Error("MediaFoundation 也失败: " + ex.Message); return null; }
            }

            try { return new MediaFoundationReader(path); }
            catch (Exception ex) { Log.Warn("MediaFoundation 失败，回退 AudioFileReader: " + ex.Message); }
            try { return new AudioFileReader(path); }
            catch (Exception ex) { Log.Error("解码失败: " + ex.Message); return null; }
        }

        /// <summary>优先 WASAPI 共享模式，构造或 Init 失败时释放并回退 WaveOutEvent。</summary>
        private static IWavePlayer CreateAndInitOutput(IWaveProvider provider)
        {
            WasapiOut wasapi = null;
            try
            {
                wasapi = new WasapiOut(AudioClientShareMode.Shared, 100);
                wasapi.Init(provider);
                Log.Info("音频输出: WASAPI 共享模式");
                return wasapi;
            }
            catch (Exception ex)
            {
                if (wasapi != null)
                {
                    try { wasapi.Dispose(); } catch { }
                }
                Log.Warn("WASAPI 不可用或初始化失败，回退 WaveOut: " + ex.Message);
            }

            var waveOut = new WaveOutEvent { DesiredLatency = 200 };
            try
            {
                waveOut.Init(provider);
                Log.Info("音频输出: WaveOut 回退模式");
                return waveOut;
            }
            catch
            {
                try { waveOut.Dispose(); } catch { }
                throw;
            }
        }

        private void OnPlaybackStopped(long sessionId, object sender, StoppedEventArgs e)
        {
            bool raiseEnded = false;
            string failedMessage = null;

            lock (_gate)
            {
                if (_disposed || _stoppingIntentionally) return;
                if (sessionId != _sessionId || !ReferenceEquals(sender, _output)) return;

                bool reachedEnd = _eofTracker != null && _eofTracker.ReachedEof;
                Exception stopException = e != null ? e.Exception : null;

                if (!reachedEnd)
                {
                    UpdateCachedTimesUnsafe();
                    DisposeOutputOnlyUnsafe();
                    _outputFaulted = true;

                    if (stopException != null)
                    {
                        Log.Error("播放设备或播放流中断；已释放失效输出，不自动切歌", stopException);
                    }
                    else
                    {
                        Log.Warn("播放流在歌曲结束前停止；已释放失效输出，不自动切歌");
                    }
                    return;
                }

                if (stopException != null)
                {
                    Log.Error("曲目结束时播放流发生异常", stopException);
                    UpdateCachedTimesUnsafe();
                    DisposeOutputOnlyUnsafe();
                    _outputFaulted = true;
                    failedMessage = Describe(stopException, _currentPath);
                }
                else
                {
                    raiseEnded = true;
                }
            }

            if (failedMessage != null)
            {
                RaiseFailed(failedMessage);
            }
            else if (raiseEnded)
            {
                var handler = PlaybackEnded;
                if (handler != null) handler();
            }
        }

        private void UpdateCachedTimesUnsafe()
        {
            if (_reader == null) return;
            try
            {
                double pos = _reader.CurrentTime.TotalSeconds;
                if (double.IsFinite(pos) && pos >= 0) _lastPositionSeconds = pos;
            }
            catch { }

            try
            {
                double total = _reader.TotalTime.TotalSeconds;
                if (total > 0 && double.IsFinite(total)) _lastDurationSeconds = total;
            }
            catch { }
        }

        private void DisposeOutputOnlyUnsafe()
        {
            if (_output != null)
            {
                try
                {
                    if (_stoppedHandler != null) _output.PlaybackStopped -= _stoppedHandler;
                }
                catch { }
                try { _output.Dispose(); } catch { }
                _output = null;
            }
            _stoppedHandler = null;
        }

        private void RaiseFailed(string message)
        {
            var handler = PlaybackFailed;
            if (handler != null) handler(message);
        }

        private static string Describe(Exception ex, string path)
        {
            var name = string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path) + " ";
            return name + "播放失败：" + ex.Message;
        }

        private void CloseInternal()
        {
            _stoppingIntentionally = true;
            _sessionId++;

            if (_output != null)
            {
                try
                {
                    if (_stoppedHandler != null) _output.PlaybackStopped -= _stoppedHandler;
                    _output.Stop();
                }
                catch { }
                try { _output.Dispose(); } catch { }
                _output = null;
            }
            _stoppedHandler = null;

            if (_reader != null)
            {
                try { _reader.Dispose(); } catch { }
                _reader = null;
            }

            _volumeProvider = null;
            _eofTracker = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                CloseInternal();
                _currentPath = null;
                _outputFaulted = false;
                _lastPositionSeconds = 0;
                _lastDurationSeconds = 0;
            }
        }
    }
}
