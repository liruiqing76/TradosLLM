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
using TradosToolkit.Common.Catalog;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 术语管理独立页，分两个页签——两者是**两码事**，各自独立的存储与用途：
    /// 1)「替换词条（译前/译后）」：扁平 from→to，写 terms 表，供翻译流水线 TermReplacer 做译前替换源文 / 译后修正译文；
    /// 2)「术语表（术语识别）」：完整模型（词性/定义/例句/状态/同义词/领域），写 term_entries 表，
    ///    是真正的术语库，供 Studio 原生术语引擎识别/查询与项目术语挂载使用。
    /// 语言对下拉带搜索框：点开下拉顶部即见输入框，敲代码/名称即输即筛（默认带出当前项目语言）。
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

        // InitializeComponent 全部完成前为 false：XAML 里带 SelectionChanged 的控件
        // （KindCombo 有 SelectedIndex="0"）会在解析期即回调，此刻 MainTabs 字段虽已赋值，
        // 但其内部控件（ReplGrid/ReplStatusText 等）尚未创建，用 MainTabs==null 当哨兵会漏判 →
        // Reload 触到 null 控件抛 NRE → 构造失败 → _instance 变僵尸实例 → 之后点击无反应。
        private bool _ready;

        // 当前页签（0=替换词条，1=术语表）
        private bool OnGlossaryTab => _ready && MainTabs != null && MainTabs.SelectedIndex == 1;

        // 两套语言对各自记忆：替换词条页用 _replSrc/_replTgt，术语表页用 _lastSrc/_lastTgt
        private string _lastSrc = "", _lastTgt = "";
        private string _replSrc = "", _replTgt = "";

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
                    w.Closed += (s, e) =>
                    {
                        if (ReferenceEquals(_instance, w)) _instance = null;
                        w.Dispatcher.InvokeShutdown(); // 窗口关了就撤线程泵
                    };
                    w.Show(); // 先 Show 成功再登记实例：Show 抛异常则 _instance 保持 null，下次点击可重试
                    _instance = w;
                    ready.Set();
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    ToolkitLog.Error("术语管理：独立线程创建失败", ex);
                    _instance = null; // 失败不留僵尸实例，否则后续点击只会 Activate 一个从未显示的窗口
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

            // 各下拉挂"下拉内搜索框"（独立 ListCollectionView，只改 Filter 不换 ItemsSource）
            AttachLangFilter(ReplSrcCombo);
            AttachLangFilter(ReplTgtCombo);
            AttachLangFilter(SrcCombo);
            AttachLangFilter(TgtCombo);

            // 领域下拉：两个页签各一份，默认取全局配置（工作台里切换的领域），与翻译插件共用同一领域
            var domNames = DomainCatalog.Names();
            var cfgDomain = ToolkitConfig.Load().Domain;
            var domDefault = domNames.FirstOrDefault(d => string.Equals(d, cfgDomain, StringComparison.OrdinalIgnoreCase))
                             ?? DomainTree.DefaultDomain;
            ReplDomCombo.ItemsSource = domNames;
            ReplDomCombo.SelectedItem = domDefault;
            DomCombo.ItemsSource = domNames;
            DomCombo.SelectedItem = domDefault;

            if (string.IsNullOrWhiteSpace(src)) src = null;
            if (string.IsNullOrWhiteSpace(tgt)) tgt = null;
            // 注意：不再在构造里取当前项目语言——SdlTradosStudio.Automation 只能在 Studio UI 线程访问，
            // 独立线程方案下由 ShowOrActivate 在建线程前取好传进来。

            // 两个页签的语言对下拉都按传入的项目语言初始化
            InitLangPair(ReplSrcCombo, ReplTgtCombo, src, tgt);
            InitLangPair(SrcCombo, TgtCombo, src, tgt);
            _replSrc = LangCodeOf(ReplSrcCombo, src);
            _replTgt = LangCodeOf(ReplTgtCombo, tgt);
            _lastSrc = LangCodeOf(SrcCombo, src);
            _lastTgt = LangCodeOf(TgtCombo, tgt);

            _ready = true; // 控件已全部建好，此后 SelectionChanged 才允许触发 Reload
            Reload(null, null);
        }

        /// <summary>
        /// 用给定语言代码初始化一对下拉：大小写不敏感匹配（Studio 的 IsoAbbreviation 大小写不稳定，
        /// 如 zh-cn / zh-CN）；绝不能"找不到就退到第一个语言"，否则会把词条存到项目语言对之外的语向下。
        /// </summary>
        private void InitLangPair(ComboBox srcCombo, ComboBox tgtCombo, string src, string tgt)
        {
            var srcItem = FindLang(_langs, src);
            var tgtItem = FindLang(_langs, tgt);
            SetComboSelection(srcCombo, srcItem ?? _langs.FirstOrDefault());
            if (tgtItem == null)
            {
                var zh = FindLang(_langs, "zh-CN")
                         ?? _langs.FirstOrDefault(l => l.Code.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
                         ?? _langs.Skip(1).FirstOrDefault();
                SetComboSelection(tgtCombo, zh);
            }
            else SetComboSelection(tgtCombo, tgtItem);
        }

        /// <summary>从下拉取出权威语言代码：优先用传入的项目代码（已归一），拿不到才回退到实际选中项。</summary>
        private string LangCodeOf(ComboBox combo, string projectCode)
        {
            if (!string.IsNullOrEmpty(projectCode)) return NormalizeLang(projectCode, _langs);
            return combo.SelectedValue as string;
        }

        /// <summary>按代码取语言项，大小写不敏感；code 为空返回 null。</summary>
        private static LangItem FindLang(List<LangItem> langs, string code)
        {
            if (string.IsNullOrWhiteSpace(code) || langs == null) return null;
            return langs.FirstOrDefault(l =>
                string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>把语言代码归一为目录中的规范写法（大小写修正）；目录里没有则原样返回。</summary>
        private static string NormalizeLang(string code, List<LangItem> langs)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;
            var hit = FindLang(langs, code);
            return hit == null ? code : hit.Code;
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
                    var code = ReferenceEquals(combo, ReplSrcCombo) ? _replSrc
                             : ReferenceEquals(combo, ReplTgtCombo) ? _replTgt
                             : ReferenceEquals(combo, SrcCombo) ? _lastSrc : _lastTgt;
                    var item = _langs.FirstOrDefault(l =>
                        string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
                    if (item != null) SetComboSelection(combo, item);
                }
            };
        }

        /// <summary>ComboBox 拒收指向被过滤项的赋值，故先清过滤再赋值。</summary>
        private static void SetComboSelection(ComboBox combo, LangItem item)
        {
            if (combo == null || item == null) return;
            var view = combo.ItemsSource as System.Windows.Data.ListCollectionView;
            if (view != null && view.Filter != null) { view.Filter = null; view.Refresh(); }
            combo.SelectedItem = item;
        }

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

        // ====================== 页签切换 / 事件 ======================

        private void MainTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            // SelectionChanged 是冒泡路由事件：内部 DataGrid 点选行、ComboBox 换选项都会冒泡到本 TabControl。
            // 若不判别来源，点一下表格行就会触发 Reload 重建 ItemsSource，把刚选中的行又清掉
            //（表现为「单击选不中、双击也弹不出编辑」）。这里只处理页签自身的切换。
            if (!ReferenceEquals(e.Source, MainTabs)) return;
            Reload(null, null);
        }

        private bool IsPost => KindCombo != null && KindCombo.SelectedIndex == 1;
        private string Kind => IsPost ? GlossaryDb.KindPost : GlossaryDb.KindPre;

        private void KindChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return; // InitializeComponent 期间
            Reload(null, null);
        }

        private void LangChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            // 只在真正点选(SelectedValue 非空)时更新权威语向；过滤清掉 SelectedItem 时不应覆盖
            var v = (sender as ComboBox)?.SelectedValue as string;
            if (!string.IsNullOrEmpty(v))
            {
                if (ReferenceEquals(sender, ReplSrcCombo)) _replSrc = v;
                else if (ReferenceEquals(sender, ReplTgtCombo)) _replTgt = v;
                else if (ReferenceEquals(sender, SrcCombo)) _lastSrc = v;
                else if (ReferenceEquals(sender, TgtCombo)) _lastTgt = v;
            }
            Reload(null, null);
        }

        private void DomainChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_ready) return;
            // 领域在"全局配置"里是共享的：两页签任一处切换都同步到另一边，保证与翻译插件同一领域
            var v = (sender as ComboBox)?.SelectedItem as string;
            if (!string.IsNullOrEmpty(v))
            {
                if (ReferenceEquals(sender, ReplDomCombo) && DomCombo != null) DomCombo.SelectedItem = v;
                else if (ReferenceEquals(sender, DomCombo) && ReplDomCombo != null) ReplDomCombo.SelectedItem = v;
            }
            Reload(null, null);
        }

        private string Src => _lastSrc;
        private string Tgt => _lastTgt;
        private string ReplSrc => _replSrc;
        private string ReplTgt => _replTgt;
        private string Domain => (OnGlossaryTab
            ? DomCombo?.SelectedValue as string
            : ReplDomCombo?.SelectedValue as string) ?? DomainTree.DefaultDomain;

        // ====================== 加载 ======================

        private void Reload(object sender, RoutedEventArgs e)
        {
            if (!_ready) return; // InitializeComponent 期间的 SelectionChanged
            if (OnGlossaryTab) ReloadGlossary();
            else ReloadRepl();
        }

        // ---------- 页签一：替换词条（terms 表，扁平 from→to） ----------

        private ObservableCollection<GlossaryEntry> ReplTerms { get; set; }
        private List<GlossaryEntry> _allRepl = new List<GlossaryEntry>();

        private void ReloadRepl()
        {
            var src = ReplSrc;
            var tgt = ReplTgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt))
            {
                ReplTerms = new ObservableCollection<GlossaryEntry>();
                ReplGrid.ItemsSource = ReplTerms;
                ReplStatusText.Text = "请在上方选择源/目标语言。";
                return;
            }
            var dom = Domain;
            try { _allRepl = _db.GetTerms(Kind, src, tgt, dom); }
            catch (Exception ex)
            {
                ToolkitLog.Error("术语管理：加载替换词条失败", ex);
                _allRepl = new List<GlossaryEntry>();
            }
            ApplyReplFilter();
            ReplStatusText.Text = string.Format("{0} · {1} → {2} · 领域 {3} · 共 {4} 条",
                IsPost ? "译后" : "译前", src, tgt, dom, _allRepl.Count);
        }

        private void ReplSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (ReplTerms == null) return;
            ApplyReplFilter();
        }

        private void ApplyReplFilter()
        {
            var q = ReplSearchBox == null ? "" : (ReplSearchBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q)) ReplTerms = new ObservableCollection<GlossaryEntry>(_allRepl);
            else
            {
                var ql = q.ToLowerInvariant();
                ReplTerms = new ObservableCollection<GlossaryEntry>(_allRepl.Where(x =>
                    (x.From ?? "").ToLowerInvariant().Contains(ql) ||
                    (x.To ?? "").ToLowerInvariant().Contains(ql) ||
                    (x.Domain ?? "").ToLowerInvariant().Contains(ql)));
            }
            ReplGrid.ItemsSource = ReplTerms;
        }

        /// <summary>新增替换词条：扁平表单（替换前/替换为/领域），写 terms 表供流水线替换。</summary>
        private void AddReplTerm(object sender, RoutedEventArgs e)
        {
            if (!EnsureReplPair()) return;
            var dlg = new TermDialog(null, KindLabel, PairLabel, DomainList, Domain, ReplSrc, ReplTgt) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _db.SaveTerm(Kind, ReplSrc, ReplTgt, new GlossaryEntry
            {
                From = dlg.Result.FromTerm,
                To = dlg.Result.ToTerm,
                Domain = Domain,
            });
            RefreshAfterMutation();
            ReplStatusText.Text = "已新增替换词条。";
        }

        private void DeleteReplTerms(object sender, RoutedEventArgs e)
        {
            if (!EnsureReplPair()) return;
            foreach (var entry in ReplGrid.SelectedItems.Cast<GlossaryEntry>().ToList())
            {
                if (entry.Id > 0) _db.DeleteTerm(entry.Id);
                ReplTerms.Remove(entry);
            }
            RefreshAfterMutation();
            ReplStatusText.Text = "已删除选中替换词条。";
        }

        /// <summary>双击替换词条行弹出表单修改：与「新增」同一表单，预填原值并保留主键。</summary>
        private void EditReplTerm(object sender, MouseButtonEventArgs e)
        {
            var row = FindRow(e.OriginalSource as DependencyObject);
            var entry = row == null ? null : row.Item as GlossaryEntry;
            if (entry == null || entry.Id <= 0) return;
            if (!EnsureReplPair()) return;

            var prefill = new TermEntry
            {
                Id = entry.Id,
                SourceLang = ReplSrc,
                TargetLang = ReplTgt,
                FromTerm = entry.From,
                ToTerm = entry.To,
                Domain = entry.Domain,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt,
            };
            var dlg = new TermDialog(prefill, KindLabel, PairLabel, DomainList, Domain, ReplSrc, ReplTgt) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _db.SaveTerm(Kind, ReplSrc, ReplTgt, new GlossaryEntry
            {
                Id = entry.Id,
                From = dlg.Result.FromTerm,
                To = dlg.Result.ToTerm,
                Domain = Domain,
                CreatedAt = entry.CreatedAt,
                UpdatedAt = entry.UpdatedAt,
            });
            RefreshAfterMutation();
            ReplStatusText.Text = "已更新替换词条。";
        }

        private bool EnsureReplPair()
        {
            if (!string.IsNullOrEmpty(ReplSrc) && !string.IsNullOrEmpty(ReplTgt)) return true;
            ReplStatusText.Text = "请先选择语言对。";
            return false;
        }

        private string KindLabel => IsPost ? "译后" : "译前";
        private string PairLabel => OnGlossaryTab
            ? string.Format("{0} → {1}", Src, Tgt)
            : string.Format("{0} → {1}", ReplSrc, ReplTgt);
        private List<string> DomainList => DomainCatalog.Names();

        // ---------- 页签二：术语表（term_entries 表，完整模型） ----------

        private ObservableCollection<TermEntry> Terms { get; set; }
        private List<TermEntry> _allTerms = new List<TermEntry>();

        private void ReloadGlossary()
        {
            var src = Src;
            var tgt = Tgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt))
            {
                Terms = new ObservableCollection<TermEntry>();
                TermsGrid.ItemsSource = Terms;
                StatusText.Text = "请在上方选择源/目标语言。";
                return;
            }
            var dom = Domain;
            _allTerms = _db.GetTermEntries(src, tgt, dom);
            ApplySearchFilter();
            StatusText.Text = string.Format("术语表 · {0} → {1} · 领域 {2} · 共 {3} 条",
                src, tgt, dom, _allTerms.Count);
        }

        private void TermSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (Terms == null) return;
            ApplySearchFilter();
        }

        /// <summary>按搜索框的 源/目标/领域/定义/同义词 关键字过滤当前已加载术语条目。</summary>
        private void ApplySearchFilter()
        {
            var q = TermSearchBox == null ? "" : (TermSearchBox.Text ?? "").Trim();
            if (string.IsNullOrEmpty(q))
            {
                Terms = new ObservableCollection<TermEntry>(_allTerms);
            }
            else
            {
                var ql = q.ToLowerInvariant();
                Terms = new ObservableCollection<TermEntry>(
                    _allTerms.Where(e =>
                        (e.FromTerm ?? "").ToLowerInvariant().Contains(ql) ||
                        (e.ToTerm ?? "").ToLowerInvariant().Contains(ql) ||
                        (e.Domain ?? "").ToLowerInvariant().Contains(ql) ||
                        (e.Definition ?? "").ToLowerInvariant().Contains(ql) ||
                        (e.SynonymText ?? "").ToLowerInvariant().Contains(ql)));
            }
            TermsGrid.ItemsSource = Terms;
        }

        /// <summary>新增：弹完整表单收集术语信息，确定后落库。</summary>
        private void AddTerm(object sender, RoutedEventArgs e)
        {
            if (!EnsurePair()) return;
            var dlg = new TermDialog(null, KindLabel, PairLabel, DomainList, Domain, Src, Tgt) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _db.SaveTermEntry(dlg.Result);
            RefreshAfterMutation();
            StatusText.Text = "已新增术语。";
        }

        /// <summary>双击某行改为弹完整表单修改；落在表头/滚动条/空白处不响应。</summary>
        private void EditTerm(object sender, MouseButtonEventArgs e)
        {
            var row = FindRow(e.OriginalSource as DependencyObject);
            var entry = row == null ? null : row.Item as TermEntry;
            if (entry == null || entry.Id <= 0) return;
            if (!EnsurePair()) return;
            var dlg = new TermDialog(entry, KindLabel, PairLabel, DomainList, Domain, Src, Tgt) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            _db.SaveTermEntry(dlg.Result);
            RefreshAfterMutation();
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

        private void DeleteTerms(object sender, RoutedEventArgs e)
        {
            var src = Src;
            var tgt = Tgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) { StatusText.Text = "请先选择语言对。"; return; }
            foreach (var entry in TermsGrid.SelectedItems.Cast<TermEntry>().ToList())
            {
                if (entry.Id > 0) _db.DeleteTermEntry(entry.Id);
                Terms.Remove(entry);
            }
            RefreshAfterMutation();
            StatusText.Text = "已删除选中术语。";
        }

        private void RefreshAfterMutation()
        {
            Reload(null, null);
            NotifyChanged();
        }

        private void NotifyChanged() => _provider?.InvalidateCache();

        // ====================== CSV / 模板 ======================

        private void DownloadTemplate(object sender, RoutedEventArgs e)
        {
            var repl = !OnGlossaryTab;
            var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = repl ? "替换词条模板.csv" : "术语表模板.csv",
            };
            if (dlg.ShowDialog(this) != true) return;
            File.WriteAllText(dlg.FileName,
                repl ? "from,to\r\n" : "from,to,pos,status,domain,definition,example,note,src_syn,tgt_syn\r\n",
                new UTF8Encoding(true));
            (repl ? ReplStatusText : StatusText).Text = "模板已下载：" + dlg.FileName;
        }

        private void ImportReplCsv(object sender, RoutedEventArgs e)
        {
            if (!EnsureReplPair()) return;
            var dlg = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*" };
            if (dlg.ShowDialog(this) != true) return;
            var n = _db.ImportCsv(Kind, ReplSrc, ReplTgt, dlg.FileName, Domain);
            RefreshAfterMutation();
            ReplStatusText.Text = string.Format("已导入 {0} 条替换词条（{1} → {2} · {3}）", n, ReplSrc, ReplTgt, Domain);
        }

        private void ExportReplCsv(object sender, RoutedEventArgs e)
        {
            if (!EnsureReplPair()) return;
            var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = string.Format("{0}-{1}.{2}.csv", ReplSrc, ReplTgt, Kind),
            };
            if (dlg.ShowDialog(this) != true) return;
            _db.ExportCsv(Kind, ReplSrc, ReplTgt, dlg.FileName, Domain);
            ReplStatusText.Text = "导出完成：" + dlg.FileName;
        }

        private void ImportCsv(object sender, RoutedEventArgs e)
        {
            var src = Src;
            var tgt = Tgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) { StatusText.Text = "请先选择语言对。"; return; }
            var dlg = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*" };
            if (dlg.ShowDialog(this) != true) return;

            var n = _db.ImportTermEntriesCsv(src, tgt, dlg.FileName, Domain);
            RefreshAfterMutation();
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
                FileName = string.Format("{0}-{1}.terms.csv", src, tgt),
            };
            if (dlg.ShowDialog(this) != true) return;

            _db.ExportTermEntriesCsv(src, tgt, dlg.FileName, Domain);
            StatusText.Text = "导出完成：" + dlg.FileName;
        }
    }
}
