using System.Windows.Forms;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Action
{
    /// <summary>
    /// 一键操作 Ribbon 按钮骨架（M6 里程碑实现：一键预翻译，
    /// 届时改为 AutocompleteTask + ProjectAutomation API 调用）。
    /// </summary>
    [Action("TradosToolkit_OneClickPretranslate", Name = "Pretranslate_Action_Name", Description = "Pretranslate_Action_Description")]
    [ActionLayout(typeof(TradosToolkitRibbonGroup), 10, DisplayType.Large)]
    public class OneClickPretranslateAction : AbstractAction
    {
        protected override void Execute()
        {
            // 骨架验证：能在 Studio Add-ins 选项卡看到按钮并弹出此框即成功
            MessageBox.Show("TradosToolkit skeleton action executed.", "TradosToolkit");
        }
    }
}
