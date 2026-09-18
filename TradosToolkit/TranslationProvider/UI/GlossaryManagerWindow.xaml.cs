using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TradosToolkit.Glossaries;

namespace TradosToolkit.TranslationProvider.UI
{
    /// <summary>
    /// 译前/译后术语库管理：增删改查 + CSV 导入导出，数据存本地 SQLite。
    /// </summary>
    public partial class GlossaryManagerWindow : Window
    {
        private readonly GlossaryDb _db;
        private readonly SqliteGlossaryProvider _provider;

        public GlossaryManagerWindow(string src, string tgt, SqliteGlossaryProvider provider = null)
        {
            InitializeComponent();
            _db = new GlossaryDb();
            _provider = provider;
            SrcBox.Text = src;
            TgtBox.Text = tgt;
            Reload(null, null);
        }

        private bool IsPost => KindCombo.SelectedIndex == 1;
        private string Kind => IsPost ? GlossaryDb.KindPost : GlossaryDb.KindPre;

        private ObservableCollection<GlossaryEntry> Terms { get; set; }

        private void Reload(object sender, RoutedEventArgs e)
        {
            if (TermsGrid == null) return; // InitializeComponent 期间的 SelectionChanged
            Terms = new ObservableCollection<GlossaryEntry>(
                _db.GetTerms(Kind, SrcBox.Text.Trim(), TgtBox.Text.Trim()));
            TermsGrid.ItemsSource = Terms;
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
            foreach (var entry in TermsGrid.SelectedItems.Cast<GlossaryEntry>().ToList())
            {
                if (entry.Id > 0) _db.DeleteTerm(entry.Id);
                Terms.Remove(entry);
            }
            NotifyChanged();
        }

        private void SaveTerms(object sender, RoutedEventArgs e)
        {
            TermsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            foreach (var entry in Terms)
                _db.SaveTerm(Kind, SrcBox.Text.Trim(), TgtBox.Text.Trim(), entry);
            Reload(null, null);
            NotifyChanged();
        }

        private void ImportCsv(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "CSV 文件|*.csv|所有文件|*.*" };
            if (dlg.ShowDialog(this) != true) return;

            var n = _db.ImportCsv(Kind, SrcBox.Text.Trim(), TgtBox.Text.Trim(), dlg.FileName);
            Reload(null, null);
            NotifyChanged();
            MessageBox.Show(this, $"已导入 {n} 条术语", "TradosToolkit");
        }

        private void ExportCsv(object sender, RoutedEventArgs e)
        {
            var src = SrcBox.Text.Trim();
            var tgt = TgtBox.Text.Trim();
            var dlg = new SaveFileDialog
            {
                Filter = "CSV 文件|*.csv",
                FileName = $"{src}-{tgt}.{(IsPost ? "post" : "pre")}.csv",
            };
            if (dlg.ShowDialog(this) != true) return;

            _db.ExportCsv(Kind, src, tgt, dlg.FileName);
            MessageBox.Show(this, "导出完成", "TradosToolkit");
        }

        private void CloseButton(object sender, RoutedEventArgs e) => Close();

        private void NotifyChanged() => _provider?.InvalidateCache();
    }
}
