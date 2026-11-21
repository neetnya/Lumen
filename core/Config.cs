using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Lumen.Models;

namespace Lumen
{
    /// <summary>
    /// 配置持久化。
    /// 便携式：存在程序旁的 data\library.json，包含分组、播放模式、音量、
    /// 窗口位置、桌面歌词设置、各分组独立进度、打乱后的顺序。
    /// </summary>
    public sealed class Config
    {
        // ---- 播放 ----
        public int VolumePercent = 80;
        public bool Muted;
        public int PlayMode;                       // 0 顺序 / 1 列表循环 / 2 单曲循环

        // ---- 界面 ----
        public bool ShowLyricsPane = true;
        public bool ShowListPane = true;

        public double WindowLeft = double.NaN;
        public double WindowTop = double.NaN;
        public double WindowWidth = 1180;
        public double WindowHeight = 720;
        public bool WindowMaximized;

        // ---- 列表 ----
        public string SortMode = "added";          // added / title / titleDesc / duration / durationDesc / format / path
        public string CurrentGroupId;

        // ---- 桌面歌词 ----
        public bool DesktopLyricsVisible;
        public bool DesktopLyricsLocked;
        public double DesktopLyricsLeft = double.NaN;
        public double DesktopLyricsTop = double.NaN;
        public double DesktopLyricsHeight = 92;
        public double DesktopLyricsFontScale = 1.0;   // 0.5 ~ 3.0

        // ---- 库 ----
        public List<Group> Groups = new List<Group>();

        /// <summary>首次启动未建分组时自动创建。</summary>
        public Group EnsureGroup()
        {
            if (Groups == null) Groups = new List<Group>();
            Groups.RemoveAll(g => g == null);
            if (Groups.Count == 0)
            {
                Groups.Add(new Group { Name = "默认分组" });
            }
            if (string.IsNullOrEmpty(CurrentGroupId) ||
                Groups.Find(g => g.Id == CurrentGroupId) == null)
            {
                CurrentGroupId = Groups[0].Id;
            }
            return CurrentGroup;
        }

        public Group CurrentGroup
        {
            get
            {
                if (Groups == null) return null;
                var g = Groups.Find(x => x != null && x.Id == CurrentGroupId);
                return g ?? (Groups.Count > 0 ? Groups[0] : null);
            }
        }

        public Group FindGroup(string id)
        {
            if (Groups == null || string.IsNullOrEmpty(id)) return null;
            return Groups.Find(g => g != null && g.Id == id);
        }

        public Group FindGroupByName(string name)
        {
            if (Groups == null || string.IsNullOrEmpty(name)) return null;
            return Groups.Find(g => g != null && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------
        // 读写
        // ------------------------------------------------------------------

        private static readonly object Gate = new object();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            IncludeFields = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static Config Load()
        {
            return Load(Paths.ConfigFile);
        }

        /// <summary>从指定路径加载配置（供自测隔离使用）。</summary>
        public static Config Load(string customPath)
        {
            lock (Gate)
            {
                if (string.IsNullOrWhiteSpace(customPath))
                {
                    var fallback = new Config();
                    fallback.EnsureGroup();
                    return fallback;
                }

                var backupPath = customPath + ".bak";

                if (!File.Exists(customPath))
                {
                    if (File.Exists(backupPath))
                    {
                        if (TryDeserializeFile(backupPath, out var recovered, out var backupEx))
                        {
                            Log.Warn("主配置文件缺失，已从备份恢复: " + backupPath);
                            Normalize(recovered);
                            return recovered;
                        }
                        if (backupEx != null) Log.Error("读取备份配置失败: " + backupPath, backupEx);
                    }

                    var fresh = new Config();
                    fresh.EnsureGroup();
                    return fresh;
                }

                if (TryDeserializeFile(customPath, out var config, out var loadEx))
                {
                    Normalize(config);
                    return config;
                }

                Log.Error("读取配置失败，准备保留损坏现场并尝试从备份恢复: " + customPath, loadEx);
                PreserveCorruptFile(customPath);

                if (File.Exists(backupPath))
                {
                    if (TryDeserializeFile(backupPath, out var recovered, out var backupEx))
                    {
                        Log.Warn("已从备份恢复配置: " + backupPath);
                        Normalize(recovered);
                        return recovered;
                    }
                    Log.Error("从备份恢复配置也失败: " + backupPath, backupEx);
                }

                var defaultConfig = new Config();
                defaultConfig.EnsureGroup();
                return defaultConfig;
            }
        }

        private static bool TryDeserializeFile(string path, out Config config, out Exception error)
        {
            config = null;
            error = null;
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(text))
                {
                    error = new InvalidDataException("配置文件内容为空");
                    return false;
                }

                var parsed = JsonSerializer.Deserialize<Config>(text, JsonOptions);
                if (parsed == null)
                {
                    error = new InvalidDataException("配置文件反序列化结果为 null");
                    return false;
                }

                config = parsed;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        private static void PreserveCorruptFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                var corruptPath = path + ".corrupt." + stamp;
                if (File.Exists(corruptPath))
                {
                    corruptPath = path + ".corrupt." + stamp + "." + Guid.NewGuid().ToString("N").Substring(0, 6);
                }
                File.Move(path, corruptPath);
                Log.Warn("已保留损坏配置文件: " + corruptPath);
            }
            catch (Exception ex)
            {
                Log.Warn("重命名损坏配置文件失败: " + ex.Message);
            }
        }

