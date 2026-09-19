using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TradosToolkit.Diagnostics;
using TradosToolkit.EditorPanel;
using TradosToolkit.Server;
using TradosToolkit.TranslationProvider.Engines;
using TradosToolkit.TranslationProvider.UI;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 工作台内容视图：挂在首页左导航"翻译中心"View 里（ElementHost 承载）。
    /// </summary>
    public partial class WorkbenchControl : System.Windows.Controls.UserControl
    {
        private readonly DispatcherTimer _tickTimer = new DispatcherTimer();
        private CancellationTokenSource _testCts;
        private Stopwatch _testWatch;

        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x6B));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE0, 0x5D, 0x4B));
        private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0xC0, 0xC6, 0xD2));

        public WorkbenchControl()
        {
            ToolkitLog.Info("工作台视图构建");
            InitializeComponent();
            _tickTimer.Interval = TimeSpan.FromSeconds(1);
            _tickTimer.Tick += (s, e) => UpdateTestProgress();
            RefreshStatus();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：手动刷新状态");
            RefreshStatus();
        }

        public void RefreshStatus()
        {
            var config = ToolkitConfig.Load();

            if (string.IsNullOrEmpty(config.TmUrl))
            {
                TmDot.Fill = Red;
                TmStatusLabel.Text = "TM 统一接口：未配置（config.json 的 tmUrl）";
            }
            else
            {
                TmDot.Fill = Green;
                TmStatusLabel.Text = "TM 统一接口：" + config.TmUrl;
            }

            if (LlmChatClient.IsReady())
            {
                LlmDot.Fill = Green;
                LlmStatusLabel.Text = "LLM 回退：" + config.LlmBaseUrl + " · " + config.LlmModel + " · Key 已保存";
            }
            else
            {
                LlmDot.Fill = Red;
                LlmStatusLabel.Text = "LLM 回退：未配置完整（Base URL / 模型 / API Key 缺项），可在提供程序配置里补齐";
            }

            var server = ToolkitApiServer.Instance;
            if (server == null)
            {
                ApiDot.Fill = Gray;
                ApiStatusLabel.Text = "本地 API：服务未初始化";
            }
            else
            {
                if (!server.IsListening)
                {
                    ToolkitLog.Info("工作台：API 未监听，尝试拉起");
                    try { server.EnsureStarted(); } catch (Exception ex) { ToolkitLog.Error("工作台：API 拉起失败", ex); }
                }
                if (server.IsListening)
                {
                    ApiDot.Fill = Green;
                    ApiStatusLabel.Text = "本地 API：http://localhost:" + server.Port + " 监听中";
                }
                else
                {
                    ApiDot.Fill = Red;
                    ApiStatusLabel.Text = "本地 API：未监听";
                }
            }
        }

        private async void TestLlm_Click(object sender, RoutedEventArgs e)
        {
            if (!LlmChatClient.IsReady())
            {
                TestResultText.Text = "LLM 未配置完整，请先在\"翻译提供程序配置\"里填写 Base URL / 模型 / API Key。";
                return;
            }

            var config = ToolkitConfig.Load();
            TestButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Visible;
            _testWatch = Stopwatch.StartNew();
            _testCts = new CancellationTokenSource();
            _tickTimer.Start();
            UpdateTestProgress();
            ToolkitLog.Info("工作台：开始 LLM 连通测试 baseUrl=" + config.LlmBaseUrl + " model=" + config.LlmModel);

            try
            {
                var body = new Dictionary<string, object>
                {
                    { "model", config.LlmModel },
                    { "temperature", 0 },
                    { "max_tokens", 8 },
                    { "messages", new List<object>
                        {
                            new Dictionary<string, object> { { "role", "user" }, { "content", "回复 OK 两个字母即可" } }
                        }
                    },
                };
                var url = config.LlmBaseUrl.TrimEnd('/') + "/chat/completions";
                var response = await EngineHttp.PostJsonAsync(url, body, config.ApiKey, _testCts.Token);

                var choices = EngineHttp.AsList(response.TryGetValue("choices", out var c) ? c : null);
                _tickTimer.Stop();
                if (choices == null || choices.Count == 0)
                {
                    LlmDot.Fill = Red;
                    TestResultText.Text = "✗ 网关有响应但没有 choices（" + _testWatch.ElapsedMilliseconds + " ms）：" +
                        EngineHttp.AsString(response.TryGetValue("error", out var err) ? err : null);
                }
                else
                {
                    LlmDot.Fill = Green;
                    TestResultText.Text = "✓ 连通正常，耗时 " + (_testWatch.ElapsedMilliseconds / 1000.0).ToString("0.0") + " 秒。";
                }
                ToolkitLog.Info("工作台：LLM 连通测试完成 " + _testWatch.ElapsedMilliseconds + "ms choices=" + (choices?.Count ?? 0));
            }
            catch (OperationCanceledException)
            {
                TestResultText.Text = "已取消测试。";
                ToolkitLog.Info("工作台：LLM 连通测试被取消 " + _testWatch.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex)
            {
                LlmDot.Fill = Red;
                TestResultText.Text = "✗ 测试失败（" + _testWatch.ElapsedMilliseconds + " ms）：" + ex.Message;
                ToolkitLog.Error("工作台：LLM 连通测试失败", ex);
            }
            finally
            {
                _tickTimer.Stop();
                TestButton.IsEnabled = true;
                CancelButton.Visibility = Visibility.Collapsed;
                _testCts.Dispose();
                _testCts = null;
            }
        }

        private void UpdateTestProgress()
        {
            TestResultText.Text = "测试中…已等待 " + (int)_testWatch.Elapsed.TotalSeconds + " 秒（点\"取消\"可中止）";
        }

        private void CancelTest_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：用户取消 LLM 连通测试");
            _testCts?.Cancel();
        }

        private void OpenProviderConfig_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：打开提供程序配置");
            new ProviderConfigWindow { Owner = Window.GetWindow(this) }.ShowDialog();
            RefreshStatus();
        }

        private void OpenGlossary_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：打开术语管理");
            new GlossaryManagerWindow("zh-CN", "en-US") { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：打开日志目录");
            ToolkitLog.OpenFolder();
        }

        private void OpenConfigFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ToolkitLog.Info("工作台：定位配置文件 " + ToolkitConfig.ConfigFilePath);
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + ToolkitConfig.ConfigFilePath + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("工作台：打开配置文件位置失败", ex);
                MessageBox.Show("打开失败：" + ex.Message, "TradosToolkit",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
