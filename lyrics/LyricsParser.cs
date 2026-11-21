using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lumen.Lyrics
{
    /// <summary>
    /// 歌词解析：LRC / WebVTT / SRT / ASS / SSA / 纯文本。
    /// 扩展名不认识时按内容嗅探；解析结果统一成 LyricsDocument。
    /// </summary>
    public static class LyricsParser
    {
        // [mm:ss.xx] / [mm:ss:xx] / [mm:ss]
        private static readonly Regex LrcTimeTag =
            new Regex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

        // 增强型 LRC 逐字标签 <mm:ss.xx>
        private static readonly Regex LrcWordTag =
            new Regex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

        // [ti:xxx] [ar:xxx] 这类元信息标签
        private static readonly Regex LrcMetaTag =
            new Regex(@"^\[(ti|ar|al|by|offset|re|ve|length)\s*:(.*)\]$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 仅匹配合法 VTT/HTML 标签与 VTT 内部时间戳标签，避免误删 <合唱> 等正常文本
        private static readonly Regex VttTag = new Regex(
            @"<(?:/?(?:b|i|u|c|v|ruby|rt|lang|font)(?:\.[^>\s]+)*(?:\s+[^>]*)?|\d{1,2}:\d{2}(?::\d{2})?[.,]\d{1,3})>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VttTimeLineRegex = new Regex(
            @"(\d{1,2}:\d{2}:\d{2}[.,]\d{1,3}|\d{1,2}:\d{2}[.,]\d{1,3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[.,]\d{1,3}|\d{1,2}:\d{2}[.,]\d{1,3})",
            RegexOptions.Compiled);

        private static readonly Regex SrtTimeLineRegex = new Regex(
            @"(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})\s*-->\s*(\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})",
            RegexOptions.Compiled);

        private static readonly Regex SrtSniffRegex = new Regex(
            @"\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->",
            RegexOptions.Compiled);

        private static readonly Regex AssOverrideTag =
            new Regex(@"\{[^}]*\}", RegexOptions.Compiled);

        /// <summary>按扩展名解析；扩展名不认识时自动嗅探内容。</summary>
        public static LyricsDocument Parse(string text, string path, double fallbackDuration)
        {
            var doc = ParseCore(text, path, fallbackDuration);
            doc.SourcePath = path;
            return doc;
        }

        private static LyricsDocument ParseCore(string rawText, string path, double fallbackDuration)
        {
            var text = TextEncoding.StripBom(rawText ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text)) return new LyricsDocument { FormatName = "空" };

            var ext = (System.IO.Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            LyricsDocument result;

            switch (ext)
            {
                case ".lrc":
                    result = ParseLrc(text, fallbackDuration);
                    // 扩展名是 .lrc 但里面没有时间轴：可能是别的格式被改了名，跳过已失败的 LRC
                    if (result.IsEmpty) result = Sniff(text, fallbackDuration, "LRC");
                    return result;
                case ".vtt":
                    result = ParseVtt(text, fallbackDuration);
                    if (result.IsEmpty) return Sniff(text, fallbackDuration, "WebVTT");
                    return result;
                case ".srt":
                    result = ParseSrt(text, fallbackDuration);
                    if (result.IsEmpty) return Sniff(text, fallbackDuration, "SRT");
                    return result;
                case ".ass":
                    result = ParseAss(text, fallbackDuration, "ASS");
                    if (result.IsEmpty) return Sniff(text, fallbackDuration, "ASS");
                    return result;
                case ".ssa":
                    result = ParseAss(text, fallbackDuration, "SSA");
                    if (result.IsEmpty) return Sniff(text, fallbackDuration, "ASS");
                    return result;
                case ".txt":
                    result = ParsePlain(text);
                    // .txt 里可能是带时间轴的歌词，嗅探一下更保险
                    if (!result.IsEmpty)
                    {
                        var sniffed = Sniff(text, fallbackDuration, null);
                        if (sniffed.IsSynced && !sniffed.IsEmpty) return sniffed;
                    }
                    return result;
            }

            // 按内容嗅探
            return Sniff(text, fallbackDuration, null);
        }

        /// <summary>内容嗅探。</summary>
        public static LyricsDocument Sniff(string text, double fallbackDuration)
        {
            return Sniff(text, fallbackDuration, null);
        }

        private static LyricsDocument Sniff(string text, double fallbackDuration, string skipFormat)
        {
            if (string.IsNullOrWhiteSpace(text)) return new LyricsDocument { FormatName = "空" };

            if (!string.Equals(skipFormat, "WebVTT", StringComparison.OrdinalIgnoreCase) &&
                text.IndexOf("WEBVTT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var doc = ParseVtt(text, fallbackDuration);
                if (!doc.IsEmpty) return doc;
            }

            if (!string.Equals(skipFormat, "ASS", StringComparison.OrdinalIgnoreCase) &&
                (text.IndexOf("[Script Info]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 text.IndexOf("Dialogue:", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var formatName = text.IndexOf("[V4 Styles]", StringComparison.OrdinalIgnoreCase) >= 0 ? "SSA" : "ASS";
                var doc = ParseAss(text, fallbackDuration, formatName);
                if (!doc.IsEmpty) return doc;
            }

            if (!string.Equals(skipFormat, "LRC", StringComparison.OrdinalIgnoreCase) &&
                LrcTimeTag.IsMatch(text))
            {
                var doc = ParseLrc(text, fallbackDuration);
                if (!doc.IsEmpty) return doc;
            }

            if (!string.Equals(skipFormat, "SRT", StringComparison.OrdinalIgnoreCase) &&
                SrtSniffRegex.IsMatch(text))
            {
                var doc = ParseSrt(text, fallbackDuration);
                if (!doc.IsEmpty) return doc;
            }

            return ParsePlain(text);
        }

        // ------------------------------------------------------------------
        // LRC
        // ------------------------------------------------------------------

        private struct RawLrcEntry
        {
            public double Time;
            public int Order;
            public string Text;
        }

        private static LyricsDocument ParseLrc(string text, double fallbackDuration)
        {
            var doc = new LyricsDocument { IsSynced = true, FormatName = "LRC" };
            double offset = 0;
            var lines = SplitLines(text);
            var rawEntries = new List<RawLrcEntry>();
            int order = 0;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var meta = LrcMetaTag.Match(line);
                if (meta.Success)
                {
                    if (meta.Groups[1].Value.Equals("offset", StringComparison.OrdinalIgnoreCase))
                    {
                        double parsed;
                        if (double.TryParse(meta.Groups[2].Value.Trim(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out parsed) &&
                            !double.IsNaN(parsed) && !double.IsInfinity(parsed))
                        {
                            // offset 单位是毫秒，正数表示歌词提前
                            offset = -parsed / 1000.0;
                        }
                    }
                    continue;
                }

                var matches = LrcTimeTag.Matches(line);
                if (matches.Count == 0) continue;

                // 去掉所有时间标签后剩下的就是歌词文本
                var content = LrcTimeTag.Replace(line, string.Empty);
                // 去掉增强型逐字标签（压平，不做卡拉OK变色）
                content = LrcWordTag.Replace(content, string.Empty).Trim();

                // 一行多时间标签：每个标签都生成一条原始记录（含空文本间奏截断标记）
                foreach (Match m in matches)
                {
                    double seconds;
                    if (!TryToSeconds(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, out seconds))
                        continue;

                    rawEntries.Add(new RawLrcEntry
                    {
                        Time = seconds,
                        Order = order++,
                        Text = content
                    });
                }
            }

            // 统一叠加 offset（解决后置 [offset:] 滞后问题），截断 < 0 为 0
            for (int i = 0; i < rawEntries.Count; i++)
            {
                var entry = rawEntries[i];
                double adjusted = entry.Time + offset;
                if (adjusted < 0) adjusted = 0;
                entry.Time = adjusted;
                rawEntries[i] = entry;
            }

            // 稳定排序：时间升序，同时间按原行号升序
            rawEntries.Sort((a, b) =>
            {
                int cmp = a.Time.CompareTo(b.Time);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });

            // 单遍合并同时间戳双语行，并用空文本时间点截断前一句的 EndTime
            for (int i = 0; i < rawEntries.Count; i++)
            {
                var entry = rawEntries[i];
                if (entry.Text.Length == 0)
                {
                    if (doc.Lines.Count > 0)
                    {
                        var prev = doc.Lines[doc.Lines.Count - 1];
                        if (entry.Time > prev.Time && prev.EndTime <= prev.Time)
                        {
                            prev.EndTime = entry.Time;
                        }
                    }
                    continue;
                }

                if (doc.Lines.Count > 0 && Math.Abs(doc.Lines[doc.Lines.Count - 1].Time - entry.Time) < 0.001)
                {
                    doc.Lines[doc.Lines.Count - 1].AppendText(entry.Text);
                }
                else
                {
                    doc.Lines.Add(new LyricLine(entry.Time, entry.Text));
                }
            }

            if (doc.Lines.Count == 0) doc.IsSynced = false;
            doc.FillEndTimes(fallbackDuration);
            return doc;
        }

        private static bool TryToSeconds(string mm, string ss, string frac, out double totalSeconds)
        {
            totalSeconds = 0;
            double minutes, seconds;
            if (!double.TryParse(mm, NumberStyles.Integer, CultureInfo.InvariantCulture, out minutes)) return false;
            if (!double.TryParse(ss, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) return false;

            double fraction = 0;
            if (!string.IsNullOrEmpty(frac))
            {
                double value;
                if (!double.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return false;
                // 1 位 = 十分之一秒，2 位 = 百分之一秒，3 位 = 毫秒
                fraction = value / Math.Pow(10, frac.Length);
            }

            double result = minutes * 60 + seconds + fraction;
            if (double.IsNaN(result) || double.IsInfinity(result) || result < 0) return false;
            totalSeconds = result;
            return true;
        }

        // ------------------------------------------------------------------
        // WebVTT
        // ------------------------------------------------------------------

        private struct RawCue
        {
            public double Start;
            public double End;
            public int Order;
            public string Text;
        }

        private static LyricsDocument ParseVtt(string text, double fallbackDuration)
        {
            var doc = new LyricsDocument { IsSynced = true, FormatName = "WebVTT" };
            var lines = SplitLines(text);
            var rawCues = new List<RawCue>();
            int order = 0;

            int i = 0;
            while (i < lines.Count)
            {
                var line = lines[i].Trim();

                // NOTE 注释块：跳到空行
                if (line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase))
                {
                    while (i < lines.Count && lines[i].Trim().Length > 0) i++;
                    continue;
                }

                var m = VttTimeLineRegex.Match(line);
                if (!m.Success) { i++; continue; }

                double start;
                if (!TryParseVttTime(m.Groups[1].Value, out start)) { i++; continue; }

                double end;
                if (!TryParseVttTime(m.Groups[2].Value, out end) || end <= start) end = -1;

                // 收集 cue 文本直到空行，多行合并
                var parts = new List<string>();
                i++;
                while (i < lines.Count && lines[i].Trim().Length > 0)
                {
                    parts.Add(CleanVttText(lines[i]));
                    i++;
                }

                var merged = string.Join(" ", parts).Trim();
                if (merged.Length > 0)
                {
                    rawCues.Add(new RawCue
                    {
                        Start = start,
                        End = end,
                        Order = order++,
                        Text = merged
                    });
                }
            }

            // 先排序再单遍合并（O(N log N)）
            rawCues.Sort((a, b) =>
            {
                int cmp = a.Start.CompareTo(b.Start);
                return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
            });

            for (int k = 0; k < rawCues.Count; k++)
            {
                var cue = rawCues[k];
                if (doc.Lines.Count > 0 && Math.Abs(doc.Lines[doc.Lines.Count - 1].Time - cue.Start) < 0.001)
                {
                    var existing = doc.Lines[doc.Lines.Count - 1];
                    existing.Text = (existing.Text + " " + cue.Text).Trim();
                    existing.EndTime = Math.Max(existing.EndTime, cue.End);
                }
                else
                {
                    doc.Lines.Add(new LyricLine(cue.Start, cue.Text) { EndTime = cue.End });
                }
            }

            if (doc.Lines.Count == 0) doc.IsSynced = false;
            doc.FillEndTimes(fallbackDuration);
            return doc;
        }

        /// <summary>去掉 &lt;v 说话人&gt;、&lt;c.class&gt; 等合法标签以及 VTT 实体。</summary>
        private static string CleanVttText(string input)
        {
            var s = VttTag.Replace(input, string.Empty);
            s = s.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                 .Replace("&nbsp;", " ").Replace("&quot;", "\"").Replace("&#39;", "'");
            return s.Trim();
        }

        private static bool TryParseVttTime(string value, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var normalized = value.Trim().Replace(',', '.');
            var parts = normalized.Split(':');
            double result;

            if (parts.Length == 3)
            {
                double h, m, s;
                if (!double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) ||
                    !double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s))
                {
                    return false;
                }
                result = h * 3600 + m * 60 + s;
            }
            else if (parts.Length == 2)
            {
                double m, s;
                if (!double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out s))
                {
                    return false;
                }
                result = m * 60 + s;
            }
            else
            {
                if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                    return false;
            }

            if (double.IsNaN(result) || double.IsInfinity(result) || result < 0) return false;
            seconds = result;
            return true;
        }

        // ------------------------------------------------------------------
        // SRT
        // ------------------------------------------------------------------

        private static LyricsDocument ParseSrt(string text, double fallbackDuration)
        {
            var doc = new LyricsDocument { IsSynced = true, FormatName = "SRT" };
            var lines = SplitLines(text);

            int i = 0;
            while (i < lines.Count)
            {
                var m = SrtTimeLineRegex.Match(lines[i]);
                if (!m.Success) { i++; continue; }

                double start;
                if (!TryParseVttTime(m.Groups[1].Value, out start)) { i++; continue; }

                double end;
                if (!TryParseVttTime(m.Groups[2].Value, out end) || end <= start) end = -1;

                var parts = new List<string>();
                i++;
                while (i < lines.Count && lines[i].Trim().Length > 0)
                {
                    parts.Add(VttTag.Replace(lines[i], string.Empty).Trim());
                    i++;
                }

                var merged = string.Join(" ", parts).Trim();
                if (merged.Length > 0) doc.Lines.Add(new LyricLine(start, merged) { EndTime = end });
            }

            doc.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (doc.Lines.Count == 0) doc.IsSynced = false;
            doc.FillEndTimes(fallbackDuration);
            return doc;
        }

        // ------------------------------------------------------------------
        // ASS / SSA
        // ------------------------------------------------------------------

        private static LyricsDocument ParseAss(string text, double fallbackDuration, string formatName = "ASS")
        {
            var doc = new LyricsDocument
            {
                IsSynced = true,
                FormatName = string.IsNullOrEmpty(formatName) ? "ASS" : formatName
            };
            var lines = SplitLines(text);

            string[] fields = null;   // [Events] 节内 Format: 行给出的列顺序
            var events = new List<string[]>();
            bool hasSectionHeader = false;
            bool inEvents = false;

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';') continue;

                // 节头切换：[Script Info] / [V4+ Styles] / [Events] 等
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    hasSectionHeader = true;
                    inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                // 仅在 [Events] 节内（或无任何节头的裸片段中）解析 Format 与 Dialogue，避免被 [V4+ Styles] 抢占
                if (!inEvents && hasSectionHeader) continue;

                if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase) && fields == null)
                {
                    fields = SplitAssFields(line.Substring("Format:".Length), -1);
                }
                else if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
                {
                    // Text 永远在最后一列，所以按列数切分，剩下的逗号全部归 Text
                    int columnCount = fields != null ? fields.Length : 10;
                    events.Add(SplitAssFields(line.Substring("Dialogue:".Length), columnCount));
                }
            }

            // 没有 Format 行时按 ASS 默认列顺序
            if (fields == null)
                fields = new[] { "Layer", "Start", "End", "Style", "Name", "MarginL", "MarginR", "MarginV", "Effect", "Text" };

            int startIdx = IndexOfField(fields, "Start");
            int endIdx = IndexOfField(fields, "End");
            int textIdx = IndexOfField(fields, "Text");
            if (startIdx < 0 || textIdx < 0) { doc.IsSynced = false; return doc; }

            foreach (var ev in events)
            {
                if (ev.Length <= textIdx || ev.Length <= startIdx) continue;

                double start;
                if (!TryParseAssTime(ev[startIdx], out start)) continue;

                double end = -1;
                if (endIdx >= 0 && ev.Length > endIdx)
                {
                    double parsedEnd;
                    if (TryParseAssTime(ev[endIdx], out parsedEnd) && parsedEnd > start)
                        end = parsedEnd;
                }

                // 去掉 {\pos(...)} 之类的样式覆盖；\N 换行转空格
                var content = AssOverrideTag.Replace(ev[textIdx], string.Empty)
                    .Replace("\\N", " ").Replace("\\n", " ").Replace("\\h", " ").Trim();

                if (content.Length > 0) doc.Lines.Add(new LyricLine(start, content) { EndTime = end });
            }

            doc.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (doc.Lines.Count == 0) doc.IsSynced = false;
            doc.FillEndTimes(fallbackDuration);
            return doc;
        }

        /// <summary>
        /// 按逗号切分 ASS 行。maxFields &gt; 0 时只切出前 maxFields-1 个逗号，
        /// 剩下的全部作为最后一列（Text 列本身可能含逗号，例如 {\pos(190,280)}）。
        /// </summary>
        private static string[] SplitAssFields(string input, int maxFields)
        {
            var parts = new List<string>();
            int start = 0;
            int limit = maxFields > 0 ? maxFields - 1 : int.MaxValue;
            int count = 0;

            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] != ',') continue;
                if (count >= limit) break;
                parts.Add(input.Substring(start, i - start).Trim());
                start = i + 1;
                count++;
            }
            parts.Add(input.Substring(start).Trim());
            return parts.ToArray();
        }

        private static int IndexOfField(string[] fields, string name)
        {
            for (int i = 0; i < fields.Length; i++)
            {
                if (string.Equals(fields[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        /// <summary>ASS 时间：H:MM:SS.cc（百分之一秒），安全 TryParse。</summary>
        private static bool TryParseAssTime(string value, out double seconds)
        {
            seconds = 0;
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0) return false;

            var parts = trimmed.Split(':');
            double result;
            if (parts.Length == 3)
            {
                double h, m, s;
                if (!double.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) ||
                    !double.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out m) ||
                    !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out s))
                {
                    return false;
                }
                result = h * 3600 + m * 60 + s;
            }
            else
            {
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                    return false;
            }

            if (double.IsNaN(result) || double.IsInfinity(result) || result < 0) return false;
            seconds = result;
            return true;
        }

        // ------------------------------------------------------------------
        // 纯文本
        // ------------------------------------------------------------------

        private static LyricsDocument ParsePlain(string text)
        {
            var doc = new LyricsDocument { IsSynced = false, FormatName = "纯文本" };
            foreach (var raw in SplitLines(text))
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0) continue;
                doc.Lines.Add(new LyricLine(-1, line.Trim()));
            }
            return doc;
        }

        private static List<string> SplitLines(string text)
        {
            var result = new List<string>();
            if (text == null) return result;
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    int end = i;
                    if (end > start && text[end - 1] == '\r') end--;
                    result.Add(text.Substring(start, end - start));
                    start = i + 1;
                }
                else if (c == '\r')
                {
                    result.Add(text.Substring(start, i - start));
                    if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                    start = i + 1;
                }
            }
            if (start < text.Length) result.Add(text.Substring(start));
            return result;
        }
    }
}
