using System;
using System.Collections.Generic;
using System.IO;
using Lumen.Models;

namespace Lumen.Media
{
    /// <summary>
    /// 媒体库扫描。
    /// 按 README：打开时只读文件名/大小，标题、时长、歌词由后台分片补上，
    /// 每片最多占用主线程一小段时间，上千首的库也能很快看到窗口。
    /// </summary>
    public static class MediaScanner
    {
        /// <summary>支持的音频扩展名。</summary>
        public static readonly string[] AudioExtensions =
        {
            ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus",
            ".wma", ".ape", ".aiff", ".aif", ".aifc", ".mp4", ".m4b", ".wv", ".mpc"
        };

        public static bool IsAudioFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            return Array.IndexOf(AudioExtensions, ext.ToLowerInvariant()) >= 0;
        }

        /// <summary>
        /// 扫描文件夹（含子目录）里的音频文件。
        /// 只做轻量工作：路径、文件名、大小 —— 不解析音频，保证快。
        /// 遇到无权限子目录自动跳过，不中断扫描。
        /// </summary>
        public static List<Track> ScanFolder(string folder, ref int orderCounter)
        {
            var result = new List<Track>();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return result;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(folder, "*", options);
            }
            catch (Exception ex)
            {
                Log.Warn("扫描文件夹失败 " + folder + ": " + ex.Message);
                return result;
            }

            var list = new List<string>();
            try
            {
                foreach (var f in files)
                {
                    if (IsAudioFile(f)) list.Add(f);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("枚举文件中断 " + folder + ": " + ex.Message);
            }

            // 稳定排序，让导入顺序可预期
            list.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (var f in list)
            {
                result.Add(Track.FromFile(f, orderCounter++));
            }
            return result;
        }

        /// <summary>把一批文件路径转成 Track（跳过非音频，并基于规范完整路径去重）。</summary>
        public static List<Track> FromFiles(IEnumerable<string> paths, ref int orderCounter)
        {
            var result = new List<Track>();
            if (paths == null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p)) continue;

