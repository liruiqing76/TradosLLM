using System;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.EditorPanel;

namespace TradosToolkit.Action
{
    /// <summary>
    /// Add-ins(附加项)选项卡的"文档预览"按钮：唤出编辑器右侧的文档预览面板。
    /// ViewPart 是否显示由 Studio 持久化的窗口布局决定，新装插件的面板不会自动进入已有布局，
    /// 因此这里显式 Show() 一次，让用户能一键打开（等价于"视图 → 重置窗口布局"）。
    /// </summary>
    [Action("TradosToolkit_DocPreview", typeof(EditorController), Name = "DocPreview_Action_Name", Description = "DocPreview_Action_Description", Icon = "DocPreview_Action_Icon")]
    [ActionLayout(typeof(TradosToolkitRibbonGroup), 24, DisplayType.Large)]
    public class DocPreviewAction : AbstractAction
    {
        protected override void Execute()
        {
            ToolkitLog.Info("附加项按钮：打开文档预览");
            try
            {
                SdlTradosStudio.Application.GetController<DocPreviewController>().Show();
            }
            catch (Exception e)
            {
                ToolkitLog.Error("文档预览：打开面板失败", e);
            }
        }
    }
}
