using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using TradosToolkit.Diagnostics;
using TradosToolkit.Inbox;

namespace TradosToolkit.Action
{
    /// <summary>
    /// Add-ins(附加项)选项卡的"收件箱"按钮：打开目录监视/流程监控页。
    /// 往监视目录里拖一个文件，自动建项目、套本地库、产出报告 / 交付包 / 匹配库三件套。
    /// </summary>
    [Action("TradosToolkit_InboxMonitor", Name = "InboxMonitor_Action_Name", Description = "InboxMonitor_Action_Description", Icon = "InboxMonitor_Action_Icon")]
    [ActionLayout(typeof(TradosToolkitRibbonGroup), 22, DisplayType.Large)]
    public class InboxMonitorAction : AbstractAction
    {
        protected override void Execute()
        {
            ToolkitLog.Info("附加项按钮：打开收件箱");
            InboxWindow.ShowOrActivate();
        }
    }
}
