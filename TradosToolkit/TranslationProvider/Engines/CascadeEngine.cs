using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sdl.LanguagePlatform.Core;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationProvider.Engines
{
    /// <summary>
    /// 级联引擎：先查 TM（内部固定接口），无匹配的段再送 LLM。
    /// 两边都可以缺席：只有 TM 时行为等同 TM 引擎；只有 LLM 时等同 LLM 引擎。
    /// </summary>
    public class CascadeEngine : ITranslationEngine
    {
        private readonly TmApiEngine _tm;
        private readonly OpenAiCompatEngine _llm;

        public CascadeEngine(string tmUrl, string llmBaseUrl, string llmModel)
        {
            if (!string.IsNullOrWhiteSpace(tmUrl))
                _tm = new TmApiEngine(tmUrl);
            if (!string.IsNullOrWhiteSpace(llmBaseUrl))
                _llm = new OpenAiCompatEngine(llmBaseUrl, llmModel);
        }

        public bool HasTm => _tm != null;

        public bool HasLlm => _llm != null;

        public string DisplayName
        {
            get
            {
                if (_tm != null && _llm != null) return "TradosToolkit (TM→LLM)";
                if (_tm != null) return "TradosToolkit TM";
                return _llm != null ? _llm.DisplayName : "TradosToolkit (未配置)";
            }
        }

        public async Task<EngineResult[][]> TranslateAsync(
            LanguagePair languagePair,
            string[] sources,
            bool[] mask,
            string apiKey,
            CancellationToken cancellationToken)
        {
            var results = new EngineResult[sources.Length][];
            for (var i = 0; i < results.Length; i++)
                results[i] = new EngineResult[0];

            var missing = new List<int>();
            for (var i = 0; i < sources.Length; i++)
                if ((mask == null || mask[i]) && !string.IsNullOrEmpty(sources[i]))
                    missing.Add(i);

            if (_tm != null && missing.Count > 0)
            {
                try
                {
                    var tmResults = await _tm.TranslateAsync(
                        languagePair, sources, mask, apiKey, cancellationToken).ConfigureAwait(false);
                    for (var i = 0; i < sources.Length; i++)
                        if (tmResults != null && i < tmResults.Length && tmResults[i] != null && tmResults[i].Length > 0)
                            results[i] = tmResults[i];
                }
                catch (Exception e)
                {
                    ToolkitLog.Error("TM 查询失败，全部段回退 LLM", e);
                }
                missing.RemoveAll(i => results[i].Length > 0);
            }

            if (_llm != null && missing.Count > 0)
            {
                var llmMask = new bool[sources.Length];
                foreach (var i in missing)
                    llmMask[i] = true;
                ToolkitLog.Info("级联: TM 未命中 " + missing.Count + " 段，送 LLM");
                var llmResults = await _llm.TranslateAsync(
                    languagePair, sources, llmMask, apiKey, cancellationToken).ConfigureAwait(false);
                foreach (var i in missing)
                    if (llmResults != null && i < llmResults.Length && llmResults[i] != null && llmResults[i].Length > 0)
                        results[i] = llmResults[i];
            }
            else if (missing.Count > 0)
            {
                ToolkitLog.Info("级联: TM 未命中 " + missing.Count + " 段，未配置 LLM，返回空");
            }
            return results;
        }
    }
}
