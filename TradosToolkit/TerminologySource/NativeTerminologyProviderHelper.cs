using System;
using System.Web;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 原生术语源的 URI 约定与读写解析，供 Provider / Factory / UI 三方共用。
    /// URI：tradostoolkit://glossary?base=<termBaseUrl>&src=<srcLang>&tgt=<tgtLang>&domain=<domain>
    /// base 是线上术语服务地址（含协议），经 Uri.EscapeDataString 编码后放入 query。
    /// </summary>
    internal static class NativeTerminologyProviderHelper
    {
        public const string SchemeActivation = "tradostoolkit://glossary";

        public static Uri BuildUri(string baseUrl, string sourceLang, string targetLang, string domain = null)
        {
            return new Uri(SchemeActivation
                + "?base=" + Uri.EscapeDataString(baseUrl ?? "")
                + "&src=" + Uri.EscapeDataString(sourceLang ?? "")
                + "&tgt=" + Uri.EscapeDataString(targetLang ?? "")
                + "&domain=" + Uri.EscapeDataString(domain ?? ""));
        }

        public static bool Supports(Uri uri)
        {
            return uri != null &&
                   uri.ToString().StartsWith(SchemeActivation, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetQueryParam(Uri uri, string key)
        {
            if (uri == null) return string.Empty;
            var qs = uri.Query.TrimStart('?');
            if (string.IsNullOrEmpty(qs)) return string.Empty;
            foreach (var part in qs.Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(part.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
                {
                    var raw = Uri.UnescapeDataString(part.Substring(eq + 1));
                    return raw ?? string.Empty;
                }
            }
            return string.Empty;
        }
    }
}