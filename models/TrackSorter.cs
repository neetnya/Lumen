using System;
using System.Collections.Generic;
using System.Globalization;
using Lumen.Models;

namespace Lumen
{
    /// <summary>
    /// 列表排序（对应 README）：
    /// 标题 ↑↓ / 时长 ↑↓ / 最近添加 / 文件格式 / 路径；
    /// 打乱顺序优先（它是一次性操作且会被记住），切排序或重新导入即恢复有序。
    /// </summary>
    public static class TrackSorter
    {
        public const string Added = "added";
        public const string Title = "title";
        public const string TitleDesc = "titleDesc";
        public const string Duration = "duration";
        public const string DurationDesc = "durationDesc";
        public const string Format = "format";
        public const string Path = "path";

        public static List<Track> Sort(Group group, string mode)
        {
            var result = new List<Track>();
            if (group == null || group.Tracks == null) return result;

            // 打乱顺序优先（存在配置里，重启后仍是这个顺序），O(N) 去重
            if (group.ShuffledOrder != null && group.ShuffledOrder.Count > 0)
            {
                var map = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in group.Tracks)
                {
                    if (t != null && !string.IsNullOrEmpty(t.Path))
                        map[t.Path] = t;
                }

                var added = new HashSet<Track>();
                foreach (var path in group.ShuffledOrder)
                {
                    if (string.IsNullOrEmpty(path)) continue;
                    Track t;
                    if (map.TryGetValue(path, out t) && added.Add(t))
                        result.Add(t);
                }

                // 打乱后新导入的曲目接在后面
                foreach (var t in group.Tracks)
                {
                    if (t != null && added.Add(t))
                        result.Add(t);
                }
                return result;
            }

            foreach (var t in group.Tracks)
            {
                if (t != null) result.Add(t);
            }

            switch (mode)
            {
                case Title:
                    result.Sort((a, b) =>
                    {
                        int c = CompareNatural(a.Title, b.Title);
                        return c != 0 ? c : a.AddedOrder.CompareTo(b.AddedOrder);
                    });
                    break;
                case TitleDesc:
                    result.Sort((a, b) =>
                    {
                        int c = CompareNatural(b.Title, a.Title);
                        return c != 0 ? c : a.AddedOrder.CompareTo(b.AddedOrder);
                    });
                    break;
                case Duration:
                    result.Sort((a, b) => CompareDuration(a, b, true));
                    break;
                case DurationDesc:
                    result.Sort((a, b) => CompareDuration(a, b, false));
                    break;
                case Format:
                    result.Sort((a, b) =>
                    {
                        int c = string.Compare(a.Format ?? string.Empty, b.Format ?? string.Empty, StringComparison.OrdinalIgnoreCase);
                        if (c != 0) return c;
                        c = CompareNatural(a.Title, b.Title);
                        return c != 0 ? c : a.AddedOrder.CompareTo(b.AddedOrder);
                    });
                    break;
                case Path:
                    result.Sort((a, b) =>
                    {
                        int c = CompareNatural(a.Path, b.Path);
                        return c != 0 ? c : a.AddedOrder.CompareTo(b.AddedOrder);
                    });
                    break;
                case Added:
                default:
                    result.Sort((a, b) => a.AddedOrder.CompareTo(b.AddedOrder));
                    break;
            }

            return result;
        }

        private static bool IsUnknownDuration(double duration)
        {
            return double.IsNaN(duration) || double.IsInfinity(duration) || duration <= 0;
        }

        private static int CompareDuration(Track a, Track b, bool ascending)
        {
            // 未知时长（<= 0 / NaN / Infinity）排在最后，不混在中间
            bool aUnknown = IsUnknownDuration(a.Duration);
            bool bUnknown = IsUnknownDuration(b.Duration);
            if (aUnknown && bUnknown) return a.AddedOrder.CompareTo(b.AddedOrder);
            if (aUnknown) return 1;
            if (bUnknown) return -1;

            int c = a.Duration.CompareTo(b.Duration);
            if (c != 0) return ascending ? c : -c;
            return a.AddedOrder.CompareTo(b.AddedOrder);
        }

        /// <summary>
        /// 自然排序比较器：连续数字段按数值大小比较（如 "Track 2" &lt; "Track 10"），
        /// 非数字段按当前文化忽略大小写比较。
        /// </summary>
        private static int CompareNatural(string a, string b)
        {
            if (ReferenceEquals(a, b)) return 0;
            a = a ?? string.Empty;
            b = b ?? string.Empty;

            int ia = 0, ib = 0;
            var compareInfo = CultureInfo.CurrentCulture.CompareInfo;

            while (ia < a.Length && ib < b.Length)
            {
                bool aDigit = IsAsciiDigit(a[ia]);
                bool bDigit = IsAsciiDigit(b[ib]);

                if (aDigit && bDigit)
                {
                    int startA = ia;
                    while (ia < a.Length && IsAsciiDigit(a[ia])) ia++;
                    int startB = ib;
                    while (ib < b.Length && IsAsciiDigit(b[ib])) ib++;

                    // 跳过前导零
                    int nzA = startA;
                    while (nzA < ia && a[nzA] == '0') nzA++;
                    int nzB = startB;
                    while (nzB < ib && b[nzB] == '0') nzB++;

                    int sigLenA = ia - nzA;
                    int sigLenB = ib - nzB;
                    if (sigLenA != sigLenB)
                        return sigLenA.CompareTo(sigLenB);

                    for (int k = 0; k < sigLenA; k++)
                    {
                        if (a[nzA + k] != b[nzB + k])
                            return a[nzA + k].CompareTo(b[nzB + k]);
                    }

                    // 数值相等时，按原始数字段长度区分（如 "01" vs "1"），保持严格弱序
                    int rawLenA = ia - startA;
                    int rawLenB = ib - startB;
                    if (rawLenA != rawLenB)
                        return rawLenA.CompareTo(rawLenB);
                }
                else
                {
                    int startA = ia;
                    while (ia < a.Length && !IsAsciiDigit(a[ia])) ia++;
                    int startB = ib;
                    while (ib < b.Length && !IsAsciiDigit(b[ib])) ib++;

                    int cmp = compareInfo.Compare(
                        a, startA, ia - startA,
                        b, startB, ib - startB,
                        CompareOptions.IgnoreCase);
                    if (cmp != 0) return cmp;
                }
            }

            if (ia < a.Length) return 1;
            if (ib < b.Length) return -1;
            return 0;
        }

        private static bool IsAsciiDigit(char c)
        {
            return c >= '0' && c <= '9';
        }

        public static string DisplayName(string mode)
        {
            switch (mode)
            {
                case Title: return "标题 ↑";
                case TitleDesc: return "标题 ↓";
                case Duration: return "时长 ↑";
                case DurationDesc: return "时长 ↓";
                case Format: return "文件格式";
                case Path: return "路径";
                case Added:
                default: return "最近添加";
            }
        }

        public static string Next(string mode)
        {
            switch (mode)
            {
                case Added: return Title;
                case Title: return TitleDesc;
                case TitleDesc: return Duration;
                case Duration: return DurationDesc;
                case DurationDesc: return Format;
                case Format: return Path;
                case Path: return Added;
                default: return Added;
            }
        }
    }
}
