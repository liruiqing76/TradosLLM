using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;

namespace TradosToolkit.TranslationProvider.Engines
{
    /// <summary>
    /// 自建 TM 服务引擎。请求契约（POST baseUrl，JSON）：
    ///   → { "sourceLang": "zh-CN", "targetLang": "en-US", "segments": ["...", ...] }
    ///   ← { "results": [ { "translation": "...", "matchPercentage": 100 }, ... ] }
    /// results 与 segments 一一对应；无匹配时 translation 可为 null。
    /// segments 可能含 [[n]] 标签占位符，服务端需原样保留返回。
    /// </summary>
    public class TmApiEngine : ITranslationEngine
    {
        private readonly string _baseUrl;

        public TmApiEngine(string baseUrl)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        }

        public string DisplayName => "TradosToolkit TM";

        public async Task<EngineResult[][]> TranslateAsync(
            LanguagePair languagePair,
            string[] sources,
            bool[] mask,
            string apiKey,
            SegmentContext[] contexts,
            CancellationToken cancellationToken)
        {
            var indexes = new List<int>();
            for (var i = 0; i < sources.Length; i++)
                if (mask == null || mask[i])
                    indexes.Add(i);

            var results = sources.Select(_ => new EngineResult[0]).ToArray();
            if (indexes.Count == 0)
                return results;

            var body = new Dictionary<string, object>
            {
                { "sourceLang", languagePair.SourceCultureName },
                { "targetLang", languagePair.TargetCultureName },
                { "segments", indexes.Select(i => sources[i]).ToList() }
            };

            var response = await EngineHttp.PostJsonAsync(_baseUrl, body, apiKey, cancellationToken).ConfigureAwait(false);
            var list = EngineHttp.AsList(response.TryGetValue("results", out var r) ? r : null)
                        ?? new List<object>();

            for (var j = 0; j < indexes.Count && j < list.Count; j++)
            {
                var item = EngineHttp.AsDict(list[j]);
                if (item == null) continue;

                var translation = EngineHttp.AsString(item.TryGetValue("translation", out var t) ? t : null);
                if (string.IsNullOrEmpty(translation)) continue;

                results[indexes[j]] = new[]
                {
                    new EngineResult
                    {
                        Translation = translation,
                        MatchPercentage = EngineHttp.AsInt(item.TryGetValue("matchPercentage", out var p) ? p : null),
                        Origin = "TM"
                    }
                };
            }
            return results;
        }
    }
}
