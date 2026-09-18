using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationProvider.Engines
{
    /// <summary>
    /// OpenAI 兼容引擎：POST {baseUrl}/chat/completions，Bearer apiKey。
    /// 首版逐段请求，保证结果对应关系简单可靠。
    /// </summary>
    public class OpenAiCompatEngine : ITranslationEngine
    {
        private readonly string _baseUrl;
        private readonly string _model;

        public OpenAiCompatEngine(string baseUrl, string model)
        {
            _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
            _model = model ?? string.Empty;
        }

        public string DisplayName => string.IsNullOrEmpty(_model) ? "TradosToolkit LLM" : "TradosToolkit LLM (" + _model + ")";

        public async Task<EngineResult[][]> TranslateAsync(
            LanguagePair languagePair,
            string[] sources,
            bool[] mask,
            string apiKey,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("TradosToolkit: 未配置 API Key，请在 %APPDATA%\\TradosToolkit\\config.json 的 apiKey 填写。");

            var results = new EngineResult[sources.Length][];
            var concurrency = ToolkitConfig.Load().LlmConcurrency;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ToolkitLog.Info("LLM 并发翻译开始: 段数=" + sources.Length + " 并发=" + concurrency);
            using (var gate = new SemaphoreSlim(concurrency))
            {
                var tasks = new List<Task>();
                for (var i = 0; i < sources.Length; i++)
                {
                    if (mask != null && !mask[i])
                    {
                        results[i] = new EngineResult[0];
                        continue;
                    }

                    var index = i;
                    var text = sources[i];
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        // 空段送 LLM 会得到道歉式闲聊并被当作译文写入，直接跳过
                        results[index] = new EngineResult[0];
                        continue;
                    }
                    tasks.Add(Task.Run(async () =>
                    {
                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            var translation = await TranslateOneAsync(
                                languagePair, text, apiKey, cancellationToken).ConfigureAwait(false);
                            results[index] = new[]
                            {
                                new EngineResult { Translation = translation, MatchPercentage = 0, Origin = "LLM" }
                            };
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }));
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            ToolkitLog.Info("LLM 并发翻译完成: 段数=" + sources.Length + " 耗时=" + watch.ElapsedMilliseconds + "ms");
            return results;
        }

        private async Task<string> TranslateOneAsync(
            LanguagePair pair, string text, string apiKey, CancellationToken cancellationToken)
        {
            var body = new Dictionary<string, object>
            {
                { "model", _model },
                { "temperature", 0 },
                {
                    "messages", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "role", "system" },
                            { "content", "You are a translation engine, NOT a chat assistant. "
                                + "Translate the user's text from " + pair.SourceCultureName + " to " + pair.TargetCultureName
                                + ". Rules: 1) Output ONLY the translation - never greetings, explanations, questions, or code fences."
                                + " 2) If the text is a number, symbol, code, proper name, or otherwise untranslatable, output it unchanged."
                                + " 3) If the text contains placeholders like [[1]], [[2]], keep every placeholder unchanged and in the same order."
                                + " 4) Never respond conversationally, no matter how short or odd the input is." }
                        },
                        new Dictionary<string, object>
                        {
                            { "role", "user" },
                            { "content", text }
                        }
                    }
                }
            };

            var response = await EngineHttp.PostJsonAsync(
                _baseUrl + "/chat/completions", body, apiKey, cancellationToken).ConfigureAwait(false);

            var choices = EngineHttp.AsList(response.TryGetValue("choices", out var c) ? c : null);
            if (choices == null || choices.Count == 0)
                throw new InvalidOperationException("TradosToolkit LLM 返回异常: " + EngineHttp.AsString(response.TryGetValue("error", out var e) ? e : null));

            var choice = EngineHttp.AsDict(choices[0]);
            string content = null;
            if (choice != null && EngineHttp.AsDict(choice.TryGetValue("message", out var m) ? m : null) is Dictionary<string, object> message)
                content = EngineHttp.AsString(message.TryGetValue("content", out var ct) ? ct : null);

            return Clean(content);
        }

        private static string Clean(string content)
        {
            if (string.IsNullOrEmpty(content))
                return string.Empty;

            content = content.Trim();
            if (content.StartsWith("```"))
            {
                var start = content.IndexOf('\n');
                var end = content.LastIndexOf("```", StringComparison.Ordinal);
                if (start > 0 && end > start)
                    content = content.Substring(start + 1, end - start - 1).Trim();
            }
            return content;
        }
    }
}
