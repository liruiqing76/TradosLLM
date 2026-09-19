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
        private readonly Lazy<ElementHost> _host;

        public TranslationCenterController()
        {
            _host = new Lazy<ElementHost>(() => new ElementHost
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                Child = new TranslationCenterBrowserControl()
            });
        }

        protected override Control GetContentControl() => _host.Value;

        protected override void Initialize(IViewContext context)
        {
            ToolkitLog.Info("翻译中心 View Initialize");
            ActivationChanged += (s, e) => ToolkitLog.Info("翻译中心 View 激活=" + e.Active);
        }
    }
}
