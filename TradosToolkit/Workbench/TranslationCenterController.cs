using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Sdl.Desktop.IntegrationApi;
using Sdl.Desktop.IntegrationApi.Extensions;
using Sdl.Desktop.IntegrationApi.Interfaces;
using Sdl.TranslationStudioAutomation.IntegrationApi.Presentation.DefaultLocations;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 首页左导航（explorer bar）"翻译中心"View——官方 [View] + AbstractViewController 模式
    /// （同 2019 社区参考 Post-Edit Compare），内容为内置 WebView2 浏览器。
    /// </summary>
    [View(Id = "TradosToolkit_TranslationCenter",
          Name = "Translation_Center_View_Name",
          Description = "Translation_Center_View_Description",
          Icon = "Translation_Center_Icon",
          LocationByType = typeof(TranslationStudioDefaultViews.TradosStudioViewsLocation))]
    public class TranslationCenterController : AbstractViewController
    {
        private readonly Lazy<TranslationCenterBrowserControl> _browser;
        private readonly Lazy<ElementHost> _host;
        private readonly Lazy<ElementHost> _navHost;

        public TranslationCenterController()
        {
            _browser = new Lazy<TranslationCenterBrowserControl>(() => new TranslationCenterBrowserControl());
            _host = new Lazy<ElementHost>(() => new ElementHost
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                Child = _browser.Value
            });
            _navHost = new Lazy<ElementHost>(() => new ElementHost
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                Child = new TranslationCenterNavControl(_browser.Value)
            });
        }

        protected override Control GetContentControl() => _host.Value;

        /// <summary>View 自带左栏（左导航条上方区域，Post-Edit 同款机制）：常用地址。</summary>
        protected override Control GetExplorerBarControl() => _navHost.Value;

        protected override void Initialize(IViewContext context)
        {
            ToolkitLog.Info("翻译中心 View Initialize");
            ActivationChanged += (s, e) => ToolkitLog.Info("翻译中心 View 激活=" + e.Active);
        }
    }
}
