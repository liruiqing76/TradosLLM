using System;
using System.Collections.Generic;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using TradosToolkit.Glossaries;
using TradosToolkit.Verification;

namespace TradosToolkit.BatchTasks
{
    /// <summary>
    /// QA 批处理处理器：逐段检查 漏译/空译、数字一致性、标点一致性、
    /// 术语未采用（对照本地术语库 pre 词）与重复段译法一致性，经 ReportMessage 输出到批处理报告。
    /// 规则核心复用 QaRules（与原生验证器 ToolkitQaVerifier 共用同一套实现）。
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

            var src = QaRules.GetPlainText(pair.Source);
            var tgt = QaRules.GetPlainText(pair.Target);
            var srcTrim = src.Trim();
            var tgtTrim = tgt.Trim();
            if (srcTrim.Length == 0)
                return;

            if (_settings.CheckUntranslated)
                foreach (var issue in QaRules.CheckUntranslated(srcTrim, tgtTrim))
                    Report(issue, srcTrim);

            // 空译文只报漏译，其余检查无意义——与 ToolkitQaVerifier 的口径保持一致，
            // 否则同段在批任务与原生验证器下结论不同（空译段会刷「数字不一致」）。
            if (tgtTrim.Length == 0)
                return;

            if (_settings.CheckNumbers)
                foreach (var issue in QaRules.CheckNumbers(srcTrim, tgt))
                    Report(issue, srcTrim);

            if (_settings.CheckPunctuation)
                foreach (var issue in QaRules.CheckPunctuation(srcTrim, tgt))
                    Report(issue, srcTrim);

            if (_settings.CheckTermAdoption)
                foreach (var issue in QaRules.CheckTermAdoption(srcTrim, tgt, _terms))
                    Report(issue, srcTrim);

            if (_settings.CheckConsistency)
                foreach (var issue in QaRules.CheckConsistency(srcTrim, tgt, _seen))
                    Report(issue, srcTrim);
        }

        private void Report(QaIssue issue, string locationText)
        {
            var origin = string.IsNullOrEmpty(_fileName) ? nameof(QaCheckProcessor) : _fileName;
            var message = issue.Rule + "：" + issue.Detail;
            ReportMessage(string.IsNullOrEmpty(locationText) ? issue.Rule : locationText,
                origin, issue.Level, message, message);
        }
    }
}
