using System;
using System.Collections.Generic;
using System.IO;

namespace Lumen.Lyrics
{
    /// <summary>
    /// 查找歌曲同目录下的同名歌词文件。
    /// 支持 LRC / VTT / SRT / ASS / SSA / TXT；扩展名不对时靠内容嗅探。
    /// </summary>
    public static class LyricsFinder
    {
        public static readonly string[] SupportedExtensions =
        {
            ".lrc", ".vtt", ".srt", ".ass", ".ssa", ".txt"
        };

        // 内容嗅探时也允许这些扩展名（有些歌词被存成 .text / .log）
        private static readonly string[] LooseExtensions =
        {
            ".text", ".log", ".sub", ".lyric", ".lyrics", ".lrcx"
        };

        private const int MaxLyricsCacheEntries = 128;
        private const int MaxDirCacheEntries = 64;

        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, LinkedListNode<CachedLyrics>> CacheMap =
            new Dictionary<string, LinkedListNode<CachedLyrics>>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<CachedLyrics> CacheLru = new LinkedList<CachedLyrics>();

        private static readonly object DirCacheGate = new object();
        private static readonly Dictionary<string, LinkedListNode<DirEntry>> DirCacheMap =
            new Dictionary<string, LinkedListNode<DirEntry>>(StringComparer.OrdinalIgnoreCase);
        private static readonly LinkedList<DirEntry> DirCacheLru = new LinkedList<DirEntry>();

        private sealed class CachedLyrics
        {
            public string Key;
            public DateTime Stamp;
            public long Size;
            public double FallbackDuration;
            public LyricsDocument Doc;
        }

        private readonly struct FileEntry
        {
            public readonly string FullPath;
            public readonly string FileName;
            public readonly string Extension;

            public FileEntry(string fullPath)
            {
                FullPath = fullPath;
                FileName = Path.GetFileName(fullPath) ?? string.Empty;
                Extension = (Path.GetExtension(fullPath) ?? string.Empty).ToLowerInvariant();
            }
        }

        private sealed class DirEntry
        {
            public string DirKey;
            public DateTime LastWriteUtc;
            public List<FileEntry> Files;
        }

        /// <summary>清空缓存（重新扫描库时用）。</summary>
        public static void ClearCache()
        {
            lock (CacheGate)
            {
                CacheMap.Clear();
                CacheLru.Clear();
            }
            lock (DirCacheGate)
            {
                DirCacheMap.Clear();
                DirCacheLru.Clear();
            }
        }

        /// <summary>
        /// 为音频文件查找歌词。找不到返回空文档（IsEmpty == true）。
        /// </summary>
        public static LyricsDocument Find(string audioPath, double fallbackDuration)
        {
            if (string.IsNullOrEmpty(audioPath)) return new LyricsDocument();

            string matched = FindLyricsFile(audioPath);
            if (matched == null) return new LyricsDocument();

            DateTime stamp;
            long size;
            try
            {
                var info = new FileInfo(matched);
                if (!info.Exists) return new LyricsDocument();
                stamp = info.LastWriteTimeUtc;
                size = info.Length;
            }
            catch
            {
                return new LyricsDocument();
            }

            lock (CacheGate)
            {
                LinkedListNode<CachedLyrics> node;
                if (CacheMap.TryGetValue(matched, out node))
                {
                    var entry = node.Value;
                    bool sameFile = entry.Stamp == stamp && entry.Size == size;
                    bool needsDurationRefresh = fallbackDuration > 0 && entry.FallbackDuration <= 0;
                    if (sameFile && !needsDurationRefresh)
                    {
                        CacheLru.Remove(node);
                        CacheLru.AddFirst(node);
                        return entry.Doc;
                    }
                }
            }

            LyricsDocument doc;
            try
            {
                var text = TextEncoding.ReadAllText(matched);
                doc = LyricsParser.Parse(text, matched, fallbackDuration);
            }
            catch (Exception ex)
            {
                Log.Warn("读取歌词失败 " + matched + ": " + ex.Message);
                return new LyricsDocument();
            }

            lock (CacheGate)
            {
                LinkedListNode<CachedLyrics> existing;
                if (CacheMap.TryGetValue(matched, out existing))
                {
                    existing.Value.Stamp = stamp;
                    existing.Value.Size = size;
                    existing.Value.FallbackDuration = fallbackDuration;
                    existing.Value.Doc = doc;
                    CacheLru.Remove(existing);
                    CacheLru.AddFirst(existing);
                }
                else
                {
                    var node = new LinkedListNode<CachedLyrics>(new CachedLyrics
                    {
                        Key = matched,
                        Stamp = stamp,
                        Size = size,
                        FallbackDuration = fallbackDuration,
                        Doc = doc
                    });
                    CacheLru.AddFirst(node);
                    CacheMap[matched] = node;

                    while (CacheMap.Count > MaxLyricsCacheEntries && CacheLru.Last != null)
                    {
                        var oldest = CacheLru.Last;
                        CacheLru.RemoveLast();
                        CacheMap.Remove(oldest.Value.Key);
                    }
                }
            }

            return doc;
        }

