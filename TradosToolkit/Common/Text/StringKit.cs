using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TradosToolkit.Common
{
    /// <summary>
    /// 字符串处理工具箱：全部空值安全，比较/大小写一律走 <see cref="StringComparison.OrdinalIgnoreCase"/>，
    /// 与操作系统区域设置无关（避免土耳其语 i 等区域陷阱）。
    /// 集中放各模块反复手写的小工具：判空、Trim、截断、包含判断、脱敏、文件名安全化等。
    /// </summary>
    public static class StringKit
    {
        /// <summary>null / 空串 / 纯空白 都算 blank。</summary>
        public static bool IsBlank(string s)
        {
            return string.IsNullOrWhiteSpace(s);
        }

        public static bool IsNotBlank(string s)
        {
            return !string.IsNullOrWhiteSpace(s);
        }

        /// <summary>Trim 后为空则返回 null，便于"空即未设置"的判断。</summary>
        public static string TrimToNull(string s)
        {
            if (s == null) return null;
            var t = s.Trim();
            return t.Length == 0 ? null : t;
        }

        /// <summary>Trim 后返回；null 归一为空串（绝不返回 null）。</summary>
        public static string TrimToEmpty(string s)
        {
            return s == null ? string.Empty : s.Trim();
        }

        /// <summary>blank 时返回 fallback，否则原样返回（不 Trim）。</summary>
        public static string Or(string s, string fallback)
        {
            return IsBlank(s) ? fallback : s;
        }

        /// <summary>blank 时返回 fallback，否则返回 Trim 后的值。</summary>
        public static string OrTrimmed(string s, string fallback)
        {
            return IsBlank(s) ? fallback : s.Trim();
        }

        public static bool EqualsIgnoreCase(string a, string b)
        {
            return string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>区域无关的"包含"判断；任一侧为空返回 false。</summary>
        public static bool ContainsIgnoreCase(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>命中任一关键字即为 true（关键字里的空项忽略）。</summary>
        public static bool ContainsAnyIgnoreCase(string haystack, params string[] needles)
        {
            if (string.IsNullOrEmpty(haystack) || needles == null) return false;
            foreach (var n in needles)
                if (!string.IsNullOrEmpty(n) && haystack.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        /// <summary>把连续空白（含换行）压成单个空格并 Trim。</summary>
        public static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            var pendingSpace = false;
            foreach (var c in s)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (sb.Length > 0) pendingSpace = true;
                }
                else
                {
                    if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        /// <summary>按行拆分（兼容 \r\n / \n / \r），去掉首尾空白行，保留行内内容。</summary>
        public static List<string> SplitLines(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (var raw in s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                list.Add(raw);
            return list;
        }

        /// <summary>用分隔符拼接非空项（各段先 Trim）。</summary>
        public static string JoinNonEmpty(string separator, params string[] parts)
        {
            if (parts == null) return string.Empty;
            var kept = new List<string>(parts.Length);
            foreach (var p in parts)
                if (IsNotBlank(p)) kept.Add(p.Trim());
            return string.Join(separator, kept);
        }

        /// <summary>超长则截断并追加省略号（按 char 计，非字素簇）。</summary>
        public static string Truncate(string s, int maxLength, string ellipsis = "…")
        {
            if (s == null) return string.Empty;
            if (maxLength <= 0) return string.Empty;
            if (s.Length <= maxLength) return s;
            var cut = Math.Max(0, maxLength - (ellipsis == null ? 0 : ellipsis.Length));
            return s.Substring(0, cut) + (ellipsis ?? string.Empty);
        }

        /// <summary>超长时保留首尾、中间省略（日志/路径展示常用）。</summary>
        public static string EllipsisMiddle(string s, int maxLength, string ellipsis = "…")
        {
            if (s == null) return string.Empty;
            if (maxLength <= 0) return string.Empty;
            if (s.Length <= maxLength) return s;
            var ell = ellipsis ?? string.Empty;
            if (maxLength <= ell.Length) return s.Substring(0, maxLength);
            var keep = maxLength - ell.Length;
            var head = (keep + 1) / 2;
            var tail = keep - head;
            return s.Substring(0, head) + ell + s.Substring(s.Length - tail);
        }

        /// <summary>安全截取：越界自动收敛，绝不抛异常。</summary>
        public static string SafeSubstring(string s, int start, int length)
        {
            if (string.IsNullOrEmpty(s) || length <= 0) return string.Empty;
            if (start < 0) start = 0;
            if (start >= s.Length) return string.Empty;
            var len = Math.Min(length, s.Length - start);
            return s.Substring(start, len);
        }

        /// <summary>
        /// 密钥脱敏：长度 &lt;= 4 时全掩；否则保留首尾各 2 位，中间用 * 代替。
        /// 供日志/状态页展示 apiKey 等敏感串。
        /// </summary>
        public static string MaskSecret(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return string.Empty;
            if (secret.Length <= 4) return new string('*', secret.Length);
            var middle = new string('*', Math.Min(8, secret.Length - 4));
            return secret.Substring(0, 2) + middle + secret.Substring(secret.Length - 2);
        }

        /// <summary>转成"安全的文件/标识名"：非字母数字（保留 . _ -）换成 -，压掉重复并去首尾。</summary>
        public static string Slug(string s, string fallback = "item")
        {
            if (IsBlank(s)) return fallback;
            var sb = new StringBuilder(s.Length);
            var lastDash = false;
            foreach (var c in s.Trim())
            {
                var ok = char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-';
                if (ok)
                {
                    sb.Append(c);
                    lastDash = c == '-';
                }
                else if (!lastDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastDash = true;
                }
            }
            var result = sb.ToString().Trim('-', '.', '_');
            return result.Length == 0 ? fallback : result;
        }

        /// <summary>区域无关小写。</summary>
        public static string Lower(string s)
        {
            return (s ?? string.Empty).ToLowerInvariant();
        }

        /// <summary>区域无关比较（供排序用）。</summary>
        public static int CompareIgnoreCase(string a, string b)
        {
            return string.Compare(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>把代码串（如 zh-CN、zh_CN）归一为 CultureInfo 可识别的形态，失败返回 null。</summary>
        public static CultureInfo TryGetCulture(string code)
        {
            if (IsBlank(code)) return null;
            try
            {
                return new CultureInfo(code.Replace('_', '-'));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>取语言的英文名（区域无关），取不到则原样返回代码。</summary>
        public static string EnglishName(string code)
        {
            var c = TryGetCulture(code);
            return c == null ? code : c.EnglishName;
        }
    }
}
