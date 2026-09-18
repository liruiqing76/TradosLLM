using System;
using System.Collections.Generic;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;
using TradosToolkit.TranslationProvider.Engines;

namespace TradosToolkit.TranslationProvider
{
    /// <summary>
    /// 两种引擎共用的 ITranslationProvider 实现。
    /// 配置在 URI query 中（随项目持久化），API Key 由工厂从凭据存储取出后注入。
    /// 成员签名对齐 Studio15 SDK。
    /// </summary>
    public class ToolkitTranslationProvider : ITranslationProvider
    {
        private readonly ITranslationEngine _engine;
        private readonly string _apiKey;
        private readonly SqliteGlossaryProvider _glossaries = new SqliteGlossaryProvider();
        private readonly Dictionary<string, ITranslationProviderLanguageDirection> _directions =
            new Dictionary<string, ITranslationProviderLanguageDirection>();

        private string _state;

        public ToolkitTranslationProvider(Uri uri, string state, ITranslationEngine engine, string apiKey)
        {
            Uri = uri;
            _state = state;
            _engine = engine;
            _apiKey = apiKey;
        }

        public ITranslationProviderLanguageDirection GetLanguageDirection(LanguagePair languageDirection)
        {
            var key = languageDirection.SourceCultureName + ">" + languageDirection.TargetCultureName;
            if (!_directions.TryGetValue(key, out var direction))
            {
                ToolkitLog.Info("GetLanguageDirection: " + key + " pre=" + ToolkitUri.UsePreTerms(Uri) +
                                " post=" + ToolkitUri.UsePostTerms(Uri) + " tags=" + ToolkitUri.SupportsTags(Uri));
                direction = new ToolkitTranslationProviderLanguageDirection(
                    this, _engine, _apiKey, languageDirection,
                    ToolkitUri.UsePreTerms(Uri), ToolkitUri.UsePostTerms(Uri),
                    ToolkitUri.SupportsTags(Uri), _glossaries);
                _directions[key] = direction;
            }
            return direction;
        }

        public bool SupportsLanguageDirection(LanguagePair languageDirection)
        {
            return languageDirection != null;
        }

        /// <summary>Studio 的 TranslationMemoriesControl 会直接解引用 StatusInfo.Available，返回 null 必 NRE。</summary>
        public ProviderStatusInfo StatusInfo { get; } = new ProviderStatusInfo(true, "TradosToolkit ready");

        public void RefreshStatusInfo()
        {
        }

        public Uri Uri { get; }

        public string Name => _engine.DisplayName;

        public bool SupportsTaggedInput => false;

        public bool SupportsScoring => true;

        public bool SupportsSearchForTranslationUnits => true;

        public bool SupportsMultipleResults => false;

        public bool SupportsFilters => false;

        public bool SupportsPenalties => false;

        public bool SupportsStructureContext => false;

        public bool SupportsDocumentSearches => false;

        public bool SupportsUpdate => false;

        public bool SupportsPlaceables => false;

        /// <summary>SDK 级联搜索（TranslationProviderSearcherBase.GetUnsupportedCascadeMessages）
        /// 对非 Concordance 查询先检查此标志，false 时直接报
        /// "does not support translation" 并跳过我们的 Search 调用。必须为 true。</summary>
        public bool SupportsTranslation => true;

        public bool SupportsFuzzySearch => false;

        public bool SupportsConcordanceSearch => false;

        public bool SupportsSourceConcordanceSearch => false;

        public bool SupportsTargetConcordanceSearch => false;

        public bool SupportsWordCounts => false;

        public TranslationMethod TranslationMethod => TranslationMethod.MachineTranslation;

        public bool IsReadOnly => true;

        public string SerializeState()
        {
            return _state ?? string.Empty;
        }

        public void LoadState(string translationProviderState)
        {
            _state = translationProviderState;
        }
    }
}
