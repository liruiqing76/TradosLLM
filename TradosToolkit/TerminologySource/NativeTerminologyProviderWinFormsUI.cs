using System;
using System.Windows.Forms;
using Sdl.Terminology.TerminologyProvider.Core;

namespace TradosToolkit.TerminologySource
{
    /// <summary>
    /// 原生术语源添加/浏览 UI。Studio"术语库"对话框中用户点"添加/新建"时走 Browse：
    /// 弹出对话框选择来源类型（本地库 / 线上服务）与语言对，生成对应 Provider。
    /// SupportsEditing=true：本地源允许在 Studio 术语视图中直接增删改（落 SQLite），
    /// 具体写入由 NativeTerminologyProviderViewerWinFormsUI 承接。
    /// </summary>
    [TerminologyProviderWinFormsUI]
    public class NativeTerminologyProviderWinFormsUI : ITerminologyProviderWinFormsUI
    {
        public bool SupportsEditing => true;

        public string TypeDescription => "本地 SQLite 术语库（可写）+ 线上术语服务（只读）";

        public string TypeName => "TradosToolkit 术语源";

        public ITerminologyProvider[] Browse(IWin32Window owner, ITerminologyProviderCredentialStore credentialStore)
        {
            using (var dlg = new NativeTerminologyDialog())
            {
                if (dlg.ShowDialog(owner) != DialogResult.OK)
                    return new ITerminologyProvider[0];
                var provider = new NativeTerminologyProvider(
                    dlg.Kind,
                    dlg.TermBaseUrl,
                    dlg.SourceLang,
                    dlg.TargetLang,
                    dlg.Domain);
                return new ITerminologyProvider[] { provider };
            }
        }

        /// <summary>重新编辑：弹出对话框回填当前参数，确定后返回新的 Provider。</summary>
        public bool Edit(IWin32Window owner, ITerminologyProvider terminologyProvider)
        {
            var current = terminologyProvider as NativeTerminologyProvider;
            if (current == null) return false;

            using (var dlg = new NativeTerminologyDialog())
            {
                dlg.Preset(current.Kind, current.SourceLang, current.TargetLang, current.Domain);
                if (dlg.ShowDialog(owner) != DialogResult.OK) return false;
                var edited = new NativeTerminologyProvider(dlg.Kind, dlg.TermBaseUrl, dlg.SourceLang, dlg.TargetLang, dlg.Domain);
                // Studio 侧以返回值刷新显示信息；此处仅更新可变参数，保持对象可供编辑视图继续使用
                return edited.Uri != current.Uri;
            }
        }

        public TerminologyProviderDisplayInfo GetDisplayInfo(Uri terminologyProviderUri)
        {
            var kind = NativeTerminologyProviderHelper.GetQueryParam(terminologyProviderUri, "kind");
            return new TerminologyProviderDisplayInfo { Name = TermSourceKind.Label(kind) + "（TradosToolkit）" };
        }

        public bool SupportsTerminologyProviderUri(Uri terminologyProviderUri)
        {
            return NativeTerminologyProviderHelper.Supports(terminologyProviderUri);
        }
    }
}
