using System;
using System.IO;
using System.Text;

namespace Lumen.Lyrics
{
    /// <summary>
    /// 文本编码识别：BOM → UTF-16 LE/BE → UTF-8 严格校验 → GBK 回退。
    /// 对应 README 的「UTF-8 / GBK / UTF-16 自动识别」。
    /// </summary>
    public static class TextEncoding
    {
        private static readonly object ProviderGate = new object();
        private static volatile bool _providerReady;
        private static Encoding _gbk;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false, throwOnInvalidBytes: false);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

        /// <summary>
        /// 注册 CodePagesEncodingProvider（提供 GBK/936 等代码页）。
        ///
        /// 说明：.NET Core 起 GBK 不再内置，必须注册该提供程序。
        /// 程序集 System.Text.Encoding.CodePages.dll 随 .NET 运行时一起分发，
        /// 但默认不会被加载，这里通过反射显式拿到它 —— 这样就不需要额外的
        /// NuGet 包，符合本项目"完全离线还原"的要求。
        /// </summary>
        private static void EnsureProvider()
        {
            if (_providerReady) return;
            lock (ProviderGate)
            {
                if (_providerReady) return;
                try
                {
                    var type = Type.GetType(
                        "System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages",
                        false);
                    if (type == null)
                    {
                        Log.Warn("找不到 CodePagesEncodingProvider，GBK 歌词将无法解码");
                        return;
                    }

                    var property = type.GetProperty("Instance");
                    var instance = property != null ? property.GetValue(null, null) as EncodingProvider : null;
                    if (instance == null)
                    {
                        Log.Warn("CodePagesEncodingProvider.Instance 不可用");
                        return;
                    }

                    Encoding.RegisterProvider(instance);
                    _gbk = Encoding.GetEncoding(936);
                    Log.Info("已注册代码页提供程序，GBK(936) 可用");
                }
                catch (Exception ex)
                {
                    Log.Warn("注册代码页提供程序失败: " + ex.Message);
                }
                finally
                {
                    _providerReady = true;
                }
            }
        }

        /// <summary>GBK(936)；不可用时返回 null。</summary>
        public static Encoding Gbk
        {
            get
            {
                EnsureProvider();
                return _gbk;
            }
        }

        /// <summary>解码 GBK 字节；无法解码时返回 null。</summary>
        public static string DecodeGbk(byte[] bytes)
        {
            var gbk = Gbk;
            if (gbk == null || bytes == null) return null;
            try { return gbk.GetString(bytes); }
            catch { return null; }
        }

        /// <summary>读取文本文件并按内容猜编码。</summary>
        public static string ReadAllText(string path)
        {
            var bytes = File.ReadAllBytes(path);
            return Decode(bytes);
        }

        public static string Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;

            // 1) BOM
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Utf8NoBom.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            // 2) 猜 UTF-16（无 BOM）：准确区分 LE 与 BE，并结合换行符特征支持纯中文 UTF-16
            var utf16 = LooksLikeUtf16(bytes);
            if (utf16 != null)
                return utf16.GetString(bytes);

            // 3) UTF-8 严格校验：能解码就是 UTF-8
            if (IsValidUtf8(bytes))
                return Utf8NoBom.GetString(bytes);

            // 4) 回退 GBK（中文 Windows 上最常见的歌词编码）
            var gbk = DecodeGbk(bytes);
            if (gbk != null) return gbk;

            // 5) 最后兜底：latin-1 保证不丢字节
            return Encoding.Latin1.GetString(bytes);
        }

        /// <summary>严格 UTF-8 校验（拒绝非法续字节 / 超长编码 / 代理区）。</summary>
        public static bool IsValidUtf8(byte[] bytes)
        {
            if (bytes == null) return false;
            if (bytes.Length == 0) return true;
            try
            {
                StrictUtf8.GetCharCount(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>
        /// 探测无 BOM 的 UTF-16 字节流，返回 Encoding.Unicode (LE) 或 Encoding.BigEndianUnicode (BE)；
        /// 无法确认时返回 null。
        /// </summary>
        private static Encoding LooksLikeUtf16(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 4 || bytes.Length % 2 != 0) return null;

            int zerosOdd = 0, zerosEven = 0;
            int leNewlines = 0, beNewlines = 0;
            int limit = Math.Min(bytes.Length, 1024);
            if (limit % 2 != 0) limit--;

            for (int i = 0; i + 1 < limit; i += 2)
            {
                byte b0 = bytes[i];
                byte b1 = bytes[i + 1];

                if (b0 == 0) zerosEven++;
                if (b1 == 0) zerosOdd++;

                if ((b0 == 0x0A || b0 == 0x0D) && b1 == 0x00) leNewlines++;
                if (b0 == 0x00 && (b1 == 0x0A || b1 == 0x0D)) beNewlines++;
            }

            int pairs = limit / 2;
            if (pairs == 0) return null;

            // ASCII 占比较高时，零字节位置明确区分 LE / BE
            if (zerosOdd > pairs * 0.3 && zerosOdd > zerosEven * 2)
                return Encoding.Unicode;
            if (zerosEven > pairs * 0.3 && zerosEven > zerosOdd * 2)
                return Encoding.BigEndianUnicode;

            // 纯中文等多字节 UTF-16：依靠 UTF-16 换行符特征 (0x0A 0x00 / 0x00 0x0A) 辅助识别
            if (leNewlines >= 1 && beNewlines == 0 && zerosEven == 0)
                return Encoding.Unicode;
            if (beNewlines >= 1 && leNewlines == 0 && zerosOdd == 0)
                return Encoding.BigEndianUnicode;

            return null;
        }

        /// <summary>去掉 UTF-8 BOM 字符。</summary>
        public static string StripBom(string text)
        {
            if (!string.IsNullOrEmpty(text) && text[0] == '\uFEFF') return text.Substring(1);
            return text;
        }
    }
}
