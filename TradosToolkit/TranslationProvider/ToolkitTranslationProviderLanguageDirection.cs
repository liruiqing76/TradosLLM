using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Sdl.Core.Globalization;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;
using TradosToolkit.TranslationProvider.Engines;

namespace TradosToolkit.TranslationProvider
{
    /// <summary>
    /// Studio 查询的实际落点，流水线：
    ///   取源文（可选：Tag 换 [[n]] 占位符保护）→ 译前 TermReplacer 替换源文术语
    ///   → ITranslationEngine 翻译 → 译后 TermReplacer 修正译文
    ///   → 占位符回填标签（或纯文本）→ 组装 SearchResults。
    /// 写回类成员（Add/Update*）只读引擎一律不支持。
    /// 成员签名对齐 Studio15 SDK。
    /// </summary>
    public class ToolkitTranslationProviderLanguageDirection : ITranslationProviderLanguageDirection
    {
        private static readonly Regex PlaceholderRegex = new Regex(@"\[\[(\d+)\]\]", RegexOptions.Compiled);

        private readonly ITranslationEngine _engine;
        private readonly string _apiKey;
        private readonly LanguagePair _pair;
        private readonly bool _usePreTerms;
        private readonly bool _usePostTerms;
        private readonly bool _supportsTags;
        private readonly SqliteGlossaryProvider _glossaries;

        private TermReplacer _preReplacer;
        private TermReplacer _postReplacer;

        public ToolkitTranslationProviderLanguageDirection(
            ITranslationProvider translationProvider,
            ITranslationEngine engine,
            string apiKey,
            LanguagePair pair,
            bool usePreTerms,
            bool usePostTerms,
            bool supportsTags,
            SqliteGlossaryProvider glossaries)
        {
            TranslationProvider = translationProvider;
            _engine = engine;
            _apiKey = apiKey;
            _pair = pair;
            _usePreTerms = usePreTerms;
            _usePostTerms = usePostTerms;
            _supportsTags = supportsTags;
            _glossaries = glossaries;
        }

        public ITranslationProvider TranslationProvider { get; }

        public CultureInfo SourceLanguage => _pair.SourceCulture;

        public CultureInfo TargetLanguage => _pair.TargetCulture;

        public bool CanReverseLanguageDirection => false;

        public SearchResults SearchSegment(SearchSettings settings, Segment segment)
        {
            return SearchSegmentsMasked(settings, new[] { segment }, new[] { true })[0];
        }

        public SearchResults[] SearchSegments(SearchSettings settings, Segment[] segments)
        {
            return SearchSegmentsMasked(settings, segments, segments.Select(_ => true).ToArray());
        }

        public SearchResults[] SearchSegmentsMasked(SearchSettings settings, Segment[] segments, bool[] mask)
        {
            if (segments == null || mask == null)
                throw new ArgumentNullException(nameof(segments));
            if (mask.Length != segments.Length)
                throw new ArgumentException("mask 长度与 segments 不一致");

            var requested = 0;
            for (var i = 0; i < mask.Length; i++)
                if (mask[i] && segments[i] != null) requested++;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ToolkitLog.Info("Search 开始: " + _pair.SourceCultureName + "->" + _pair.TargetCultureName +
                            " engine=" + _engine.DisplayName + " 段数=" + requested +
                            " pre=" + _usePreTerms + " post=" + _usePostTerms + " tags=" + _supportsTags);

            try
            {
                var sources = new string[segments.Length];
                var protectedElements = new List<SegmentElement>[segments.Length];
                for (var i = 0; i < segments.Length; i++)
                {
                    if (!mask[i] || segments[i] == null)
                        continue;
                    sources[i] = ExtractText(segments[i], out protectedElements[i]);
                }

                var batches = _engine
                    .TranslateAsync(_pair, sources, mask, _apiKey, default)
                    .GetAwaiter().GetResult();

                var results = new SearchResults[segments.Length];
                var hits = 0;
                for (var i = 0; i < segments.Length; i++)
                {
                    if (!mask[i] || segments[i] == null)
                        continue;
                    var candidate = batches != null && i < batches.Length && batches[i] != null && batches[i].Length > 0
                        ? batches[i][0]
                        : null;
                    results[i] = BuildResult(segments[i], candidate, protectedElements[i]);
                    if (results[i].Results.Count > 0) hits++;
                }
                ToolkitLog.Info("Search 完成: 命中=" + hits + "/" + requested + " 耗时=" + watch.ElapsedMilliseconds + "ms");
                return results;
            }
            catch (Exception e)
            {
                ToolkitLog.Error("Search 失败: " + _pair.SourceCultureName + "->" + _pair.TargetCultureName +
                                 " 段数=" + requested + " 耗时=" + watch.ElapsedMilliseconds + "ms", e);
                throw;
            }
        }

        public SearchResults SearchText(SearchSettings settings, string segment)
        {
            var current = new Segment(_pair.SourceCulture);
            current.Add(segment);
            return SearchSegment(settings, current);
        }

        public SearchResults SearchTranslationUnit(SearchSettings settings, TranslationUnit translationUnit)
        {
            return SearchSegment(settings, translationUnit.SourceSegment);
        }

        public SearchResults[] SearchTranslationUnits(SearchSettings settings, TranslationUnit[] translationUnits)
        {
            return SearchTranslationUnitsMasked(
                settings, translationUnits, translationUnits.Select(tu => tu != null).ToArray());
        }

