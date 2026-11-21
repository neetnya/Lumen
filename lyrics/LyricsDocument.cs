using System;
using System.Collections.Generic;

namespace Lumen.Lyrics
{
    /// <summary>歌词中的一行：开始时间 + 文本 + 可选结束时间。</summary>
    public sealed class LyricLine
    {
        public double Time;          // 秒
        public double EndTime;       // 秒；未知时为 -1（取下一行时间）
        public string Text;
        public bool IsInterlude;     // 是否为间奏标记行

        public LyricLine(double time, string text)
        {
            Time = time;
            Text = text ?? string.Empty;
            EndTime = -1;
            IsInterlude = false;
        }

        /// <summary>合并同时间戳的双语/多行文本（按换行符拼接）。</summary>
        public void AppendText(string extra)
        {
            if (string.IsNullOrWhiteSpace(extra)) return;
            var trimmed = extra.Trim();
            if (string.IsNullOrEmpty(Text))
            {
                Text = trimmed;
            }
            else
            {
                Text = Text + "\n" + trimmed;
            }
        }

        /// <summary>主歌词文本（双语合并时取第一行）。</summary>
        public string PrimaryText
        {
            get
            {
                if (string.IsNullOrEmpty(Text)) return string.Empty;
                int idx = Text.IndexOf('\n');
                return idx >= 0 ? Text.Substring(0, idx) : Text;
            }
        }

        /// <summary>翻译/副歌词文本（无双语时返回空字符串）。</summary>
        public string TranslationText
        {
            get
            {
                if (string.IsNullOrEmpty(Text)) return string.Empty;
                int idx = Text.IndexOf('\n');
                return idx >= 0 && idx + 1 < Text.Length ? Text.Substring(idx + 1) : string.Empty;
            }
        }

        public override string ToString()
        {
            return Time.ToString("F2") + " " + Text;
        }
    }

    /// <summary>解析结果：可以是时间轴歌词（同步），也可以是纯文本歌词（不同步）。</summary>
    public sealed class LyricsDocument
    {
        public readonly List<LyricLine> Lines = new List<LyricLine>();

        /// <summary>true = 带时间轴，可同步高亮；false = 纯文本，只滚动。</summary>
        public bool IsSynced;

        public string SourcePath;
        public string FormatName = "无";

        public bool IsEmpty { get { return Lines.Count == 0; } }

        /// <summary>总时长（秒），非同步或空文档返回 0。</summary>
        public double TotalSeconds
        {
            get
            {
                if (!IsSynced || Lines.Count == 0) return 0;
                var last = Lines[Lines.Count - 1];
                double value = last.EndTime >= 0 ? last.EndTime : last.Time;
                if (double.IsNaN(value) || double.IsInfinity(value)) return 0;
                return Math.Max(0, value);
            }
        }

        /// <summary>按时间找到当前应高亮的行号；非同步、空文档或早于第一行返回 -1。</summary>
        public int IndexAt(double seconds)
        {
            if (!IsSynced || Lines.Count == 0) return -1;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return -1;
            if (seconds < Lines[0].Time) return -1;

            int lo = 0, hi = Lines.Count - 1, result = 0;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (Lines[mid].Time <= seconds)
                {
                    result = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return result;
        }

        /// <summary>补齐 EndTime（跳过已有效设置的项，同时间戳向后查找首个严格大于当前时间的行）。</summary>
        public void FillEndTimes(double fallbackDuration)
        {
            if (!IsSynced || Lines.Count == 0) return;

            bool hasFallback = !double.IsNaN(fallbackDuration) && !double.IsInfinity(fallbackDuration);

            for (int i = 0; i < Lines.Count; i++)
            {
                if (Lines[i].EndTime > Lines[i].Time) continue;

                double nextTime = -1;
                for (int j = i + 1; j < Lines.Count; j++)
                {
                    if (Lines[j].Time > Lines[i].Time)
                    {
                        nextTime = Lines[j].Time;
                        break;
                    }
                }

                if (nextTime >= 0)
                {
                    Lines[i].EndTime = nextTime;
                }
                else
                {
                    Lines[i].EndTime = hasFallback && fallbackDuration > Lines[i].Time
                        ? fallbackDuration
                        : Lines[i].Time + 5.0;
                }
            }
        }
    }
}
