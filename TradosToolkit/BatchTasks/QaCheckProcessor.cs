using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using TradosToolkit.Glossaries;

namespace TradosToolkit.BatchTasks
{
    /// <summary>
    /// QA 批处理处理器：逐段检查 漏译/空译、数字一致性、标点一致性、
    /// 术语未采用（对照本地术语库 pre 词）与重复段译法一致性，经 ReportMessage 输出到批处理报告。
    /// </summary>
    public class QaCheckProcessor : AbstractBilingualContentProcessor
    {
        private readonly QaCheckSettings _settings;
        private string _sourceLang = "en";
        private string _targetLang = "zh-CN";
        private string _fileName = string.Empty;
        private GlossaryDb _glossary;
        private List<GlossaryEntry> _terms;
        private readonly Dictionary<string, string> _seen = new Dictionary<string, string>(StringComparer.Ordinal);

        // 终结标点（用于标点一致性启发式）
        private const string TerminalPunct = "。！？.!?；;";

        public QaCheckProcessor(QaCheckSettings settings)
        {
            _settings = settings ?? new QaCheckSettings();
        }

        public override void SetFileProperties(IFileProperties fileInfo)
        {
            base.SetFileProperties(fileInfo);
            if (fileInfo?.FileConversionProperties == null)
                return;

            _fileName = fileInfo.FileConversionProperties.OriginalFilePath ?? string.Empty;
            _sourceLang = fileInfo.FileConversionProperties.SourceLanguage?.IsoAbbreviation ?? "en";
            _targetLang = fileInfo.FileConversionProperties.TargetLanguage?.IsoAbbreviation ?? "zh-CN";
            _seen.Clear();

            // 换文件时按该文件语言对重新加载术语库
            if (_glossary == null) _glossary = new GlossaryDb();
            try
            {
                _terms = _glossary.GetTerms(GlossaryDb.KindPre, _sourceLang, _targetLang);
            }
            catch
            {
                _terms = null; // 术语库不可用时不阻断 QA
            }
        }

        public override void ProcessParagraphUnit(IParagraphUnit paragraphUnit)
        {
            if (paragraphUnit != null && !paragraphUnit.IsStructure)
            {
                foreach (var pair in paragraphUnit.SegmentPairs)
                    CheckSegment(pair);
            }
            base.ProcessParagraphUnit(paragraphUnit);
        }

        private void CheckSegment(ISegmentPair pair)
        {
            if (pair?.Source == null || pair.Target == null)
                return;

            var src = GetPlainText(pair.Source);
            var tgt = GetPlainText(pair.Target);
            var srcTrim = src.Trim();
            var tgtTrim = tgt.Trim();
            if (srcTrim.Length == 0)
                return;

            if (_settings.CheckUntranslated)
            {
                if (tgtTrim.Length == 0)
                {
                    Report("空译文", "目标段为空，源文尚未翻译。", srcTrim, ErrorLevel.Error);
                    return;
                }
                if (Equals(srcTrim, tgtTrim) && srcTrim.Length > 3)
                    Report("疑未翻译", "目标与源文完全一致，可能漏翻。", srcTrim, ErrorLevel.Warning);
            }

            if (_settings.CheckNumbers) CheckNumbers(srcTrim, tgtTrim);
            if (_settings.CheckPunctuation) CheckPunctuation(srcTrim, tgtTrim);
            if (tgtTrim.Length > 0)
            {
                if (_settings.CheckTermAdoption) CheckTermAdoption(srcTrim, tgtTrim);
                if (_settings.CheckConsistency) CheckConsistency(srcTrim, tgtTrim);
            }
        }

