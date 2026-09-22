using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TradosToolkit.Common;
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
            InitFlows();
            _tickTimer.Interval = TimeSpan.FromSeconds(1);
            _tickTimer.Tick += (s, e) => UpdateTestProgress();
            TmDirBox.Text = ToolkitConfig.Load().TmScanDirectory;
            DomCombo.ItemsSource = DomainCatalog.Names();
            var cfgDomain = ToolkitConfig.Load().Domain;
            DomCombo.SelectedItem = DomCombo.Items.OfType<string>()
                .FirstOrDefault(d => string.Equals(d, cfgDomain, StringComparison.OrdinalIgnoreCase))
                ?? Glossaries.DomainTree.DefaultDomain;
            TmStatusText.Text = UiText.T("WB_Mem_Idle");
            VersionText.Text = "v" + typeof(WorkbenchWindow).Assembly.GetName().Version.ToString(3);
            RefreshStatus();
        }

        /// <summary>把随包资源里的文案刷到各控件（缺文化自动回退中性英文）。</summary>
        private void ApplyTexts()
        {
            Title = UiText.T("WB_Title");
            SubtitleText.Text = UiText.T("WB_Subtitle");
            NavOverviewText.Text = UiText.T("WB_Tab_Overview");
            NavMemoriesText.Text = UiText.T("WB_Tab_Memories");
            NavQuickText.Text = UiText.T("WB_Tab_Quick");
            NavToolsText.Text = "流程编排";
            PageTitleOverview.Text = UiText.T("WB_Tab_Overview");
            PageTitleMemories.Text = UiText.T("WB_Tab_Memories");
            PageTitleQuick.Text = UiText.T("WB_Tab_Quick");

            RefreshButton.Content = UiText.T("WB_Btn_Refresh");
            TmCardTitle.Text = UiText.T("WB_Card_Tm").ToUpperInvariant();
            LlmCardTitle.Text = UiText.T("WB_Card_Llm").ToUpperInvariant();
            ApiCardTitle.Text = UiText.T("WB_Card_Api").ToUpperInvariant();

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
            NewTmButton.Content = UiText.T("WB_Mem_Tool_New");
            MergeButton.Content = UiText.T("WB_Mem_Tool_Merge");
            DupesButton.Content = UiText.T("WB_Mem_Tool_Dupes");
            AddProjButton.Content = UiText.T("WB_Mem_Tool_AddProj");
            MemOpCancel.Content = UiText.T("WB_Mem_Btn_Cancel");

            QuickTitle.Text = UiText.T("WB_Quik_Title").ToUpperInvariant();
            ProviderButton.Content = UiText.T("WB_Quik_Provider");
            GlossaryButton.Content = UiText.T("WB_Quik_Glossary");
            LogButton.Content = UiText.T("WB_Quik_Logs");
            ConfigButton.Content = UiText.T("WB_Quik_Config");
            ApiLink.ToolTip = UiText.T("WB_Status_Api_Open");
        }

        private void Nav_Changed(object sender, RoutedEventArgs e)
        {
            // InitializeComponent 解析 RadioButton IsChecked=True 时字段尚未就绪
            if (OverviewPanel == null) return;
            var picked = sender as System.Windows.Controls.RadioButton;
            OverviewPanel.Visibility = Visibility.Collapsed;
            MemoriesPanel.Visibility = Visibility.Collapsed;
            QuickPanel.Visibility = Visibility.Collapsed;
            ToolsPanel.Visibility = Visibility.Collapsed;
            ReviewPanel.Visibility = Visibility.Collapsed;
            var target = picked?.Tag as string;
            if (target == "MemoriesPanel") MemoriesPanel.Visibility = Visibility.Visible;
            else if (target == "QuickPanel") QuickPanel.Visibility = Visibility.Visible;
            else if (target == "ToolsPanel") ToolsPanel.Visibility = Visibility.Visible;
            else if (target == "ReviewPanel") ReviewPanel.Visibility = Visibility.Visible;
            else OverviewPanel.Visibility = Visibility.Visible;
            ToolkitLog.Info("工作台：切换到 " + (target ?? "overview"));
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
                ApiBarText.Text = "api: -";
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
                    ApiBarText.Text = "localhost:" + server.Port;
                }
                else
                {
                    ApiDot.Fill = Red;
                    ApiStatusLabel.Text = UiText.T("WB_Status_Api_Down");
                    ApiBarText.Text = "api: down";
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

        private void ApplyDomain_Click(object sender, RoutedEventArgs e)
        {
            var sel = DomCombo.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(sel)) return;
            try
            {
                ToolkitConfig.Save(domain: sel);
                ToolkitLog.Info("工作台：已应用全局领域 " + sel);
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("工作台：应用全局领域失败", ex);
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

        // ==================== 记忆库工具（新建/合并/查重/加入项目） ====================

        private CancellationTokenSource _memOpCts;
        private bool _memOpBusy;

        /// <summary>后台跑记忆库操作：状态行 + 进度条 + 取消，全部按钮禁用防重入。</summary>
        private async Task RunMemOpAsync(string opName, Func<IProgress<string>, CancellationToken, Task> op)
        {
            if (_scanning || _memOpBusy) return;
            _memOpBusy = true;
            SetMemToolsEnabled(false);
            MemOpCancel.Visibility = Visibility.Visible;
            TmProgress.Visibility = Visibility.Visible;
            var watch = Stopwatch.StartNew();
            ToolkitLog.Info("工作台：记忆库操作开始 " + opName);
            try
            {
                var progress = new Progress<string>(s => TmStatusText.Text = UiText.Tf("WB_Mem_Busy", s));
                await op(progress, _memOpCts.Token);
                ToolkitLog.Info("工作台：记忆库操作完成 " + opName + " 耗时=" + watch.ElapsedMilliseconds + "ms");
            }
            catch (OperationCanceledException)
            {
                TmStatusText.Text = UiText.T("WB_Mem_Cancelled");
                ToolkitLog.Info("工作台：记忆库操作取消 " + opName);
            }
            catch (Exception ex)
            {
                TmStatusText.Text = UiText.Tf("WB_Mem_Err_Load", ex.Message);
                ToolkitLog.Error("工作台：记忆库操作失败 " + opName, ex);
            }
            finally
            {
                _memOpBusy = false;
                MemOpCancel.Visibility = Visibility.Collapsed;
                TmProgress.Visibility = Visibility.Collapsed;
                SetMemToolsEnabled(true);
                _memOpCts.Dispose();
                _memOpCts = null;
            }
        }

        private void SetMemToolsEnabled(bool enabled)
        {
            NewTmButton.IsEnabled = enabled;
            MergeButton.IsEnabled = enabled;
            DupesButton.IsEnabled = enabled;
            AddProjButton.IsEnabled = enabled;
            ScanButton.IsEnabled = enabled;
        }

        private void CancelMemOp_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：用户取消记忆库操作");
            _memOpCts?.Cancel();
        }

        /// <summary>操作成功后重跑一次扫描刷新列表（复用当前目录，不打扰用户）。</summary>
        private async Task RescanAfterOpAsync()
        {
            if (!string.IsNullOrWhiteSpace(TmDirBox.Text) && Directory.Exists(TmDirBox.Text.Trim()))
                await Scan_Click_Inline();
        }

        private async Task Scan_Click_Inline()
        {
            // Scan_Click 是 async void 事件处理器，这里等价的 Task 版本供操作完成后刷新
            var dir = TmDirBox.Text.Trim();
            if (_scanning || !Directory.Exists(dir)) return;
            _scanning = true;
            SetMemToolsEnabled(false);
            ScanCancelButton.Visibility = Visibility.Visible;
            TmProgress.Visibility = Visibility.Visible;
            _scanCts = new CancellationTokenSource();
            try
            {
                var items = await LocalTmScanner.ScanAsync(dir, null, _scanCts.Token);
                TmList.ItemsSource = items;
            }
            finally
            {
                _scanning = false;
                SetMemToolsEnabled(true);
                ScanCancelButton.Visibility = Visibility.Collapsed;
                TmProgress.Visibility = Visibility.Collapsed;
                _scanCts.Dispose();
                _scanCts = null;
            }
        }

        private void NewTm_Click(object sender, RoutedEventArgs e)
        {
            if (!TmToolDialogs.CreateTm(this, out var path, out var name, out var src, out var tgt)) return;
            TmStatusText.Text = UiText.Tf("WB_Mem_Result_New", name);
            ToolkitLog.Info("工作台：新建记忆库完成 " + path + " " + src.Name + "→" + tgt.Name);
            _ = RescanAfterOpAsync();
        }

        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            var selected = TmList.SelectedItems.OfType<LocalTmInfo>()
                .Where(x => x.State == LocalTmState.Ok).ToList();
            if (selected.Count < 2)
            {
                TmStatusText.Text = UiText.T("WB_Mem_Err_NeedTwo");
                return;
            }
            if (!TmToolDialogs.MergeDialog(this, selected, out var targetPath, out var policy)) return;

            var sources = selected.Select(x => x.FilePath).ToList();
            _memOpCts = new CancellationTokenSource();
            _ = RunMemOpAsync("merge(" + sources.Count + ")", async (progress, ct) =>
            {
                var report = await Task.Run(() => TmToolkit.Merge(sources, targetPath, policy, progress, ct), CancellationToken.None);
                TmStatusText.Text = UiText.Tf("WB_Mem_Result_Merge", report.Read, report.Written,
                    report.SkippedDuplicates, report.ConflictsResolved);
                await RescanAfterOpAsync();
            });
        }

        private void Dupes_Click(object sender, RoutedEventArgs e)
        {
            if (!(TmList.SelectedItem is LocalTmInfo item) || item.State != LocalTmState.Ok)
            {
                TmStatusText.Text = UiText.T("WB_Mem_Err_NoSel");
                return;
            }
            _memOpCts = new CancellationTokenSource();
            var path = item.FilePath;
            _ = RunMemOpAsync("duplicates(" + item.Name + ")", async (progress, ct) =>
            {
                var groups = await Task.Run(() => TmToolkit.FindDuplicates(path, null, ct), CancellationToken.None);
                if (groups.Count == 0)
                {
                    TmStatusText.Text = UiText.T("WB_Mem_Result_None");
                    return;
                }
                TmStatusText.Text = UiText.Tf("WB_Mem_Result_Dupes", groups.Count);
                Dispatcher.Invoke(() => TmToolDialogs.ShowDuplicates(this, item.Name, groups));
            });
        }

        private void AddToProject_Click(object sender, RoutedEventArgs e)
        {
            if (!(TmList.SelectedItem is LocalTmInfo item) || item.State != LocalTmState.Ok)
            {
                TmStatusText.Text = UiText.T("WB_Mem_Err_NoSel");
                return;
            }
            try
            {
                var projects = Sdl.TranslationStudioAutomation.IntegrationApi.SdlTradosStudio.Application
                    .GetController<Sdl.TranslationStudioAutomation.IntegrationApi.ProjectsController>();
                var project = projects.CurrentProject;
                if (project == null)
                {
                    TmStatusText.Text = UiText.T("WB_Mem_Err_NoProject");
                    return;
                }
                var uri = Sdl.LanguagePlatform.TranslationMemoryApi.FileBasedTranslationMemory
                    .GetFileBasedTranslationMemoryUri(item.FilePath);
                var config = project.GetTranslationProviderConfiguration();
                if (config.Entries != null && config.Entries.Any(en =>
                        en.MainTranslationProvider != null &&
                        en.MainTranslationProvider.Uri != null &&
                        en.MainTranslationProvider.Uri.Equals(uri)))
                {
                    TmStatusText.Text = UiText.T("WB_Mem_AlreadyAdded");
                    return;
                }
                config.Entries.Add(new Sdl.ProjectAutomation.Core.TranslationProviderCascadeEntry(
                    new Sdl.ProjectAutomation.Core.TranslationProviderReference(uri, null, true), true, true, false));
                project.UpdateTranslationProviderConfiguration(config);
                TmStatusText.Text = UiText.Tf("WB_Mem_Result_Added", item.Name);
                ToolkitLog.Info("工作台：已把记忆库加入当前项目主 TM " + item.FilePath);
            }
            catch (Exception ex)
            {
                TmStatusText.Text = UiText.Tf("WB_Mem_Err_Load", ex.Message);
                ToolkitLog.Error("工作台：加入当前项目失败 " + item.FilePath, ex);
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
            ToolkitLog.Info("工作台：打开术语管理（独立页）");
            GlossaryManagerWindow.ShowOrActivate();
        }

        private void OpenTm_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：打开记忆库管理（独立页）");
            TmManagerWindow.ShowOrActivate();
        }

        private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：打开日志目录");
            ToolkitLog.OpenFolder();
        }

        private void OpenStatusPanel_Click(object sender, RoutedEventArgs e)
        {
            var url = "http://localhost:" + ToolkitApiServer.Instance.Port + "/";
            try
            {
                ToolkitLog.Info("工作台：打开状态面板 " + url);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("工作台：打开状态面板失败", ex);
                MessageBox.Show(this, UiText.Tf("WB_Err_OpenFailed", ex.Message), UiText.T("WB_Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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

        // ==================== 流程编排 ====================

        /// <summary>一个可编排的操作步骤。Studio 自动任务只做透传，不重做；插件步骤是插件真正增值的部分。</summary>
        private sealed class ProcOp
        {
            public string Key;
            public string Label;
            public string Kind; // "studio" 原生自动任务(透传 pipeline task 名) | "plugin" 插件特有步骤
            public ProcOp(string key, string label, string kind) { Key = key; Label = label; Kind = kind; }
        }

        /// <summary>可选操作清单：Studio 已有功能仅透传其 task 名，插件新能力按独立步骤串入。</summary>
        private static readonly List<ProcOp> ProcOps = new List<ProcOp>
        {
            // ---- Studio 原生自动任务（插件只负责触发，绝不去重做实现）----
            new ProcOp("pretranslate", "预翻译（Studio 自动任务）", "studio"),
            new ProcOp("analyze", "分析（Studio 自动任务）", "studio"),
            new ProcOp("wordcount", "字数统计（Studio 自动任务）", "studio"),
            new ProcOp("target", "生成目标译文（Studio 自动任务）", "studio"),
            new ProcOp("export", "导出译文（Studio 自动任务）", "studio"),
            new ProcOp("updatetm", "更新记忆库（Studio 自动任务）", "studio"),
            // ---- 插件特有步骤（把上面 Studio 动作与插件能力串成完整流程）----
            new ProcOp("health", "健康自检（插件）", "plugin"),
            new ProcOp("report", "词数/报价报告（插件）", "plugin"),
            new ProcOp("triage", "翻译前分诊（插件）", "plugin"),
            new ProcOp("audit", "一致性审计·预览（插件）", "plugin"),
            new ProcOp("auditapply", "一键统一译文（插件·备份.bak）", "plugin"),
            new ProcOp("auditall", "一致性审计·跨文件（插件）", "plugin"),
            new ProcOp("backfill", "术语自动回填（插件）", "plugin"),
        };

        private static readonly Dictionary<string, ProcOp> OpIndex = ProcOps.ToDictionary(o => o.Key, StringComparer.OrdinalIgnoreCase);

        private List<ToolkitConfig.ProcessCard> _flowCards;
        private bool _flowBusy;
        private CancellationTokenSource _flowCts;
        private string _pendingOp;

        private void InitFlows()
        {
            try { _flowCards = ToolkitConfig.Load().ProcessCards; }
            catch (Exception ex) { ToolkitLog.Error("工作台：流程卡读取失败，用默认", ex); _flowCards = ToolkitConfig.DefaultProcessCards(); }
            if (_flowCards.Count == 0) _flowCards.Add(new ToolkitConfig.ProcessCard { name = "我的流程" });
            foreach (var op in ProcOps) FlowOpCombo.Items.Add(op);
            if (ProcOps.Count > 0) { FlowOpCombo.SelectedIndex = 0; _pendingOp = ProcOps[0].Key; }
            RebuildFlowList();
        }

        private void PersistFlows()
        {
            try { ToolkitConfig.Save(processCards: _flowCards); }
            catch (Exception ex)
            {
                ToolkitLog.Error("工作台：流程卡保存失败", ex);
                FlowStatus.Text = "保存失败：" + ex.Message;
            }
        }

        private void RebuildFlowList()
        {
            var prev = (FlowList.SelectedItem as ToolkitConfig.ProcessCard)?.name;
            FlowList.Items.Clear();
            foreach (var c in _flowCards) FlowList.Items.Add(c);
            if (prev != null)
            {
                var match = _flowCards.FirstOrDefault(x => x.name == prev);
                if (match != null) FlowList.SelectedItem = match;
            }
            if (FlowList.SelectedItem == null && _flowCards.Count > 0) FlowList.SelectedIndex = 0;
            SelectCard(_flowCards.FirstOrDefault(x => FlowList.SelectedItem == x));
        }

        private ToolkitConfig.ProcessCard SelectedCard() => FlowList.SelectedItem as ToolkitConfig.ProcessCard;

        private void SelectCard(ToolkitConfig.ProcessCard card)
        {
            FlowSteps.Items.Clear();
            FlowRunNote.Text = "";
            if (card != null)
            {
                FlowTitle.Text = "步骤  —  " + card.name;
                for (int k = 0; k < card.steps.Count; k++)
                    FlowSteps.Items.Add((k + 1) + ".  " + OpLabel(card.steps[k]));
                FlowRunText.Text = "运行《" + card.name + "》";
            }
            else
            {
                FlowTitle.Text = "步骤";
                FlowRunText.Text = "运行本流程";
            }
        }

        private static string OpLabel(string key)
        {
            ProcOp op;
            return OpIndex.TryGetValue(key ?? "", out op) ? op.Label : "未知步骤:" + key;
        }

        private static bool IsStudio(string key) => OpIndex.TryGetValue(key ?? "", out var op) && op.Kind == "studio";

        private void FlowList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            SelectCard(SelectedCard());
        }

        private void FlowAdd_Click(object sender, RoutedEventArgs e)
        {
            var n = _flowCards.Count + 1;
            var name = "新流程 " + n;
            while (_flowCards.Any(c => c.name == name)) name = "新流程 " + (++n);
            var card = new ToolkitConfig.ProcessCard { name = name, steps = new List<string>() };
            _flowCards.Add(card);
            PersistFlows();
            RebuildFlowList();
            FlowList.SelectedItem = card;
            ToolkitLog.Info("工作台：新建流程卡 " + name);
        }

        private void FlowRename_Click(object sender, RoutedEventArgs e)
        {
            var card = SelectedCard();
            if (card == null) { FlowStatus.Text = "请先在左侧选择一张流程卡。"; return; }
            var input = Microsoft.VisualBasic.Interaction.InputBox("新流程名称：", "重命名流程", card.name, -1, -1);
            if (string.IsNullOrWhiteSpace(input) || input == card.name) return;
            card.name = input.Trim();
            PersistFlows();
            RebuildFlowList();
            ToolkitLog.Info("工作台：流程卡重命名 " + input);
        }

        private void FlowDel_Click(object sender, RoutedEventArgs e)
        {
            var card = SelectedCard();
            if (card == null) { FlowStatus.Text = "请先在左侧选择一张流程卡。"; return; }
            if (MessageBox.Show(this, "删除流程《" + card.name + "》？", "流程编排",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            _flowCards.Remove(card);
            PersistFlows();
            RebuildFlowList();
        }

        private void FlowOpCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (FlowOpCombo.SelectedItem is ProcOp op) _pendingOp = op.Key;
        }

        private void FlowSteps_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var card = SelectedCard();
            var idx = FlowSteps.SelectedIndex;
            if (card == null || idx < 0 || idx >= card.steps.Count) return;
            // 选中某步时，把该步当前操作带到下拉框，便于"设为该操作"二次编辑
            var op = OpIndex.TryGetValue(card.steps[idx], out var o) ? o : null;
            if (op != null)
            {
                FlowOpCombo.SelectedItem = op;
                _pendingOp = op.Key;
            }
        }

        private string PendingOp()
        {
            if (string.IsNullOrEmpty(_pendingOp) || !OpIndex.ContainsKey(_pendingOp)) return ProcOps[0].Key;
            return _pendingOp;
        }

        private void FlowStepAdd_Click(object sender, RoutedEventArgs e)
        {
            var card = SelectedCard();
            if (card == null) { FlowStatus.Text = "请先选一张流程卡再添加步骤。"; return; }
            card.steps.Add(PendingOp());
            PersistFlows();
            SelectCard(card);
            FlowSteps.SelectedIndex = card.steps.Count - 1;
        }

        private void FlowStepSet_Click(object sender, RoutedEventArgs e)
        {
            var card = SelectedCard();
            var idx = FlowSteps.SelectedIndex;
            if (card == null || idx < 0 || idx >= card.steps.Count) { FlowStatus.Text = "请先在上方选中一个步骤。"; return; }
            var key = PendingOp();
            if (card.steps[idx] == key) return;
            card.steps[idx] = key;
            PersistFlows();
            SelectCard(card);
            FlowSteps.SelectedIndex = idx;
        }

        private void MoveStep(int dir)
        {
            var card = SelectedCard();
            var idx = FlowSteps.SelectedIndex;
            if (card == null || idx < 0) return;
            var j = idx + dir;
            if (j < 0 || j >= card.steps.Count) return;
            var tmp = card.steps[idx];
            card.steps[idx] = card.steps[j];
            card.steps[j] = tmp;
            PersistFlows();
            SelectCard(card);
            FlowSteps.SelectedIndex = j;
        }

        private void FlowStepUp_Click(object sender, RoutedEventArgs e) => MoveStep(-1);

        private void FlowStepDown_Click(object sender, RoutedEventArgs e) => MoveStep(1);

        private void FlowStepDel_Click(object sender, RoutedEventArgs e)
        {
            var card = SelectedCard();
            var idx = FlowSteps.SelectedIndex;
            if (card == null || idx < 0 || idx >= card.steps.Count) { FlowStatus.Text = "请先在上方选中一个步骤。"; return; }
            card.steps.RemoveAt(idx);
            PersistFlows();
            SelectCard(card);
        }

        private void FlowRunCancel_Click(object sender, RoutedEventArgs e)
        {
            ToolkitLog.Info("工作台：用户取消流程运行");
            _flowCts?.Cancel();
        }

        private void SetFlowRunning(bool running)
        {
            FlowRunBtn.IsEnabled = !running;
            FlowRunCancel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            FlowAddBtn.IsEnabled = !running; FlowRenameBtn.IsEnabled = !running; FlowDelBtn.IsEnabled = !running;
            FlowStepAdd.IsEnabled = !running; FlowStepSet.IsEnabled = !running;
            FlowStepUp.IsEnabled = !running; FlowStepDown.IsEnabled = !running; FlowStepDel.IsEnabled = !running;
        }

        /// <summary>直调插件 HTTP 端点；返回 (状态是否成功, 展示文本)。Studio 自动任务不走这里。</summary>
        private Tuple<bool, string> RunPluginStep(string key)
        {
            var q = new Dictionary<string, string>();
            var body = "";
            var method = "GET";
            var path = "/api/health";
            switch (key)
            {
                case "health":
                    break;
                case "report":
                    path = "/api/project/report";
                    q = new Dictionary<string, string> { { "path", NeedPath() } };
                    break;
                case "triage":
                    path = "/api/project/triage";
                    q = BaseQuery();
                    break;
                case "audit":
                    path = "/api/project/audit";
                    q = BaseQuery();
                    break;
                case "auditapply":
                    path = "/api/project/audit";
                    method = "POST";
                    q = BaseQuery();
                    break;
                case "auditall":
                    path = "/api/project/audit";
                    q = new Dictionary<string, string> { { "path", NeedPath() }, { "all", "1" } };
                    break;
                case "backfill":
                    path = "/api/glossary/backfill";
                    var src = ToolsSrcBox.Text.Trim();
                    var tgt = ToolsTgtBox.Text.Trim();
                    if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt))
                        return Tuple.Create(false, "请填写语言对（源/目标），或先“取当前项目”自动带入。");
                    var req = new Dictionary<string, object> { { "srcLang", src }, { "tgtLang", tgt } };
                    var bp = ToolsFileBox.Text.Trim();
                    if (!string.IsNullOrEmpty(bp)) req["bilingualPath"] = bp;
                    body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(req);
                    break;
            }
            var token = ApiConfig.Load().GetOrCreateToken();
            var result = ProjectApi.Handle(method, path, q, body, token, token);
            var ok = result.Status >= 200 && result.Status < 300;
            var json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result.Payload);
            return Tuple.Create(ok, (ok ? "" : "[HTTP " + result.Status + "] ") + json);
        }

        /// <summary>把连续的一段 Studio 自动任务合并成一次 pipeline 调用（tolerant，单步顺序执行）。</summary>
        private Tuple<bool, string> RunStudioPipeline(List<string> keys)
        {
            var steps = new List<Dictionary<string, object>>();
            foreach (var k in keys) steps.Add(new Dictionary<string, object> { { "task", k } });
            var body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(
                new Dictionary<string, object> { { "steps", steps }, { "tolerant", true } });
            var token = ApiConfig.Load().GetOrCreateToken();
            var result = ProjectApi.Handle("POST", "/api/project/pipeline",
                new Dictionary<string, string> { { "path", NeedPath() } }, body, token, token);
            var ok = result.Status >= 200 && result.Status < 300;
            var json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result.Payload);
            return Tuple.Create(ok, (ok ? "" : "[HTTP " + result.Status + "] ") + json);
        }

        private async void FlowRun_Click(object sender, RoutedEventArgs e)
        {
            var card = SelectedCard();
            if (card == null) { ToolsOutput.Text = "请先在左侧选一张流程卡。"; return; }
            if (card.steps.Count == 0) { ToolsOutput.Text = "该流程没有步骤。请用下方“＋添加/设为该操作”编排步骤。"; return; }
            if (_flowBusy) { ToolsOutput.Text = "流程正在运行，请稍候。"; return; }

            _flowBusy = true;
            SetFlowRunning(true);
            _flowCts = new CancellationTokenSource();
            var token = _flowCts.Token;
            var watch = Stopwatch.StartNew();
            var sb = new StringBuilder();
            sb.AppendLine(">> 运行流程《" + card.name + "》  ·  " + string.Join(" → ", card.steps.Select(OpLabel)));
            sb.AppendLine();
            ToolsOutput.Text = sb.ToString();
            ToolkitLog.Info("工作台：运行流程卡 " + card.name + " 步骤=" + string.Join(",", card.steps));

            var failed = 0;
            var index = 0;
            try
            {
                while (index < card.steps.Count)
                {
                    token.ThrowIfCancellationRequested();
                    if (IsStudio(card.steps[index]))
                    {
                        // 合并连续一段 Studio 自动任务为一次 pipeline 调用，保持相对顺序
                        var seq = new List<string>();
                        while (index < card.steps.Count && IsStudio(card.steps[index])) { seq.Add(card.steps[index]); index++; }
                        var start = index - seq.Count;
                        var stepLine = string.Join(" → ", seq.Select(OpLabel));
                        AppendLine(sb, "· [开始] " + (start + 1) + ".." + index + " " + stepLine);
                        var sw = Stopwatch.StartNew();
                        var r = await Task.Run(() => RunStudioPipeline(seq), CancellationToken.None);
                        sw.Stop();
                        AppendLine(sb, r.Item1 ? "[完成] " + stepLine + "  ·  " + sw.ElapsedMilliseconds + " ms"
                                              : "[失败] " + stepLine + "  ·  " + r.Item2);
                        if (!r.Item1) failed++;
                    }
                    else
                    {
                        var key = card.steps[index];
                        var nth = index + 1;
                        var sw = Stopwatch.StartNew();
                        AppendLine(sb, "· [开始] " + nth + ". " + OpLabel(key));
                        try
                        {
                            var r = await Task.Run(() => RunPluginStep(key), CancellationToken.None);
                            sw.Stop();
                            AppendLine(sb, r.Item1 ? "[完成] " + nth + ". " + OpLabel(key) + "  ·  " + sw.ElapsedMilliseconds + " ms"
                                                  : "[失败] " + nth + ". " + OpLabel(key) + "  ·  " + r.Item2);
                            if (!r.Item1) failed++;
                        }
                        catch (InvalidOperationException ex)
                        {
                            // 例如未选项目 —— 中断
                            sb.AppendLine("[失败] " + nth + ". " + OpLabel(key) + "  ·  " + ex.Message);
                            ToolsOutput.Text = sb.ToString();
                            throw;
                        }
                        index++;
                    }
                    ToolsOutput.Text = sb.ToString();
                }
                token.ThrowIfCancellationRequested();
                sb.AppendLine();
                sb.AppendLine("—— 流程结束：失败 " + failed + " 步，总耗时 " + watch.ElapsedMilliseconds + " ms");
            }
            catch (OperationCanceledException)
            {
                sb.AppendLine();
                sb.AppendLine("—— 已取消，已执行部分保留。");
                ToolkitLog.Info("工作台：流程运行被取消 " + card.name);
            }
            catch (Exception ex)
            {
                sb.AppendLine();
                sb.AppendLine("—— 流程中断：" + ex.Message);
                ToolkitLog.Error("工作台：流程运行中断 " + card.name, ex);
            }
            finally
            {
                _flowBusy = false;
                SetFlowRunning(false);
                _flowCts.Dispose();
                _flowCts = null;
            }
            ToolsOutput.Text = sb.ToString();
        }

        private void AppendLine(StringBuilder sb, string line)
        {
            sb.AppendLine(line);
            ToolsOutput.Text = sb.ToString();
        }

        // ==================== 批处理工具输入区（流程编排共用） ====================

        private string NeedPath()
        {
            var p = ToolsProjBox.Text.Trim();
            if (string.IsNullOrEmpty(p)) throw new InvalidOperationException("请先选择项目 (.sdlp)：点“取当前项目”或“浏览…”");
            if (!File.Exists(p)) throw new InvalidOperationException("项目文件不存在: " + p);
            return p;
        }

        private Dictionary<string, string> BaseQuery()
        {
            var q = new Dictionary<string, string> { { "path", NeedPath() } };
            var f = ToolsFileBox.Text.Trim();
            if (!string.IsNullOrEmpty(f)) q["file"] = f;
            return q;
        }

        private void ToolsPickCurrent_Click(object sender, RoutedEventArgs e)
        {
            var proj = CurrentProjectPath();
            if (proj == null) { ToolsOutput.Text = "未找到当前激活的项目。"; return; }
            ToolsProjBox.Text = proj;
            FillDefaultLanguage();
            ToolsOutput.Text = "已取当前项目：" + proj;
            ToolkitLog.Info("工作台：流程页取当前项目 " + proj);
        }

        private void ToolsBrowseProj_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "SDL 项目|*.sdlp", CheckFileExists = true };
            if (dlg.ShowDialog(this) == true) ToolsProjBox.Text = dlg.FileName;
        }

        private void ToolsBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "SDLXLiff|*.sdlxliff", CheckFileExists = true };
            if (dlg.ShowDialog(this) == true) ToolsFileBox.Text = dlg.FileName;
        }

        private string CurrentProjectPath()
        {
            try
            {
                var ctl = Sdl.TranslationStudioAutomation.IntegrationApi.SdlTradosStudio.Application
                    .GetController<Sdl.TranslationStudioAutomation.IntegrationApi.ProjectsController>();
                var p = ctl.CurrentProject;
                return p == null ? null : p.FilePath;
            }
            catch (Exception ex) { ToolkitLog.Error("工作台：读取当前项目失败", ex); return null; }
        }

        private void FillDefaultLanguage()
        {
            try
            {
                var ctl = Sdl.TranslationStudioAutomation.IntegrationApi.SdlTradosStudio.Application
                    .GetController<Sdl.TranslationStudioAutomation.IntegrationApi.ProjectsController>();
                var p = ctl.CurrentProject;
                if (p == null) return;
                var info = p.GetProjectInfo();
                if (string.IsNullOrWhiteSpace(ToolsSrcBox.Text) && info.SourceLanguage != null)
                    ToolsSrcBox.Text = info.SourceLanguage.IsoAbbreviation;
                if (string.IsNullOrWhiteSpace(ToolsTgtBox.Text) && info.TargetLanguages != null && info.TargetLanguages.Count() > 0)
                    ToolsTgtBox.Text = info.TargetLanguages.First().IsoAbbreviation;
            }
            catch (Exception ex) { ToolkitLog.Error("工作台：读取当前项目语言失败", ex); }
        }

        // ==================== AI 审校 ====================

        private bool _revBusy;
        private CancellationTokenSource _revCts;

        private void RevPickCurrent_Click(object sender, RoutedEventArgs e)
        {
            var proj = CurrentProjectPath();
            if (proj == null) { RevStatus.Text = "未找到当前激活的项目。"; return; }
            RevProjBox.Text = proj;
            RevStatus.Text = "已取当前项目：" + proj;
            ToolkitLog.Info("工作台：审校页取当前项目 " + proj);
        }

        private void RevBrowseProj_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "SDL 项目|*.sdlp", CheckFileExists = true };
            if (dlg.ShowDialog(this) == true) RevProjBox.Text = dlg.FileName;
        }

        private async void RevRun_Click(object sender, RoutedEventArgs e)
        {
            var path = RevProjBox.Text.Trim();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                RevStatus.Text = "请先选择项目 (.sdlp)：点“取当前项目”或“浏览…”";
                return;
            }
            if (!LlmChatClient.IsReady())
            {
                RevStatus.Text = "未配置 LLM(llmBaseUrl/llmModel/apiKey 见 config.json)，无法审校。";
                return;
            }

            var q = new Dictionary<string, string> { { "path", path } };
            var file = RevFileBox.Text.Trim();
            if (!string.IsNullOrEmpty(file)) q["file"] = file;

            int maxSeg = 1000;
            int.TryParse(RevMaxBox.Text.Trim(), out maxSeg);
            if (maxSeg <= 0) maxSeg = 1000;
            var body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(
                new Dictionary<string, object> { { "maxSegments", maxSeg } });

            _revBusy = true;
            _revCts = new CancellationTokenSource();
            RevRunBtn.IsEnabled = false;
            RevCancelBtn.Visibility = Visibility.Visible;
            RevList.ItemsSource = null;
            RevStatus.Text = "AI 审校进行中（已交后台，批式调用 LLM，段数多时需等待）…";
            RevContext.Text = Path.GetFileNameWithoutExtension(path) + (string.IsNullOrEmpty(file) ? "" : " / " + file);
            RevDetailTitle.Text = "选中段：等待结果";
            RevDetailBox.Text = "";
            RevAppliedText.Text = "";

            var token = ApiConfig.Load().GetOrCreateToken();
            try
            {
                var result = await Task.Run(() =>
                    ProjectApi.Handle("POST", "/api/review", q, body, token, token), CancellationToken.None);
                if (_revCts == null || _revCts.IsCancellationRequested)
                {
                    RevStatus.Text = "已取消。";
                    return;
                }
                if (result.Status >= 400)
                {
                    var err = PayloadText(result);
                    RevStatus.Text = "审校失败：[HTTP " + result.Status + "] " + err;
                    return;
                }
                PopulateReview(result.Payload);
            }
            catch (Exception ex)
            {
                RevStatus.Text = "审校异常：" + ex.Message;
                ToolkitLog.Error("工作台：AI 审校异常", ex);
            }
            finally
            {
                _revBusy = false;
                RevRunBtn.IsEnabled = true;
                RevCancelBtn.Visibility = Visibility.Collapsed;
                _revCts.Dispose();
                _revCts = null;
            }
        }

        private void RevCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_revCts != null) _revCts.Cancel();
            RevStatus.Text = "正在取消（当前 LLM 调用完成后停止）…";
        }

        private void PopulateReview(object payload)
        {
            var dict = payload as Dictionary<string, object>;
            if (dict == null || !dict.ContainsKey("items")) { RevStatus.Text = "返回数据格式异常。"; return; }
            var items = dict["items"] as List<Dictionary<string, object>>;
            var rows = new List<ReviewRow>();
            if (items != null)
                foreach (var it in items)
                    rows.Add(ReviewRow.From(it));
            RevList.ItemsSource = rows;
            var summary = dict.ContainsKey("summary") ? dict["summary"] as Dictionary<string, object> : null;
            RevStatus.Text = dict.ContainsKey("message")
                ? Convert.ToString(dict["message"])
                : (summary == null ? "" : BuildSummaryText(summary) + ("，共 " + rows.Count + " 段"));
            RevContext.Text = Convert.ToString(dict.ContainsKey("file")
                ? (dict["file"] == null ? "" : dict["file"]) : "") ;
            if (rows.Count > 0) RevList.SelectedIndex = 0;
        }

        private static string BuildSummaryText(Dictionary<string, object> summary)
        {
            var sb = new StringBuilder();
            if (summary.ContainsKey("red")) sb.Append("红=").Append(summary["red"]).Append(' ');
            if (summary.ContainsKey("amber")) sb.Append("黄(机器可改)=").Append(summary["amber"]).Append(' ');
            if (summary.ContainsKey("ok")) sb.Append("通过=").Append(summary["ok"]).Append(' ');
            if (summary.ContainsKey("error")) sb.Append("失败=").Append(summary["error"]).Append(' ');
            return sb.ToString().Trim();
        }

        private void RevList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_revCts == null) return; // 初始化期
            var row = RevList.SelectedItem as ReviewRow;
            if (row == null) { RevDetailTitle.Text = "选中段：未选择"; RevDetailBox.Text = ""; return; }
            var header = "段 #" + row.ItemId;
            if (!string.IsNullOrEmpty(row.VerdictText)) header += "  ·  " + row.VerdictText;
            if (!string.IsNullOrEmpty(row.Issue)) header += "  ·  " + row.Issue;
            RevDetailTitle.Text = header;
            var sb = new StringBuilder();
            sb.Append("原文:  ").Append(row.Source).AppendLine().AppendLine()
              .Append("现译文:  ").Append(row.Target).AppendLine().AppendLine();
            if (!string.IsNullOrEmpty(row.Suggestion))
                sb.Append("建议修订:  ").Append(row.Suggestion);
            else
                sb.Append("建议修订:  (无，需人工处理)");
            RevDetailBox.Text = sb.ToString();
            RevAppliedText.Text = "";
        }

        private void RevDetail_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            // 手动编辑详情区，标记需写回
            if (RevAppliedText != null) RevAppliedText.Text = "";
        }

        private async void RevApply_Click(object sender, RoutedEventArgs e)
        {
            var row = RevList.SelectedItem as ReviewRow;
            if (row == null) { RevAppliedText.Text = "请先选中一段。"; return; }
            var path = RevProjBox.Text.Trim();
            if (string.IsNullOrEmpty(path)) { RevAppliedText.Text = "缺少项目路径。"; return; }
            var file = RevFileBox.Text.Trim();

            var target = parseDetailTarget(RevDetailBox.Text);
            if (string.IsNullOrWhiteSpace(target)) { RevAppliedText.Text = "详情区没有可写回的译文。"; return; }
            if (string.Equals(target, row.Target, StringComparison.Ordinal)) { RevAppliedText.Text = "译文未变化，无需写回。"; return; }

            var q = new Dictionary<string, string> { { "path", path } };
            if (!string.IsNullOrEmpty(file)) q["file"] = file;
            var body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(
                new List<object>
                {
                    new Dictionary<string, object> { { "id", row.ItemId }, { "target", target } }
                });
            var token = ApiConfig.Load().GetOrCreateToken();
            RevApplyBtn.IsEnabled = false;
            RevAppliedText.Text = "写回中…";
            try
            {
                var result = await Task.Run(() =>
                    ProjectApi.Handle("POST", "/api/project/sdlxliff", q, body, token, token), CancellationToken.None);
                if (result.Status >= 400)
                    RevAppliedText.Text = "写回失败：[HTTP " + result.Status + "] " + PayloadText(result);
                else
                    RevAppliedText.Text = "已写回（含 .bak 备份）。在 Studio 重新打开该文件生效。";
            }
            catch (Exception ex)
            {
                RevAppliedText.Text = "写回异常：" + ex.Message;
                ToolkitLog.Error("工作台：审校写回异常", ex);
            }
            finally
            {
                RevApplyBtn.IsEnabled = true;
            }
        }

        /// <summary>从未知的详情文本里提取"建议修订"行以后的内容作为写回译文。</summary>
        private static string parseDetailTarget(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return null;
            var lines = detail.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int idx = -1;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].StartsWith("建议修订:", StringComparison.Ordinal))
                { idx = i + 1; break; }
            if (idx < 0) return null;
            var sb = new StringBuilder();
            for (int i = idx; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("建议修订:")) continue;
                sb.AppendLine(lines[i]);
            }
            return sb.ToString().Trim();
        }

        private void RevExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "CSV 报告|*.csv",
                FileName = "审校报告_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv",
            };
            if (dlg.ShowDialog(this) != true) return;
            var csv = BuildReviewCsv();
            if (string.IsNullOrEmpty(csv)) { RevStatus.Text = "没有审校结果可导出。"; return; }
            File.WriteAllText(dlg.FileName, csv, Encoding.UTF8);
            RevStatus.Text = "已导出：" + dlg.FileName;
        }

        private string BuildReviewCsv()
        {
            var sb = new StringBuilder();
            sb.Append('\ufeff');
            sb.Append("id,verdict,score,type,reason,suggestion,source,target\r\n");
            var rows = RevList.ItemsSource as List<ReviewRow>;
            if (rows == null) return null;
            foreach (var r in rows)
                sb.Append(r.ItemId).Append(',').Append(r.VerdictText).Append(',').Append(r.Score).Append(',')
                  .Append(r.Issue).Append(',').Append(r.Suggestion).Append(',').Append(r.Source).Append(',').Append(r.Target)
                  .Append("\r\n");
            return sb.ToString();
        }

        private static string PayloadText(ApiResult result)
        {
            try
            {
                var dict = result.Payload as Dictionary<string, object>;
                if (dict != null && dict.ContainsKey("error"))
                    return Convert.ToString(dict["error"]);
                return new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result.Payload);
            }
            catch { return result.Status.ToString(); }
        }
    }

    public class ReviewRow
    {
        public string ItemId { get; set; }
        public string VerdictText { get; set; }
        public Brush VerdictBrush { get; set; }
        public int Score { get; set; }
        public string Issue { get; set; }
        public string Source { get; set; }
        public string Target { get; set; }
        public string Suggestion { get; set; }

        private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5D, 0x4B));
        private static readonly Brush AmberBrush = new SolidColorBrush(Color.FromRgb(0xB0, 0x7F, 0x2A));
        private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xA8, 0x6B));
        private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2));

        public static ReviewRow From(Dictionary<string, object> it)
        {
            string verdict = it.ContainsKey("verdict") ? Convert.ToString(it["verdict"]) : "ok";
            string verdictText = verdict;
            var brush = GrayBrush;
            if (string.Equals(verdict, "red", StringComparison.OrdinalIgnoreCase)) { verdictText = "红"; brush = RedBrush; }
            else if (string.Equals(verdict, "amber", StringComparison.OrdinalIgnoreCase)) { verdictText = "黄"; brush = AmberBrush; }
            else if (string.Equals(verdict, "ok", StringComparison.OrdinalIgnoreCase)) { verdictText = "通过"; brush = GreenBrush; }

            var type = it.ContainsKey("type") ? Convert.ToString(it["type"]) : null;
            var reason = it.ContainsKey("reason") ? Convert.ToString(it["reason"]) : null;
            var issue = (string.IsNullOrEmpty(type) ? "" : type)
                        + (string.IsNullOrEmpty(reason) ? "" : (string.IsNullOrEmpty(type) ? "" : "：") + reason);

            return new ReviewRow
            {
                ItemId = it.ContainsKey("id") ? Convert.ToString(it["id"]) : "",
                VerdictText = verdictText,
                VerdictBrush = brush,
                Score = it.ContainsKey("score") ? Convert.ToInt32(it["score"]) : 0,
                Issue = issue,
                Source = it.ContainsKey("source") ? Convert.ToString(it["source"]) : "",
                Target = it.ContainsKey("target") ? Convert.ToString(it["target"]) : "",
                Suggestion = it.ContainsKey("suggestion") ? Convert.ToString(it["suggestion"]) : "",
            };
        }
    }
}
