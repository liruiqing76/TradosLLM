using System.Collections.Generic;
using System.Linq;

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
                text = text.Replace(entry.From, entry.To ?? string.Empty);
            return text;
        }
    }
}
