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

        /// <summary>
        /// Studio 内嵌术语库设置里 &lt;Path&gt; 的"提供程序 URI"与"术语库名"之间的分隔符
        /// （对应 MultiTerm 内部常量 TerminologyProviderPathSeparator，值为 \%\）。
        /// Studio 的 TermbaseSettings.GetProviderUri() 以该分隔符切分 Path：
        /// 取前半段为提供程序 URI、后半段为术语库名；若 Path 不含此分隔符，
        /// 其内部会对 Substring 传入负长度而抛
        /// "长度不能小于 0。参数名: length"，导致打开项目即崩溃。
        /// 因此写入 &lt;Path&gt; 时必须带上它。
        /// </summary>
        public const string ProviderPathSeparator = "\\%\\";

        /// <summary>拼出 Studio 期望的 &lt;Path&gt;：&lt;providerUri&gt;\%\&lt;name&gt;。</summary>
        public static string ComposePath(string providerUri, string name)
        {
            return (providerUri ?? string.Empty) + ProviderPathSeparator + (name ?? string.Empty);
        }

        /// <summary>取 &lt;Path&gt; 中分隔符之前的纯提供程序 URI；无分隔符时原样返回。</summary>
        public static string ProviderUriPart(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var i = path.IndexOf(ProviderPathSeparator, StringComparison.Ordinal);
            return i >= 0 ? path.Substring(0, i) : path;
        }

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
                    // 防御：若整个 Path（含 \%\名）被当作 URI 传入，最后一个 query 值会带上后缀，
                    // 这里统一截掉，保证 domain/kind/src/tgt 解析结果干净。
                    if (raw != null)
                    {
                        var sep = raw.IndexOf(ProviderPathSeparator, StringComparison.Ordinal);
                        if (sep >= 0) raw = raw.Substring(0, sep);
                    }
                    return raw ?? string.Empty;
                }
            }
            return string.Empty;
        }
    }
}
