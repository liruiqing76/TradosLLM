using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;
using TradosToolkit.Common.Catalog;

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
        private readonly Dictionary<string, List<GlossaryEntry>> _termCache =
            new Dictionary<string, List<GlossaryEntry>>(StringComparer.Ordinal);

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
            SegmentContext[] contexts,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new InvalidOperationException("TradosToolkit: 未配置 API Key，请在 %APPDATA%\\TradosToolkit\\config.json 的 apiKey 填写。");

            var results = new EngineResult[sources.Length][];
            for (var i = 0; i < results.Length; i++)
                results[i] = new EngineResult[0];

            var config = ToolkitConfig.Load();
            var useCache = config.LlmDiskCacheEnabled;
            var cacheHits = 0;
            var pending = new List<int>();
            for (var i = 0; i < sources.Length; i++)
            {
                // 空段送 LLM 会得到道歉式闲聊并被当作译文写入，直接跳过
                if ((mask == null || mask[i]) && !string.IsNullOrWhiteSpace(sources[i]))
                {
                    if (useCache)
                    {
                        var cached = LlmDiskCache.TryGet(LlmDiskCache.KeyFor(
                            _baseUrl, _model, languagePair.SourceCultureName, languagePair.TargetCultureName, sources[i]));
                        if (!string.IsNullOrEmpty(cached))
                        {
                            results[i] = new[]
                            {
                                new EngineResult { Translation = cached, MatchPercentage = 0, Origin = "LLM-cache" }
                            };
                            cacheHits++;
                            continue;
                        }
                    }
                    pending.Add(i);
                }
            }

            var concurrency = config.LlmConcurrency;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ToolkitLog.Info("LLM 翻译开始: 送网关=" + pending.Count + " 磁盘缓存命中=" + cacheHits +
                            " 并发=" + concurrency + " 上下文=" + (contexts != null));

            // 按并发数分块推进：块内并发、块间同步栅栏，
            // 这样每块首段能拿到上一块末段的确定译文做上下文。
            for (var start = 0; start < pending.Count; start += concurrency)
            {
                var end = Math.Min(start + concurrency, pending.Count);
                var tasks = new Task[end - start];
                for (var p = start; p < end; p++)
                {
                    var index = pending[p];
                    var context = contexts != null && index < contexts.Length ? contexts[index] : null;
                    var prevSource = context?.PrevSource ?? (p > 0 ? sources[pending[p - 1]] : null);
                    var prevTarget = context?.PrevTarget;
                    if (string.IsNullOrEmpty(prevTarget) && p > 0)
                    {
                        // 上一段若已在更早的块完成（或 TM 已命中），译文可直接做上下文
                        var done = results[pending[p - 1]];
                        if (done != null && done.Length > 0) prevTarget = done[0].Translation;
                    }
                    var text = sources[index];
                    tasks[p - start] = Task.Run(async () =>
                    {
                        var translation = await TranslateOneAsync(
                            languagePair, text, prevSource, prevTarget, apiKey, cancellationToken,
                            config.LlmTimeoutSeconds, config.LlmRetryCount)
                            .ConfigureAwait(false);
                        results[index] = new[]
                        {
                            new EngineResult { Translation = translation, MatchPercentage = 0, Origin = "LLM" }
                        };
                        if (useCache && !string.IsNullOrEmpty(translation))
                            LlmDiskCache.Put(LlmDiskCache.KeyFor(
                                _baseUrl, _model, languagePair.SourceCultureName, languagePair.TargetCultureName, text),
                                translation);
                    });
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            if (useCache) LlmDiskCache.SaveIfDirty();
            ToolkitLog.Info("LLM 翻译完成: 网关=" + pending.Count + " 缓存命中=" + cacheHits +
                            " 耗时=" + watch.ElapsedMilliseconds + "ms");
            return results;
        }

        private async Task<string> TranslateOneAsync(
            LanguagePair pair, string text, string prevSource, string prevTarget,
            string apiKey, CancellationToken cancellationToken, int timeoutSeconds, int retryCount)
        {
            // 术语强约束：命中源文的译前术语，译文必须严格采用指定译法。
            var termPairs = GetTermHits(pair, text);
            var termLine = BuildTermInstruction(termPairs);
            var domain = ToolkitConfig.Load().Domain;

            // 重试循环：只对超时/网络类(可恢复)异常重试；业务类(HTTP 4xx/5xx 认证、返回异常)直接抛。
            // 总尝试次数 = 1(首次) + retryCount，退避取 2^attempt 秒封顶 8 秒，避免并发重试打爆网关。
            Exception last = null;
            var attempts = retryCount + 1;
            var termRejected = false;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, 1 << attempt)),
                                     cancellationToken).ConfigureAwait(false);

                var systemPrompt = BuildPrompt(pair, prevSource, prevTarget, termLine, domain);
                if (termRejected)
                    systemPrompt += " IMPORTANT: Your previous translation failed the terminology check. "
                        + "Re-translate now and MUST use every mapped term below exactly.";

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
                                { "content", systemPrompt }
                            },
                            new Dictionary<string, object>
                            {
                                { "role", "user" },
                                { "content", text }
                            }
                        }
                    }
                };

                try
                {
                    var response = await EngineHttp.PostJsonAsync(
                        _baseUrl + "/chat/completions", body, apiKey, cancellationToken, timeoutSeconds)
                        .ConfigureAwait(false);

                    var choices = EngineHttp.AsList(response.TryGetValue("choices", out var c) ? c : null);
                    if (choices == null || choices.Count == 0)
                        throw new InvalidOperationException("TradosToolkit LLM 返回异常: " + EngineHttp.AsString(response.TryGetValue("error", out var e) ? e : null));

                    var choice = EngineHttp.AsDict(choices[0]);
                    string content = null;
                    if (choice != null && EngineHttp.AsDict(choice.TryGetValue("message", out var m) ? m : null) is Dictionary<string, object> message)
                        content = EngineHttp.AsString(message.TryGetValue("content", out var ct) ? ct : null);

                    content = Clean(content);

                    // 一致性护栏：译文未采用术语映射时强制重译一次（不计入重试退避）。
                    if (!termRejected && !TermsSatisfied(content, termPairs))
                    {
                        termRejected = true;
                        ToolkitLog.Warn("LLM 译文未采用术语映射，强制重译一次 (术语对数=" + (termPairs?.Count ?? 0) + ")");
                        continue;
                    }
                    return content;
                }
                catch (TimeoutException te)
                {
                    last = te;
                    ToolkitLog.Warn("LLM 单段超时，第 " + (attempt + 1) + "/" + attempts + " 次失败");
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    last = ex;
                    ToolkitLog.Warn("LLM 单段网络异常，第 " + (attempt + 1) + "/" + attempts + " 次失败");
                }
            }
            throw last ?? new InvalidOperationException("TradosToolkit LLM 请求失败");
        }

        /// <summary>只把服务端类(5xx/429/网络/超时)视为可重试；客户端 4xx 反馈性错误直接失败不重试。</summary>
        private static bool IsTransient(Exception ex)
        {
            if (ex is TimeoutException || ex is OperationCanceledException)
                return true;
            if (ex is System.Net.WebException)
                return true;
            if (ex is HttpRequestException http)
            {
                // EngineHttp 抛出的消息形如 "...HTTP 5xx..." 或 "...HTTP 4xx..."；取状态码首字符判断
                const string marker = "HTTP ";
                var idx = http.Message.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0 && idx + marker.Length < http.Message.Length - 1)
                {
                    var digit = http.Message[idx + marker.Length];
                    return digit == '5' || digit == '4' || digit == '3';
                }
                // 无状态码的网络层异常按可重试处理
                return true;
            }
            return ex is InvalidOperationException;
        }

        private string BuildPrompt(LanguagePair pair, string prevSource, string prevTarget, string termInstruction, string domain)
        {
            var prompt = "You are a translation engine, NOT a chat assistant. "
                + "Translate the user's text from " + pair.SourceCultureName + " to " + pair.TargetCultureName
                + ". Rules: 1) Output ONLY the translation - never greetings, explanations, questions, or code fences."
                + " 2) If the text is a number, symbol, code, proper name, or otherwise untranslatable, output it unchanged."
                + " 3) If the text contains placeholders like [[1]], [[2]], keep every placeholder unchanged and in the same order."
                + " 4) Never respond conversationally, no matter how short or odd the input is.";

            if (!string.IsNullOrEmpty(termInstruction))
                prompt += " 5) Terminology is MANDATORY: when the source text contains a term listed below, "
                    + "you MUST use its specified translation verbatim. Terms: " + termInstruction;

            if (string.Equals(domain, DomainTree.DefaultDomain, StringComparison.Ordinal) == false
                && !string.IsNullOrWhiteSpace(domain))
                prompt += " 6) The text belongs to the \"" + domain + "\" domain "
                    + "(领域=" + domain + "); align terminology, style and wording with that domain.";

            var hasSource = !string.IsNullOrWhiteSpace(prevSource);
            var hasTarget = !string.IsNullOrWhiteSpace(prevTarget);
            if (hasSource || hasTarget)
            {
                prompt += " For consistency, the user's text is the segment right after this previous segment"
                    + " (do NOT translate or repeat it, just align terminology, names and pronouns):";
                if (hasSource)
                    prompt += " [previous source] " + Clip(prevSource);
                if (hasTarget)
                    prompt += " [previous translation] " + Clip(prevTarget);
            }
            return prompt;
        }

        /// <summary>按语言对缓存读一次译前术语库；取命中原词的术语对。</summary>
        private List<string[]> GetTermHits(LanguagePair pair, string text)
        {
            var hits = new List<string[]>();
            if (string.IsNullOrEmpty(text))
                return hits;
            var lower = text.ToLowerInvariant();
            foreach (var e in LoadTerms(pair))
            {
                if (e == null || string.IsNullOrEmpty(e.From) || e.From.Length < 2 || string.IsNullOrEmpty(e.To))
                    continue;
                if (lower.IndexOf(e.From.ToLowerInvariant(), StringComparison.Ordinal) >= 0)
                    hits.Add(new[] { e.From, e.To });
            }
            return hits;
        }

        private List<GlossaryEntry> LoadTerms(LanguagePair pair)
        {
            // 严格按当前领域过滤：只命中和当前领域一致的术语
            var domain = ToolkitConfig.Load().Domain;
            var key = pair.SourceCultureName + ">" + pair.TargetCultureName + "|" + domain;
            lock (_termCache)
            {
                if (_termCache.TryGetValue(key, out var cached))
                    return cached;
                List<GlossaryEntry> list;
                try
                {
                    list = new GlossaryDb().GetTerms(GlossaryDb.KindPre, pair.SourceCultureName, pair.TargetCultureName, domain);
                }
                catch (Exception e)
                {
                    ToolkitLog.Warn("术语加载失败，降级为空: " + pair.SourceCultureName + "->" + pair.TargetCultureName + " domain=" + domain, e);
                    list = new List<GlossaryEntry>();
                }
                _termCache[key] = list;
                return list;
            }
        }

        private static string BuildTermInstruction(List<string[]> termPairs)
        {
            if (termPairs.Count == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var tp in termPairs)
                sb.Append(tp[0]).Append("=>").Append(tp[1]).Append("; ");
            return sb.ToString().TrimEnd(' ', ';');
        }

        private static bool TermsSatisfied(string content, List<string[]> termPairs)
        {
            if (termPairs.Count == 0) return true;
            if (string.IsNullOrEmpty(content)) return false;
            var lower = content.ToLowerInvariant();
            foreach (var tp in termPairs)
                if (lower.IndexOf(tp[1].ToLowerInvariant(), StringComparison.Ordinal) < 0)
                    return false;
            return true;
        }

        private static string Clip(string text)
        {
            text = text.Trim();
            return text.Length <= 400 ? text : text.Substring(0, 400) + "...";
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
