using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TradosToolkit.Diagnostics;
using TradosToolkit.EditorPanel;
using TradosToolkit.Server;
using TradosToolkit.TranslationMemories;
using TradosToolkit.TranslationProvider.Engines;
using TradosToolkit.TranslationProvider.UI;

namespace TradosToolkit.Workbench
{
    /// <summary>
    /// 工作台窗口（Home 功能区"工作台"按钮打开）：概览（TM/LLM/API 状态 + 网关连通测试）、
    /// 记忆库（扫描目录内 .sdltm 并展示元数据）、快捷入口。文案经 UiText 走随包资源做国际化。
    /// </summary>
    public partial class WorkbenchWindow : Window
    {
        private static WorkbenchWindow _instance;

        private readonly DispatcherTimer _tickTimer = new DispatcherTimer();
        private CancellationTokenSource _testCts;
        private Stopwatch _testWatch;

        private CancellationTokenSource _scanCts;
        private bool _scanning;

        private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x6B));
        private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE0, 0x5D, 0x4B));
        private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0xC0, 0xC6, 0xD2));

        /// <summary>由 Home 功能区按钮调用：已打开则激活，否则新建。</summary>
        public static void ShowOrActivate()
        {
            var current = _instance;
            if (current != null)
            {
                ToolkitLog.Info("工作台已打开，激活");
                current.Activate();
                if (current.WindowState == WindowState.Minimized)
                    current.WindowState = WindowState.Normal;
                return;
            }
            var window = new WorkbenchWindow();
            _instance = window;
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_instance, window)) _instance = null;
            };
            window.Show();
        }

        public WorkbenchWindow()
        {
            ToolkitLog.Info("工作台窗口打开");
            InitializeComponent();
            ApplyTexts();
            _tickTimer.Interval = TimeSpan.FromSeconds(1);
            _tickTimer.Tick += (s, e) => UpdateTestProgress();
            TmDirBox.Text = ToolkitConfig.Load().TmScanDirectory;
            TmStatusText.Text = UiText.T("WB_Mem_Idle");
            VersionText.Text = "v" + typeof(WorkbenchWindow).Assembly.GetName().Version.ToString(3);
            RefreshStatus();
        }

        /// <summary>把随包资源里的文案刷到各控件（缺文化自动回退中性英文）。</summary>
        private void ApplyTexts()
        {
            Title = UiText.T("WB_Title");
            TitleText.Text = UiText.T("WB_Title");
            SubtitleText.Text = UiText.T("WB_Subtitle");
            OverviewTab.Header = UiText.T("WB_Tab_Overview");
            MemoriesTab.Header = UiText.T("WB_Tab_Memories");
            QuickTab.Header = UiText.T("WB_Tab_Quick");

            StatusTitle.Text = UiText.T("WB_Status_Title");
            RefreshButton.Content = UiText.T("WB_Btn_Refresh");
            TmCardTitle.Text = UiText.T("WB_Card_Tm");
            LlmCardTitle.Text = UiText.T("WB_Card_Llm");
            ApiCardTitle.Text = UiText.T("WB_Card_Api");

            TestTitle.Text = UiText.T("WB_Test_Title");
            TestButton.Content = UiText.T("WB_Test_Run");
            CancelButton.Content = UiText.T("WB_Test_Cancel");
            TestResultText.Text = UiText.T("WB_Test_Idle");

            DirLabel.Text = UiText.T("WB_Mem_Dir_Label");
            BrowseButton.Content = UiText.T("WB_Mem_Btn_Browse");
            ScanButton.Content = UiText.T("WB_Mem_Btn_Scan");
            ScanCancelButton.Content = UiText.T("WB_Mem_Btn_Cancel");
            ColName.Content = UiText.T("WB_Mem_Col_Name");
            ColLang.Content = UiText.T("WB_Mem_Col_Lang");
            ColUnits.Content = UiText.T("WB_Mem_Col_Units");
            ColSize.Content = UiText.T("WB_Mem_Col_Size");
            ColModified.Content = UiText.T("WB_Mem_Col_Modified");
            ColStatus.Content = UiText.T("WB_Mem_Col_Status");
            MenuOpenFolder.Header = UiText.T("WB_Mem_OpenFolder");
            MenuCopyPath.Header = UiText.T("WB_Mem_CopyPath");

            QuickTitle.Text = UiText.T("WB_Quik_Title");
            ProviderButton.Content = UiText.T("WB_Quik_Provider");
            GlossaryButton.Content = UiText.T("WB_Quik_Glossary");
            LogButton.Content = UiText.T("WB_Quik_Logs");
            ConfigButton.Content = UiText.T("WB_Quik_Config");
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：手动刷新状态");
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var config = ToolkitConfig.Load();

            if (string.IsNullOrEmpty(config.TmUrl))
            {
                TmDot.Fill = Red;
                TmStatusLabel.Text = UiText.T("WB_Status_Tm_Unconfigured");
            }
            else
            {
                TmDot.Fill = Green;
                TmStatusLabel.Text = config.TmUrl;
            }

            if (LlmChatClient.IsReady())
            {
                LlmDot.Fill = Green;
                LlmStatusLabel.Text = UiText.Tf("WB_Status_Llm_Ok", config.LlmBaseUrl, config.LlmModel);
            }
            else
            {
                LlmDot.Fill = Red;
                LlmStatusLabel.Text = UiText.T("WB_Status_Llm_Missing");
            }

            var server = ToolkitApiServer.Instance;
            if (server == null)
            {
                ApiDot.Fill = Gray;
                ApiStatusLabel.Text = UiText.T("WB_Status_Api_Init");
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
                    ApiStatusLabel.Text = UiText.Tf("WB_Status_Api_Ok", server.Port);
                }
                else
                {
                    ApiDot.Fill = Red;
                    ApiStatusLabel.Text = UiText.T("WB_Status_Api_Down");
                }
            }
        }

        private async void TestLlm_Click(object sender, RoutedEventArgs e)
        {
            if (!LlmChatClient.IsReady())
            {
                TestResultText.Text = UiText.T("WB_Test_NotConfigured");
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
                    TestResultText.Text = UiText.Tf("WB_Test_NoChoices", _testWatch.ElapsedMilliseconds,
                        EngineHttp.AsString(response.TryGetValue("error", out var err) ? err : null));
                }
                else
                {
                    LlmDot.Fill = Green;
                    TestResultText.Text = UiText.Tf("WB_Test_Ok", (_testWatch.ElapsedMilliseconds / 1000.0).ToString("0.0"));
                }
                ToolkitLog.Info("工作台：LLM 连通测试完成 " + _testWatch.ElapsedMilliseconds + "ms choices=" + (choices?.Count ?? 0));
            }
            catch (OperationCanceledException)
            {
                TestResultText.Text = UiText.T("WB_Test_Cancelled");
                ToolkitLog.Info("工作台：LLM 连通测试被取消 " + _testWatch.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex)
            {
                LlmDot.Fill = Red;
                TestResultText.Text = UiText.Tf("WB_Test_Fail", _testWatch.ElapsedMilliseconds, ex.Message);
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
            TestResultText.Text = UiText.Tf("WB_Test_Running", (int)_testWatch.Elapsed.TotalSeconds);
        }

        private void CancelTest_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：用户取消 LLM 连通测试");
            _testCts?.Cancel();
        }

        // ==================== 记忆库 ====================

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
            {
                var initial = TmDirBox.Text.Trim();
                if (!string.IsNullOrEmpty(initial) && Directory.Exists(initial))
                    dlg.SelectedPath = initial;
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

                TmDirBox.Text = dlg.SelectedPath;
                ToolkitLog.Info("工作台：选择记忆库目录 " + dlg.SelectedPath);
                SaveScanDirectory(dlg.SelectedPath);
            }
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_scanning) return;
            var dir = TmDirBox.Text.Trim();
            if (string.IsNullOrEmpty(dir))
            {
                TmStatusText.Text = UiText.T("WB_Mem_NoDir");
                return;
            }
            if (!Directory.Exists(dir))
            {
                TmStatusText.Text = UiText.Tf("WB_Mem_BadDir", dir);
                return;
            }

            SaveScanDirectory(dir);
            _scanning = true;
            ScanButton.IsEnabled = false;
            BrowseButton.IsEnabled = false;
            ScanCancelButton.Visibility = Visibility.Visible;
            TmProgress.Visibility = Visibility.Visible;
            TmList.ItemsSource = null;
            _scanCts = new CancellationTokenSource();
            var watch = Stopwatch.StartNew();
            var progress = new Progress<int>(n => TmStatusText.Text = UiText.Tf("WB_Mem_Scanning", n));

            try
            {
                var items = await LocalTmScanner.ScanAsync(dir, progress, _scanCts.Token);
                var ok = items.Count(x => x.State == LocalTmState.Ok);
                var prot = items.Count(x => x.State == LocalTmState.Protected);
                var err = items.Count(x => x.State == LocalTmState.Error);
                TmList.ItemsSource = items;
                TmStatusText.Text = items.Count == 0
                    ? UiText.Tf("WB_Mem_None", dir)
                    : UiText.Tf("WB_Mem_Done", items.Count, ok, prot, err, (watch.Elapsed.TotalSeconds).ToString("0.0"));
                ToolkitLog.Info("工作台：记忆库扫描完成 dir=" + dir + " 总数=" + items.Count +
                                " 正常=" + ok + " 受保护=" + prot + " 出错=" + err +
                                " 耗时=" + watch.ElapsedMilliseconds + "ms");
            }
            catch (OperationCanceledException)
            {
                TmStatusText.Text = UiText.T("WB_Mem_Cancelled");
                ToolkitLog.Info("工作台：记忆库扫描被取消 " + watch.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex)
            {
                TmStatusText.Text = UiText.Tf("WB_Test_Fail", watch.ElapsedMilliseconds, ex.Message);
                ToolkitLog.Error("工作台：记忆库扫描失败 dir=" + dir, ex);
            }
            finally
            {
                _scanning = false;
                ScanButton.IsEnabled = true;
                BrowseButton.IsEnabled = true;
                ScanCancelButton.Visibility = Visibility.Collapsed;
                TmProgress.Visibility = Visibility.Collapsed;
                _scanCts.Dispose();
                _scanCts = null;
            }
        }

        private void CancelScan_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：用户取消记忆库扫描");
            _scanCts?.Cancel();
        }

        private void SaveScanDirectory(string dir)
        {
            try
            {
                ToolkitConfig.Save(tmScanDirectory: dir);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("工作台：保存扫描目录失败", ex);
            }
        }

        private void TmList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TmList.SelectedItem is LocalTmInfo item) RevealInExplorer(item.FilePath);
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (TmList.SelectedItem is LocalTmInfo item) RevealInExplorer(item.FilePath);
        }

        private void CopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (TmList.SelectedItem is LocalTmInfo item)
            {
                try { Clipboard.SetText(item.FilePath); } catch (Exception ex) { ToolkitLog.Error("工作台：复制路径失败", ex); }
            }
        }

        private void RevealInExplorer(string filePath)
        {
            try
            {
                ToolkitLog.Info("工作台：定位记忆库文件 " + filePath);
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + filePath + "\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("工作台：定位文件失败", ex);
                MessageBox.Show(this, UiText.Tf("WB_Err_OpenFailed", ex.Message), UiText.T("WB_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ==================== 快捷入口 ====================

        private void OpenProviderConfig_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：打开提供程序配置");
            new ProviderConfigWindow { Owner = this }.ShowDialog();
            RefreshStatus();
        }

        private void OpenGlossary_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：打开术语管理");
            new GlossaryManagerWindow("zh-CN", "en-US") { Owner = this }.ShowDialog();
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
                MessageBox.Show(this, UiText.Tf("WB_Err_OpenFailed", ex.Message), UiText.T("WB_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
