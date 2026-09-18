using System;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.TranslationProvider.Engines;

namespace TradosToolkit.TranslationProvider
{
    /// <summary>
    /// 全插件唯一的提供程序工厂：同时识别 TM 与 LLM 两种 URI scheme，
    /// 在这里按 scheme 选定引擎并从 URI query 还原配置，其余逻辑全部共享。
    /// </summary>
    [TranslationProviderFactory(
        Id = "TradosToolkit.ProviderFactory",
        Name = "Toolkit_Provider_Factory_Name",
        Description = "Toolkit_Provider_Factory_Description")]
    public class ToolkitTranslationProviderFactory : ITranslationProviderFactory
    {
        public ITranslationProvider CreateTranslationProvider(Uri translationProviderUri, string translationProviderState, ITranslationProviderCredentialStore credentialStore)
        {
            ToolkitLog.Boot("factory");
            if (!ToolkitUri.IsSupported(translationProviderUri))
            {
                ToolkitLog.Info("CreateTranslationProvider: 不认识的 URI，忽略 uri=" + translationProviderUri);
                return null;
            }

            var engine = CreateEngine(translationProviderUri);
            // API Key 一律读 config.json，不使用 Studio 凭据存储
            var apiKey = ToolkitConfig.Load().ApiKey;
            ToolkitLog.Info("CreateTranslationProvider: uri=" + translationProviderUri +
                            " engine=" + engine.DisplayName +
                            " state=" + (string.IsNullOrEmpty(translationProviderState) ? "(空)" : translationProviderState) +
                            " apiKey=" + (string.IsNullOrEmpty(apiKey) ? "(未配置)" : "(已配置,长度" + apiKey.Length + ")"));
            return new ToolkitTranslationProvider(
                translationProviderUri, translationProviderState, engine, apiKey);
        }

        public TranslationProviderInfo GetTranslationProviderInfo(Uri translationProviderUri, string translationProviderState)
        {
            var model = ToolkitUri.GetModel(translationProviderUri);
            var name = string.IsNullOrEmpty(model) ? "TradosToolkit" : "TradosToolkit (" + model + ")";

            return new TranslationProviderInfo
            {
                Name = name,
                TranslationMethod = TranslationMethod.MachineTranslation,
            };
        }

        public bool SupportsTranslationProviderUri(Uri translationProviderUri)
        {
            return ToolkitUri.IsSupported(translationProviderUri);
        }

        private static ITranslationEngine CreateEngine(Uri uri)
        {
            return new CascadeEngine(
                ToolkitConfig.Load().TmUrl, ToolkitUri.GetBaseUrl(uri), ToolkitUri.GetModel(uri));
        }
    }
}