                if (Directory.Exists(p))
                {
                    var folderTracks = ScanFolder(p, ref orderCounter);
                    foreach (var track in folderTracks)
                    {
                        var key = SafeGetFullPath(track.Path);
                        if (seen.Add(key)) result.Add(track);
                    }
                }
                else if (File.Exists(p) && IsAudioFile(p))
                {
                    var key = SafeGetFullPath(p);
                    if (seen.Add(key))
                    {
                        result.Add(Track.FromFile(p, orderCounter++));
                    }
                }
            }
            return result;
        }

        private static string SafeGetFullPath(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path ?? string.Empty; }
        }

        /// <summary>探测单个文件的时长（秒）；优先零依赖轻量头解析，复杂格式回退 NAudio/MF，失败返回 0。</summary>
        public static double ProbeDuration(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;

            // 1) 前置零依赖纯 C# 轻量音频头解析（WAV / FLAC / MP3）
            double fastSeconds;
            if (TryProbeHeaderDuration(path, out fastSeconds))
                return fastSeconds;

            // 2) 复杂格式回退 NAudio AudioFileReader / MediaFoundationReader
            Exception firstError = null;
            try
            {
                using (var reader = new NAudio.Wave.AudioFileReader(path))
                {
                    var seconds = reader.TotalTime.TotalSeconds;
                    if (IsValidDuration(seconds)) return seconds;
                }
            }
            catch (Exception ex)
            {
                firstError = ex;
            }

            try
            {
                using (var reader = new NAudio.Wave.MediaFoundationReader(path))
                {
                    var seconds = reader.TotalTime.TotalSeconds;
                    if (IsValidDuration(seconds)) return seconds;
                }
            }
            catch (Exception ex)
            {
                Log.Warn("探测音频时长失败 " + path + ": " + ex.Message +
                         (firstError != null ? " (AudioFileReader: " + firstError.Message + ")" : ""));
                return 0;
            }

            if (firstError != null)
            {
                Log.Warn("探测音频时长无有效结果 " + path + ": " + firstError.Message);
            }
            return 0;
        }

        private static bool IsValidDuration(double seconds)
        {
            return seconds > 0 && !double.IsNaN(seconds) && !double.IsInfinity(seconds);
        }

        // ------------------------------------------------------------------
        // 零依赖纯 C# 轻量音频头解析（WAV / FLAC / MP3）
        // ------------------------------------------------------------------

        private static bool TryProbeHeaderDuration(string path, out double seconds)
        {
            seconds = 0;
            var ext = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            if (ext != ".wav" && ext != ".flac" && ext != ".mp3") return false;

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (ext == ".wav") return TryProbeWav(fs, out seconds);
                    if (ext == ".flac") return TryProbeFlac(fs, out seconds);
                    if (ext == ".mp3") return TryProbeMp3(fs, out seconds);
                }
            }
            catch
            {
                // 头解析失败时静默交由 NAudio / MediaFoundation 回退
            }
            return false;
        }

        private static bool TryProbeWav(FileStream fs, out double seconds)
        {
            seconds = 0;
            if (fs.Length < 44) return false;

            var header = new byte[12];
            if (!ReadExact(fs, header, 0, 12)) return false;
            if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F') return false;
            if (header[8] != 'W' || header[9] != 'A' || header[10] != 'V' || header[11] != 'E') return false;

            uint byteRate = 0;
            uint dataSize = 0;
            var chunkHeader = new byte[8];

            while (fs.Position + 8 <= fs.Length)
            {
                if (!ReadExact(fs, chunkHeader, 0, 8)) break;
                uint chunkSize = ReadUInt32LE(chunkHeader, 4);
                long nextPos = fs.Position + chunkSize + (chunkSize & 1);

                if (chunkHeader[0] == 'f' && chunkHeader[1] == 'm' && chunkHeader[2] == 't' && chunkHeader[3] == ' ')
                {
                    if (chunkSize >= 16)
                    {
                        var fmt = new byte[16];
                        if (!ReadExact(fs, fmt, 0, 16)) return false;
                        byteRate = ReadUInt32LE(fmt, 8);
                    }
                }
                else if (chunkHeader[0] == 'd' && chunkHeader[1] == 'a' && chunkHeader[2] == 't' && chunkHeader[3] == 'a')
                {
                    dataSize = chunkSize;
                }

                if (byteRate > 0 && dataSize > 0)
                {
                    double dur = (double)dataSize / byteRate;
                    if (IsValidDuration(dur))
                    {
                        seconds = dur;
                        return true;
                    }
                    return false;
                }

                if (nextPos <= fs.Position || nextPos > fs.Length) break;
                fs.Position = nextPos;
            }
            return false;
        }

        private static bool TryProbeFlac(FileStream fs, out double seconds)
        {
            seconds = 0;
            if (fs.Length < 42) return false;

            var magic = new byte[4];
            if (!ReadExact(fs, magic, 0, 4)) return false;
            if (magic[0] != 'f' || magic[1] != 'L' || magic[2] != 'a' || magic[3] != 'C') return false;

            var blockHeader = new byte[4];
            if (!ReadExact(fs, blockHeader, 0, 4)) return false;

            int blockType = blockHeader[0] & 0x7F;
            int blockLen = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];
            if (blockType != 0 || blockLen < 34) return false;

            var info = new byte[34];
            if (!ReadExact(fs, info, 0, 34)) return false;

            int sampleRate = (info[10] << 12) | (info[11] << 4) | (info[12] >> 4);
            long totalSamples = ((long)(info[13] & 0x0F) << 32) |
                                ((long)info[14] << 24) |
                                ((long)info[15] << 16) |
                                ((long)info[16] << 8) |
                                info[17];

            if (sampleRate > 0 && totalSamples > 0)
            {
                double dur = (double)totalSamples / sampleRate;
                if (IsValidDuration(dur))
                {
                    seconds = dur;
                    return true;
                }
            }
            return false;
        }

        private static readonly int[] Mp3BitrateV1L1 = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 };
        private static readonly int[] Mp3BitrateV1L2 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 };
        private static readonly int[] Mp3BitrateV1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
        private static readonly int[] Mp3BitrateV2L1 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 };
        private static readonly int[] Mp3BitrateV2L23 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
        private static readonly int[] Mp3SampleRateV1 = { 44100, 48000, 32000, 0 };
        private static readonly int[] Mp3SampleRateV2 = { 22050, 24000, 16000, 0 };
        private static readonly int[] Mp3SampleRateV25 = { 11025, 12000, 8000, 0 };

        private static bool TryProbeMp3(FileStream fs, out double seconds)
        {
            seconds = 0;
            long fileLen = fs.Length;
            if (fileLen < 128) return false;

            long audioStart = 0;
            var id3 = new byte[10];
            if (ReadExact(fs, id3, 0, 10) && id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3')
            {
                int tagSize = ((id3[6] & 0x7F) << 21) |
                              ((id3[7] & 0x7F) << 14) |
                              ((id3[8] & 0x7F) << 7) |
                              (id3[9] & 0x7F);
                bool hasFooter = (id3[5] & 0x10) != 0;
                audioStart = 10L + tagSize + (hasFooter ? 10 : 0);
                if (audioStart >= fileLen) return false;
            }

            fs.Position = audioStart;
            int probeLen = (int)Math.Min(16384, fileLen - audioStart);
            if (probeLen < 4) return false;

            var buf = new byte[probeLen];
            int read = fs.Read(buf, 0, probeLen);
            if (read < 4) return false;

            for (int i = 0; i <= read - 4; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;

                int versionId = (buf[i + 1] >> 3) & 0x03; // 0=2.5, 1=reserved, 2=2, 3=1
                int layer = (buf[i + 1] >> 1) & 0x03;     // 0=reserved, 1=L3, 2=L2, 3=L1
                int bitrateIdx = (buf[i + 2] >> 4) & 0x0F;
                int srIdx = (buf[i + 2] >> 2) & 0x03;
                int padding = (buf[i + 2] >> 1) & 0x01;
                int channelMode = (buf[i + 3] >> 6) & 0x03; // 3 = mono

                if (versionId == 1 || layer == 0 || bitrateIdx == 0 || bitrateIdx == 15 || srIdx == 3)
                    continue;

                int bitrate = GetMp3Bitrate(versionId, layer, bitrateIdx);
                int sampleRate = GetMp3SampleRate(versionId, srIdx);
                if (bitrate <= 0 || sampleRate <= 0) continue;

                int samplesPerFrame = (layer == 3) ? 384 : (layer == 2 || versionId == 3 ? 1152 : 576);
                int frameLen = (layer == 3)
                    ? ((12 * bitrate * 1000 / sampleRate) + padding) * 4
                    : ((samplesPerFrame / 8 * bitrate * 1000) / sampleRate) + padding;
                if (frameLen < 24) continue;

                // 优先检查 Xing / Info VBR 头
                bool isMono = channelMode == 3;
                int xingOffset = i + 4 + (versionId == 3 ? (isMono ? 17 : 32) : (isMono ? 9 : 17));
                if (xingOffset + 12 <= read &&
                    ((buf[xingOffset] == 'X' && buf[xingOffset + 1] == 'i' && buf[xingOffset + 2] == 'n' && buf[xingOffset + 3] == 'g') ||
                     (buf[xingOffset] == 'I' && buf[xingOffset + 1] == 'n' && buf[xingOffset + 2] == 'f' && buf[xingOffset + 3] == 'o')))
                {
                    uint flags = ReadUInt32BE(buf, xingOffset + 4);
                    if ((flags & 0x01) != 0)
                    {
                        uint frames = ReadUInt32BE(buf, xingOffset + 8);
                        if (frames > 0)
                        {
                            double dur = (double)frames * samplesPerFrame / sampleRate;
                            if (IsValidDuration(dur))
                            {
                                seconds = dur;
                                return true;
                            }
                        }
                    }
                }

                // 检查 VBRI 头（偏移 36）
                int vbriOffset = i + 36;
                if (vbriOffset + 18 <= read &&
                    buf[vbriOffset] == 'V' && buf[vbriOffset + 1] == 'B' && buf[vbriOffset + 2] == 'R' && buf[vbriOffset + 3] == 'I')
                {
                    uint frames = ReadUInt32BE(buf, vbriOffset + 14);
                    if (frames > 0)
                    {
                        double dur = (double)frames * samplesPerFrame / sampleRate;
                        if (IsValidDuration(dur))
                        {
                            seconds = dur;
                            return true;
                        }
                    }
                }

                // CBR：校验下一帧同步头，防止误匹配随机字节
                int nextFrame = i + frameLen;
                if (nextFrame + 4 <= read && buf[nextFrame] == 0xFF && (buf[nextFrame + 1] & 0xE0) == 0xE0)
                {
                    long audioBytes = fileLen - (audioStart + i);
                    // 扣除末尾 ID3v1 标签（128 字节）
                    if (fileLen >= 128)
                    {
                        fs.Position = fileLen - 128;
                        var tag = new byte[3];
                        if (ReadExact(fs, tag, 0, 3) && tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G')
                            audioBytes -= 128;
                    }
                    if (audioBytes > 0)
                    {
                        double dur = (audioBytes * 8.0) / (bitrate * 1000.0);
                        if (IsValidDuration(dur))
                        {
                            seconds = dur;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static int GetMp3Bitrate(int versionId, int layer, int idx)
        {
            if (versionId == 3)
            {
                if (layer == 3) return Mp3BitrateV1L1[idx];
                if (layer == 2) return Mp3BitrateV1L2[idx];
                return Mp3BitrateV1L3[idx];
            }
            return layer == 3 ? Mp3BitrateV2L1[idx] : Mp3BitrateV2L23[idx];
        }

        private static int GetMp3SampleRate(int versionId, int idx)
        {
            if (versionId == 3) return Mp3SampleRateV1[idx];
            if (versionId == 2) return Mp3SampleRateV2[idx];
            return Mp3SampleRateV25[idx];
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = stream.Read(buffer, offset + total, count - total);
                if (n <= 0) return false;
                total += n;
            }
            return true;
        }

        private static uint ReadUInt32LE(byte[] b, int offset)
        {
            return (uint)(b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24));
        }

        private static uint ReadUInt32BE(byte[] b, int offset)
        {
            return (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);
        }
    }
}
