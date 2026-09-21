using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.TranslationProvider.Engines
{
    /// <summary>
    /// 引擎共用的 JSON HTTP 助手。用框架自带 JavaScriptSerializer，
    /// 避免往 .sdlplugin 包里再塞第三方 JSON 依赖。
    /// </summary>
    internal static class EngineHttp
    {
        private static readonly HttpClient Client = new HttpClient();
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static async Task<Dictionary<string, object>> PostJsonAsync(
            string url, object body, string bearerToken, CancellationToken cancellationToken)
        {
            return await PostJsonAsync(url, body, bearerToken, cancellationToken, 0).ConfigureAwait(false);
        }

        public static async Task<Dictionary<string, object>> PostJsonAsync(
            string url, object body, string bearerToken, CancellationToken cancellationToken, int timeoutSeconds)
        {
            var watch = Stopwatch.StartNew();
            ToolkitLog.Info("HTTP POST " + url + " key=" + (string.IsNullOrEmpty(bearerToken) ? "(无)" : "(有)"));
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
                if (!string.IsNullOrEmpty(bearerToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());

                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    if (timeoutSeconds > 0)
                        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                    try
                    {
                        using (var response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false))
                        {
                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            ToolkitLog.Info("HTTP " + (int)response.StatusCode + " " + url +
                                            " " + watch.ElapsedMilliseconds + "ms 响应长度=" + (text?.Length ?? 0) +
                                            " 响应头段=" + Truncate(text, 300));
                            if (!response.IsSuccessStatusCode)
                            {
                                var error = new HttpRequestException(
                                    "TradosToolkit 请求失败: HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase +
                                    (string.IsNullOrEmpty(text) ? "" : " | " + Truncate(text, 500)));
                                ToolkitLog.Error("HTTP 失败 " + url + " 响应体: " + Truncate(text, 2000), error);
                                throw error;
                            }
                            return Deserialize(text);
                        }
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        var timeout = new TimeoutException(
                            "TradosToolkit 请求超时: " + url + " 超过 " + timeoutSeconds + " 秒");
                        ToolkitLog.Error("HTTP 超时 " + url + " " + watch.ElapsedMilliseconds + "ms", timeout);
                        throw timeout;
                    }
                }
            }
        }

        public static Dictionary<string, object> Deserialize(string json)
        {
            return Json.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
        }

        /// <summary>探测结果：Code=-1 表示网络/超时/异常，Code∈[0,399] 视为可达。</summary>
        public struct ProbeResult
        {
            public int Code;
            public long LatencyMs;
            public string Text;
            public string Error;
            public bool Ok => Code >= 0 && Code < 400;
        }

        /// <summary>轻量可达性探测（HTTP GET + 可选 Bearer + 超时）。用于内网基线自检。</summary>
        public static async Task<ProbeResult> ProbeAsync(
            string url, string bearerToken, int timeoutSeconds)
        {
            var watch = Stopwatch.StartNew();
            ToolkitLog.Info("HTTP GET " + url + " key=" + (string.IsNullOrEmpty(bearerToken) ? "(无)" : "(有)"));
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (!string.IsNullOrEmpty(bearerToken))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                {
                    try
                    {
                        using (var response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false))
                        {
                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            ToolkitLog.Info("HTTP " + (int)response.StatusCode + " " + url +
                                            " " + watch.ElapsedMilliseconds + "ms");
                            return new ProbeResult
                            {
                                Code = (int)response.StatusCode,
                                LatencyMs = watch.ElapsedMilliseconds,
                                Text = Truncate(text, 200),
                            };
                        }
                    }
                    catch (Exception e)
                    {
                        ToolkitLog.Error("HTTP 探测失败 " + url + " " + watch.ElapsedMilliseconds + "ms", e);
                        return new ProbeResult { Code = -1, LatencyMs = watch.ElapsedMilliseconds, Error = e.Message };
                    }
                }
            }
        }

        public static Dictionary<string, object> AsDict(object value)
        {
            return value as Dictionary<string, object>;
        }

        /// <summary>JavaScriptSerializer 把 JSON 数组反序列化成 ArrayList，
        /// 不能直接强转 List&lt;object&gt;，必须兼容 IEnumerable 逐元素收集。</summary>
        public static List<object> AsList(object value)
        {
            if (value is List<object> list) return list;
            var seq = value as System.Collections.IEnumerable;
            if (seq == null || value is string) return null;
            var result = new List<object>();
            foreach (var item in seq) result.Add(item);
            return result;
        }

        public static string AsString(object value)
        {
            return value as string;
        }

        public static int AsInt(object value, int fallback = 0)
        {
            try
            {
                return value == null ? fallback : System.Convert.ToInt32(value);
            }
            catch
            {
                return fallback;
            }
        }

        private static string Truncate(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
