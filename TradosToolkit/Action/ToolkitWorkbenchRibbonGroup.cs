using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;

namespace TradosToolkit.Action
{
    /// <summary>
    /// 工作台 Ribbon 组，挂在 Studio 首页 "Home" 选项卡下。
    /// </summary>
    [RibbonGroup("TradosToolkit_WorkbenchGroup", Name = "Workbench_Group_Name", Description = "Workbench_Group_Description")]
    [RibbonGroupLayout(LocationByType = typeof(TranslationStudioDefaultRibbonTabs.HomeRibbonTabLocation))]
    public class ToolkitWorkbenchRibbonGroup : AbstractRibbonGroup
    {
    }
}
