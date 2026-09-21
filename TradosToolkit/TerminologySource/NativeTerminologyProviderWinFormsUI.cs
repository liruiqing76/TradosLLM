using System;
using System.Windows.Forms;
using Sdl.Terminology.TerminologyProvider.Core;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 原生术语源添加/浏览 UI。Studio"术语库"对话框中用户点"添加键/新建"时走 Browse：
    /// 弹出对话框，用户输入线上术语服务地址 termBaseUrl 与语言对，生成 Provider。
    /// 只读（SupportsEditing=false），编辑仍走 GlossaryManager。
    /// </summary>
    [TerminologyProviderWinFormsUI]
    public class NativeTerminologyProviderWinFormsUI : ITerminologyProviderWinFormsUI
    {
        public bool SupportsEditing => false;

        public string TypeDescription => "从内网术语服务实时检索的只读术语源";

        public string TypeName => "TradosToolkit 术语服务";

        public ITerminologyProvider[] Browse(IWin32Window owner, ITerminologyProviderCredentialStore credentialStore)
        {
            using (var dlg = new NativeTerminologyDialog())
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK)
                    return new ITerminologyProvider[0];
                var provider = new NativeTerminologyProvider(
                    dlg.TermBaseUrl.Trim().TrimEnd('/'),
                    dlg.SourceLang.Trim(),
                    dlg.TargetLang.Trim());
                return new ITerminologyProvider[] { provider };
            }
        }

        public bool Edit(IWin32Window owner, ITerminologyProvider terminologyProvider)
        {
            return false;
        }

        public TerminologyProviderDisplayInfo GetDisplayInfo(Uri terminologyProviderUri)
        {
            return new TerminologyProviderDisplayInfo { Name = "TradosToolkit 术语服务" };
        }

        public bool SupportsTerminologyProviderUri(Uri terminologyProviderUri)
        {
            return NativeTerminologyProviderHelper.Supports(terminologyProviderUri);
        }
    }
}