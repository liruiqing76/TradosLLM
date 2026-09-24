using System;
using System.Collections.Generic;
using Sdl.Core.Settings;
using Sdl.FileTypeSupport.Framework.BilingualApi;
using Sdl.FileTypeSupport.Framework.NativeApi;
using Sdl.Verification.Api;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;

namespace TradosToolkit.Verification
{
    /// <summary>
    /// 原生全局验证器：保存 sdlxliff 时自动跑 QA 检查，
    /// 结果进 Studio 原生 Messages 面板，双击可跳转到问题段。
    /// 检查规则复用 QaRules（与 QaCheckProcessor 共用核心）。
    /// </summary>
    [GlobalVerifier("Toolkit_Qa_Verifier_Name", "Toolkit_Qa_Verifier_Name", "Toolkit_Qa_Verifier_Description")]
    public class ToolkitQaVerifier : IGlobalVerifier, IBilingualVerifier, ISharedObjectsAware
    {
        private ISharedObjects _sharedObjects;
        private ToolkitQaVerifierSettings _settings;
        private string _sourceLang = "en";
        private string _targetLang = "zh-CN";
        private GlossaryDb _glossary;
        private List<GlossaryEntry> _terms;
        private readonly Dictionary<string, string> _seen = new Dictionary<string, string>(StringComparer.Ordinal);

        internal ToolkitQaVerifierSettings Settings
        {
            get
            {
                if (_settings == null && _sharedObjects != null)
                {
                    var bundle = _sharedObjects.GetSharedObject<object>("SettingsBundle") as ISettingsBundle;
                    if (bundle != null)
                        _settings = bundle.GetSettingsGroup("ToolkitQaVerifier") as ToolkitQaVerifierSettings;
                }
                return _settings;
            }
        }

        // —— ISharedObjectsAware ——
        public void SetSharedObjects(ISharedObjects sharedObjects)
        {
            _sharedObjects = sharedObjects;
        }

        // —— IGlobalVerifier ——
        public string Name => "Toolkit QA Verifier";
        public string Description => "TradosToolkit QA：漏译/数字/标点/术语/一致性检查（保存时自动运行）";
        public System.Drawing.Icon Icon => null;

        public string SettingsId => "ToolkitQaVerifier";

        public Type SettingsType => typeof(ToolkitQaVerifierSettings);

        public string HelpTopic => string.Empty;

        public IList<string> GetSettingsPageExtensionIds()
        {
            // 无自定义设置页面（设置走 Options → Verification 默认界面）
            return new List<string>();
        }

        // —— IBilingualContentHandler / IBilingualVerifier ——
        public IDocumentItemFactory ItemFactory { get; set; }
        public IBilingualContentMessageReporter MessageReporter { get; set; }

        public void Initialize(IDocumentProperties documentInfo)
        {
            _seen.Clear();
        }

        public void SetFileProperties(IFileProperties fileInfo)
        {
            if (fileInfo?.FileConversionProperties == null) return;
            _sourceLang = fileInfo.FileConversionProperties.SourceLanguage?.IsoAbbreviation ?? "en";
            _targetLang = fileInfo.FileConversionProperties.TargetLanguage?.IsoAbbreviation ?? "zh-CN";
            _seen.Clear();

            if (_glossary == null) _glossary = new GlossaryDb();
            try
            {
                _terms = _glossary.GetTerms(GlossaryDb.KindPre, _sourceLang, _targetLang);
            }
            catch
            {
                _terms = null;
            }
        }

        public void ProcessParagraphUnit(IParagraphUnit paragraphUnit)
        {
            if (paragraphUnit == null || paragraphUnit.IsStructure) return;

            var s = Settings;
            var checkUntrans = s == null || s.CheckUntranslated;
            var checkNum = s == null || s.CheckNumbers;
            var checkPunct = s == null || s.CheckPunctuation;
            // 与其余项一致：设置不可用时按「开启」处理，而不是静默关闭（否则同一份设置下
            // 术语检查在批任务里开、在原生验证器里关，结论对不上）。
            var checkTerm = s == null || s.CheckTermAdoption;
            var checkCons = s == null || s.CheckConsistency;

            foreach (var pair in paragraphUnit.SegmentPairs)
            {
                if (pair?.Source == null || pair.Target == null) continue;

                var src = QaRules.GetPlainText(pair.Source);
                var tgt = QaRules.GetPlainText(pair.Target);
                var srcTrim = src.Trim();
                if (srcTrim.Length == 0) continue;

                var allIssues = new List<QaIssue>();

                var hasTarget = tgt.Trim().Length > 0;
                if (checkUntrans) allIssues.AddRange(QaRules.CheckUntranslated(srcTrim, tgt.Trim()));
                if (hasTarget)
                {
                    if (checkNum) allIssues.AddRange(QaRules.CheckNumbers(srcTrim, tgt));
                    if (checkPunct) allIssues.AddRange(QaRules.CheckPunctuation(srcTrim, tgt));
                    if (checkTerm) allIssues.AddRange(QaRules.CheckTermAdoption(srcTrim, tgt, _terms));
                    if (checkCons) allIssues.AddRange(QaRules.CheckConsistency(srcTrim, tgt, _seen));
                }

                foreach (var issue in allIssues)
                {
                    var locationText = srcTrim;
                    var message = issue.Rule + "：" + issue.Detail;
                    var targetSegment = pair.Target;
                    var startLoc = new TextLocation(new Location(targetSegment, true), 0);
                    var endLoc = new TextLocation(new Location(targetSegment, false), Math.Max(0, tgt.Length - 1));
                    try
                    {
                        MessageReporter.ReportMessage(this, "Toolkit QA", issue.Level, message, startLoc, endLoc);
                    }
                    catch (Exception ex)
                    {
                        ToolkitLog.Error("ToolkitQaVerifier.ReportMessage 异常", ex);
                    }
                }
            }
        }

        public void FileComplete() { }
        public void Complete() { }
    }
}