        /// <summary>返回匹配到的歌词文件路径，没有则返回 null。</summary>
        public static string FindLyricsFile(string audioPath)
        {
            string dir;
            string baseName;
            try
            {
                dir = Path.GetDirectoryName(audioPath);
                baseName = Path.GetFileNameWithoutExtension(audioPath);
            }
            catch { return null; }
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return null;

            var files = GetDirectoryFiles(dir);
            if (files == null || files.Count == 0) return null;

            // 1) 同名 + 受支持的扩展名（按优先级，在内存中一次匹配）
            foreach (var ext in SupportedExtensions)
            {
                string target = baseName + ext;
                for (int i = 0; i < files.Count; i++)
                {
                    if (string.Equals(files[i].FileName, target, StringComparison.OrdinalIgnoreCase))
                        return files[i].FullPath;
                }
            }

            // 2) 全名后多带一段（如 xxx.mpc.lrc、xxx.zh-hans.vtt），仅允许 '.' 作为分隔符
            foreach (var ext in SupportedExtensions)
            {
                var suffixed = FindSuffixed(files, baseName, ext);
                if (suffixed != null) return suffixed;
            }

            // 3) 同名但扩展名可疑：严格校验去扩展名后等于 baseName，再读头部嗅探
            for (int i = 0; i < files.Count; i++)
            {
                var entry = files[i];
                if (Array.IndexOf(SupportedExtensions, entry.Extension) >= 0) continue;
                if (Array.IndexOf(LooseExtensions, entry.Extension) < 0) continue;

                var candidateBase = Path.GetFileNameWithoutExtension(entry.FileName);
                if (!string.Equals(candidateBase, baseName, StringComparison.OrdinalIgnoreCase)) continue;

                if (SniffFile(entry.FullPath)) return entry.FullPath;
            }

            return null;
        }

