using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
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
            SrcCombo.ItemsSource = _langs;
            TgtCombo.ItemsSource = _langs;

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

            Reload(null, null);
        }

        private bool IsPost => KindCombo.SelectedIndex == 1;
        private string Kind => IsPost ? GlossaryDb.KindPost : GlossaryDb.KindPre;

        private ObservableCollection<GlossaryEntry> Terms { get; set; }

        /// <summary>枚举 Studio 支持的全部语言，中文化名并按代码排序。</summary>
        private List<LangItem> BuildLanguages()
        {
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
            return list.OrderBy(l => l.Label).ToList();
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
            Reload(null, null);
        }

        private string Src => (SrcCombo.SelectedValue as string) ?? "";
        private string Tgt => (TgtCombo.SelectedValue as string) ?? "";

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
            Terms = new ObservableCollection<GlossaryEntry>(_db.GetTerms(kind, src, tgt));
            TermsGrid.ItemsSource = Terms;
            StatusText.Text = string.Format("{0} · {1} → {2} · 共 {3} 条",
                IsPost ? "译后" : "译前", src, tgt, Terms.Count);
        }

        private void AddTerm(object sender, RoutedEventArgs e)
        {
            var entry = new GlossaryEntry { From = "新术语", To = "替换为" };
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
            foreach (var entry in Terms)
                _db.SaveTerm(Kind, src, tgt, entry);
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

            var n = _db.ImportCsv(Kind, src, tgt, dlg.FileName);
            RefreshAfterMutation();
            NotifyChanged();
            StatusText.Text = string.Format("已导入 {0} 条术语（{1} → {2}）", n, src, tgt);
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

            _db.ExportCsv(Kind, src, tgt, dlg.FileName);
            StatusText.Text = "导出完成：" + dlg.FileName;
        }

        private void CloseButton(object sender, RoutedEventArgs e) => Close();

        private void NotifyChanged() => _provider?.InvalidateCache();
    }
}