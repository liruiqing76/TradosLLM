using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TradosToolkit.Diagnostics;
using TradosToolkit.Workbench;

namespace TradosToolkit.Inbox
{
    /// <summary>
    /// 收件箱（目录监视）可操作界面 + 流程监控：
    /// 上：监视/产出/本地库目录、语向、随插件自启；中：任务列表 + 选中任务的分步流程与日志。
    /// 界面只是外壳——真正的监视与编排在 InboxWatcher / InboxOrchestrator；窗口关闭后若仍在监视则退化为后台模式。
    /// </summary>
    public partial class InboxWindow : Window
    {
        private static InboxWindow _instance;
        private readonly ObservableCollection<InboxJob> _jobs = new ObservableCollection<InboxJob>();
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private InboxJob _selected;

        public InboxWindow()
        {
            InitializeComponent();
            InputProbe.Attach(this);
            JobGrid.ItemsSource = _jobs;

            // 界面打开即接管 UI 线程归属，任务步骤/日志会实时回到这里
            InboxJob.UiDispatcher = Dispatcher;
            InboxWatcher.Instance.JobCreated += OnJobCreated;

            LoadIntoUi(ToolkitConfig.Load());

            _timer.Interval = TimeSpan.FromMilliseconds(500);
            _timer.Tick += (s, e) =>
            {
                foreach (var job in _jobs)
                    if (job.Status == "running" || job.Status == "queued") job.Tick();
            };
            _timer.Start();

            RefreshState();
        }

