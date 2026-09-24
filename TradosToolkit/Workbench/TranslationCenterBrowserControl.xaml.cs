using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 翻译中心主界面：内置 WebView2 浏览器（地址栏 + 前进/后退/刷新/主页 + 设主页）。
    /// 用系统 Edge WebView2 运行时，插件包只多 ~3MB 托管/加载器 DLL。
    /// </summary>
    public partial class TranslationCenterBrowserControl : UserControl
    {
        private bool _initStarted;
        private bool _webReady;

        // 保存环境与事件处理器引用，卸载时才能解绑并释放——否则浏览器进程、用户数据目录句柄
        // 会一直挂到 Studio 退出（WebView2 事件是 CoreWebView2 上的强引用，不解绑则控件无法回收）。
        private Microsoft.Web.WebView2.Core.CoreWebView2Environment _env;
        private EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs> _navStarting;
        private EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs> _navCompleted;
        private EventHandler<Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs> _newWindow;

        public TranslationCenterBrowserControl()
        {
            ToolkitLog.Info("翻译中心浏览器构建");
            InitializeComponent();
            Loaded += (s, e) => InitWeb();
            Unloaded += (s, e) => Shutdown();
        }

        private async void InitWeb()
        {
            if (_initStarted) return;
            _initStarted = true;

            try
            {
                PreloadNativeLoader();
                var userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TradosToolkit", "WebView2");
                Directory.CreateDirectory(userData);

                ShowStatus("正在启动内置浏览器…");
                _env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userData);
                await Web.EnsureCoreWebView2Async(_env);
                _webReady = true;

                Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _navStarting = (s, ev) => Dispatcher.Invoke(() =>
                {
                    NavProgress.Visibility = System.Windows.Visibility.Visible;
                    FallbackText.Visibility = System.Windows.Visibility.Collapsed;
                });
                Web.CoreWebView2.NavigationStarting += _navStarting;
                _navCompleted = (s, ev) => Dispatcher.Invoke(() =>
                {
                    NavProgress.Visibility = System.Windows.Visibility.Collapsed;
                    AddressBox.Text = Web.Source == null ? "" : Web.Source.AbsoluteUri;
                    if (!ev.IsSuccess)
                        ShowStatus("页面加载失败：" + ev.WebErrorStatus);
                    else
                        HideStatus();
                    ToolkitLog.Info("翻译中心导航完成: " + Web.Source + " success=" + ev.IsSuccess);
                });
                Web.NavigationCompleted += _navCompleted;
                _newWindow = (s, ev) =>
                {
                    // target=_blank 弹窗一律就地打开，避免脱离 Studio
                    ToolkitLog.Info("翻译中心新窗口请求转内联: " + ev.Uri);
                    ev.Handled = true;
                    try { Web.Source = new Uri(ev.Uri); } catch (Exception e) { ToolkitLog.Error("翻译中心内联打开失败", e); }
                };
                Web.CoreWebView2.NewWindowRequested += _newWindow;

                Web.Visibility = System.Windows.Visibility.Visible;
                Navigate(HomePageUrl());
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心 WebView2 初始化失败", ex);
                Web.Visibility = System.Windows.Visibility.Collapsed;
                FallbackText.Text = "内置浏览器不可用：" + ex.Message +
                    "\n需要系统安装 Microsoft Edge WebView2 运行时（Win10/11 一般自带）。";
                FallbackText.Visibility = System.Windows.Visibility.Visible;
                HideStatus();
            }
        }

        /// <summary>释放 WebView2 浏览器进程与用户数据目录句柄，并解绑事件。视图卸载/Studio 退出时调用；
        /// 幂等，重复调用安全。释放后再次加载会重新初始化（InitWeb 的 _initStarted 已复位）。</summary>
        public void Shutdown()
        {
            try
            {
                var core = _webReady ? Web.CoreWebView2 : null;
                if (core != null)
                {
                    if (_navStarting != null) core.NavigationStarting -= _navStarting;
                    if (_newWindow != null) core.NewWindowRequested -= _newWindow;
                }
                if (_navCompleted != null) Web.NavigationCompleted -= _navCompleted;

                // WebView2 控件释放会一并终止其浏览器进程；CoreWebView2Environment 本身不实现 IDisposable，
                // 只需断开引用（真正的句柄归控件所有）。
                try { Web.Dispose(); } catch (Exception ex) { ToolkitLog.Error("翻译中心 WebView2 控件释放失败", ex); }
                ToolkitLog.Info("翻译中心 WebView2 已释放");
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心 WebView2 释放异常", ex);
            }
            finally
            {
                _env = null;
                _navStarting = null;
                _navCompleted = null;
                _newWindow = null;
                _webReady = false;
                _initStarted = false;
            }
        }

        /// <summary>插件目录里的 WebView2Loader.dll 不在 Studio 进程默认搜索路径，先按绝对路径预载。</summary>
        private static void PreloadNativeLoader()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(TranslationCenterBrowserControl).Assembly.Location);
                var loader = Path.Combine(dir ?? "", "WebView2Loader.dll");
                if (File.Exists(loader) && LoadLibrary(loader) == IntPtr.Zero)
                    ToolkitLog.Info("翻译中心: WebView2Loader.dll 预载失败（将依赖默认搜索） " + Marshal.GetLastWin32Error());
                else
                    ToolkitLog.Info("翻译中心: WebView2Loader 预载 " + (File.Exists(loader) ? "已尝试 " + loader : "文件不在包内，走默认搜索"));
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心: WebView2Loader 预载异常", ex);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private string HomePageUrl()
        {
            var url = ToolkitConfig.Load().TranslationCenterUrl;
            return string.IsNullOrWhiteSpace(url) ? "https://www.bing.com" : url;
        }

        private void Navigate(string url)
        {
            if (!_webReady) return;
            try
            {
                if (!url.Contains("://")) url = "https://" + url;
                Web.Source = new Uri(url);
                ToolkitLog.Info("翻译中心导航: " + url);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心地址无效", ex);
                ShowStatus("地址无效：" + ex.Message);
            }
        }

        private void Go_Click(object sender, System.Windows.RoutedEventArgs e) => Navigate(AddressBox.Text.Trim());

        private void AddressBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Navigate(AddressBox.Text.Trim());
        }

        private void Back_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_webReady && Web.CoreWebView2 != null && Web.CoreWebView2.CanGoBack) Web.CoreWebView2.GoBack();
        }

        private void Forward_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_webReady && Web.CoreWebView2 != null && Web.CoreWebView2.CanGoForward) Web.CoreWebView2.GoForward();
        }

        private void Reload_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_webReady) Web.Reload();
        }

        private void Home_Click(object sender, System.Windows.RoutedEventArgs e) => Navigate(HomePageUrl());

        private void SetHome_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            var current = Web.Source == null ? AddressBox.Text.Trim() : Web.Source.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(current)) return;
            try
            {
                ToolkitConfig.Save(translationCenterUrl: current);
                ShowStatus("已设为主页：" + current);
                ToolkitLog.Info("翻译中心主页已保存: " + current);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("翻译中心主页保存失败", ex);
                ShowStatus("保存失败：" + ex.Message);
            }
        }

        private void ShowStatus(string text)
        {
            StatusText.Text = text;
            StatusText.Visibility = System.Windows.Visibility.Visible;
        }

        private void HideStatus() => StatusText.Visibility = System.Windows.Visibility.Collapsed;

        /// <summary>左栏"常用地址"点击条目时调用（TranslationCenterNavControl）。</summary>
        public void NavigateFromBookmark(string url) => Navigate(url);

        /// <summary>书签左栏取当前页（URL+标题）；未就绪或无页面返回 false。</summary>
        public bool TryGetCurrentPage(out string url, out string title)
        {
            url = null;
            title = null;
            if (!_webReady || Web.CoreWebView2 == null || Web.Source == null) return false;
            url = Web.Source.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(url)) return false;
            title = Web.CoreWebView2.DocumentTitle;
            if (string.IsNullOrWhiteSpace(title)) title = Web.Source.Host;
            return true;
        }
    }
}
