using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using TradosToolkit.Diagnostics;

namespace TradosToolkit.EditorPanel
{
    /// <summary>
    /// 编辑器右侧"文档预览"面板：内置 WebView2 渲染按段生成的 HTML。
    /// 宿主→页面用 PostWebMessageAsJson（setActive/update/setMode），
    /// 页面→宿主用 window.chrome.webview.postMessage（点击段 → 跳转编辑器）。
    /// Studio15 的 ViewPart 只收 WinForms Control，宿主用 ElementHost 包这个 WPF 控件。
    /// </summary>
    public partial class DocPreviewView : UserControl
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        /// <summary>点"刷新"：请求控制器重新读取文档并重建预览。</summary>
        public event System.Action RefreshRequested;

        /// <summary>预览里点击某段：回传段 id，由控制器把编辑器跳到该段。</summary>
        public event Action<string> SegmentActivated;

        private bool _initStarted;
        private bool _webReady;
        private bool _pageReady;
        private string _pendingHtml;
        private string _activeId;
        private string _mode = "both";

        public DocPreviewView()
        {
            ToolkitLog.Info("DocPreviewView ctor");
            InitializeComponent();
            Loaded += (s, e) => InitWeb();
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

                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userData);
                await Web.EnsureCoreWebView2Async(env);
                _webReady = true;

                Web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                Web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                Web.CoreWebView2.WebMessageReceived += OnWebMessage;
                Web.NavigationCompleted += (s, ev) => Dispatcher.Invoke(() =>
                {
                    if (!ev.IsSuccess)
                    {
                        ToolkitLog.Info("文档预览导航失败: " + ev.WebErrorStatus);
                        ShowFallback("预览加载失败：" + ev.WebErrorStatus);
                        return;
                    }
                    _pageReady = true;
                    Web.Visibility = Visibility.Visible;
                    FallbackText.Visibility = Visibility.Collapsed;
                    Post(new { type = "setMode", mode = _mode });
                    if (!string.IsNullOrEmpty(_activeId))
                        Post(new { type = "setActive", id = _activeId, scroll = true });
                });

                Web.Visibility = Visibility.Visible;
                NavigatePending();
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("文档预览 WebView2 初始化失败", ex);
                Web.Visibility = Visibility.Collapsed;
                ShowFallback("内置浏览器不可用：" + ex.Message +
                    "\n需要系统安装 Microsoft Edge WebView2 运行时（Win10/11 一般自带）。");
            }
        }

        /// <summary>控制器在文档打开/切换/手动刷新时推整篇 HTML。</summary>
        public void ShowHtml(string html)
        {
            _pendingHtml = html;
            if (_webReady) NavigatePending();
        }

        /// <summary>无活动文档：清空并给出提示。</summary>
        public void ShowEmpty(string message)
        {
            _pendingHtml = null;
            _activeId = null;
            _pageReady = false;
            ShowFallback(message);
        }

        public void SetStatus(string text) => StatusChip.Text = text ?? string.Empty;

        /// <summary>高亮并（可选）滚动到当前段。</summary>
        public void SetActive(string id, bool scroll)
        {
            _activeId = id;
            if (string.IsNullOrEmpty(id)) return;
            Post(new { type = "setActive", id, scroll });
        }

        /// <summary>就地更新某段文本与状态（编辑器打字时用，避免整篇重载）。</summary>
        public void UpdateSegment(string id, string src, string tgt, string level)
        {
            if (string.IsNullOrEmpty(id)) return;
            Post(new { type = "update", id, src, tgt, level });
        }

        public void ShowFallback(string message)
        {
            Web.Visibility = Visibility.Collapsed;
            FallbackText.Text = message;
            FallbackText.Visibility = Visibility.Visible;
        }

        /// <summary>把 HTML 写到本地临时文件再导航（NavigateToString 对超大文档有 2MB 上限）。</summary>
        private void NavigatePending()
        {
            if (!_webReady || _pendingHtml == null) return;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TradosToolkit", "DocPreview");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "preview.html");
                File.WriteAllText(file, _pendingHtml, new UTF8Encoding(false));

                _pageReady = false;
                FallbackText.Visibility = Visibility.Collapsed;
                Web.CoreWebView2.Navigate(new Uri(file).AbsoluteUri);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("文档预览：写入/导航失败", ex);
                ShowFallback("预览生成失败：" + ex.Message);
            }
        }

        private void Post(object payload)
        {
            if (!_webReady || !_pageReady || Web.CoreWebView2 == null) return;
            try
            {
                Web.CoreWebView2.PostWebMessageAsJson(Json.Serialize(payload));
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("文档预览：发送消息失败", ex);
            }
        }

        private void OnWebMessage(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var msg = Json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson);
                if (msg == null) return;
                object type;
                if (!msg.TryGetValue("type", out type) || !Equals(type, "activate")) return;
                object id;
                if (!msg.TryGetValue("id", out id) || id == null) return;
                SegmentActivated?.Invoke(Convert.ToString(id));
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("文档预览：解析页面消息失败", ex);
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke();

        private void Mode_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ModeBox == null) return;
            switch (ModeBox.SelectedIndex)
            {
                case 1: _mode = "target"; break;
                case 2: _mode = "source"; break;
                default: _mode = "both"; break;
            }
            Post(new { type = "setMode", mode = _mode });
        }

        /// <summary>插件目录里的 WebView2Loader.dll 不在 Studio 进程默认搜索路径，先按绝对路径预载。</summary>
        private static void PreloadNativeLoader()
        {
            try
            {
                var dir = Path.GetDirectoryName(typeof(DocPreviewView).Assembly.Location);
                var loader = Path.Combine(dir ?? "", "WebView2Loader.dll");
                if (File.Exists(loader) && LoadLibrary(loader) == IntPtr.Zero)
                    ToolkitLog.Info("文档预览: WebView2Loader.dll 预载失败（将依赖默认搜索） " + Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("文档预览: WebView2Loader 预载异常", ex);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);
    }
}
