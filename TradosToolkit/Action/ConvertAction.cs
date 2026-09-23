using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using TradosToolkit.FileConvert;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Action
{
    /// <summary>
    /// Add-ins(附加项)选项卡的"文件转换"按钮：打开任意文件 → sdlxliff 的简单界面。
    /// 选源文件、指定语向，一键产出目标 sdlxliff；等价命令行接口 POST /api/convert。
    /// </summary>
    [Action("TradosToolkit_Convert", Name = "Convert_Action_Name", Description = "Convert_Action_Description", Icon = "Convert_Action_Icon")]
    [ActionLayout(typeof(TradosToolkitRibbonGroup), 23, DisplayType.Large)]
    public class ConvertAction : AbstractAction
    {
        protected override void Execute()
        {
            ToolkitLog.Info("附加项按钮：打开文件转换");
            ConvertWindow.ShowOrActivate();
        }
    }
}
