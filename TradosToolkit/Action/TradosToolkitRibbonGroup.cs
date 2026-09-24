using System;
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
            // 每项独立 try/catch：任一子系统启动失败都不能让整个 Ribbon 组构造失败
            // （构造抛异常会导致 Studio 加载插件时报错，且其余功能也一并不可用）。
            ToolkitLog.Boot("ribbon");
            ToolkitLog.Info("Ribbon 组实例化，启动 API 服务");
            try { ToolkitApiServer.Instance.EnsureStarted(); }
            catch (Exception e) { ToolkitLog.Error("Ribbon：启动本地 API 服务失败", e); }
            try { InboxApi.EnsureInit(); }
            catch (Exception e) { ToolkitLog.Error("Ribbon：初始化收件箱 API 失败", e); }
            try { InboxWatcher.Instance.AutoStart(); }
            catch (Exception e) { ToolkitLog.Error("Ribbon：自动启动收件箱监视失败", e); }
            try { TranslationMemories.TmIndexScheduler.Start(); }
            catch (Exception e) { ToolkitLog.Error("Ribbon：启动记忆库索引调度失败", e); }
        }
    }
}
