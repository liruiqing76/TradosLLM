using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TradosToolkit.Diagnostics;
using TradosToolkit.TranslationProvider.Engines;

namespace TradosToolkit.EditorPanel
{
    public class ChatTurn
    {
        public string Role;
        public string Content;
    }

    /// <summary>
    /// 编辑器面板的对话客户端：复用网关（OpenAI 兼容 /chat/completions）
    /// 与 config.json 的 llmBaseUrl/llmModel/apiKey。
    /// 约定：模型给出的最终修订译文用 <<<…>>> 包裹，供"写入当前段"提取。
    /// </summary>
    public static class LlmChatClient
    {
        public static bool IsReady()
        {
            var config = ToolkitConfig.Load();
            return !string.IsNullOrWhiteSpace(config.LlmBaseUrl)
                   && !string.IsNullOrWhiteSpace(config.ApiKey)
                   && !string.IsNullOrWhiteSpace(config.LlmModel);
        }

        public static async Task<string> ChatAsync(
            string source, string target, string targetLang,
            IList<ChatTurn> history, string userText,
            string prevSource, string prevTarget, string nextSource,
            CancellationToken cancellationToken = default)
        {
            var config = ToolkitConfig.Load();
            if (IsReady() == false)
                throw new InvalidOperationException(
                    "TradosToolkit: 未配置 LLM，请在 %APPDATA%\\TradosToolkit\\config.json 填 llmBaseUrl/llmModel/apiKey。");

            var messages = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "role", "system" },
                    { "content", BuildSystemPrompt(source, target, targetLang, prevSource, prevTarget, nextSource) }
                }
            };
            foreach (var turn in history)
                messages.Add(new Dictionary<string, object> { { "role", turn.Role }, { "content", turn.Content } });
            messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", userText } });

            var body = new Dictionary<string, object>
            {
                { "model", config.LlmModel },
                { "temperature", 0.3 },
                { "messages", messages },
            };

            var url = config.LlmBaseUrl.TrimEnd('/') + "/chat/completions";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ToolkitLog.Info("面板对话请求: 历史轮数=" + history.Count / 2 + " 输入长度=" + userText.Length);
            var response = await EngineHttp.PostJsonAsync(url, body, config.ApiKey, cancellationToken)
                               .ConfigureAwait(false);

            var choices = EngineHttp.AsList(response.TryGetValue("choices", out var c) ? c : null);
            if (choices == null || choices.Count == 0)
                throw new InvalidOperationException("TradosToolkit LLM 返回异常: " +
                    EngineHttp.AsString(response.TryGetValue("error", out var e) ? e : null));

            var choice = EngineHttp.AsDict(choices[0]);
            string content = null;
            if (choice != null &&
                EngineHttp.AsDict(choice.TryGetValue("message", out var m) ? m : null) is Dictionary<string, object> message)
                content = EngineHttp.AsString(message.TryGetValue("content", out var ct) ? ct : null);

            content = (content ?? string.Empty).Trim();
            ToolkitLog.Info("面板对话完成: 耗时=" + watch.ElapsedMilliseconds + "ms 回复长度=" + content.Length);
            return content;
        }

        private static string BuildSystemPrompt(
            string source, string target, string targetLang,
            string prevSource, string prevTarget, string nextSource)
        {
            var prompt = "You are a translation review assistant embedded in the Trados Studio editor. "
                   + "Target language: " + (string.IsNullOrEmpty(targetLang) ? "unknown (detect from the current translation)" : targetLang) + ". "
                   + "Current segment source text: " + source + " "
                   + "Current translation: " + target + " "
                   + "The user asks you to revise, polish, shorten, expand or explain this translation. "
                   + "Rules: 1) Whenever you propose a final revised translation, output it on its own line wrapped exactly as <<< and >>> around the translation. "
                   + "2) Keep any placeholders/tags (e.g. [[1]]) from the source intact and in order. "
                   + "3) Write explanations in the same language the user writes in, keep them brief, no code fences.";

            var context = string.Empty;
            if (!string.IsNullOrWhiteSpace(prevSource) || !string.IsNullOrWhiteSpace(prevTarget))
                context += " [previous segment] " + Clip(prevSource) + " => " + (string.IsNullOrWhiteSpace(prevTarget) ? "(untranslated)" : Clip(prevTarget));
            if (!string.IsNullOrWhiteSpace(nextSource))
                context += " [next segment source] " + Clip(nextSource);
            if (context.Length > 0)
                prompt += " Surrounding document context for terminology and pronoun consistency (do NOT translate or quote it):" + context;

            // 功能 #5：客户/项目级风格指南，作为强制约定注入润色对话
            var styleGuide = ToolkitConfig.Load().StyleGuide;
            if (!string.IsNullOrWhiteSpace(styleGuide))
                prompt += " Client style guide (MANDATORY): every revision you propose must adhere to these conventions - " + styleGuide;
            return prompt;
        }

        private static string Clip(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(empty)";
            text = text.Trim();
            return text.Length <= 400 ? text : text.Substring(0, 400) + "…";
        }

        /// <summary>取最后一段 <<<…>>> 修订译文；没有标记时退化为整条回复。</summary>
        public static string ExtractRevised(string reply)
        {
            if (string.IsNullOrEmpty(reply))
                return null;
            var matches = System.Text.RegularExpressions.Regex.Matches(reply, "<<<(.*?)>>>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (matches.Count > 0)
                return matches[matches.Count - 1].Groups[1].Value.Trim();
            return reply.Trim();
        }
    }
}
