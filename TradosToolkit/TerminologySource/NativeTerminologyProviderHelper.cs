using System;
using System.Web;

namespace TradosToolkit.TerminologySource
{
    /// <summary>术语源类型：本地 SQLite 库 或 线上 URL 服务。</summary>
    public static class TermSourceKind
    {
        public const string Local = "local";
        public const string Online = "online";

        public static string Normalize(string kind)
        {
            return string.Equals(kind, Online, StringComparison.OrdinalIgnoreCase) ? Online : Local;
        }

        public static string Label(string kind)
        {
            return Normalize(kind) == Online ? "线上术语服务" : "本地术语库";
        }
    }

    /// <summary>
    /// 原生术语源的 URI 约定与读写解析，供 Provider / Factory / UI 三方共用。
    /// URI：tradostoolkit://glossary?kind=<local|online>&base=<termBaseUrl>&src=<srcLang>&tgt=<tgtLang>&domain=<domain>
    /// 两套术语源共用同一套 URI：
    ///   kind=local  —— 读本地 SQLite（GlossaryDb.term_entries），base 留空、可写；
    ///   kind=online —— 读线上 termBaseUrl，base 为服务地址（含协议），只读。
    /// 所有值经 Uri.EscapeDataString 编码后放入 query。
    /// </summary>
    internal static class NativeTerminologyProviderHelper
    {
        public const string SchemeActivation = "tradostoolkit://glossary";

        public static Uri BuildUri(string kind, string baseUrl, string sourceLang, string targetLang, string domain = null)
        {
            return new Uri(SchemeActivation
                + "?kind=" + Uri.EscapeDataString(TermSourceKind.Normalize(kind))
                + "&base=" + Uri.EscapeDataString(baseUrl ?? "")
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
