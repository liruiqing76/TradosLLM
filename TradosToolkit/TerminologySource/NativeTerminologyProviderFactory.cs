using System;
using Sdl.Terminology.TerminologyProvider.Core;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 原生术语源的工厂注册。Studio 通过本类识别 tradostoolkit://glossary URI 并实例化 Provider。
    /// </summary>
    [TerminologyProviderFactory(Id = "TradosToolkit.NativeTerminologyFactory",
                                 Name = "TradosToolkit 术语服务",
                                 Description = "从内网术语服务实时检索的本机只读术语源")]
    public class NativeTerminologyProviderFactory : ITerminologyProviderFactory
    {
        public ITerminologyProvider CreateTerminologyProvider(Uri terminologyProviderUri,
                                                              ITerminologyProviderCredentialStore credentials)
        {
            var baseUrl = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "base");
            var src = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "src");
            var tgt = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "tgt");
            return new NativeTerminologyProvider(baseUrl, src, tgt);
        }

        public bool SupportsTerminologyProviderUri(Uri terminologyProviderUri)
        {
            return NativeTerminologyProviderHelper.Supports(terminologyProviderUri);
        }
    }
}