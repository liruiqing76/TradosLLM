using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TradosToolkit.Common;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 术语管理独立页：译前/译后术语库的增删改查 + CSV 导入导出 + 模板下载。
    /// 语言对下拉带搜索框：点开下拉顶部即见输入框，敲代码/名称即输即筛（默认带出当前项目语言），
    /// 覆盖所有语种。Add-ins 附加项 Ribbon 按钮独立打开。
    /// 窗口跑在专用 STA 线程（ShowOrActivate）：Studio 宿主消息泵不为外挂顶层窗口 TranslateMessage，
    /// 留在宿主线程的 Show() 窗口收不到 WM_CHAR（英文敲不进，中文走 IME/TSF 幸存）；
    /// 专用线程上由 WPF 自己的 Dispatcher 泵转译消息，输入恢复正常且仍是无属主独立窗口。
    /// </summary>
    public partial class GlossaryManagerWindow : Window
    {
        private static GlossaryManagerWindow _instance;
        private readonly GlossaryDb _db;
        private readonly SqliteGlossaryProvider _provider;
        private readonly List<LangItem> _langs;

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
            ToolkitLog.Info("术语管理：新建独立页（专用 UI 线程）");
            // 根因（2026-09-21 探针日志定案）：本窗口若在 Studio UI 线程上 Show()（无属主/有属主都一样，
            // 0790ac0 挂 Owner 后 WM_CHAR 仍为 0），物理按键 WM_KEYDOWN 能到，但宿主消息泵不给本窗口
            // TranslateMessage，钩子内补发 WM_CHAR 也被泵吞掉——英文敲不进，中文 IME 走 TSF 通道幸存。
            // 模态 ShowDialog 的 WPF 嵌套泵自己 TranslateMessage，所以配置窗口一直正常。
            // 修复：窗口放专用 STA 线程，跑 WPF 自己的 Dispatcher 泵，输入链路不再过宿主泵。
            string src = null, tgt = null;
            TryFillProjectLanguages(ref src, ref tgt); // 必须在 Studio UI 线程取：SdlTradosStudio.Automation 跨线程不可用
            var ready = new ManualResetEvent(false);
            var t = new Thread(() =>
            {
                try
                {
                    var w = new GlossaryManagerWindow(src, tgt);
                    _instance = w;
                    w.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_instance, w)) _instance = null;
                        w.Dispatcher.InvokeShutdown(); // 窗口关了就撤线程泵
                    };
                    ready.Set();
                    w.Show();
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    ToolkitLog.Error("术语管理：独立线程创建失败", ex);
                    ready.Set();
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true; // Studio 退出时进程不被本线程拖住
            t.Name = "TradosToolkit.GlossaryUI";
            t.Start();
            ready.WaitOne(TimeSpan.FromSeconds(15)); // 等构造完成再返回，防连点出两窗
        }

        /// <summary>从外部（工作台/配置窗口）打开时也可带语言默认值；为 null 则自动取当前项目。</summary>
        public GlossaryManagerWindow(string src, string tgt, SqliteGlossaryProvider provider = null)
        {
            InitializeComponent();
            InputProbe.Attach(this); // 键盘链路探针（诊断 Studio 下英文敲不进）
            _db = new GlossaryDb();
            _provider = provider;
            _langs = LanguageCatalog.All();
            // 语言下拉"下拉内搜索框"：点开下拉顶部即见输入框，各挂独立 ListCollectionView，只改 Filter 不换 ItemsSource
            AttachLangFilter(SrcCombo);
            AttachLangFilter(TgtCombo);
            DomCombo.ItemsSource = DomainCatalog.Names();

            // 领域默认取全局配置（工作台里切换的领域），保证术语管理与翻译插件同一领域
            var cfgDomain = ToolkitConfig.Load().Domain;
            DomCombo.SelectedItem = DomCombo.Items.OfType<string>()
                .FirstOrDefault(d => string.Equals(d, cfgDomain, StringComparison.OrdinalIgnoreCase))
                ?? DomainTree.DefaultDomain;

            if (string.IsNullOrWhiteSpace(src)) src = null;
            if (string.IsNullOrWhiteSpace(tgt)) tgt = null;
            // 注意：不再在构造里取当前项目语言——SdlTradosStudio.Automation 只能在 Studio UI 线程访问，
            // 独立线程方案下由 ShowOrActivate 在建线程前取好传进来。

            var srcItem = _langs.FirstOrDefault(l => l.Code == src);
            var tgtItem = _langs.FirstOrDefault(l => l.Code == tgt);
            SrcCombo.SelectedItem = srcItem ?? _langs.FirstOrDefault();
            if (tgtItem == null)
            {
                var zh = _langs.FirstOrDefault(l => l.Code.StartsWith("zh-"))
                          ?? _langs.Skip(1).FirstOrDefault();
                TgtCombo.SelectedItem = zh;
            }
            else TgtCombo.SelectedItem = tgtItem;

            // 记录权威语言代码：过滤/失焦可能清空选中项，但 _last* 始终保持用户选定的语向
            _lastSrc = (SrcCombo.SelectedValue as string) ?? src;
            _lastTgt = (TgtCombo.SelectedValue as string) ?? tgt;

            Reload(null, null);
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

        private bool IsPost => KindCombo.SelectedIndex == 1;
        private string Kind => IsPost ? GlossaryDb.KindPost : GlossaryDb.KindPre;

        private ObservableCollection<GlossaryEntry> Terms { get; set; }
        private List<GlossaryEntry> _allTerms = new List<GlossaryEntry>();

        /// <summary>取当前项目的源/目标语言；只能从 Studio UI 线程调用（宿主自动化对象跨线程不可用）。</summary>
        private static void TryFillProjectLanguages(ref string src, ref string tgt)
        {
            try
            {
                var ctl = Sdl.TranslationStudioAutomation.IntegrationApi.SdlTradosStudio.Application
                    .GetController<Sdl.TranslationStudioAutomation.IntegrationApi.ProjectsController>();
                var info = ctl?.CurrentProject?.GetProjectInfo();
                if (info == null) return;
                if (src == null && info.SourceLanguage != null) src = info.SourceLanguage.IsoAbbreviation;
                if (tgt == null && info.TargetLanguages != null && info.TargetLanguages.Count() > 0)
                    tgt = info.TargetLanguages.First().IsoAbbreviation;
            }
            catch (Exception ex) { ToolkitLog.Error("术语管理：读取当前项目语言失败", ex); }
        }

        private void KindChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SrcCombo == null) return; // InitializeComponent 期间
            Reload(null, null);
        }

        private void LangChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SrcCombo == null) return;
            // 只在真正点选(SelectedValue 非空)时更新权威语向；过滤清掉 SelectedItem 时不应覆盖
            var v = (sender as ComboBox)?.SelectedValue as string;
            if (!string.IsNullOrEmpty(v))
            {
                if (ReferenceEquals(sender, SrcCombo)) _lastSrc = v;
                else if (ReferenceEquals(sender, TgtCombo)) _lastTgt = v;
            }
            Reload(null, null);
        }

        private void DomainChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SrcCombo == null) return;
            Reload(null, null);
        }

        private string _lastSrc = "", _lastTgt = "";
        private string Src => _lastSrc;
        private string Tgt => _lastTgt;
        private string Domain => (DomCombo.SelectedValue as string) ?? DomainTree.DefaultDomain;

        private void Reload(object sender, RoutedEventArgs e)
        {
            if (TermsGrid == null) return; // InitializeComponent 期间的 SelectionChanged
            var src = Src;
            var tgt = Tgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt))
            {
                Terms = new ObservableCollection<GlossaryEntry>();
                TermsGrid.ItemsSource = Terms;
                StatusText.Text = "请在上方选择源/目标语言。";
                return;
            }
            var kind = Kind;
            var dom = Domain;
            _allTerms = _db.GetTerms(kind, src, tgt, dom);
            ApplySearchFilter();
            StatusText.Text = string.Format("{0} · {1} → {2} · 领域 {3} · 共 {4} 条",
                IsPost ? "译后" : "译前", src, tgt, dom, _allTerms.Count);
        }

        private void TermSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Terms == null) return;
            ApplySearchFilter();
        }

        /// <summary>按搜索框的 来源/目标/领域 关键字过滤当前已加载术语。</summary>
        private void ApplySearchFilter()
        {
            var q = TermSearchBox == null ? "" : (TermSearchBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q))
            {
                Terms = new ObservableCollection<GlossaryEntry>(_allTerms);
            }
            else
            {
                var ql = q.ToLowerInvariant();
                Terms = new ObservableCollection<GlossaryEntry>(
                    _allTerms.Where(e =>
                        (e.From ?? "").ToLowerInvariant().Contains(ql) ||
                        (e.To ?? "").ToLowerInvariant().Contains(ql) ||
                        (e.Domain ?? "").ToLowerInvariant().Contains(ql)));
            }
            TermsGrid.ItemsSource = Terms;
        }

        /// <summary>新增：弹表单收集 替换前/替换为/领域，确定后落库（不再在格子里直接编辑）。</summary>
        private void AddTerm(object sender, RoutedEventArgs e)
        {
            if (!EnsurePair()) return;
            var dlg = new TermDialog(null, KindLabel, PairLabel, DomainList, Domain) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _db.SaveTerm(Kind, Src, Tgt, dlg.Result);
            RefreshAfterMutation();
            NotifyChanged();
            StatusText.Text = "已新增术语。";
        }

        /// <summary>双击某行改为弹表单修改；落在表头/滚动条/空白处不响应。</summary>
        private void EditTerm(object sender, MouseButtonEventArgs e)
        {
            var row = FindRow(e.OriginalSource as DependencyObject);
            var entry = row == null ? null : row.Item as GlossaryEntry;
            if (entry == null || entry.Id <= 0) return;
            if (!EnsurePair()) return;
            var dlg = new TermDialog(entry, KindLabel, PairLabel, DomainList, Domain) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _db.SaveTerm(Kind, Src, Tgt, dlg.Result);
            RefreshAfterMutation();
            NotifyChanged();
            StatusText.Text = "已更新术语。";
        }

        /// <summary>从命中的可视元素向上找所属 DataGridRow；命中表头/空白则返回 null。</summary>
        private static DataGridRow FindRow(DependencyObject src)
        {
            while (src != null && !(src is DataGridRow))
            {
                if (src is DataGrid) return null;
                src = System.Windows.Media.VisualTreeHelper.GetParent(src);
            }
            return src as DataGridRow;
        }

        private bool EnsurePair()
        {
            if (!string.IsNullOrEmpty(Src) && !string.IsNullOrEmpty(Tgt)) return true;
            StatusText.Text = "请先选择语言对。";
            return false;
        }

        private string KindLabel => IsPost ? "译后" : "译前";
        private string PairLabel => string.Format("{0} → {1}", Src, Tgt);
        private List<string> DomainList => DomainCatalog.Names();

        private void DeleteTerms(object sender, RoutedEventArgs e)
        {
            var src = Src;
            var tgt = Tgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) { StatusText.Text = "请先选择语言对。"; return; }
            foreach (var entry in TermsGrid.SelectedItems.Cast<GlossaryEntry>().ToList())
            {
                if (entry.Id > 0) _db.DeleteTerm(entry.Id);
                Terms.Remove(entry);
            }
            RefreshAfterMutation();
            NotifyChanged();
        }

        private void RefreshAfterMutation()
        {
            Reload(null, null);
        }

        private void DownloadTemplate(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = "术语模板.csv",
            };
            if (dlg.ShowDialog(this) != true) return;
            File.WriteAllText(dlg.FileName, "from,to\r\n", Encoding.UTF8);
            StatusText.Text = "模板已下载：" + dlg.FileName;
        }

        private void ImportCsv(object sender, RoutedEventArgs e)
        {
            var src = Src;
            var tgt = Tgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) { StatusText.Text = "请先选择语言对。"; return; }
            var dlg = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*" };
            if (dlg.ShowDialog(this) != true) return;

            var n = _db.ImportCsv(Kind, src, tgt, dlg.FileName, Domain);
            RefreshAfterMutation();
            NotifyChanged();
            StatusText.Text = string.Format("已导入 {0} 条术语（{1} → {2} · {3}）", n, src, tgt, Domain);
        }

        private void ExportCsv(object sender, RoutedEventArgs e)
        {
            var src = Src;
            var tgt = Tgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) { StatusText.Text = "请先选择语言对。"; return; }
            var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = string.Format("{0}-{1}.{2}.csv", src, tgt, IsPost ? "post" : "pre"),
            };
            if (dlg.ShowDialog(this) != true) return;

            _db.ExportCsv(Kind, src, tgt, dlg.FileName, Domain);
            StatusText.Text = "导出完成：" + dlg.FileName;
        }

        private void CloseButton(object sender, RoutedEventArgs e) => Close();

        private void NotifyChanged() => _provider?.InvalidateCache();
    }
}