using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using TradosToolkit.Diagnostics;
using TradosToolkit.TranslationProvider.UI;

namespace TradosToolkit.Action
{
    /// <summary>
    /// Add-ins(附加项)选项卡的"记忆库管理"按钮：打开独立记忆库管理页面。
    /// 扫描本地 .sdltm、新建空库、把 TMX / SDLXLIFF 导入到指定语向的记忆库。
    /// </summary>
    [Action("TradosToolkit_TmManager", Name = "TmManager_Action_Name", Description = "TmManager_Action_Description", Icon = "TmManager_Action_Icon")]
    [ActionLayout(typeof(TradosToolkitRibbonGroup), 21, DisplayType.Large)]
    public class TmManagerAction : AbstractAction
    {
        protected override void Execute()
        {
            ToolkitLog.Info("附加项按钮：打开记忆库管理");
            TmManagerWindow.ShowOrActivate();
        }
    }
}