using System;
using Sdl.Terminology.TerminologyProvider.Core;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 原生术语源的工厂注册。Studio 通过本类识别 tradostoolkit://glossary URI 并实例化 Provider。
    /// URI 同时支持本地库（kind=local）与线上服务（kind=online），见 NativeTerminologyProviderHelper。
    /// </summary>
    [TerminologyProviderFactory(Id = "TradosToolkit.NativeTerminologyFactory",
                                 Name = "TradosToolkit 术语源",
                                 Description = "本地 SQLite 术语库（可写）+ 线上术语服务（只读）")]
    public class NativeTerminologyProviderFactory : ITerminologyProviderFactory
    {
        public ITerminologyProvider CreateTerminologyProvider(Uri terminologyProviderUri,
                                                              ITerminologyProviderCredentialStore credentials)
        {
            var kind = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "kind");
            var baseUrl = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "base");
            var src = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "src");
            var tgt = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "tgt");
            var domain = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "domain");
            return new NativeTerminologyProvider(kind, baseUrl, src, tgt, domain);
        }

        public bool SupportsTerminologyProviderUri(Uri terminologyProviderUri)
        {
            return NativeTerminologyProviderHelper.Supports(terminologyProviderUri);
        }
    }
}
