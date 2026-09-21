using System;
using System.Collections.Generic;
using System.Linq;
using Sdl.Core.Globalization;
using Sdl.Terminology.TerminologyProvider.Core;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 原生术语源：把线上术语服务 termBaseUrl 暴露给 Studio 的原生术语引擎。
    /// 编辑器术语识别（Fuzzy→/match）、术语库查词（Normal→/search）、验证器检查都由它承接。
    /// 与本地 SQLite 术语库（Glossaries/，供翻译流水线 + GlossaryManager 人工干预）完全隔离，
    /// 只读取不写入（IsReadOnly=true，编辑仍走 GlossaryManager）。
    /// 继承 AbstractTerminologyProvider（Studio15 术语 API 的推荐基类，见 references IATETerminologyProvider）。
    /// </summary>
    public sealed class NativeTerminologyProvider : AbstractTerminologyProvider
    {
        private readonly string _baseUrl;
        private readonly string _sourceLang;
        private readonly string _targetLang;
        private readonly string _domain;
        private readonly List<IEntry> _entryCache = new List<IEntry>();

        public NativeTerminologyProvider(string baseUrl, string sourceLang, string targetLang, string domain = null)
        {
            _baseUrl = baseUrl ?? string.Empty;
            _sourceLang = sourceLang ?? "en-US";
            _targetLang = targetLang ?? "ru-RU";
            _domain = string.IsNullOrWhiteSpace(domain) ? Glossaries.DomainTree.DefaultDomain : domain.Trim();
        }

        public override IDefinition Definition => new Definition(GetDescriptiveFields(), GetDefinitionLanguages());

        public override string Description => "从内网术语服务实时检索的本机只读术语源";

        public override string Name => "TradosToolkit 术语服务";

        /// <summary>URI 约定见 NativeTerminologyProviderHelper（携带 base/src/tgt/domain）。</summary>
        public override Uri Uri => NativeTerminologyProviderHelper.BuildUri(_baseUrl, _sourceLang, _targetLang, _domain);

        public override IEntry GetEntry(int id)
        {
            return _entryCache.FirstOrDefault(e => e.Id == id);
        }

        public override IEntry GetEntry(int id, IEnumerable<ILanguage> languages)
        {
            return GetEntry(id);
        }

        public override IList<ILanguage> GetLanguages()
        {
            var list = new List<IDefinitionLanguage>
            {
                MakeLanguage(_sourceLang),
                MakeLanguage(_targetLang),
            };
            return list.Cast<ILanguage>().ToList();
        }

        /// <summary>
        /// Fuzzy = 编辑器术语识别 → 段文本包含匹配（POST /match）。
        /// Normal = 术语库查词窗口 → 前缀匹配（GET /search）。
        /// 结果经 TermEntriesChanged 通知 Studio 编辑器绘制下划线。
        /// </summary>
        public override IList<ISearchResult> Search(string text, ILanguage source, ILanguage destination,
                                                    int maxResultsCount, SearchMode mode, bool targetRequired)
        {
            _entryCache.Clear();
            if (string.IsNullOrWhiteSpace(_baseUrl))
                return new List<ISearchResult>();

            var src = source?.Locale != null ? source.Locale.Name : _sourceLang;
            var tgt = destination?.Locale != null ? destination.Locale.Name : _targetLang;

            var hits = mode == SearchMode.Normal
                ? TermHttpClient.Search(_baseUrl, src, tgt, text, maxResultsCount, _domain)
                : TermHttpClient.Match(_baseUrl, src, tgt, text, maxResultsCount, _domain);

            var results = new List<ISearchResult>();
            foreach (var hit in hits)
            {
                if (string.IsNullOrEmpty(hit.target)) continue;
                _entryCache.Add(BuildEntry(hit));
                var result = new SearchResult
                {
                    Text = hit.source ?? hit.target,
                    Score = Math.Max(0, hit.score),
                    Id = hit.id,
                };
                results.Add(result);
            }
            return results;
        }

        public IList<IDescriptiveField> GetDescriptiveFields()
        {
            return new List<IDescriptiveField>
            {
                new DescriptiveField { Label = "Source", Level = FieldLevel.EntryLevel, Type = FieldType.String },
            };
        }

        /// <summary>语言对来自本 Provider 构造参数；无项目上下文时可正确向 Studio 声明。</summary>
        public IList<IDefinitionLanguage> GetDefinitionLanguages()
        {
            return new List<IDefinitionLanguage>
            {
                MakeLanguage(_sourceLang),
                MakeLanguage(_targetLang),
            };
        }

        private static IDefinitionLanguage MakeLanguage(string locale)
        {
            var lang = new Language(locale);
            return new DefinitionLanguage
            {
                IsBidirectional = true,
                Locale = lang.CultureInfo,
                Name = lang.DisplayName,
                TargetOnly = false,
            };
        }

        private static IEntry BuildEntry(TermHit hit)
        {
            var entry = new Entry { Id = hit.id };
            if (!string.IsNullOrEmpty(hit.source))
            {
                var srcLang = new EntryLanguage();
                srcLang.Terms.Add(new EntryTerm { Value = hit.source });
                entry.Languages.Add(srcLang);
            }
            if (!string.IsNullOrEmpty(hit.target))
            {
                var tgtLang = new EntryLanguage();
                tgtLang.Terms.Add(new EntryTerm { Value = hit.target });
                entry.Languages.Add(tgtLang);
            }
            return entry;
        }
    }
}