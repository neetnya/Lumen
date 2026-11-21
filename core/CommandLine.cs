using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Lumen
{
    /// <summary>命令行参数（对应 README 的 --selftest / --smoke / 从文件关联打开）。</summary>
    public sealed class CommandLine
    {
        private const int ATTACH_PARENT_PROCESS = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        public readonly List<string> Files = new List<string>();
        public double SelfTestSeconds;

        /// <summary>--smoke：正常启动界面，若干秒后自动退出（实机冒烟用）。</summary>
        public double SmokeSeconds;

        public bool ShowHelp;
        public string DataDirOverride;

        public static CommandLine Parse(string[] args)
        {
            var result = new CommandLine();
            if (args == null) return result;

            bool endOfOptions = false;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.IsNullOrWhiteSpace(a)) continue;

                if (!endOfOptions)
                {
                    if (string.Equals(a, "--", StringComparison.Ordinal))
                    {
                        endOfOptions = true;
                        continue;
                    }

                    if (string.Equals(a, "-h", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a, "-?", StringComparison.Ordinal) ||
                        string.Equals(a, "/?", StringComparison.Ordinal))
                    {
                        result.ShowHelp = true;
                        continue;
                    }

                    if (a.StartsWith("--", StringComparison.Ordinal))
                    {
                        var name = a.Substring(2);
                        string value = null;
                        int eq = name.IndexOf('=');
                        if (eq >= 0)
                        {
                            value = name.Substring(eq + 1);
                            name = name.Substring(0, eq);
                        }

                        switch (name.ToLowerInvariant())
                        {
                            case "selftest":
                                if (value == null && i + 1 < args.Length && TryParsePositiveDouble(args[i + 1], out _))
                                {
                                    value = args[++i];
                                }
                                result.SelfTestSeconds = ParseSeconds(value, 8);
                                break;
                            case "smoke":
                                if (value == null && i + 1 < args.Length && TryParsePositiveDouble(args[i + 1], out _))
                                {
                                    value = args[++i];
                                }
                                result.SmokeSeconds = ParseSeconds(value, 6);
                                break;
                            case "data-dir":
                                if (value == null && i + 1 < args.Length) value = args[++i];
                                result.DataDirOverride = value != null ? value.Trim('"') : null;
                                break;
                            case "help":
                            case "h":
                            case "?":
                                result.ShowHelp = true;
                                break;
                        }
                        continue;
                    }

                    if (a.StartsWith("-", StringComparison.Ordinal) && a.Length > 1) continue;
                }

                // 文件 / 文件夹（可能带引号）
                var path = a.Trim('"');
                if (File.Exists(path) || Directory.Exists(path))
                {
                    try { path = Path.GetFullPath(path); }
                    catch { }
                    result.Files.Add(path);
                }
            }

            return result;
        }

        private static bool TryParsePositiveDouble(string value, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)
                   && double.IsFinite(seconds)
                   && seconds > 0;
        }

        private static double ParseSeconds(string value, double fallback)
        {
            if (TryParsePositiveDouble(value, out double seconds))
            {
                return seconds;
            }
            return fallback;
        }

        public static void PrintHelp()
        {
            var lines = new[]
            {
                "Lumen 音乐 - 本地音乐播放器",
                "用法: Lumen.exe [选项] [--] [音频文件或文件夹 ...]",
                "  --selftest [秒数]    内置自检，不显示界面（默认 8 秒）",
                "  --smoke [秒数]       正常启动界面，若干秒后自动退出（默认 6 秒）",
                "  --data-dir <目录>    指定数据目录（覆盖默认便携目录与 LUMEN_DATA_DIR）",
                "  --help, -h, -?       显示本帮助"
            };

            foreach (var line in lines)
            {
                Log.Info(line);
            }

            try
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
                foreach (var line in lines)
                {
                    Console.WriteLine(line);
                }
                Console.Out.Flush();
            }
            catch { }
        }
    }
}