        /// <summary>源文中的每个数字都须出现在译文（忽略空白与大小写差异导致的误报）。</summary>
        private void CheckNumbers(string src, string tgt)
        {
            var tgtForCompare = Regex.Replace(tgt, @"\s+", string.Empty);
            foreach (Match m in Regex.Matches(src, @"-?\d+(?:[.,]\d+)*"))
            {
                if (tgtForCompare.IndexOf(m.Value, StringComparison.Ordinal) < 0)
                    Report("数字不一致", "目标段缺少源文中的数字。", m.Value, ErrorLevel.Warning);
            }
        }

        /// <summary>结尾终结标点须为同类（中文句号/感叹号/问号等）。</summary>
        private void CheckPunctuation(string src, string tgt)
        {
            var srcEnd = LastTerminal(src);
            var tgtEnd = LastTerminal(tgt);
            if (srcEnd == '\0' || tgtEnd == '\0')
                return;
            if (srcEnd != tgtEnd && !IsEquivalentTerminal(srcEnd, tgtEnd))
                Report("标点不一致", "结尾标点与源文不一致（源“" + srcEnd + "”/译“" + tgtEnd + "”）。", src, ErrorLevel.Warning);
        }

        /// <summary>预译术语：源文命中 on-term，译文须含对应 to-term。</summary>
        private void CheckTermAdoption(string src, string tgt)
        {
            if (_terms == null || _terms.Count == 0)
                return;
            var tgtLower = tgt.ToLowerInvariant();
            var srcLower = src.ToLowerInvariant();
            foreach (var e in _terms)
            {
                var from = e.From;
                var to = e.To;
                if (from == null || from.Length < 2 || to == null || to.Length == 0)
                    continue;
                if (srcLower.IndexOf(from.ToLowerInvariant(), StringComparison.Ordinal) >= 0
                    && tgtLower.IndexOf(to.ToLowerInvariant(), StringComparison.Ordinal) < 0)
                    Report("术语未采用", "术语“" + from + "”建议译法“" + to + "”未出现在译文。", src, ErrorLevel.Note);
            }
        }

        private void CheckConsistency(string src, string tgt)
        {
            if (_seen.TryGetValue(src, out var first))
            {
                if (!Equals(first, tgt))
                {
                    Report("重复段译法不一致", "相同源文此前译为“" + first + "”，本次为“" + tgt + "”。",
                        src, ErrorLevel.Note);
                }
            }
            else
            {
                _seen[src] = tgt;
            }
        }

        private void Report(string rule, string detail, string locationText, ErrorLevel level)
        {
            var origin = string.IsNullOrEmpty(_fileName) ? nameof(QaCheckProcessor) : _fileName;
            var message = rule + "：" + detail;
            ReportMessage(locationText ?? rule, origin, level, message, message);
        }

        private static bool IsTerminalPunct(char c) => TerminalPunct.IndexOf(c) >= 0;

        private static char LastTerminal(string s)
        {
            for (var i = s.Length - 1; i >= 0; i--)
                if (IsTerminalPunct(s[i]))
                    return s[i];
            return '\0';
        }

        /// <summary>中文句号与英文句点、中文问号与英文问号等视为同义标点。</summary>
        private static bool IsEquivalentTerminal(char a, char b)
        {
            if (a == b) return true;
            return (a == '。' && b == '.') || (a == '.' && b == '。')
                || (a == '？' && b == '?') || (a == '?' && b == '？')
                || (a == '！' && b == '!') || (a == '!' && b == '！')
                || (a == '；' && b == ';') || (a == ';' && b == '；');
        }

        /// <summary>递归抽取段内纯文本（跳过标签/占位符/注释）。</summary>
        private static string GetPlainText(ISegment segment)
        {
            var sb = new StringBuilder();
            Collect(segment, sb);
            return sb.ToString();
        }

        private static void Collect(IAbstractMarkupDataContainer container, StringBuilder sb)
        {
            foreach (IAbstractMarkupData item in container)
            {
                if (item is IText text)
                    sb.Append(text.Properties.Text);
                else if (item is IAbstractMarkupDataContainer child)
                    Collect(child, sb);
            }
        }
    }
}