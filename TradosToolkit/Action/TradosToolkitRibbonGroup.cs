using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using TradosToolkit.Diagnostics;
using TradosToolkit.Inbox;
using TradosToolkit.Server;

namespace TradosToolkit.Action
{
    /// <summary>
    /// 插件专属 Ribbon 组，挂在 Studio "Add-ins" 选项卡下。
    /// Ribbon 组在 Studio 启动时实例化，借此时机自启本地 HTTP API 服务。
    /// </summary>
    [RibbonGroup("TradosToolkit_RibbonGroup", Name = "Ribbon_Group_Name", Description = "Ribbon_Group_Description")]
    [RibbonGroupLayout(LocationByType = typeof(TranslationStudioDefaultRibbonTabs.AddinsRibbonTabLocation))]
    public class TradosToolkitRibbonGroup : AbstractRibbonGroup
    {
        public TradosToolkitRibbonGroup()
        {
            ToolkitLog.Boot("ribbon");
            ToolkitLog.Info("Ribbon 组实例化，启动 API 服务");
            ToolkitApiServer.Instance.EnsureStarted();
            InboxApi.EnsureInit();
            InboxWatcher.Instance.AutoStart();
            TranslationMemories.TmIndexScheduler.Start();
        }
    }
}
