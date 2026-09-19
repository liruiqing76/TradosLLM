using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using TradosToolkit.Diagnostics;
using TradosToolkit.Workbench;

namespace TradosToolkit.Action
{
    /// <summary>
    /// 首页 Home 选项卡"工作台"按钮：打开/激活工作台窗口。
    /// </summary>
    [Action("TradosToolkit_Workbench", Name = "Workbench_Action_Name", Description = "Workbench_Action_Description")]
    [ActionLayout(typeof(ToolkitWorkbenchRibbonGroup), 10, DisplayType.Large)]
    public class WorkbenchAction : AbstractAction
    {
        protected override void Execute()
        {
            ToolkitLog.Info("Home 功能区按钮：打开工作台");
            WorkbenchWindow.ShowOrActivate();
        }
    }
}