        /// <summary>获取目录下的文件列表（带 LastWriteTimeUtc 校验的轻量 LRU 目录索引缓存）。</summary>
        private static List<FileEntry> GetDirectoryFiles(string dir)
        {
            string key;
            DateTime lastWriteUtc;
            try
            {
                if (!Directory.Exists(dir)) return null;
                key = Path.GetFullPath(dir);
                lastWriteUtc = Directory.GetLastWriteTimeUtc(key);
            }
            catch
            {
                return null;
            }

            lock (DirCacheGate)
            {
                LinkedListNode<DirEntry> node;
                if (DirCacheMap.TryGetValue(key, out node))
                {
                    if (node.Value.LastWriteUtc == lastWriteUtc)
                    {
                        DirCacheLru.Remove(node);
                        DirCacheLru.AddFirst(node);
                        return node.Value.Files;
                    }
                }
            }

            var list = new List<FileEntry>();
            try
            {
                foreach (var file in Directory.EnumerateFiles(key))
                {
                    list.Add(new FileEntry(file));
                }
            }
            catch
            {
                return null;
            }

            lock (DirCacheGate)
            {
                LinkedListNode<DirEntry> existing;
                if (DirCacheMap.TryGetValue(key, out existing))
                {
                    existing.Value.LastWriteUtc = lastWriteUtc;
                    existing.Value.Files = list;
                    DirCacheLru.Remove(existing);
                    DirCacheLru.AddFirst(existing);
                }
                else
                {
                    var node = new LinkedListNode<DirEntry>(new DirEntry
                    {
                        DirKey = key,
                        LastWriteUtc = lastWriteUtc,
                        Files = list
                    });
                    DirCacheLru.AddFirst(node);
                    DirCacheMap[key] = node;

                    while (DirCacheMap.Count > MaxDirCacheEntries && DirCacheLru.Last != null)
                    {
                        var oldest = DirCacheLru.Last;
                        DirCacheLru.RemoveLast();
                        DirCacheMap.Remove(oldest.Value.DirKey);
                    }
                }
            }

            return list;
        }

        /// <summary>读取文件头判断是不是歌词。</summary>
        private static bool SniffFile(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length == 0 || info.Length > 4 * 1024 * 1024) return false;

                bool truncated = info.Length > 8192;
                int toRead = (int)Math.Min(8192, info.Length);
                var bytes = new byte[toRead];
                int read;
                using (var stream = File.OpenRead(path))
                {
                    read = stream.Read(bytes, 0, bytes.Length);
                }
                if (read <= 0) return false;

                // 发生截断时回退到最后一个换行符，避免劈断多字节 UTF-8 字符
                if (truncated && read == toRead)
                {
                    int lastLf = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
                    if (lastLf > 0)
                    {
                        // 兼容 UTF-16 LE 换行符 0x0A 0x00
                        int safeLen = (lastLf + 1 < read && bytes[lastLf + 1] == 0x00)
                            ? lastLf + 2
                            : lastLf + 1;
                        read = safeLen;
                    }
                }

                if (read < bytes.Length) Array.Resize(ref bytes, read);

                var text = TextEncoding.Decode(bytes);
                if (string.IsNullOrWhiteSpace(text)) return false;

                // 二进制脏数据（比如误配的同名封面）
                int control = 0;
                foreach (var c in text)
                {
                    if (c < 0x09) control++;
                }
                if (text.Length > 0 && control > text.Length * 0.05) return false;

                var doc = LyricsParser.Sniff(text, 0);
                return !doc.IsEmpty;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 找「主名 + .中间段 + 扩展名」的歌词，比如 song.mpc.lrc。
        /// 中间段必须以 '.' 开头且非空；多个候选时取名字最短的，再按名字排序保证稳定。
        /// </summary>
        private static string FindSuffixed(List<FileEntry> files, string baseName, string ext)
        {
            string bestPath = null;
            string bestName = null;

            for (int i = 0; i < files.Count; i++)
            {
                var name = files[i].FileName;
                if (name.Length <= baseName.Length + ext.Length) continue;
                if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;

                // 仅允许 '.' 作为中间分隔符，避免把 song - live.lrc 或 songbook.lrc 误配给 song.mp3
                if (name[baseName.Length] != '.') continue;

                var middleLength = name.Length - baseName.Length - ext.Length;
                if (middleLength <= 1) continue; // 至少包含 '.' 和一个字符

                if (bestPath == null ||
                    name.Length < bestName.Length ||
                    (name.Length == bestName.Length &&
                     string.Compare(name, bestName, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    bestPath = files[i].FullPath;
                    bestName = name;
                }
            }

            return bestPath;
        }
    }
}