        public static void ShowOrActivate()
        {
            var cur = _instance;
            if (cur != null)
            {
                cur.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    cur.Activate();
                    if (cur.WindowState == WindowState.Minimized) cur.WindowState = WindowState.Normal;
                }));
                return;
            }
            ToolkitLog.Info("收件箱：新建独立页");
            var w = new InboxWindow();
            _instance = w;
            w.Closed += (s, e) =>
            {
                if (ReferenceEquals(_instance, w)) _instance = null;
                InboxWatcher.Instance.JobCreated -= w.OnJobCreated;
                InboxJob.UiDispatcher = null; // 退化为后台模式
            };
            w.Show();
        }

        // ==================== 配置读写 ====================

        private void LoadIntoUi(ToolkitConfig cfg)
        {
            WatchBox.Text = cfg.InboxWatchFolder ?? string.Empty;
            OutputBox.Text = cfg.InboxOutputFolder ?? string.Empty;
            TmBox.Text = cfg.TmScanDirectory ?? string.Empty;
            SourceLangBox.Text = string.IsNullOrWhiteSpace(cfg.InboxSourceLang) ? "zh-CN" : cfg.InboxSourceLang;
            TargetLangBox.Text = string.IsNullOrWhiteSpace(cfg.InboxTargetLang) ? "en-US" : cfg.InboxTargetLang;
            AutoStartBox.IsChecked = cfg.InboxAutoStart;
        }

        /// <summary>把界面上的值写回配置并返回最新配置（供 Start 使用）。</summary>
        private ToolkitConfig Persist()
        {
            var watch = WatchBox.Text.Trim();
            var output = OutputBox.Text.Trim();
            var tm = TmBox.Text.Trim();
            var src = SourceLangBox.Text.Trim();
            var tgt = TargetLangBox.Text.Trim();
            var auto = AutoStartBox.IsChecked == true;
            ToolkitConfig.Save(tmScanDirectory: tm, inboxWatchFolder: watch, inboxOutputFolder: output,
                inboxSourceLang: string.IsNullOrEmpty(src) ? "zh-CN" : src,
                inboxTargetLang: string.IsNullOrEmpty(tgt) ? "en-US" : tgt,
                inboxAutoStart: auto);
            var cfg = ToolkitConfig.Load();
            cfg.TmScanDirectory = tm;
            cfg.InboxWatchFolder = watch;
            cfg.InboxOutputFolder = output;
            cfg.InboxSourceLang = string.IsNullOrEmpty(src) ? "zh-CN" : src;
            cfg.InboxTargetLang = string.IsNullOrEmpty(tgt) ? "en-US" : tgt;
            cfg.InboxAutoStart = auto;
            return cfg;
        }

        // ==================== 启停 / 处理 ====================

        private void Toggle_Click(object sender, RoutedEventArgs e)
        {
            var watcher = InboxWatcher.Instance;
            if (watcher.IsRunning)
            {
                watcher.Stop();
                RefreshState();
                StatusText.Text = "已停止监视。";
                return;
            }

            var cfg = Persist();
            try
            {
                watcher.Start(cfg);
                StatusText.Text = "开始监视：" + cfg.InboxWatchFolder + "（把文件拖进去即可）";
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("收件箱：开始监视失败", ex);
                StatusText.Text = "开始失败：" + ex.Message;
            }
            RefreshState();
        }

        private void ProcessExisting_Click(object sender, RoutedEventArgs e)
        {
            var watcher = InboxWatcher.Instance;
            if (!watcher.IsRunning) { StatusText.Text = "请先点「开始监视」再处理收件箱。"; return; }
            var n = watcher.EnqueueExisting();
            StatusText.Text = n > 0 ? "已把收件箱里 " + n + " 个文件排入队列。" : "收件箱里没有可处理的文件。";
        }

        private void OpenOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = OutputBox.Text.Trim();
            if (string.IsNullOrEmpty(folder))
                folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TradosToolkit 收件箱");
            try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", folder); }
            catch (Exception ex) { ToolkitLog.Error("收件箱：打开产出目录失败", ex); }
        }

        // ==================== 任务到达 / 选中 ====================

        private void OnJobCreated(InboxJob job)
        {
            InboxJob.Post(() =>
            {
                _jobs.Insert(0, job);
                JobGrid.SelectedItem = job;
                StatusText.Text = "新任务：" + job.FileName;
            });
        }

        private void JobGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_selected != null) _selected.PropertyChanged -= OnSelectedChanged;
            _selected = JobGrid.SelectedItem as InboxJob;
            if (_selected != null)
            {
                _selected.PropertyChanged += OnSelectedChanged;
                StepList.ItemsSource = _selected.Steps;
                DetailTitle.Text = _selected.FileName;
                LogBox.Text = _selected.LogText;
            }
            else
            {
                StepList.ItemsSource = null;
                DetailTitle.Text = "（未选择任务）";
                LogBox.Text = string.Empty;
            }
        }

        private void OnSelectedChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "LogText" && ReferenceEquals(sender, _selected))
                LogBox.Text = _selected.LogText;
        }

        // ==================== 目录选择 ====================

        private void BrowseWatch_Click(object sender, RoutedEventArgs e) => PickFolder(WatchBox, "选择要监视的投放目录");
        private void BrowseOutput_Click(object sender, RoutedEventArgs e) => PickFolder(OutputBox, "选择产出目录");
        private void BrowseTm_Click(object sender, RoutedEventArgs e) => PickFolder(TmBox, "选择本地记忆库目录");

        private void PickFolder(TextBox box, string title)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = title,
                SelectedPath = Directory.Exists(box.Text)
                    ? box.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                box.Text = dlg.SelectedPath;
        }

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "收件箱说明：\n" +
                "· 监视目录：把待处理的源文件拖进这个目录，插件自动开始处理，无需手动操作。\n" +
                "· 每个文件产出三件套：分析报告(.csv) + 交付包(.sdlppx) + 匹配到的本地库(.sdltm)。\n" +
                "· 本地库目录：从该目录（含子目录）里挑与「源/目标语言」语言对一致、可写的 .sdltm 套进项目并预翻译。\n" +
                "· 语言用 ISO 代码（如 zh-CN、en-US）。\n" +
                "· 「随插件自动开始监视」勾选后，每次打开 Studio 会自动开始监视。\n" +
                "· 关掉本窗口不影响后台监视；处理进度仍在继续。",
                "TradosToolkit 收件箱", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // ==================== 状态同步 ====================

        private void RefreshState()
        {
            var running = InboxWatcher.Instance.IsRunning;
            ToggleBtn.Content = running ? "停止监视" : "开始监视";
            ProcessBtn.IsEnabled = running;
            StateText.Text = running
                ? "正在监视：" + InboxWatcher.Instance.WatchingFolder
                : "未开始监视";
        }
    }
}
