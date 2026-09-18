using System;
using System.Windows.Forms;
using System.Windows.Interop;
using Sdl.LanguagePlatform.Core;
using Sdl.LanguagePlatform.TranslationMemory;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.TranslationProvider.UI;
using IWin32Window = System.Windows.Forms.IWin32Window;

namespace TradosToolkit.TranslationProvider
{
    /// <summary>
    /// 全插件唯一的配置 UI：用户在向导里选引擎类型（TM 服务 / LLM）、
    /// 填 baseUrl 与 API Key。Key 存凭据存储，baseUrl 等进 URI/state。
    /// 注册接口固定为 WinForms，但配置窗口用 WPF（XAML）实现。
    /// </summary>
    [TranslationProviderWinFormsUi(
        Id = "TradosToolkit.WinFormsUI",
        Name = "Toolkit_Provider_WinFormsUI_Name",
        Description = "Toolkit_Provider_WinFormsUI_Description")]
    public class ToolkitTranslationProviderWinFormsUI : ITranslationProviderWinFormsUI
    {
        public ITranslationProvider[] Browse(IWin32Window owner, LanguagePair[] languagePairs, ITranslationProviderCredentialStore credentialStore)
        {
            ToolkitLog.Boot("winforms-ui");
            ToolkitLog.Info("Browse: 语言对数=" + (languagePairs?.Length ?? 0));
            var pair = languagePairs != null && languagePairs.Length > 0 ? languagePairs[0] : null;
            var window = new ProviderConfigWindow(
                pair?.SourceCultureName ?? "zh-CN",
                pair?.TargetCultureName ?? "en-US");
            if (owner != null)
                new WindowInteropHelper(window).Owner = owner.Handle;

            bool? confirmed;
            try
            {
                confirmed = window.ShowDialog();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("Browse: 配置窗口异常", e);
                throw;
            }
            if (confirmed != true)
            {
                ToolkitLog.Info("Browse: 用户取消");
                return null;
            }

            var uri = ToolkitUri.Build(
                window.BaseUrl, window.Model, window.UsePreGlossary, window.UsePostGlossary, window.SupportsTags);

            // API Key 走 config.json，不再写 Studio 凭据存储
            ToolkitLog.Info("Browse: 确认 uri=" + uri);
            var provider = new ToolkitTranslationProviderFactory()
                .CreateTranslationProvider(uri, string.Empty, credentialStore);
            return new[] { provider };
        }

        public bool Edit(IWin32Window owner, ITranslationProvider translationProvider, LanguagePair[] languagePairs, ITranslationProviderCredentialStore credentialStore)
        {
            // 首版不支持原地编辑：删除后重新添加即可
            return false;
        }

        public bool GetCredentialsFromUser(IWin32Window owner, Uri translationProviderUri, string translationProviderState, ITranslationProviderCredentialStore credentialStore)
        {
            // API Key 统一走 config.json，无需凭据弹窗
            ToolkitLog.Info("GetCredentialsFromUser: 跳过（apiKey 读自 config.json） uri=" + translationProviderUri);
            return true;
        }

        public TranslationProviderDisplayInfo GetDisplayInfo(Uri translationProviderUri, string translationProviderState)
        {
            return new TranslationProviderDisplayInfo
            {
                Name = "TradosToolkit",
                TooltipText = TypeDescription,
            };
        }

        public bool SupportsEditing => false;

        public bool SupportsTranslationProviderUri(Uri translationProviderUri)
        {
            return ToolkitUri.IsSupported(translationProviderUri);
        }

        public string TypeDescription => "TM 优先查询，无匹配自动回退 OpenAI 兼容 LLM";

        public string TypeName => "TradosToolkit";
    }
}
