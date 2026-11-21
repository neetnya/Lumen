using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lumen
{
    /// <summary>
    /// 单实例。
    /// 用命名管道 + 互斥体：第二个实例把要打开的文件转交给已有窗口，
    /// 并唤醒它。按当前用户隔离，崩溃后不会留下"打不开"的残留。
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private const int MaxMessageBytes = 1024 * 1024;
        private const int ReadTimeoutMs = 5000;

        private static readonly string UserSuffix = ComputeUserSuffix();
        private static readonly string MutexName = "Local\\Lumen.SingleInstance.Mutex." + UserSuffix;
        private static readonly string PipeName = "Lumen.SingleInstance.Pipe." + UserSuffix;

        private readonly object _eventGate = new object();
        private readonly List<IList<string>> _pendingFiles = new List<IList<string>>();
        private bool _pendingActivation;
        private Action<IList<string>> _filesReceived;
        private Action _activationRequested;

        private Mutex _mutex;
        private CancellationTokenSource _cts;
        private Task _listenerTask;
        private volatile bool _running;
        private bool _owned;
        private int _disposed;

        /// <summary>收到另一个实例转交的文件（若订阅前已收到消息会立即补发）。</summary>
        public event Action<IList<string>> FilesReceived
        {
            add
            {
                List<IList<string>> replay = null;
                lock (_eventGate)
                {
                    _filesReceived += value;
                    if (value != null && _pendingFiles.Count > 0)
                    {
                        replay = new List<IList<string>>(_pendingFiles);
                        _pendingFiles.Clear();
                    }
                }

                if (replay != null)
                {
                    foreach (var batch in replay)
                    {
                        try { value(batch); }
                        catch (Exception ex) { Log.Warn("补发单实例文件失败: " + ex.Message); }
                    }
                }
            }
            remove
            {
                lock (_eventGate)
                {
                    _filesReceived -= value;
                }
            }
        }

        /// <summary>另一个实例请求把窗口叫到前台（若订阅前已收到请求会立即补发）。</summary>
        public event Action ActivationRequested
        {
            add
            {
                bool replay = false;
                lock (_eventGate)
                {
                    _activationRequested += value;
                    if (value != null && _pendingActivation)
                    {
                        _pendingActivation = false;
                        replay = true;
                    }
                }

                if (replay)
                {
                    try { value(); }
                    catch (Exception ex) { Log.Warn("补发单实例激活请求失败: " + ex.Message); }
                }
            }
            remove
            {
                lock (_eventGate)
                {
                    _activationRequested -= value;
                }
            }
        }

        private static string ComputeUserSuffix()
        {
            string raw = null;
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    if (identity != null && identity.User != null)
                    {
                        raw = identity.User.Value;
                    }
                }
            }
            catch { }

            if (string.IsNullOrEmpty(raw))
            {
                raw = Environment.UserDomainName + "\\" + Environment.UserName;
            }

            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public bool TryAcquire()
        {
            try
            {
                bool createdNew;
                _mutex = new Mutex(true, MutexName, out createdNew);
                if (!createdNew)
                {
                    _mutex.Dispose();
                    _mutex = null;
                    return false;
                }

                _owned = true;
                StartListener();
                return true;
            }
            catch (Exception ex)
            {
                // 拿不到互斥体也不要阻止启动（宁可多开也不能打不开）
                Log.Warn("单实例检查失败，按多实例继续: " + ex.Message);
                return true;
            }
        }

        private void StartListener()
        {
            _running = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _listenerTask = Task.Run(() => ListenLoopAsync(token), token);
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try
                {
                    using (var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
                    {
                        await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                        var message = await ReadLimitedMessageAsync(server, ct).ConfigureAwait(false);
                        if (string.IsNullOrEmpty(message)) continue;

                        HandleMessage(message);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running && !ct.IsCancellationRequested)
                    {
                        Log.Warn("单实例监听异常: " + ex.Message);
                        try { await Task.Delay(200, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
        }

        private static async Task<string> ReadLimitedMessageAsync(Stream stream, CancellationToken ct)
        {
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(ReadTimeoutMs);
                using (var ms = new MemoryStream())
                {
                    var buffer = new byte[4096];
                    int total = 0;

                    while (true)
                    {
                        int remaining = MaxMessageBytes - total;
                        if (remaining <= 0)
                        {
                            Log.Warn("单实例管道消息超过 1MB 上限，已忽略");
                            return null;
                        }

                        int toRead = Math.Min(buffer.Length, remaining);
                        int read = await stream.ReadAsync(buffer, 0, toRead, timeoutCts.Token).ConfigureAwait(false);
                        if (read <= 0) break;

                        ms.Write(buffer, 0, read);
                        total += read;
                    }

                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }

        private void HandleMessage(string message)
        {
            using (var reader = new StringReader(message))
            {
                var first = reader.ReadLine();
                if (string.Equals(first, "ACTIVATE", StringComparison.Ordinal))
                {
                    Action handler;
                    lock (_eventGate)
                    {
                        handler = _activationRequested;
                        if (handler == null) _pendingActivation = true;
                    }
                    if (handler != null) handler();
                }
                else if (string.Equals(first, "OPEN", StringComparison.Ordinal))
                {
                    var files = new List<string>();
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        var f = line.Trim();
                        if (f.Length > 0) files.Add(f);
                    }

                    if (files.Count > 0)
                    {
                        Action<IList<string>> handler;
                        lock (_eventGate)
                        {
                            handler = _filesReceived;
                            if (handler == null) _pendingFiles.Add(files);
                        }
                        if (handler != null) handler(files);
                    }
                }
            }
        }

        /// <summary>把文件转交给已有实例。</summary>
        public bool SendFilesToExisting(IList<string> files)
        {
            if (files == null || files.Count == 0) return SendActivate();

            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly))
                {
                    client.Connect(2000);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        writer.WriteLine("OPEN");
                        foreach (var f in files) writer.WriteLine(f);
                        writer.Flush();
                    }
                }
                Log.Info("已把 " + files.Count + " 个文件转交给已有窗口");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("转交文件失败: " + ex.Message);
                return false;
            }
        }

        public bool SendActivate()
        {
            try
            {
                using (var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly))
                {
                    client.Connect(2000);
                    using (var writer = new StreamWriter(client, new UTF8Encoding(false)))
                    {
                        writer.WriteLine("ACTIVATE");
                        writer.Flush();
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("唤醒已有窗口失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 把主窗口带到前台并激活。
        /// 仅在最小化时调用 SW_RESTORE，避免破坏最大化窗口，并保留原有 Topmost 状态。
        /// </summary>
        public static void BringToFront(System.Windows.Window window)
        {
            if (window == null) return;
            try
            {
                var helper = new System.Windows.Interop.WindowInteropHelper(window);
                if (helper.Handle == IntPtr.Zero) return;

                var hwnd = helper.Handle;
                bool wasMinimized = window.WindowState == System.Windows.WindowState.Minimized;

                if (wasMinimized)
                {
                    window.WindowState = System.Windows.WindowState.Normal;
                    Native.Win32.ShowWindow(hwnd, 9 /*SW_RESTORE*/);
                }

                window.Show();
                Native.Win32.SetForegroundWindow(hwnd);
                window.Activate();

                bool previousTopmost = window.Topmost;
                if (!previousTopmost)
                {
                    window.Topmost = true;
                    window.Topmost = false;
                }
                window.Focus();
            }
            catch (Exception ex)
            {
                Log.Warn("前置窗口失败: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _running = false;
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { }
                try { _cts.Dispose(); } catch { }
                _cts = null;
            }

            if (_mutex != null)
            {
                try
                {
                    if (_owned) _mutex.ReleaseMutex();
                }
                catch { }
                try { _mutex.Dispose(); } catch { }
                _mutex = null;
            }
        }
    }
}
