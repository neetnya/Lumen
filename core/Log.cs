using System;
using System.IO;
using System.Text;
using System.Threading;

namespace Lumen
{
    /// <summary>崩溃日志：写到数据目录的 lumen.log。</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static long _currentSize = -1;

        public static string Path
        {
            get
            {
                lock (Gate)
                {
                    return GetPathLocked();
                }
            }
        }

        internal static void ResetPath()
        {
            lock (Gate)
            {
                _path = null;
                _currentSize = -1;
            }
        }

        private static string GetPathLocked()
        {
            if (_path == null)
            {
                try { _path = System.IO.Path.Combine(Paths.DataDir, "lumen.log"); }
                catch { _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumen.log"); }
                _currentSize = -1;
            }
            return _path;
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message, Exception ex = null)
        {
            var text = ex == null ? message : message + " :: " + ex.ToString();
            Write("ERROR", text);
        }

        private const long MaxLogBytes = 2 * 1024 * 1024;

        private static void RotateIfNeededLocked(string path, int incomingBytes)
        {
            try
            {
                if (_currentSize < 0)
                {
                    var info = new FileInfo(path);
                    _currentSize = info.Exists ? info.Length : 0;
                }

                if (_currentSize + incomingBytes < MaxLogBytes) return;

                var bak = path + ".1";
                if (File.Exists(bak)) File.Delete(bak);
                if (File.Exists(path)) File.Move(path, bak);
                _currentSize = 0;
            }
            catch
            {
                _currentSize = -1;
            }
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    var path = GetPathLocked();
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    var line = string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                        DateTime.Now, level, message, Environment.NewLine);
                    var bytes = Encoding.UTF8.GetBytes(line);

                    RotateIfNeededLocked(path, bytes.Length);

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                            {
                                fs.Write(bytes, 0, bytes.Length);
                            }

                            if (_currentSize < 0) _currentSize = bytes.Length;
                            else _currentSize += bytes.Length;
                            return;
                        }
                        catch (IOException) when (attempt < 2)
                        {
                            Thread.Sleep(15);
                        }
                    }
                }
            }
            catch { /* 日志失败不能影响播放 */ }
        }
    }
}
