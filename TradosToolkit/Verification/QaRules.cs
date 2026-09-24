using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using TradosToolkit.Glossaries;

namespace TradosToolkit.Verification
{
    /// <summary>单条 QA 检查结果。</summary>
    public sealed class QaIssue
    {
        public string Rule { get; set; }
        public string Detail { get; set; }
        public ErrorLevel Level { get; set; }
        /// <summary>问题在译文中的起始偏移（若可定位），否则 -1。</summary>
        public int TargetOffset { get; set; }
        /// <summary>问题在译文中的长度（若可定位），否则 -1。</summary>
        public int TargetLength { get; set; }
    }

    /// <summary>
    /// QA 检查规则核心：数字一致性、标点一致性、术语未采用、重复段译法一致性。
    /// 供 QaCheckProcessor（批处理）和 ToolkitQaVerifier（原生验证器）共用。
    /// </summary>
    public static class QaRules
    {
        private const string TerminalPunct = "。！？.!?；;";

        /// <summary>递归抽取段内纯文本（跳过标签/占位符/注释）。</summary>
        public static string GetPlainText(ISegment segment)
        {
            if (segment == null) return string.Empty;
            var sb = new StringBuilder();
            Collect(segment, sb);
            return sb.ToString();
        }

        private static void Collect(IAbstractMarkupDataContainer container, StringBuilder sb)
        {
            if (container == null) return;
            // IAbstractMarkupDataContainer 实现 IList<IAbstractMarkupData>，用 Count + 索引器遍历：
            // Studio 2019 的部分实现在 GetEnumerator 上抛 NotImplemented，foreach 会直接失败。
            int count;
            try { count = container.Count; }
            catch (NotImplementedException) { CollectByEnumerator(container, sb); return; }
            for (var i = 0; i < count; i++)
            {
                IAbstractMarkupData item;
                try { item = container[i]; }
                catch (NotImplementedException) { CollectByEnumerator(container, sb); return; }
                CollectItem(item, sb);
            }
        }

        private static void CollectByEnumerator(IAbstractMarkupDataContainer container, StringBuilder sb)
        {
            foreach (IAbstractMarkupData item in container)
                CollectItem(item, sb);
        }

        private static void CollectItem(IAbstractMarkupData item, StringBuilder sb)
        {
            if (item is IText text)
                sb.Append(text.Properties.Text);
            else if (item is IAbstractMarkupDataContainer child)
                Collect(child, sb);
        }

        /// <summary>漏译 / 空译检查。</summary>
        public static List<QaIssue> CheckUntranslated(string src, string tgt)
        {
            var issues = new List<QaIssue>();
            var srcTrim = src.Trim();
            var tgtTrim = tgt.Trim();
            if (srcTrim.Length == 0) return issues;
            if (tgtTrim.Length == 0)
            {
                issues.Add(new QaIssue { Rule = "空译文", Detail = "目标段为空，源文尚未翻译。", Level = ErrorLevel.Error, TargetOffset = 0, TargetLength = 0 });
                return issues;
            }
            if (Equals(srcTrim, tgtTrim) && srcTrim.Length > 3)
            {
                issues.Add(new QaIssue { Rule = "疑未翻译", Detail = "目标与源文完全一致，可能漏翻。", Level = ErrorLevel.Warning, TargetOffset = 0, TargetLength = tgtTrim.Length });
            }
            return issues;
        }

        /// <summary>数字一致性：源文中的每个数字都须出现在译文。</summary>
        public static List<QaIssue> CheckNumbers(string src, string tgt)
        {
            var issues = new List<QaIssue>();
            var tgtForCompare = Regex.Replace(tgt, @"\s+", string.Empty);
            foreach (Match m in Regex.Matches(src, @"-?\d+(?:[.,]\d+)*"))
            {
                if (tgtForCompare.IndexOf(m.Value, StringComparison.Ordinal) < 0)
                    issues.Add(new QaIssue { Rule = "数字不一致", Detail = "目标段缺少源文中的数字。", Level = ErrorLevel.Warning, TargetOffset = -1, TargetLength = -1 });
            }
            return issues;
        }

        /// <summary>标点一致性：结尾终结标点须与源文一致。</summary>
        public static List<QaIssue> CheckPunctuation(string src, string tgt)
        {
            var issues = new List<QaIssue>();
            var srcEnd = LastTerminal(src);
            var tgtEnd = LastTerminal(tgt);
            if (srcEnd == '\0' || tgtEnd == '\0') return issues;
            if (srcEnd != tgtEnd && !IsEquivalentTerminal(srcEnd, tgtEnd))
                issues.Add(new QaIssue { Rule = "标点不一致", Detail = "结尾标点与源文不一致（源" + srcEnd + "/译" + tgtEnd + "）。", Level = ErrorLevel.Warning, TargetOffset = -1, TargetLength = -1 });
            return issues;
        }

        /// <summary>术语未采用：源文命中术语，译文须含对应译法。</summary>
        public static List<QaIssue> CheckTermAdoption(string src, string tgt, IList<GlossaryEntry> terms)
        {
            var issues = new List<QaIssue>();
            if (terms == null || terms.Count == 0) return issues;
            var tgtLower = tgt.ToLowerInvariant();
            var srcLower = src.ToLowerInvariant();
            foreach (var e in terms)
            {
                var from = e.From;
                var to = e.To;
                if (from == null || from.Length < 2 || to == null || to.Length == 0) continue;
                if (srcLower.IndexOf(from.ToLowerInvariant(), StringComparison.Ordinal) >= 0
                    && tgtLower.IndexOf(to.ToLowerInvariant(), StringComparison.Ordinal) < 0)
                {
                    issues.Add(new QaIssue
                    {
                        Rule = "术语未采用",
                        Detail = "术语\"" + from + "\"建议译法\"" + to + "\"未出现在译文。",
                        Level = ErrorLevel.Note,
                        TargetOffset = -1,
                        TargetLength = -1,
                    });
                }
            }
            return issues;
        }

        /// <summary>重复段译法一致性检查。seenDict 由调用方维护跨段记忆。</summary>
        public static List<QaIssue> CheckConsistency(string src, string tgt, Dictionary<string, string> seenDict)
        {
            var issues = new List<QaIssue>();
            if (seenDict.TryGetValue(src, out var first))
            {
                if (!Equals(first, tgt))
                    issues.Add(new QaIssue { Rule = "重复段译法不一致", Detail = "相同源文此前译为\"" + first + "\"，本次为\"" + tgt + "\"。", Level = ErrorLevel.Note, TargetOffset = 0, TargetLength = tgt.Length });
            }
            else
            {
                seenDict[src] = tgt;
            }
            return issues;
        }

        private static bool IsTerminalPunct(char c) => TerminalPunct.IndexOf(c) >= 0;

        private static char LastTerminal(string s)
        {
            for (var i = s.Length - 1; i >= 0; i--)
                if (IsTerminalPunct(s[i])) return s[i];
            return '\0';
        }

        private static bool IsEquivalentTerminal(char a, char b)
        {
            if (a == b) return true;
            return (a == '。' && b == '.') || (a == '.' && b == '。')
                || (a == '？' && b == '?') || (a == '?' && b == '？')
                || (a == '！' && b == '!') || (a == '!' && b == '！')
                || (a == '；' && b == ';') || (a == ';' && b == '；');
        }
    }
}