        public SearchResults[] SearchTranslationUnitsMasked(SearchSettings settings, TranslationUnit[] translationUnits, bool[] mask)
        {
            var segments = translationUnits.Select(tu => tu?.SourceSegment ?? new Segment(_pair.SourceCulture)).ToArray();
            return SearchSegmentsMasked(settings, segments, mask);
        }

        private SearchResults BuildResult(Segment source, EngineResult candidate, List<SegmentElement> protectedElements)
        {
            var searchResults = new SearchResults
            {
                SourceSegment = source.Duplicate(),
                Results = new List<SearchResult>()
            };
            if (candidate == null || string.IsNullOrEmpty(candidate.Translation))
                return searchResults;

            var text = _usePostTerms ? PostReplacer().Replace(candidate.Translation) : candidate.Translation;
            var target = RebuildTarget(text, protectedElements);

            var translationUnit = new TranslationUnit(source.Duplicate(), target)
            {
                Origin = candidate.Origin == "TM"
                    ? TranslationUnitOrigin.TM
                    : TranslationUnitOrigin.MachineTranslation,
                ConfirmationLevel = ConfirmationLevel.Draft
            };
            translationUnit.ResourceId = new PersistentObjectToken(translationUnit.GetHashCode(), Guid.Empty);

            var searchResult = new SearchResult(translationUnit)
            {
                ScoringResult = new ScoringResult { BaseScore = candidate.MatchPercentage },
                TranslationProposal = new TranslationUnit(translationUnit)
            };
            searchResults.Results.Add(searchResult);
            return searchResults;
        }

        /// <summary>
        /// 抽取送引擎的源文本。开启标签支持时把 Tag 换成 [[n]] 占位符保护，
        /// 同时收集对应元素用于回填；关闭时直接取纯文本。抽取后再做译前术语替换。
        /// </summary>
        private string ExtractText(Segment segment, out List<SegmentElement> protectedElements)
        {
            protectedElements = null;
            string text;
            if (_supportsTags && segment.HasTags)
            {
                var builder = new StringBuilder();
                protectedElements = new List<SegmentElement>();
                foreach (var element in segment.Elements)
                {
                    if (element is Tag)
                    {
                        protectedElements.Add(element);
                        builder.Append("[[").Append(protectedElements.Count - 1).Append("]]");
                    }
                    else
                    {
                        builder.Append(element.ToString());
                    }
                }
                text = builder.ToString();
            }
            else
            {
                text = segment.ToPlain();
            }

            return _usePreTerms ? PreReplacer().Replace(text) : text;
        }

        /// <summary>
        /// 把占位符还原成标签元素组装目标段；占位符缺失/多余/重复时
        /// 回退为纯文本（剥掉占位符），保证不出现坏段。
        /// </summary>
        private Segment RebuildTarget(string text, List<SegmentElement> protectedElements)
        {
            var target = new Segment(_pair.TargetCulture);
            if (protectedElements == null || protectedElements.Count == 0)
            {
                target.Add(PlaceholderRegex.Replace(text, string.Empty));
                return target;
            }

            var matches = PlaceholderRegex.Matches(text);
            var seen = new HashSet<int>();
            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups[1].Value, out var index)
                    || index < 0 || index >= protectedElements.Count
                    || !seen.Add(index))
                {
                    target.Add(PlaceholderRegex.Replace(text, string.Empty));
                    return target;
                }
            }
            // 占位符数量与标签数不符时，缺失的标签稍后按源序补在末尾
            var lastIndex = 0;
            foreach (Match match in matches)
            {
                if (match.Index > lastIndex)
                    target.Add(text.Substring(lastIndex, match.Index - lastIndex));
                target.Add(protectedElements[int.Parse(match.Groups[1].Value)]);
                lastIndex = match.Index + match.Length;
            }
            if (lastIndex < text.Length)
                target.Add(text.Substring(lastIndex));
            if (matches.Count != protectedElements.Count)
            {
                for (var i = 0; i < protectedElements.Count; i++)
                    if (!seen.Contains(i))
                        target.Add(protectedElements[i]);
            }
            return target;
        }

        private TermReplacer PreReplacer()
        {
            return _preReplacer ?? (_preReplacer =
                new TermReplacer(_glossaries.Load(GlossaryDb.KindPre, _pair.SourceCultureName, _pair.TargetCultureName)));
        }

        private TermReplacer PostReplacer()
        {
            return _postReplacer ?? (_postReplacer =
                new TermReplacer(_glossaries.Load(GlossaryDb.KindPost, _pair.SourceCultureName, _pair.TargetCultureName)));
        }

        public ImportResult AddTranslationUnit(TranslationUnit translationUnit, ImportSettings settings)
        {
            throw new NotSupportedException();
        }

        public ImportResult[] AddTranslationUnits(TranslationUnit[] translationUnits, ImportSettings settings)
        {
            throw new NotSupportedException();
        }

        public ImportResult[] AddOrUpdateTranslationUnits(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings)
        {
            throw new NotSupportedException();
        }

        public ImportResult[] AddTranslationUnitsMasked(TranslationUnit[] translationUnits, ImportSettings settings, bool[] mask)
        {
            throw new NotSupportedException();
        }

        public ImportResult[] AddOrUpdateTranslationUnitsMasked(TranslationUnit[] translationUnits, int[] previousTranslationHashes, ImportSettings settings, bool[] mask)
        {
            throw new NotSupportedException();
        }

        public ImportResult UpdateTranslationUnit(TranslationUnit translationUnit)
        {
            throw new NotSupportedException();
        }

        public ImportResult[] UpdateTranslationUnits(TranslationUnit[] translationUnits)
        {
            throw new NotSupportedException();
        }
    }
}
