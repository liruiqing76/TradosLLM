using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using TradosToolkit.Diagnostics;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 术语管理独立页：译前/译后术语库的增删改查 + CSV 导入导出 + 模板下载。
    /// 语言对用纯下拉：列出 Studio 支持的全部语言（默认带出当前项目语言），既可点选常用，
    /// 也涵盖所有语种，无需手打语言代码。通过 Add-ins 附加项 Ribbon 按钮独立打开（不再是嵌套弹窗）。
    /// </summary>
    public partial class GlossaryManagerWindow : Window
    {
        private static GlossaryManagerWindow _instance;
        private readonly GlossaryDb _db;
        private readonly SqliteGlossaryProvider _provider;
        private readonly List<LangItem> _langs;

        /// <summary>下拉项：显示中文名+代码，选中带回 IsoAbbreviation。</summary>
        private class LangItem
        {
            public string Code { get; set; }
            public string Label { get; set; }
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
            ToolkitLog.Info("术语管理：新建独立页");
            var w = new GlossaryManagerWindow(null, null);
            _instance = w;
            w.Closed += (s, e) => { if (ReferenceEquals(_instance, w)) _instance = null; };
            w.Show();
        }

        /// <summary>从外部（工作台/配置窗口）打开时也可带语言默认值；为 null 则自动取当前项目。</summary>
        public GlossaryManagerWindow(string src, string tgt, SqliteGlossaryProvider provider = null)
        {
            InitializeComponent();
            _db = new GlossaryDb();
            _provider = provider;
            _langs = BuildLanguages();
            // 用独立 ListCollectionView 做打字过滤（重设 ItemsSource 会把输入框文本清掉，故不能换源）
            AttachLangFilter(SrcCombo);
            AttachLangFilter(TgtCombo);
            DomCombo.ItemsSource = DomainTree.Flatten(DomainTree.Defaults());

            // 领域默认取全局配置（工作台里切换的领域），保证术语管理与翻译插件同一领域
            var cfgDomain = ToolkitConfig.Load().Domain;
            DomCombo.SelectedItem = DomCombo.Items.OfType<string>()
                .FirstOrDefault(d => string.Equals(d, cfgDomain, StringComparison.OrdinalIgnoreCase))
                ?? DomainTree.DefaultDomain;

            if (string.IsNullOrWhiteSpace(src)) src = null;
            if (string.IsNullOrWhiteSpace(tgt)) tgt = null;
            if (src == null && tgt == null) TryFillProjectLanguages(ref src, ref tgt);

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
        /// 语言下拉"下拉内搜索框"过滤：模板里 Popup 顶部有可见输入框 LangSearchBox，
        /// 打开下拉自动聚焦它；输入只改 ListCollectionView.Filter（不换 ItemsSource、不清文本）；
        /// 收起下拉清空关键词并恢复完整清单。
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
                    }
                }
                if (search != null)
                {
                    // 下拉打开后 ComboBox 会把焦点抢回列表选中项，必须延迟一帧再聚焦搜索框，否则敲不进字
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
                if (search != null) search.Text = "";
                view.Filter = null;
                view.Refresh();
            };

            // Studio 宿主下 Popup 的独立 Win32 窗口可能拿不到激活，搜索框永远得不到键盘焦点
            // （纯 WPF 测试窗口复现不出）。兜底：落在 combo 子树上的 TextInput/退格/空格一律转发进搜索框。
            combo.AddHandler(UIElement.TextInputEvent, new System.Windows.Input.TextCompositionEventHandler((s, ev) =>
            {
                if (search == null || string.IsNullOrEmpty(ev.Text)) return;
                if (System.Windows.Input.Keyboard.FocusedElement is TextBoxBase) return; // 已在搜索框里直接输入
                search.AppendText(ev.Text);
                ev.Handled = true;
            }), true);
            combo.AddHandler(UIElement.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler((s, ev) =>
            {
                if (search == null) return;
                if (System.Windows.Input.Keyboard.FocusedElement is TextBoxBase) return;
                if (ev.Key == System.Windows.Input.Key.Back)
                {
                    var t = search.Text;
                    if (t.Length > 0) search.Text = t.Substring(0, t.Length - 1);
                    ev.Handled = true;
                }
                else if (ev.Key == System.Windows.Input.Key.Space)
                {
                    search.AppendText(" ");
                    ev.Handled = true;
                }
            }), true);
        }

        private bool IsPost => KindCombo.SelectedIndex == 1;
        private string Kind => IsPost ? GlossaryDb.KindPost : GlossaryDb.KindPre;

        private ObservableCollection<GlossaryEntry> Terms { get; set; }
        private List<GlossaryEntry> _allTerms = new List<GlossaryEntry>();

        /// <summary>枚举 Studio 支持的全部语言，中文化名并按代码排序；进程级缓存，二次打开窗口不再重复枚举。</summary>
        private static List<LangItem> _langsCache;
        private List<LangItem> BuildLanguages()
        {
            if (_langsCache != null) return _langsCache;
            var list = new List<LangItem>();
            try
            {
                IEnumerable<Sdl.Core.Globalization.Language> all =
                    Sdl.Core.Globalization.Language.GetAllLanguages();
                foreach (var l in all)
                {
                    var code = l.IsoAbbreviation;
                    var name = l.IsoAbbreviation;
                    try { name = new CultureInfo(code.Replace("_", "-")).EnglishName; }
                    catch (Exception) { name = code; }
                    list.Add(new LangItem { Code = code, Label = name + "  ·  " + code });
                }
                if (list.Count == 0) throw new Exception("Studio 语言清单为空");
            }
            catch (Exception ex)
            {
                ToolkitLog.Error("术语管理：枚举 Studio 语言失败", ex);
                // 回退：至少给出常用中英两种，保证界面可用
                list.Add(new LangItem { Code = "zh-CN", Label = "Chinese simplified  ·  zh-CN" });
                list.Add(new LangItem { Code = "en-US", Label = "English (US)  ·  en-US" });
            }
            return _langsCache = list.OrderBy(l => l.Label).ToList();
        }

        private void TryFillProjectLanguages(ref string src, ref string tgt)
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
            // 只在真正点选(SelectedValue 非空)时更新权威语向；过滤时 SelectedItem 被清空不应覆盖
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

        private void AddTerm(object sender, RoutedEventArgs e)
        {
            var entry = new GlossaryEntry { From = "新术语", To = "替换为", Domain = Domain };
            Terms.Add(entry);
            TermsGrid.SelectedItem = entry;
            TermsGrid.BeginEdit();
        }

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

        private void SaveTerms(object sender, RoutedEventArgs e)
        {
            var src = Src;
            var tgt = Tgt;
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(tgt)) { StatusText.Text = "请先选择语言对。"; return; }
            TermsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var dom = Domain;
            foreach (var entry in Terms)
            {
                if (string.IsNullOrWhiteSpace(entry.Domain)) entry.Domain = dom;
                _db.SaveTerm(Kind, src, tgt, entry);
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