using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using TradosToolkit.Diagnostics;
using TradosToolkit.TranslationProvider.UI;

namespace TradosToolkit.Action
{
    /// <summary>
    /// Add-ins(附加项)选项卡的"术语管理"按钮：打开独立的术语管理页面。
    /// 面向本地译者：在一个专用整页里维护译前/译后术语（增删改查、CSV 导入导出、模板下载），
    /// 语言对默认取当前项目并可自由新增。
    /// </summary>
    [Action("TradosToolkit_Terminology", Name = "Terminology_Action_Name", Description = "Terminology_Action_Description")]
    [ActionLayout(typeof(TradosToolkitRibbonGroup), 20, DisplayType.Normal)]
    public class TerminologyAction : AbstractAction
    {
        protected override void Execute()
        {
            ToolkitLog.Info("附加项按钮：打开术语管理");
            GlossaryManagerWindow.ShowOrActivate();
        }
    }
}