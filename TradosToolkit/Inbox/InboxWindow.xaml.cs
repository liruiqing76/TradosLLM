using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TradosToolkit.Common;
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
        private readonly List<LangItem> _langs;
        private string _lastSrc = "zh-CN";
        private string _lastTgt = "en-US";
        private InboxJob _selected;

        public InboxWindow()
        {
            InitializeComponent();
            InputProbe.Attach(this);
            JobGrid.ItemsSource = _jobs;

            // 界面打开即接管 UI 线程归属，任务步骤/日志会实时回到这里
            InboxJob.UiDispatcher = Dispatcher;
            InboxWatcher.Instance.JobCreated += OnJobCreated;

            // 语言下拉：与术语管理共用同一份进程级缓存清单（LanguageCatalog）
            _langs = LanguageCatalog.All();
            AttachLangFilter(SrcCombo);
            AttachLangFilter(TgtCombo);

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
            ToolkitLog.Info("收件箱：新建独立页（专用 UI 线程）");
            // 根因同术语管理：Studio 宿主消息泵不为外挂顶层窗口 TranslateMessage，留在宿主线程
            // Show() 的窗口收不到 WM_CHAR，英文敲不进（语言下拉搜索框会失效）。放专用 STA 线程跑
            // WPF 自己的 Dispatcher 泵，输入链路不再过宿主泵，仍是无属主独立窗口。
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    var w = new InboxWindow();
                    _instance = w;
                    w.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_instance, w)) _instance = null;
                        InboxWatcher.Instance.JobCreated -= w.OnJobCreated;
                        InboxJob.UiDispatcher = null; // 退化为后台模式
                        w.Dispatcher.InvokeShutdown(); // 窗口关了就撤线程泵
                    };
                    ready.Set();
                    w.Show();
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    ToolkitLog.Error("收件箱：独立线程创建失败", ex);
                    ready.Set();
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true; // Studio 退出时进程不被本线程拖住
            t.Name = "TradosToolkit.InboxUI";
            t.Start();
            ready.WaitOne(TimeSpan.FromSeconds(15)); // 等构造完成再返回，防连点出两窗
        }

        // ==================== 配置读写 ====================

        private void LoadIntoUi(ToolkitConfig cfg)
        {
            WatchBox.Text = cfg.InboxWatchFolder ?? string.Empty;
            OutputBox.Text = cfg.InboxOutputFolder ?? string.Empty;
            TmBox.Text = cfg.TmScanDirectory ?? string.Empty;
            SelectLang(SrcCombo, cfg.InboxSourceLang, "zh-CN");
            SelectLang(TgtCombo, cfg.InboxTargetLang, "en-US");
            // 记录权威语言代码：过滤/失焦可能清空选中项，但 _last* 始终保持用户选定的语向
            _lastSrc = (SrcCombo.SelectedValue as string) ?? "zh-CN";
            _lastTgt = (TgtCombo.SelectedValue as string) ?? "en-US";
            AutoStartBox.IsChecked = cfg.InboxAutoStart;
        }

        /// <summary>把界面上的值写回配置并返回最新配置（供 Start 使用）。</summary>
        private ToolkitConfig Persist()
        {
            var watch = WatchBox.Text.Trim();
            var output = OutputBox.Text.Trim();
            var tm = TmBox.Text.Trim();
            var src = string.IsNullOrEmpty(_lastSrc) ? "zh-CN" : _lastSrc;
            var tgt = string.IsNullOrEmpty(_lastTgt) ? "en-US" : _lastTgt;
            var auto = AutoStartBox.IsChecked == true;
            ToolkitConfig.Save(tmScanDirectory: tm, inboxWatchFolder: watch, inboxOutputFolder: output,
                inboxSourceLang: src,
                inboxTargetLang: tgt,
                inboxAutoStart: auto);
            var cfg = ToolkitConfig.Load();
            cfg.TmScanDirectory = tm;
            cfg.InboxWatchFolder = watch;
            cfg.InboxOutputFolder = output;
            cfg.InboxSourceLang = src;
            cfg.InboxTargetLang = tgt;
            cfg.InboxAutoStart = auto;
            return cfg;
        }

        // ==================== 语言下拉（与术语管理同一套） ====================

        /// <summary>按代码选中语言；找不到则回退默认代码，再回退第一项。</summary>
        private void SelectLang(ComboBox combo, string code, string fallback)
        {
            var item = _langs.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
                    ?? _langs.FirstOrDefault(l => string.Equals(l.Code, fallback, StringComparison.OrdinalIgnoreCase))
                    ?? _langs.FirstOrDefault();
            combo.SelectedItem = item;
        }

        /// <summary>只在真正点选(SelectedValue 非空)时更新权威语向；过滤清掉 SelectedItem 时不应覆盖。</summary>
        private void LangChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SrcCombo == null) return; // InitializeComponent 期间的 SelectionChanged
            var v = (sender as ComboBox)?.SelectedValue as string;
            if (string.IsNullOrEmpty(v)) return;
            if (ReferenceEquals(sender, SrcCombo)) _lastSrc = v;
            else if (ReferenceEquals(sender, TgtCombo)) _lastTgt = v;
        }

        /// <summary>
        /// 语言下拉"下拉内搜索框"过滤：模板 Popup 顶部有可见输入框 LangSearchBox，
        /// 打开下拉自动聚焦它；输入只改 ListCollectionView.Filter（不换 ItemsSource、不清文本）；
        /// 回车=选中过滤后第一项；收起下拉清空关键词恢复完整清单，未点选则回显权威语向
        /// （过滤会清掉 SelectedItem，但 ComboBox 拒收指向被过滤项的赋值，故必须先清过滤再恢复）。
        /// </summary>
        private void AttachLangFilter(ComboBox combo)
        {
            var view = new System.Windows.Data.ListCollectionView(_langs);
            combo.IsSynchronizedWithCurrentItem = false;
            combo.ItemsSource = view;

            TextBox search = null;
            // Popup 内容首次展开才实例化，DropDownOpened 时兜底再找一次
            combo.DropDownOpened += (s, e) =>
            {
                if (search == null)
                {
                    search = combo.Template.FindName("LangSearchBox", combo) as TextBox;
                    if (search != null)
                    {
                        search.TextChanged += (a, b) =>
                        {
                            var q = (search.Text ?? "").Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(q)) view.Filter = null;
                            else view.Filter = item =>
                            {
                                var l = item as LangItem;
                                if (l == null) return false;
                                return l.Label.ToLowerInvariant().Contains(q) ||
                                       l.Code.ToLowerInvariant().Contains(q);
                            };
                            view.Refresh();
                        };
                        search.PreviewKeyDown += (a, b) =>
                        {
                            if (b.Key == System.Windows.Input.Key.Enter)
                            {
                                var first = view.OfType<LangItem>().FirstOrDefault();
                                if (first != null) combo.SelectedItem = first;
                                combo.IsDropDownOpen = false;
                                b.Handled = true;
                            }
                        };
                    }
                }
                if (search != null)
                {
                    // 下拉打开后 ComboBox 会把焦点抢回列表选中项，延迟一帧再聚焦搜索框，否则敲不进字
                    combo.Dispatcher.BeginInvoke(new System.Action(() =>
                    {
                        search.Focus();
                        System.Windows.Input.Keyboard.Focus(search);
                        search.SelectAll();
                    }), System.Windows.Threading.DispatcherPriority.Input);
                }
            };
            combo.DropDownClosed += (s, e) =>
            {
                if (search != null) search.Text = ""; // 触发 TextChanged → 清过滤
                view.Filter = null;
                view.Refresh();
                if (combo.SelectedItem == null)
                {
                    // 未点选（过滤期间选中项被清空）：按权威语向恢复显示
                    var code = ReferenceEquals(combo, SrcCombo) ? _lastSrc : _lastTgt;
                    var item = _langs.FirstOrDefault(l =>
                        string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
                    if (item != null) combo.SelectedItem = item;
                }
            };
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
                "· 源/目标语言：点开下拉，顶部输入框里敲代码或名称即可过滤（如 zh-CN、English），回车选中第一项。\n" +
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
