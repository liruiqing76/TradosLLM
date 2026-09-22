using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Sdl.LanguagePlatform.TranslationMemoryApi;
using TradosToolkit.Diagnostics;
using TradosToolkit.TranslationMemories;
using TradosToolkit.Workbench;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 记忆库管理独立页（Add-ins 附加项 / 工作台 Quick 入口均可打开）：
    /// 扫描本地 .sdltm、新建空库、把 TMX / SDLXLIFF 导入到选中记忆库（语向=该库语言对，自动过滤不匹配句对）。
    /// 导入全程后台线程 + 进度/取消，避免卡 UI；单文件失败降级提示不打断整批。
    /// </summary>
    public partial class TmManagerWindow : Window
    {
        private static TmManagerWindow _instance;
        private readonly ObservableCollection<LocalTmInfo> _tms = new ObservableCollection<LocalTmInfo>();
        private CancellationTokenSource _cts;
        private bool _loading;

        public TmManagerWindow()
        {
            InitializeComponent();
            InputProbe.Attach(this); // 键盘链路探针（诊断 Studio 下英文敲不进）
            TmGrid.ItemsSource = _tms;
            var cfg = ToolkitConfig.Load();
            TmDirBox.Text = string.IsNullOrWhiteSpace(cfg.TmScanDirectory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TradosToolkit")
                : cfg.TmScanDirectory.Trim();

            _loading = true;
            AutoRefreshBox.IsChecked = cfg.TmIndexAutoRefresh;
            RefreshTimeBox.Text = string.IsNullOrWhiteSpace(cfg.TmIndexRefreshTime) ? "02:00" : cfg.TmIndexRefreshTime;
            _loading = false;
            UpdateNextRunText();

            TmIndexScheduler.Refreshed += OnSchedulerRefreshed;
            Closed += (s, e) => TmIndexScheduler.Refreshed -= OnSchedulerRefreshed;
            UpdateImportState();
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
            ToolkitLog.Info("记忆库管理：新建独立页");
            var w = new TmManagerWindow();
            _instance = w;
            w.Closed += (s, e) => { if (ReferenceEquals(_instance, w)) _instance = null; };
            w.Show();
        }

        private LocalTmInfo SelectedTm => TmGrid.SelectedItem as LocalTmInfo;

        private void UpdateImportState()
        {
            var sel = SelectedTm;
            var ok = sel != null && sel.State == LocalTmState.Ok;
            ImportTmxBtn.IsEnabled = ok;
            ImportSdlBtn.IsEnabled = ok;
            if (!ok)
                StatusText.Text = sel == null
                    ? "请先扫描，再在列表里选中一个可用记忆库，随后即可导入 TMX / SDLXLIFF"
                    : "该记忆库不可写（" + (sel.State == LocalTmState.Protected ? "受保护" : "读取失败") + "），请另选一个。";
        }

        private void TmGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateImportState();

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择记忆库目录",
                SelectedPath = Directory.Exists(TmDirBox.Text)
                    ? TmDirBox.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                TmDirBox.Text = dlg.SelectedPath;
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            var dir = TmDirBox.Text.Trim();
            if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在: " + dir; return; }
            _cts?.Cancel();
            ShowBusy(true, "扫描 " + dir + " …");
            var cts = new CancellationTokenSource();
            _cts = cts;
            var watch = new System.Diagnostics.Stopwatch();
            watch.Start();
            try
            {
                var list = await Task.Run(() => LocalTmIndex.Refresh(dir, null, cts.Token), CancellationToken.None);
                _tms.Clear();
                foreach (var t in list) _tms.Add(t);
                ShowBusy(false, string.Format("扫描完成：{0} 个记忆库，耗时 {1:0.0}s（已写入共享索引，收件箱直接复用）",
                    _tms.Count, watch.ElapsedMilliseconds / 1000.0));
            }
            catch (OperationCanceledException)
            {
                ShowBusy(false, "扫描已取消");
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("记忆库扫描失败", ex);
                ShowBusy(false, "扫描失败：" + ex.Message);
            }
        }

        private void NewTm_Click(object sender, RoutedEventArgs e)
        {
            string filePath, name;
            System.Globalization.CultureInfo s, t;
            // 传当前目录作兜底：对话框里"保存目录"留空即建在这里
            if (!TmToolDialogs.CreateTm(this, out filePath, out name, out s, out t, TmDirBox.Text.Trim())) return;
            TmDirBox.Text = Path.GetDirectoryName(filePath);
            StatusText.Text = "已新建空记忆库：" + name + "（" + s.Name + " → " + t.Name + "）";
            Scan_Click(null, null);
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var target = SelectedTm;
            if (target == null || target.State != LocalTmState.Ok) return;
            var tag = (sender as Button)?.Tag as string ?? ".tmx";
            var isTmx = string.Equals(tag, ".tmx", StringComparison.OrdinalIgnoreCase);
            var dlg = new OpenFileDialog
            {
                Title = isTmx ? "选择 TMX 文件（可多选）" : "选择已翻译的 SDLXLIFF 文件（可多选）",
                Filter = isTmx ? "TMX 文件 (*.tmx)|*.tmx" : "SDLXLIFF 文件 (*.sdlxliff)|*.sdlxliff",
                Multiselect = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            _cts?.Cancel();
            var cts = new CancellationTokenSource();
            _cts = cts;
            var list = dlg.FileNames;
            ShowBusy(true, "准备导入到 " + target.Name + " …");
            Task.Run(async () =>
            {
                var total = new TmImportReport { FileName = string.Join("; ", list.Select(Path.GetFileName)),
                                                 Format = isTmx ? "TMX" : "SDLXLIFF" };
                foreach (var file in list)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    var stage = new Progress<string>(msg =>
                        Dispatcher.BeginInvoke(new System.Action(() => StatusText.Text = msg)));
                    try
                    {
                        var r = TmImporter.Import(target.FilePath, file, stage, cts.Token);
                        total.Pairs += r.Pairs; total.Added += r.Added;
                        total.SkippedMismatch += r.SkippedMismatch; total.SkippedTags += r.SkippedTags;
                    }
                    catch (Exception ex)
                    {
                        ToolkitLog.Error("记忆库导入失败 " + file, ex);
                        Dispatcher.BeginInvoke(new System.Action(() =>
                            StatusText.Text = "导入失败 " + Path.GetFileName(file) + "：" + ex.Message));
                        return;
                    }
                }
                Dispatcher.BeginInvoke(new System.Action(() => ImportDone(target, total)));
            }, CancellationToken.None);
        }

        private void ImportDone(LocalTmInfo target, TmImportReport r)
        {
            ShowBusy(false, string.Format("导入『{0}』完成：句对 {1} → 写入 {2}；跳过语向不符 {3}、含标签 {4}",
                target.Name, r.Pairs, r.Added, r.SkippedMismatch, r.SkippedTags));
            var item = _tms.FirstOrDefault(x => string.Equals(x.FilePath, target.FilePath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                item.Units = CountUnits(item.FilePath);
                item.Modified = File.Exists(item.FilePath) ? File.GetLastWriteTime(item.FilePath) : item.Modified;
                TmGrid.Items.Refresh();
                // 导入改动了库 → 回写共享索引，收件箱下次查命中即用最新状态
                LocalTmIndex.Upsert(item);
            }
        }

        private string CountUnits(string path)
        {
            try
            {
                var tm = new FileBasedTranslationMemory(path);
                return tm.GetTranslationUnitCount().ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
            }
            catch (Exception ex) { ToolkitLog.Error("读条目数失败 " + path, ex); return "-"; }
        }

        private void Help_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.MessageBox.Show(
                "记忆库管理说明：\n· 扫描：把目录（含子目录）里的 .sdltm 列出来。\n" +
                "· 导入 TMX / SDLXLIFF：先在列表选中一个可用记忆库作目标（语向=该库的语言对）。\n" +
                "  文件里语向不匹配的句对会跳过；含内联结构标签（保护占位）的段跳过不破坏。\n" +
                "· 新建空库：选源/目标语言（下拉内可搜索）与保存目录，名称默认=目标语言英文全称_源语言缩略语_目标语言缩略语，\n" +
                "  保存目录留空就建在当前记忆库目录；建好后可继续向其导入。\n" +
                "· 定时刷新：勾选后每天到点由插件后台线程自动重建共享索引（%APPDATA%\\TradosToolkit\\tm-index.json），\n" +
                "  记忆库管理与收件箱共用同一份；同一自然日只跑一次。需立即刷新可点「立即重建」。",
                "记忆库管理", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>勾选/取消「每日自动重建索引」，以及时间变化时保存定时配置。</summary>
        private void Scheduler_Changed(object sender, RoutedEventArgs e) => SaveScheduler();

        private void RefreshTime_Changed(object sender, TextChangedEventArgs e) => SaveScheduler();

        private void SaveScheduler()
        {
            if (_loading) return; // 初始化回填时不落盘
            if (AutoRefreshBox == null || RefreshTimeBox == null) return; // XAML 初始化过程中的 TextChanged 早于控件就绪
            var enabled = AutoRefreshBox.IsChecked == true;
            var time = (RefreshTimeBox.Text ?? string.Empty).Trim();
            if (!ToolkitConfig.IsValidTime(time))
            {
                if (SchedulerHint != null)
                    SchedulerHint.Text = "时间格式应为 24 小时制 HH:mm（如 08:30），当前值未保存。";
                return;
            }
            ToolkitConfig.Save(tmIndexAutoRefresh: enabled, tmIndexRefreshTime: time);
            if (SchedulerHint != null)
                SchedulerHint.Text = enabled
                    ? "已启用：每天 " + time + " 自动重建共享索引；同一自然日只跑一次。"
                    : "已停用每日定时；共享索引仍会在扫描/导入时按需更新。";
            UpdateNextRunText();
        }

        private void UpdateNextRunText()
        {
            if (NextRunText == null) return;
            try
            {
                var cfg = ToolkitConfig.Load();
                if (!cfg.TmIndexAutoRefresh)
                {
                    NextRunText.Text = "定时未启用";
                    return;
                }
                var due = TmIndexScheduler.NextDue(cfg, DateTime.Now);
                var last = TmIndexScheduler.LastRun;
                var lastTxt = last == DateTime.MinValue ? "未运行过" : last.ToString("MM-dd HH:mm");
                NextRunText.Text = due == null
                    ? "下次运行：需先设置记忆库目录（上次 " + lastTxt + "）"
                    : "下次运行：" + due.Value.ToString("MM-dd HH:mm") + "（上次 " + lastTxt + "）";
            }
            catch (Exception ex) { ToolkitLog.Error("更新下次运行时间失败", ex); }
        }

        /// <summary>不等定时，马上全量重建一次共享索引。</summary>
        private async void RunIndexNow_Click(object sender, RoutedEventArgs e)
        {
            var dir = TmDirBox.Text.Trim();
            if (!Directory.Exists(dir)) { StatusText.Text = "目录不存在: " + dir; return; }
            if (AutoRefreshBox.IsChecked == true && ToolkitConfig.IsValidTime(RefreshTimeBox.Text.Trim()))
                ToolkitConfig.Save(tmScanDirectory: dir); // 定时启用时，确保定时用的是同一个目录
            ShowBusy(true, "正在重建共享索引 " + dir + " …");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var list = await Task.Run(() => LocalTmIndex.Refresh(dir, null, CancellationToken.None), CancellationToken.None);
                _tms.Clear();
                foreach (var t in list) _tms.Add(t);
                ShowBusy(false, string.Format("索引已重建：{0} 个记忆库，耗时 {1:0.0}s", _tms.Count, watch.ElapsedMilliseconds / 1000.0));
                UpdateNextRunText();
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("立即重建索引失败", ex);
                ShowBusy(false, "重建失败：" + ex.Message);
            }
        }

        /// <summary>定时线程完成自动重建后回显到界面（工作线程 → marshal 回 UI）。</summary>
        private void OnSchedulerRefreshed(string message)
        {
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                StatusText.Text = message;
                UpdateNextRunText();
            }));
        }

        private void ShowBusy(bool active, string msg)
        {
            ImportProgress.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            ImportProgress.IsIndeterminate = active;
            StatusText.Text = msg;
        }
    }
}