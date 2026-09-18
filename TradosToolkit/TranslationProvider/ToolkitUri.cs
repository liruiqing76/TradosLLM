using System;
using System.Collections.Generic;
using System.Text;

namespace TradosToolkit.TranslationProvider
{
    /// <summary>
    /// 单一提供程序入口：tradostoolkit://config/?baseUrl=...&model=...&pre=1&post=1&tags=1
    /// baseUrl/model 为 LLM 回退参数（可空）；TM 接口地址不进 URI，来自 %APPDATA%\TradosToolkit\config.json。
    /// 配置编码在 URI query（随项目持久化），API Key 走凭据存储（见 ToolkitCredential）。
    /// </summary>
    public static class ToolkitUri
    {
        public const string Scheme = "tradostoolkit";

        public static bool IsSupported(Uri uri)
        {
            return uri != null && uri.Scheme == Scheme;
        }

        public static Uri Build(string baseUrl, string model, bool usePreTerms, bool usePostTerms, bool supportsTags)
        {
            var query = new StringBuilder();
            query.Append("baseUrl=").Append(Uri.EscapeDataString(baseUrl ?? string.Empty));
            if (!string.IsNullOrEmpty(model))
                query.Append("&model=").Append(Uri.EscapeDataString(model));
            query.Append("&pre=").Append(usePreTerms ? "1" : "0");
            query.Append("&post=").Append(usePostTerms ? "1" : "0");
            query.Append("&tags=").Append(supportsTags ? "1" : "0");
            return new Uri(Scheme + "://config/?" + query);
        }

        public static string GetBaseUrl(Uri uri) => GetParam(uri, "baseUrl");

        public static string GetModel(Uri uri) => GetParam(uri, "model");

        public static bool UsePreTerms(Uri uri) => GetParam(uri, "pre") == "1";

        public static bool UsePostTerms(Uri uri) => GetParam(uri, "post") == "1";

        public static bool SupportsTags(Uri uri) => GetParam(uri, "tags") == "1";

        private static string GetParam(Uri uri, string name)
        {
            foreach (var pair in ParseQuery(uri))
                if (pair.Key == name)
                    return pair.Value;
            return string.Empty;
        }

        private static IEnumerable<KeyValuePair<string, string>> ParseQuery(Uri uri)
        {
            var query = uri == null ? null : uri.Query;
            if (string.IsNullOrEmpty(query))
                yield break;

            foreach (var part in query.TrimStart('?').Split('&'))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                yield return new KeyValuePair<string, string>(
                    part.Substring(0, idx),
                    Uri.UnescapeDataString(part.Substring(idx + 1)));
            }
        }
    }
}