        private static void Normalize(Config config)
        {
            config.VolumePercent = Math.Clamp(config.VolumePercent, 0, 100);
            config.PlayMode = (config.PlayMode >= 0 && config.PlayMode <= 2) ? config.PlayMode : 0;

            config.WindowWidth = double.IsFinite(config.WindowWidth)
                ? Math.Clamp(config.WindowWidth, 400, 7680)
                : 1180;
            config.WindowHeight = double.IsFinite(config.WindowHeight)
                ? Math.Clamp(config.WindowHeight, 300, 4320)
                : 720;

            if (!double.IsFinite(config.WindowLeft)) config.WindowLeft = double.NaN;
            if (!double.IsFinite(config.WindowTop)) config.WindowTop = double.NaN;
            if (!double.IsFinite(config.DesktopLyricsLeft)) config.DesktopLyricsLeft = double.NaN;
            if (!double.IsFinite(config.DesktopLyricsTop)) config.DesktopLyricsTop = double.NaN;

            config.DesktopLyricsFontScale = double.IsFinite(config.DesktopLyricsFontScale)
                ? Math.Clamp(config.DesktopLyricsFontScale, 0.5, 3.0)
                : 1.0;
            config.DesktopLyricsHeight = double.IsFinite(config.DesktopLyricsHeight)
                ? Math.Clamp(config.DesktopLyricsHeight, 48, 400)
                : 92;

            if (config.Groups == null) config.Groups = new List<Group>();
            config.Groups.RemoveAll(g => g == null);
            foreach (var g in config.Groups)
            {
                if (g.Tracks == null) g.Tracks = new List<Track>();
                g.Tracks.RemoveAll(t => t == null);
                if (string.IsNullOrEmpty(g.Id)) g.Id = Guid.NewGuid().ToString("N");
            }

            config.EnsureGroup();
        }

        /// <summary>保存。写唯一临时文件并刷盘，再原子替换并保留 .bak 备份。</summary>
        public bool Save()
        {
            return SaveInternal(Paths.ConfigFile);
        }

        /// <summary>保存到指定路径（供自测隔离使用）。</summary>
        public void Save(string customPath)
        {
            SaveInternal(customPath);
        }

        private bool SaveInternal(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;

            try
            {
                var json = JsonSerializer.Serialize(this, JsonOptions);
                WriteJsonAtomic(path, json);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("保存配置失败", ex);
                return false;
            }
        }

        private static void WriteJsonAtomic(string path, string json)
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var backup = path + ".bak";

                try
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush(flushToDisk: true);
                    }

                    if (File.Exists(path))
                    {
                        File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                    }
                    else
                    {
                        File.Move(temp, path);
                    }
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        try { File.Delete(temp); } catch { }
                    }
                }
            }
        }

        private readonly object _asyncSaveGate = new object();
        private string _pendingJson;
        private string _pendingPath;
        private bool _dirty;
        private bool _saving;

        /// <summary>
        /// 后台保存：先在调用线程完成 JSON 序列化快照，再投递纯 I/O 落盘。
        /// 写盘期间到达的新修改会标记 _dirty 并触发下一轮落盘，不丢更新。
        /// </summary>
        public void SaveAsync()
        {
            string json;
            string path;
            try
            {
                path = Paths.ConfigFile;
                json = JsonSerializer.Serialize(this, JsonOptions);
            }
            catch (Exception ex)
            {
                Log.Warn("后台保存配置序列化失败: " + ex.Message);
                return;
            }

            bool startWorker = false;
            lock (_asyncSaveGate)
            {
                _pendingJson = json;
                _pendingPath = path;
                _dirty = true;
                if (!_saving)
                {
                    _saving = true;
                    startWorker = true;
                }
            }

            if (!startWorker) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                while (true)
                {
                    string snapshot;
                    string targetPath;
                    lock (_asyncSaveGate)
                    {
                        if (!_dirty)
                        {
                            _saving = false;
                            return;
                        }
                        snapshot = _pendingJson;
                        targetPath = _pendingPath;
                        _dirty = false;
                    }

                    try
                    {
                        WriteJsonAtomic(targetPath, snapshot);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("后台保存配置失败: " + ex.Message);
                    }
                }
            });
        }
    }
}
