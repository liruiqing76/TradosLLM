using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TradosToolkit.Glossaries
{
    /// <summary>
    /// 按条目顺序做文本替换（长词优先，避免短词先吃掉长词的命中）。
    /// 译前/译后共用同一个替换器，只是作用文本不同。
    /// </summary>
    public class TermReplacer
    {
        private readonly List<GlossaryEntry> _entries;

        public TermReplacer(IEnumerable<GlossaryEntry> entries)
        {
            _entries = (entries ?? Enumerable.Empty<GlossaryEntry>())
                .Where(e => !string.IsNullOrEmpty(e?.From))
                .OrderByDescending(e => e.From.Length)
                .ToList();
        }

        public bool IsEmpty => _entries.Count == 0;

        public string Replace(string text)
        {
            if (string.IsNullOrEmpty(text) || IsEmpty)
                return text;

            foreach (var entry in _entries)
                text = ReplaceIgnoreCase(text, entry.From, entry.To ?? string.Empty);
            return text;
        }

        /// <summary>大小写不敏感替换（.NET Framework 的 string.Replace 不支持传入比较方式）。
        /// 与 LLM 侧「大小写不敏感命中」的口径对齐，避免同一术语在插件替换与 LLM 替换下结果不同。</summary>
        private static string ReplaceIgnoreCase(string text, string from, string to)
        {
            if (string.IsNullOrEmpty(from)) return text;
            var idx = text.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return text;

            var sb = new StringBuilder(text.Length);
            var pos = 0;
            while (idx >= 0)
            {
                sb.Append(text, pos, idx - pos).Append(to);
                pos = idx + from.Length;
                idx = pos < text.Length
                    ? text.IndexOf(from, pos, StringComparison.OrdinalIgnoreCase)
                    : -1;
            }
            sb.Append(text, pos, text.Length - pos);
            return sb.ToString();
        }
    }
}
