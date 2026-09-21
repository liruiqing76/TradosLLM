using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 线上术语服务的 HTTP 客户端：向 termBaseUrl 发起搜索请求。
    /// 复用框架自带 JavaScriptSerializer，不引入第三方 JSON 依赖（与 EngineHttp 约定一致）。
    /// 契约见 docs；端点：
    ///   POST {base}/match     —— 用完整段文本做包含匹配（编辑器术语识别，SearchMode.Fuzzy）
    ///   GET  {base}/search    —— 文本前缀匹配（术语库查词窗口，SearchMode.Normal）
    /// 响应统一 { "matches": [ { "id","source","target","score" } ] }。
    /// 注：此为同步高层封装，供 Studio 术语搜索引擎同步调用；内部异步同步化。
    /// </summary>
    internal static class TermHttpClient
    {
        private static readonly HttpClient Client = new HttpClient();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        /// <summary>对段文本做包含匹配（SearchMode.Fuzzy / 编辑器识别）。失败或未配置返回空结果。</summary>
        public static List<TermHit> Match(string baseUrl, string src, string tgt, string text, int max, string domain = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return new List<TermHit>();
                var body = new TermMatchRequest { src = src, tgt = tgt, text = text, max = Math.Max(1, max), domain = domain };
                var url = baseUrl.TrimEnd('/') + "/match";
                var payload = Send(new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json")
                });
                return ParseMatches(payload);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("术语服务 /match 失败: " + baseUrl + " (" + src + "->" + tgt + ")", e);
                return new List<TermHit>();
            }
        }

        /// <summary>文本前缀匹配（SearchMode.Normal / 术语库查词窗口）。失败或未配置返回空结果。</summary>
        public static List<TermHit> Search(string baseUrl, string src, string tgt, string text, int max, string domain = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text)) return new List<TermHit>();
                var url = baseUrl.TrimEnd('/') + "/search?src=" + Uri.EscapeDataString(src)
                    + "&tgt=" + Uri.EscapeDataString(tgt)
                    + "&q=" + Uri.EscapeDataString(text)
                    + "&max=" + Math.Max(1, max)
                    + "&domain=" + Uri.EscapeDataString(domain ?? "");
                var payload = Send(new HttpRequestMessage(HttpMethod.Get, url));
                return ParseMatches(payload);
            }
            catch (Exception e)
            {
                ToolkitLog.Error("术语服务 /search 失败: " + baseUrl + " (" + src + "->" + tgt + ")", e);
                return new List<TermHit>();
            }
        }

        private static Dictionary<string, object> Send(HttpRequestMessage request)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (request)
            using (var response = Client.SendAsync(request, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult())
            {
                var text = response.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                ToolkitLog.Info("术语请求 " + request.Method + " " + request.RequestUri +
                                " " + (int)response.StatusCode + " " + watch.ElapsedMilliseconds + "ms len=" + (text?.Length ?? 0));
                if (!response.IsSuccessStatusCode)
                {
                    ToolkitLog.Error("术语请求失败: HTTP " + (int)response.StatusCode + " " +
                                     Truncate(text, 500), null);
                    return new Dictionary<string, object>();
                }
                try
                {
                    return Json.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>();
                }
                catch
                {
                    return new Dictionary<string, object>();
                }
            }
        }

        private static List<TermHit> ParseMatches(Dictionary<string, object> payload)
        {
            var result = new List<TermHit>();
            if (payload == null || !payload.TryGetValue("matches", out var raw) || raw == null)
                return result;
            var arr = raw as System.Collections.IEnumerable;
            if (arr == null || raw is string) return result;
            foreach (var item in arr)
            {
                var d = item as Dictionary<string, object>;
                if (d == null) continue;
                var hit = new TermHit
                {
                    id = IntOr(d, "id", 0),
                    source = StrOr(d, "source"),
                    target = StrOr(d, "target"),
                    score = IntOr(d, "score", 100),
                };
                if (!string.IsNullOrEmpty(hit.source) || !string.IsNullOrEmpty(hit.target))
                    result.Add(hit);
            }
            return result;
        }

        private static int IntOr(Dictionary<string, object> d, string key, int fallback)
        {
            return d.TryGetValue(key, out var v) ? TryInt(v, fallback) : fallback;
        }

        private static string StrOr(Dictionary<string, object> d, string key)
        {
            return d.TryGetValue(key, out var v) & v != null ? v.ToString() : string.Empty;
        }

        private static int TryInt(object value, int fallback)
        {
            try { return value == null ? fallback : System.Convert.ToInt32(value); }
            catch { return fallback; }
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}