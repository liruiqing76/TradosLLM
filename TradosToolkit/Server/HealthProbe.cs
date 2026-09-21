using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradosToolkit.TranslationProvider.Engines;

namespace TradosToolkit.Server
{
    /// <summary>
    /// 内网基线自检：GET /api/health — 对 API 自身 / TM / LLM / 术语源 逐项发轻量探测，
    /// 返回每项 ok|fail + 延迟(ms) + 关键信息。全部探测并行、各自 5s 超时，绝不阻塞 UI 线程。
    /// 用于部署到新内网机器后一键验证网关/服务链路可达性（详见 docs/ROADMAP §爆点6）。
    /// </summary>
    public static class HealthProbe
    {
        private const int ProbeTimeoutSeconds = 5;

        public static ApiResult Build()
        {
            var server = ToolkitApiServer.Instance;
            var config = ToolkitConfig.Load();
            var started = server.StartedAt == default(DateTime) ? TimeSpan.Zero : DateTime.Now - server.StartedAt;

            var results = new Dictionary<string, object>
            {
                { "product", "TradosToolkit" },
                { "studio", Environment.Is64BitProcess ? "x64" : "x86" },
                { "version", typeof(ProjectApi).Assembly.GetName().Version.ToString() },
                { "port", server.Port },
                { "listening", server.IsListening },
                { "started", started.TotalSeconds.ToString("0") + "s" },
                { "checked", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
            };

            var probes = new List<KeyValuePair<string, Task<EngineHttp.ProbeResult>>>();

            // TM / 术语源：GET 裸基址做可达性 + 延迟
            if (!string.IsNullOrEmpty(config.TmUrl))
                probes.Add(Pair("tm", Probe(config.TmUrl, null)));
            else
                results["tm"] = Unconfigured("tmUrl 未配置");

            if (!string.IsNullOrEmpty(config.TermBaseUrl))
                probes.Add(Pair("term", Probe(config.TermBaseUrl, null)));
            else
                results["term"] = Unconfigured("termBaseUrl 未配置");

            // LLM：OpenAI 兼容网关一般有 GET {base}/models
            if (!string.IsNullOrEmpty(config.LlmBaseUrl))
                probes.Add(Pair("llm", Probe(config.LlmBaseUrl.TrimEnd('/') + "/models", config.ApiKey)));
            else
                results["llm"] = Unconfigured("llmBaseUrl 未配置");

            try
            {
                Task.WaitAll(probes.Select(p => p.Value).Cast<Task>().ToArray());
            }
            catch (AggregateException)
            {
                // 单项已各自吞错并返回 Code=-1，这里可安全忽略
            }

            foreach (var p in probes)
                results[p.Key] = Describe(p.Value);
            return ApiResult.Json(200, results);
        }

        private static Task<EngineHttp.ProbeResult> Probe(string url, string bearer)
            => EngineHttp.ProbeAsync(url, bearer, ProbeTimeoutSeconds);

        private static KeyValuePair<string, Task<EngineHttp.ProbeResult>> Pair(
            string name, Task<EngineHttp.ProbeResult> task)
            => new KeyValuePair<string, Task<EngineHttp.ProbeResult>>(name, task);

        private static Dictionary<string, object> Describe(Task<EngineHttp.ProbeResult> task)
        {
            var r = task.IsFaulted || task.IsCanceled
                ? new EngineHttp.ProbeResult { Code = -1, Error = (task.Exception?.GetBaseException()?.Message) ?? "探测异常" }
                : task.Result;
            var map = new Dictionary<string, object>
            {
                { "ok", r.Ok },
                { "latencyMs", r.LatencyMs },
            };
            if (r.Code >= 0) map["http"] = r.Code;
            if (r.Ok && !string.IsNullOrEmpty(r.Text)) map["body"] = r.Text;
            if (!r.Ok) map["error"] = r.Error ?? ("HTTP " + r.Code);
            return map;
        }

        private static Dictionary<string, object> Unconfigured(string detail)
        {
            return new Dictionary<string, object> { { "ok", false }, { "error", detail } };
        }
    }
}